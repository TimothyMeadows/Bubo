"""Fixed, source-preserving Blender construction inspection and CPU QA views.

This separate QA tool loads one exact authored .blend with scripts disabled,
inventories its original data, then changes only in-memory render visibility,
lighting and cameras. It never saves the source, exports a model or grants
human/runtime acceptance. CPU Cycles is explicit; there is no renderer fallback.
"""

import hashlib
import importlib.util
import json
import math
import os
from pathlib import Path
import re
import stat
import struct
import sys

sys.dont_write_bytecode = True
MAX_JOB_BYTES = 1024 * 1024
MAX_FLOORS = 128
IMAGE_SIZE = 768
HASH = re.compile(r"[0-9a-f]{64}\Z")


def _path(value):
    path = Path(value)
    normalized = str(path).replace("\\", "/")
    if (not path.is_absolute() or normalized.startswith("//") or
            any(part.lower() in (".", "..", ".git") for part in normalized.split("/"))):
        raise ValueError("Review requires absolute local non-traversing job/resource paths")
    current = Path(path.anchor)
    for component in path.parts[1:]:
        if component.endswith((".", " ")) or ":" in component:
            raise ValueError("Review job/resource has an ambiguous path component")
        current /= component
        info = current.lstat()
        if (stat.S_ISLNK(info.st_mode) or
                getattr(info, "st_file_attributes", 0) & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)):
            raise ValueError("Review job/resource contains a symbolic link/reparse point")
    if not path.is_file():
        raise ValueError("Review job/resource is not a regular file")
    return path.resolve(strict=True)


def _unique_pairs(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("Duplicate review-job property: " + key)
        result[key] = value
    return result


def bootstrap(arguments):
    if len(arguments) != 2 or HASH.fullmatch(arguments[1]) is None:
        raise ValueError("Usage: review.py -- ABSOLUTE_JOB_JSON EXPECTED_JOB_SHA256")
    source = _path(arguments[0])
    if not 2 <= source.stat().st_size <= MAX_JOB_BYTES:
        raise ValueError("Missing or oversized review job")
    with source.open("rb") as handle:
        raw = handle.read(MAX_JOB_BYTES + 1)
    if len(raw) > MAX_JOB_BYTES or hashlib.sha256(raw).hexdigest() != arguments[1]:
        raise ValueError("Review job identity changed")
    job = json.loads(raw.decode("utf-8"), object_pairs_hook=_unique_pairs,
                     parse_constant=lambda value: (_ for _ in ()).throw(ValueError("Non-finite review JSON: " + value)))
    if not isinstance(job, dict) or not isinstance(job.get("bundle"), dict) or set(job["bundle"]) != {"review.py", "admission.py"}:
        raise ValueError("Review requires its exact fixed bundle identity")
    directory = Path(__file__).resolve(strict=True).parent
    for name, expected in job["bundle"].items():
        if not isinstance(expected, str) or HASH.fullmatch(expected) is None:
            raise ValueError("Invalid fixed review bundle hash")
        path = _path(directory / name)
        if path.stat().st_size > 2 * 1024 * 1024 or hashlib.sha256(path.read_bytes()).hexdigest() != expected:
            raise ValueError("Audited review resource changed: " + name)
    specification = importlib.util.spec_from_file_location("_bubo_floor_plan_review_admission", directory / "admission.py")
    if specification is None or specification.loader is None:
        raise ValueError("Fixed review admission module missing")
    module = importlib.util.module_from_spec(specification)
    sys.modules[specification.name] = module
    specification.loader.exec_module(module)
    # Use the common bounded decoder for its explicit 64-level nesting guard.
    # Review jobs are at most one MiB, not native geometry payloads.
    decoded = module.decode_json(raw)
    admitted, blend, floors = validate_job(module, decoded, source, len(raw))
    verify_sources(module, admitted, blend, arguments[1], directory)
    return module, admitted, blend, floors, arguments[1], directory


def validate_job(admission, job, job_path, raw_length):
    admission.closed(job, ("schemaVersion", "workspaceRoot", "source", "sourceSha256", "outputDirectory", "bundle", "limits", "floors"), label="review job")
    if job["schemaVersion"] != "bubo-floor-plan-blender-review-job/v1":
        admission.fail("unsupported review job schema")
    admission.closed(job["bundle"], ("review.py", "admission.py"), label="review bundle")
    for name, value in job["bundle"].items():
        admission.hash_value(value, name)
    admission.hash_value(job["sourceSha256"], "review sourceSha256")
    limits = admission.closed(job["limits"], ("maxObjects", "maxVertices", "maxTriangles", "maxOutputBytes", "maxSourceBytes", "maxJobBytes", "maxViews", "imageSize"), label="review limits")
    for name, maximum in (("maxObjects", admission.MAX_OBJECTS), ("maxVertices", admission.MAX_VERTICES),
                          ("maxTriangles", admission.MAX_TRIANGLES), ("maxOutputBytes", admission.MAX_OUTPUT_BYTES),
                          ("maxSourceBytes", admission.MAX_OUTPUT_BYTES), ("maxJobBytes", MAX_JOB_BYTES),
                          ("maxViews", MAX_FLOORS + 4)):
        admission.integer(limits[name], name, 1, maximum)
    if admission.integer(limits["imageSize"], "imageSize") != IMAGE_SIZE:
        admission.fail("fixed QA imageSize must be 768")
    admission.integer(raw_length, "review job bytes", 2, limits["maxJobBytes"])
    root, source, output = admission.validate_locations(job, job_path)
    blend = admission.safe_absolute(job["source"], "review source")
    if not blend.is_relative_to(root) or blend.suffix.lower() != ".blend" or blend == source or blend.is_relative_to(output):
        admission.fail("review source must be a separate existing project-local blend")
    if not blend.is_file() or not 1 <= blend.stat().st_size <= limits["maxSourceBytes"]:
        admission.fail("review source is missing or exceeds maxSourceBytes")
    if admission.hash_file(blend, limits["maxSourceBytes"]) != job["sourceSha256"]:
        admission.fail("review source identity changed")
    floors = []
    seen = set()
    for value in admission.array(job["floors"], "review floors", MAX_FLOORS, 1):
        admission.closed(value, ("id", "elevation", "height"), label="review floor")
        identifier = admission.text(value["id"], "review floor id")
        if identifier in seen:
            admission.fail("duplicate review floor id")
        seen.add(identifier)
        elevation = admission.number(value["elevation"], "review floor elevation")
        height = admission.number(value["height"], "review floor height", 1e-12)
        floors.append({"id": identifier, "elevation": elevation, "height": height})
    floors.sort(key=lambda value: (value["elevation"], value["id"]))
    if len(floors) + 4 > limits["maxViews"]:
        admission.fail("all floor/roof/exterior/vertical views must fit maxViews")
    return admission.AdmittedJob(job, root, source, output, {}), blend, floors


def verify_sources(admission, admitted, blend, expected_job_hash, bundle_directory):
    admission.verify_current_sources(admitted, expected_job_hash, bundle_directory)
    current = admission.safe_absolute(str(blend), "review source")
    if (not current.is_relative_to(admitted.workspace) or
            admission.hash_file(current, admitted.job["limits"]["maxSourceBytes"]) != admitted.job["sourceSha256"]):
        admission.fail("review source changed; it is never resaved or rebound")


def _point(admission, vector):
    values = tuple(float(component) for component in vector)
    if not all(math.isfinite(value) for value in values):
        raise ValueError("Review encountered a non-finite authored transform")
    return admission.point_object(values)


def _identity(item, name):
    value = item.get("bubo_" + name)
    if not isinstance(value, str) or not value:
        raise ValueError("Authored data lacks semantic property: " + name)
    return value


def require_source_scene_policy(scene):
    """Reject inherited pipelines before explicit view-layer update/rendering."""
    if scene.use_nodes:
        raise ValueError("Source scene compositor use is outside construction QA")
    tree = scene.node_tree
    if tree is not None and (tree.nodes or tree.links):
        raise ValueError("Source scene has a nonempty compositor node graph")
    if getattr(scene, "compositing_node_group", None) is not None:
        raise ValueError("Source scene has an inherited compositor node group")
    if scene.sequence_editor is not None:
        raise ValueError("Source scene sequence editor/strips are outside construction QA")
    if scene.animation_data is not None:
        raise ValueError("Source scene animation is outside construction QA")


def require_source_scope(data):
    """Pure data guards; raw authored vertices must also drive rendered geometry."""
    for scene in data.scenes:
        require_source_scene_policy(scene)
    for mesh in data.meshes:
        if mesh.shape_keys is not None:
            raise ValueError("Source mesh shape keys are outside construction QA")
    for collection in (data.scenes, data.meshes, data.materials, data.worlds):
        for item in collection:
            if item.animation_data is not None:
                raise ValueError("Source scene/mesh/material/world animation is outside construction QA")
            tree = getattr(item, "node_tree", None)
            if tree is not None and tree.animation_data is not None:
                raise ValueError("Source shader/compositor animation is outside construction QA")


def fixed_render_policy(scene, apply=False):
    require_source_scene_policy(scene)
    render = scene.render
    if apply:
        render.use_compositing = False
        render.use_sequencer = False
        render.use_multiview = False
        render.use_border = False
        render.use_crop_to_border = False
        render.use_freestyle = False
    if (render.use_compositing or render.use_sequencer or render.use_multiview or
            render.use_border or render.use_crop_to_border or render.use_freestyle):
        raise ValueError("Fixed QA render policy changed")
    return {"useCompositing": bool(render.use_compositing), "useSequencer": bool(render.use_sequencer),
            "useMultiview": bool(render.use_multiview), "useBorder": bool(render.use_border),
            "useCropToBorder": bool(render.use_crop_to_border), "useFreestyle": bool(render.use_freestyle),
            "sourceFeaturePolicy": {"enabledCompositor": "rejected", "nonemptyCompositorGraph": "rejected",
                                    "sequenceEditor": "rejected", "animatedData": "rejected", "shapeKeys": "rejected"}}


def inventory(bpy, admission, floors, limits):
    from mathutils import Vector

    require_source_scope(bpy.data)
    if len(bpy.data.scenes) != 1 or bpy.data.libraries or bpy.data.texts:
        raise ValueError("Review only accepts the self-contained audited architectural scene")
    if any(image.source not in ("GENERATED", "VIEWER") or image.filepath for image in bpy.data.images):
        raise ValueError("External/packed file-image dependencies are outside construction QA")
    all_objects = list(bpy.data.objects)
    if not 1 <= len(all_objects) <= limits["maxObjects"]:
        raise ValueError("Loaded object count exceeds review bounds")
    floor_ids = {floor["id"] for floor in floors}
    rows, anchors, meshes = [], [], {}
    vertex_count = triangle_count = 0
    seen = set()
    for item in sorted(all_objects, key=lambda value: str(value.get("bubo_id", ""))):
        if item.type not in ("MESH", "EMPTY") or item.modifiers or item.constraints or item.animation_data:
            raise ValueError("Unexpected authored object type/modifier/constraint/animation")
        identifier = _identity(item, "id")
        if identifier in seen:
            raise ValueError("Duplicate loaded object identity")
        seen.add(identifier)
        storey_id = _identity(item, "storeyId")
        if storey_id not in floor_ids:
            raise ValueError("Loaded object belongs to an undeclared review floor")
        common = {"id": identifier, "semanticId": _identity(item, "semanticId"), "storeyId": storey_id, "kind": _identity(item, "kind")}
        if item.type == "EMPTY":
            anchors.append(common | {"position": _point(admission, item.matrix_world.translation),
                                     "axis": _point(admission, item.matrix_world.to_3x3() @ Vector((0, 0, 1))),
                                     "rotationRadians": float(item.rotation_euler.z),
                                     "matrixWorld": [[float(value) for value in row] for row in item.matrix_world]})
            continue
        data = item.data
        data.calc_loop_triangles()
        vertex_count += len(data.vertices)
        triangle_count += len(data.loop_triangles)
        if vertex_count > limits["maxVertices"] or triangle_count > limits["maxTriangles"]:
            raise ValueError("Loaded mesh geometry exceeds review bounds")
        vertices = [tuple(float(component) for component in item.matrix_world @ vertex.co) for vertex in data.vertices]
        if not vertices or not all(math.isfinite(value) for vertex in vertices for value in vertex):
            raise ValueError("Loaded mesh has empty/non-finite world geometry")
        indices = [int(index) for triangle in data.loop_triangles for index in triangle.vertices]
        statistics = admission.inspect_triangle_solid(vertices, indices)
        materials = [_identity(material, "id") for material in data.materials]
        assignments = []
        for triangle in data.loop_triangles:
            index = data.polygons[triangle.polygon_index].material_index
            if not 0 <= index < len(materials):
                raise ValueError("Loaded mesh has an invalid material slot")
            assignments.append(materials[index])
        role = _identity(item, "representation")
        collision = _identity(item, "collisionRole")
        if role not in ("visual", "collision") or collision not in ("none", "static", "dynamic"):
            raise ValueError("Loaded mesh has an unknown representation")
        parent = _identity(item.parent, "id") if item.parent else None
        if (item.get("bubo_parent_anchor_id") or None) != parent:
            raise ValueError("Loaded mesh pivot ownership differs from authored metadata")
        rows.append(common | {"representation": role, "collisionRole": collision,
                              "parentAnchorId": parent, "materialId": _identity(item, "materialId"), "materialSlots": materials,
                              "vertexCount": len(vertices), "triangleCount": len(indices) // 3,
                              "minimum": admission.point_object(admission.vector_min(vertices)),
                              "maximum": admission.point_object(admission.vector_max(vertices)),
                              "geometrySha256": admission.geometry_sha256(vertices, indices),
                              "indexSha256": admission.index_sha256(indices),
                              "materialAssignmentsSha256": admission.material_assignments_sha256(assignments), **statistics})
        meshes[identifier] = item
    if {row["storeyId"] for row in rows} != floor_ids or not any(row["representation"] == "visual" for row in rows):
        raise ValueError("Every requested floor requires actual visual mesh evidence")
    material_rows = []
    if len(bpy.data.materials) > admission.MAX_MATERIALS:
        raise ValueError("Loaded material palette exceeds its independent bound")
    for material in sorted(bpy.data.materials, key=lambda value: str(value.get("bubo_id", ""))):
        if not material.use_nodes or len(material.node_tree.nodes) != 2:
            raise ValueError("Unexpected authored material graph")
        shaders = [node for node in material.node_tree.nodes if node.bl_idname == "ShaderNodeBsdfPrincipled"]
        outputs = [node for node in material.node_tree.nodes if node.bl_idname == "ShaderNodeOutputMaterial"]
        if len(shaders) != 1 or len(outputs) != 1 or len(material.node_tree.links) != 1:
            raise ValueError("Unexpected authored shader/link graph")
        shader = shaders[0]
        link = material.node_tree.links[0]
        if (link.from_node != shader or link.from_socket != shader.outputs["BSDF"] or
                link.to_node != outputs[0] or link.to_socket != outputs[0].inputs["Surface"]):
            raise ValueError("Authored material BSDF is not linked to its surface output")
        material_rows.append({"id": _identity(material, "id"), "name": _identity(material, "name"), "color": _identity(material, "color"),
                              "baseColor": [float(value) for value in shader.inputs["Base Color"].default_value],
                              "roughness": float(shader.inputs["Roughness"].default_value), "metallic": float(shader.inputs["Metallic"].default_value),
                              "transmission": float(shader.inputs["Transmission Weight"].default_value), "ior": float(shader.inputs["IOR"].default_value)})
    return {"objects": len(all_objects), "meshCount": len(rows), "anchorCount": len(anchors), "vertexCount": vertex_count,
            "triangleCount": triangle_count, "meshes": rows, "anchors": anchors, "materials": material_rows}, meshes


def _camera(bpy, scene, minimum, maximum, direction):
    from mathutils import Vector

    low, high = Vector(minimum), Vector(maximum)
    target = (low + high) / 2
    radius = max((high - low).length / 2, 0.1)
    unit = Vector(direction).normalized()
    data = bpy.data.cameras.new("qa-camera")
    camera = bpy.data.objects.new("qa-camera", data)
    scene.collection.objects.link(camera)
    camera.location = target + unit * max(4 * radius, 2)
    camera.rotation_euler = (target - camera.location).to_track_quat("-Z", "Y").to_euler()
    data.type = "ORTHO"
    data.ortho_scale = radius * 2.3
    data.clip_start = 0.001
    data.clip_end = max(10 * radius, 10)
    scene.camera = camera
    return camera, target, data.ortho_scale


def _light(bpy, scene, name, position, target, energy, size):
    from mathutils import Vector

    data = bpy.data.lights.new(name, "AREA")
    data.energy = energy
    data.shape = "DISK"
    data.size = size
    item = bpy.data.objects.new(name, data)
    scene.collection.objects.link(item)
    item.location = position
    item.rotation_euler = (Vector(target) - item.location).to_track_quat("-Z", "Y").to_euler()


def _visible(item, spec, center, direction):
    from mathutils import Vector

    if item.get("bubo_representation") != "visual":
        return False
    if spec["kind"] == "floor-cutaway" and item.get("bubo_storeyId") != spec["floorId"]:
        return False
    if spec["kind"] not in ("floor-cutaway", "vertical-cutaway"):
        return True
    kind = item.get("bubo_kind")
    if kind in ("roof", "roof-fascia", "ceiling-panel"):
        return False
    if kind in ("wall", "gable-wall", "door-frame", "window-frame", "window-glass", "door-leaf", "plinth", "cornice"):
        corners = [item.matrix_world @ Vector(corner) for corner in item.bound_box]
        x, y = sum(corner.x for corner in corners) / 8, sum(corner.y for corner in corners) / 8
        if (x - center[0]) * direction[0] + (y - center[1]) * direction[1] > 0:
            return False
    return True


def _png(path, expected_size, maximum):
    if not path.is_file() or not 8 < path.stat().st_size <= maximum:
        raise ValueError("Rendered PNG is missing or exceeds the output allowance")
    with path.open("rb") as handle:
        header = handle.read(33)
    if header[:8] != b"\x89PNG\r\n\x1a\n" or header[12:16] != b"IHDR":
        raise ValueError("Renderer did not produce an actual PNG")
    width, height = struct.unpack(">II", header[16:24])
    if width != expected_size or height != expected_size:
        raise ValueError("Renderer changed fixed image dimensions")
    return width, height


def _write_inspection(admission, path, report, allowance):
    if path.exists() or path.is_symlink() or path.name != "inspection.json":
        raise ValueError("Inspection destination already exists or has a wrong name")
    partial = path.with_name("inspection.json.partial")
    total = 0
    descriptor = os.open(partial, os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0), 0o600)
    with os.fdopen(descriptor, "wb") as handle:
        for fragment in json.JSONEncoder(ensure_ascii=False, allow_nan=False, sort_keys=True, separators=(",", ":")).iterencode(report):
            raw = fragment.encode("utf-8")
            total += len(raw)
            if total + 1 > min(allowance, admission.MAX_REPORT_BYTES):
                raise ValueError("Inspection exceeds bounded report/output bytes")
            handle.write(raw)
        handle.write(b"\n")
        handle.flush()
        os.fsync(handle.fileno())
    os.link(partial, path)
    partial.unlink()


def review(admission, admitted, blend, floors, expected_job_hash, bundle_directory):
    import bpy
    from mathutils import Vector

    if tuple(bpy.app.version[:2]) != (4, 5) or not bpy.app.background:
        raise ValueError("Review requires background Blender 4.5 LTS")
    for flag in ("--factory-startup", "--disable-autoexec", "--offline-mode"):
        if flag not in sys.argv:
            raise ValueError("Review requires explicit " + flag)
    if bpy.data.filepath:
        raise ValueError("Review must start before opening any source blend")
    verify_sources(admission, admitted, blend, expected_job_hash, bundle_directory)
    output = admission.create_output(admitted)
    opened = bpy.ops.wm.open_mainfile(filepath=str(blend), load_ui=False, use_scripts=False)
    if "FINISHED" not in opened or Path(bpy.data.filepath).resolve(strict=True) != blend:
        raise ValueError("Blender did not open the exact reviewed source")
    scene = bpy.context.scene
    require_source_scope(bpy.data)
    fixed_render_policy(scene, apply=True)
    bpy.context.view_layer.update()
    original, source_objects = inventory(bpy, admission, floors, admitted.job["limits"])
    if scene.get("bubo_schema_version") != "bubo-floor-plan-construction-scene/v1":
        raise ValueError("Loaded source has no native construction identity")
    identities = {key: admission.hash_value(scene.get("bubo_" + key), "source " + key) for key in ("planSha256", "designFingerprint", "runtimeSha256")}
    source_job_hash = admission.hash_value(scene.get("bubo_job_sha256"), "source job hash")
    plan_id = admission.text(scene.get("bubo_plan_id"), "loaded plan id")
    visual_rows = [row for row in original["meshes"] if row["representation"] == "visual"]
    lower = admission.vector_min([admission.point(row["minimum"], "visual minimum") for row in visual_rows])
    upper = admission.vector_max([admission.point(row["maximum"], "visual maximum") for row in visual_rows])
    center = tuple((left + right) / 2 for left, right in zip(lower, upper))
    span = max(maximum - minimum for minimum, maximum in zip(lower, upper))
    distance = max(span, 1)
    scene.render.engine = "CYCLES"
    scene.cycles.device = "CPU"
    scene.cycles.samples = 8
    scene.cycles.use_adaptive_sampling = False
    scene.cycles.use_denoising = False
    scene.render.threads_mode = "FIXED"
    scene.render.threads = 2
    scene.render.resolution_x = scene.render.resolution_y = IMAGE_SIZE
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = "PNG"
    scene.render.image_settings.color_mode = "RGB"
    scene.render.image_settings.color_depth = "8"
    scene.render.use_file_extension = True
    scene.render.film_transparent = False
    scene.render.use_stamp = False
    for property_value in scene.render.bl_rna.properties:
        if property_value.identifier.startswith("use_stamp_") and property_value.type == "BOOLEAN":
            setattr(scene.render, property_value.identifier, False)
    scene.render.stamp_note_text = ""
    scene.view_settings.view_transform = "Standard"
    scene.view_settings.look = "None"
    scene.view_settings.exposure = 0
    scene.view_settings.gamma = 1
    world = bpy.data.worlds.new("qa-world")
    world.use_nodes = True
    world.node_tree.nodes["Background"].inputs["Color"].default_value = (0.8, 0.8, 0.8, 1)
    world.node_tree.nodes["Background"].inputs["Strength"].default_value = 0.8
    scene.world = world
    for index, offset in enumerate(((-1.5, -1.5, 2), (1.5, 0.5, 1.5), (0, 1.5, 1.5))):
        position = tuple(center[axis] + offset[axis] * distance for axis in range(3))
        _light(bpy, scene, "qa-light-" + str(index), position, center, max(100, 60 * distance * distance), distance)
    specs = [{"kind": "exterior-front", "direction": (-1, -1, 0.8)},
             {"kind": "exterior-back", "direction": (1, 1, 0.8)},
             {"kind": "roof-plan", "direction": (0, 0, 1)}]
    specs.extend({"kind": "floor-cutaway", "floorId": floor["id"], "elevation": floor["elevation"], "height": floor["height"], "direction": (-1, -1, 1)} for floor in floors)
    specs.append({"kind": "vertical-cutaway", "direction": (-1, -1, 0.18)})
    if len(specs) != len(floors) + 4 or len(specs) > admitted.job["limits"]["maxViews"]:
        raise ValueError("Complete fixed view inventory exceeds the allowance")
    views = []
    written_bytes = 0
    for index, spec in enumerate(specs):
        verify_sources(admission, admitted, blend, expected_job_hash, bundle_directory)
        active_rows = visual_rows if spec["kind"] != "floor-cutaway" else [row for row in visual_rows if row["storeyId"] == spec["floorId"]]
        view_min = admission.vector_min([admission.point(row["minimum"], "view minimum") for row in active_rows])
        view_max = admission.vector_max([admission.point(row["maximum"], "view maximum") for row in active_rows])
        view_center = tuple((left + right) / 2 for left, right in zip(view_min, view_max))
        visible = []
        for identifier, item in source_objects.items():
            item.hide_render = not _visible(item, spec, view_center, spec["direction"])
            if not item.hide_render:
                visible.append(identifier)
        if not visible:
            raise ValueError("A required QA view has no actual visible meshes")
        camera, target, scale = _camera(bpy, scene, view_min, view_max, spec["direction"])
        bpy.context.view_layer.update()
        name = "view-" + str(index).zfill(3) + ".png"
        image = output / name
        if image.exists() or image.is_symlink():
            raise ValueError("A QA render destination already exists")
        scene.render.filepath = str(image)
        fixed_render_policy(scene)
        rendered = bpy.ops.render.render(write_still=True)
        if "FINISHED" not in rendered:
            raise ValueError("The selected CPU renderer did not finish")
        checked_image = admission.safe_absolute(str(image), "rendered PNG")
        if checked_image != image or not checked_image.is_relative_to(output):
            raise ValueError("Rendered PNG identity escaped the owned output")
        width, height = _png(image, IMAGE_SIZE, admitted.job["limits"]["maxOutputBytes"] - written_bytes)
        size = image.stat().st_size
        written_bytes += size
        views.append({"path": name, "kind": spec["kind"], "floorId": spec.get("floorId"), "width": width, "height": height,
                      "sha256": admission.hash_file(image, admitted.job["limits"]["maxOutputBytes"]), "bytes": size,
                      "cameraPosition": _point(admission, camera.location), "target": _point(admission, target), "orthoScale": scale,
                      "visibleMeshCount": len(visible), "visibleMeshIdsSha256": admission.material_assignments_sha256(sorted(visible)),
                      "renderOnlyVisibilityChanges": spec["kind"] in ("floor-cutaway", "vertical-cutaway")})
        camera_data = camera.data
        bpy.data.objects.remove(camera, do_unlink=True)
        bpy.data.cameras.remove(camera_data)
    verify_sources(admission, admitted, blend, expected_job_hash, bundle_directory)
    report = {"schemaVersion": "bubo-floor-plan-construction-review/v1", "jobSha256": expected_job_hash,
              "sourceSha256": admitted.job["sourceSha256"], "sourceBytes": blend.stat().st_size, "sourcePlanId": plan_id,
              "sourceJobSha256": source_job_hash, "identities": identities, "bundle": admitted.job["bundle"],
              "blenderVersion": ".".join(str(part) for part in bpy.app.version), "floors": floors, "original": original,
              "renderSettings": {"engine": scene.render.engine, "device": scene.cycles.device, "samples": scene.cycles.samples,
                                 "threads": scene.render.threads, "imageSize": IMAGE_SIZE, "viewTransform": scene.view_settings.view_transform,
                                 "denoising": scene.cycles.use_denoising, "adaptiveSampling": scene.cycles.use_adaptive_sampling,
                                 **fixed_render_policy(scene)},
              "views": views, "sourceIndependentlyLoaded": True, "sourceUnchanged": True, "exportReimportVerified": False,
              "humanModelAccepted": False, "runtimeAccepted": False}
    _write_inspection(admission, output / "inspection.json", report, admitted.job["limits"]["maxOutputBytes"] - written_bytes)
    expected_files = {view["path"] for view in views} | {"inspection.json"}
    if {path.name for path in output.iterdir()} != expected_files:
        raise ValueError("Unexpected output appeared during QA review")
    if sum(path.stat().st_size for path in output.iterdir()) > admitted.job["limits"]["maxOutputBytes"]:
        raise ValueError("Actual QA outputs exceed approved bytes")
    verify_sources(admission, admitted, blend, expected_job_hash, bundle_directory)
    print(json.dumps({"status": "reviewed-technical", "views": len(views), "sourceUnchanged": True}))
    return report


def main():
    arguments = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    admission, admitted, blend, floors, expected_job_hash, directory = bootstrap(arguments)
    return review(admission, admitted, blend, floors, expected_job_hash, directory)


if __name__ == "__main__":
    main()
