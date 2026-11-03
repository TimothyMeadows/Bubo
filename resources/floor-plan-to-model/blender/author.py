"""Fixed Blender 4.5 authoring of a hash-bound native construction scene.

The compiler owns dimensions and triangle solids. This script creates editable
Blender data without remeshing, callbacks, source-code execution or external
assets, then measures the actual authored meshes. It is not an independent
re-import check or a human/engine acceptance authority.
"""

import hashlib
import importlib.util
import json
import math
from pathlib import Path
import re
import stat
import sys

sys.dont_write_bytecode = True
HARD_JOB_BYTES = 256 * 1024 * 1024
HASH = re.compile(r"[0-9a-f]{64}\Z")


def _bootstrap_path(path):
    path = Path(path)
    normalized = str(path).replace("\\", "/")
    if (not path.is_absolute() or normalized.startswith("//") or
            any(part.lower() in (".", "..", ".git") for part in normalized.split("/"))):
        raise ValueError("Authoring requires an absolute, non-traversing job/bundle path")
    current = Path(path.anchor)
    for component in path.parts[1:]:
        if component.endswith((".", " ")) or ":" in component:
            raise ValueError("Authoring job/bundle path contains an ambiguous component")
        current /= component
        info = current.lstat()
        if (stat.S_ISLNK(info.st_mode) or
                getattr(info, "st_file_attributes", 0) & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)):
            raise ValueError("Authoring job/bundle path contains a link or reparse point")
    if not path.is_file():
        raise ValueError("Authoring job/bundle must be a regular file")
    return path.resolve(strict=True)


def _unique_pairs(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("Duplicate authoring-job JSON property: " + key)
        result[key] = value
    return result


def bootstrap(arguments):
    if len(arguments) != 2 or HASH.fullmatch(arguments[1]) is None:
        raise ValueError("Usage: author.py -- ABSOLUTE_JOB_JSON EXPECTED_JOB_SHA256")
    source = _bootstrap_path(arguments[0])
    if not 2 <= source.stat().st_size <= HARD_JOB_BYTES:
        raise ValueError("Missing or oversized authoring job")
    with source.open("rb") as handle:
        raw = handle.read(HARD_JOB_BYTES + 1)
    if len(raw) > HARD_JOB_BYTES or hashlib.sha256(raw).hexdigest() != arguments[1]:
        raise ValueError("Authoring job byte identity changed")
    depth = 0
    quoted = escaped = False
    for value in raw:
        if quoted:
            if escaped:
                escaped = False
            elif value == 92:
                escaped = True
            elif value == 34:
                quoted = False
        elif value == 34:
            quoted = True
        elif value in (123, 91):
            depth += 1
            if depth > 64:
                raise ValueError("Authoring JSON depth exceeds 64")
        elif value in (125, 93):
            depth -= 1
    job = json.loads(raw.decode("utf-8"), object_pairs_hook=_unique_pairs,
                     parse_constant=lambda value: (_ for _ in ()).throw(ValueError("Non-finite authoring JSON: " + value)))
    if not isinstance(job, dict) or not isinstance(job.get("bundle"), dict) or set(job["bundle"]) != {"author.py", "admission.py"}:
        raise ValueError("Missing exact authoring bundle identity")
    directory = Path(__file__).resolve(strict=True).parent
    for name in ("author.py", "admission.py"):
        expected = job["bundle"][name]
        if not isinstance(expected, str) or HASH.fullmatch(expected) is None:
            raise ValueError("Invalid authoring bundle hash")
        resource = _bootstrap_path(directory / name)
        if resource.stat().st_size > 2 * 1024 * 1024 or hashlib.sha256(resource.read_bytes()).hexdigest() != expected:
            raise ValueError("Audited authoring bundle changed: " + name)
    # Import only this fixed, already hash-checked resource. Job data never
    # supplies module names, import roots, callbacks or code.
    specification = importlib.util.spec_from_file_location("_bubo_floor_plan_admission", directory / "admission.py")
    if specification is None or specification.loader is None:
        raise ValueError("Audited authoring admission is unavailable")
    admission = importlib.util.module_from_spec(specification)
    sys.modules[specification.name] = admission
    specification.loader.exec_module(admission)
    admitted = admission.validate_job(job, source, len(raw))
    admission.verify_current_sources(admitted, arguments[1], directory)
    return admission, admitted, arguments[1], directory


def _name(prefix, identifier):
    return prefix + "-" + hashlib.sha256(identifier.encode("utf-8")).hexdigest()[:32]


def _srgb_channel(value):
    return value / 12.92 if value <= 0.04045 else ((value + 0.055) / 1.055) ** 2.4


def _shader_color(color):
    return tuple(_srgb_channel(int(color[index:index + 2], 16) / 255) for index in (1, 3, 5))


def _properties(owner, values, keys):
    for key in keys:
        owner["bubo_" + key] = values[key]


def _check_properties(owner, values, keys):
    if any(owner.get("bubo_" + key) != values[key] for key in keys):
        raise ValueError("Blender changed a semantic identity")


def _create_material(bpy, source):
    requested = _name("material", source["id"])
    material = bpy.data.materials.new(requested)
    if material.name != requested:
        raise ValueError("Material identity collision")
    material.use_nodes = True
    material.node_tree.nodes.clear()
    shader = material.node_tree.nodes.new("ShaderNodeBsdfPrincipled")
    output = material.node_tree.nodes.new("ShaderNodeOutputMaterial")
    material.node_tree.links.new(shader.outputs["BSDF"], output.inputs["Surface"])
    color = _shader_color(source["color"])
    shader.inputs["Base Color"].default_value = (*color, 1)
    for target, key in (("Roughness", "roughness"), ("Metallic", "metallic"), ("Transmission Weight", "transmission"), ("IOR", "ior")):
        shader.inputs[target].default_value = source[key]
    material.diffuse_color = (*color, 1)
    material.roughness = source["roughness"]
    material.metallic = source["metallic"]
    _properties(material, source, ("id", "name", "color"))
    # Shader scalar storage is float32. This fixed binary-storage check never
    # substitutes for the approved spatial tolerance used on geometry.
    shader_tolerance = 2**-21
    if any(abs(shader.inputs["Base Color"].default_value[index] - color[index]) > shader_tolerance for index in range(3)):
        raise ValueError("Blender changed a declared material color")
    for target, key in (("Roughness", "roughness"), ("Metallic", "metallic"), ("Transmission Weight", "transmission"), ("IOR", "ior")):
        if abs(shader.inputs[target].default_value - source[key]) > shader_tolerance:
            raise ValueError("Blender changed a declared material scalar")
    return material


def _collection(bpy, parent, name, properties):
    result = bpy.data.collections.new(name)
    if result.name != name:
        raise ValueError("Collection identity collision")
    parent.children.link(result)
    for key, value in properties.items():
        result["bubo_" + key] = value
    return result


def author(admission, admitted, expected_job_hash, bundle_directory):
    import bpy
    from mathutils import Matrix, Vector

    if tuple(bpy.app.version[:2]) != (4, 5) or not bpy.app.background:
        raise ValueError("Construction requires background Blender 4.5 LTS")
    for flag in ("--factory-startup", "--disable-autoexec", "--offline-mode"):
        if flag not in sys.argv:
            raise ValueError("Construction requires explicit " + flag)
    if bpy.data.filepath:
        raise ValueError("Construction cannot author into an opened source blend")
    job, source_scene = admitted.job, admitted.job["scene"]
    admission.verify_current_sources(admitted, expected_job_hash, bundle_directory)
    output_directory = admission.create_output(admitted)
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.context.preferences.filepaths.save_version = 0
    scene = bpy.context.scene
    # Factory startup may retain an empty sequencer; authored architecture has no editing pipeline.
    scene.sequence_editor_clear()
    scene.render.use_sequencer = False
    scene.render.use_compositing = False
    scene.render.use_multiview = False
    scene.unit_settings.system = "METRIC"
    scene.unit_settings.scale_length = 1
    scene["bubo_schema_version"] = source_scene["schemaVersion"]
    scene["bubo_plan_id"] = source_scene["planId"]
    scene["bubo_job_sha256"] = expected_job_hash
    for key, value in job["identities"].items():
        scene["bubo_" + key] = value
    scene["bubo_independent_reimport"] = False
    scene["bubo_human_model_accepted"] = False
    scene["bubo_runtime_accepted"] = False
    root_collection = _collection(bpy, scene.collection, _name("building", source_scene["planId"]), {"planId": source_scene["planId"]})
    storeys = sorted({item["storeyId"] for item in source_scene["meshes"] + source_scene["anchors"]})
    collections = {}
    for identifier in storeys:
        floor = _collection(bpy, root_collection, _name("storey", identifier), {"storeyId": identifier})
        for role in ("visual", "collision-static", "collision-dynamic", "anchors"):
            collections[(identifier, role)] = _collection(bpy, floor, _name(role, identifier), {"storeyId": identifier, "representation": role})
            if role.startswith("collision-"):
                collections[(identifier, role)].hide_render = True
    materials = {item["id"]: _create_material(bpy, item) for item in source_scene["materials"]}
    anchors = {}
    anchor_positions = {}
    for source in source_scene["anchors"]:
        requested = _name("anchor", source["id"])
        item = bpy.data.objects.new(requested, None)
        if item.name != requested:
            raise ValueError("Anchor identity collision")
        collections[(source["storeyId"], "anchors")].objects.link(item)
        item.empty_display_type = "ARROWS"
        item.empty_display_size = 0.15
        item.location = admission.point(source["position"], "anchor position")
        item.rotation_mode = "XYZ"
        item.rotation_euler = (0, 0, source["rotationRadians"])
        _properties(item, source, ("id", "semanticId", "storeyId", "kind"))
        anchors[source["id"]] = item
        anchor_positions[source["id"]] = admission.point(source["position"], "anchor position")
    objects = {}
    for source in source_scene["meshes"]:
        requested = _name("solid", source["id"])
        data = bpy.data.meshes.new(requested)
        if data.name != requested:
            raise ValueError("Mesh-data identity collision")
        parent = anchors[source["parentAnchorId"]] if source["parentAnchorId"] is not None else None
        origin = anchor_positions[source["parentAnchorId"]] if parent else (0, 0, 0)
        vertices = [tuple(vertex[axis] - origin[index] for index, axis in enumerate(("x", "y", "z"))) for vertex in source["vertices"]]
        faces = [source["triangles"][offset:offset + 3] for offset in range(0, len(source["triangles"]), 3)]
        data.from_pydata(vertices, [], faces)
        data.update(calc_edges=True)
        item = bpy.data.objects.new(requested, data)
        if item.name != requested:
            raise ValueError("Mesh-object identity collision")
        role = source["representation"] if source["representation"] == "visual" else "collision-" + source["collisionRole"]
        collections[(source["storeyId"], role)].objects.link(item)
        if parent is not None:
            item.parent = parent
            item.matrix_parent_inverse = Matrix.Identity(4)
            item.matrix_basis = Matrix.Identity(4)
        if source["representation"] == "collision":
            item.hide_render = True
            item.display_type = "WIRE"
        _properties(item, source, ("id", "semanticId", "storeyId", "kind", "representation", "collisionRole", "materialId"))
        item["bubo_parent_anchor_id"] = source["parentAnchorId"] or ""
        overrides = source.get("triangleMaterialIds")
        slot_ids = [source["materialId"]] + sorted(set(overrides or []) - {source["materialId"]})
        for identifier in slot_ids:
            data.materials.append(materials[identifier])
        slots = {identifier: index for index, identifier in enumerate(slot_ids)}
        for index, polygon in enumerate(data.polygons):
            polygon.material_index = slots[overrides[index] if overrides is not None else source["materialId"]]
        if data.validate(verbose=False, clean_customdata=False):
            raise ValueError("Blender repaired the submitted native geometry")
        objects[source["id"]] = item
    bpy.context.view_layer.update()
    if len(bpy.data.objects) != len(source_scene["meshes"]) + len(source_scene["anchors"]):
        raise ValueError("Unexpected objects were introduced into construction")
    mesh_reports, anchor_reports = [], []
    maximum_error = 0
    for source in source_scene["anchors"]:
        item = anchors[source["id"]]
        _check_properties(item, source, ("id", "semanticId", "storeyId", "kind"))
        actual = tuple(float(value) for value in item.matrix_world.translation)
        error = math.dist(actual, admission.point(source["position"], "source anchor"))
        maximum_error = max(maximum_error, error)
        if error > source_scene["linearTolerance"]:
            raise ValueError("Blender anchor coordinates exceed the approved linear tolerance")
        axis = tuple(float(value) for value in item.matrix_world.to_3x3() @ Vector((0, 0, 1)))
        if axis != (0, 0, 1) or tuple(item.scale) != (1, 1, 1) or tuple(item.rotation_euler) != (0, 0, 0):
            raise ValueError("Blender changed an initial anchor axis/transform")
        anchor_reports.append({key: source[key] for key in ("id", "semanticId", "storeyId", "kind")} |
                              {"position": admission.point_object(actual), "axis": admission.point_object(axis), "rotationRadians": 0})
    for source in source_scene["meshes"]:
        item = objects[source["id"]]
        _check_properties(item, source, ("id", "semanticId", "storeyId", "kind", "representation", "collisionRole", "materialId"))
        actual_parent = item.parent.get("bubo_id") if item.parent else None
        if actual_parent != source["parentAnchorId"] or item.modifiers or item.constraints:
            raise ValueError("Unexpected mesh parent, modifier or constraint")
        data = item.data
        data.calc_loop_triangles()
        actual_vertices = [tuple(float(component) for component in item.matrix_world @ vertex.co) for vertex in data.vertices]
        actual_indices = [int(index) for triangle in data.loop_triangles for index in triangle.vertices]
        if len(actual_vertices) != len(source["vertices"]) or actual_indices != source["triangles"]:
            raise ValueError("Blender changed submitted vertex/triangle topology")
        error = max(math.dist(actual, admission.point(expected, "expected vertex")) for actual, expected in zip(actual_vertices, source["vertices"]))
        maximum_error = max(maximum_error, error)
        if error > source_scene["linearTolerance"]:
            raise ValueError("Blender mesh coordinates exceed the approved linear tolerance")
        for value in actual_vertices:
            if not all(math.isfinite(component) for component in value):
                raise ValueError("Blender produced a non-finite vertex")
        statistics = admission.inspect_triangle_solid(actual_vertices, actual_indices)
        actual_materials = [data.materials[data.polygons[triangle.polygon_index].material_index].get("bubo_id") for triangle in data.loop_triangles]
        expected_materials = source.get("triangleMaterialIds")
        if expected_materials is None:
            expected_materials = [source["materialId"]] * (len(source["triangles"]) // 3)
        if actual_materials != expected_materials:
            raise ValueError("Blender changed a per-triangle material assignment")
        material_counts = {}
        for identifier in actual_materials:
            material_counts[identifier] = material_counts.get(identifier, 0) + 1
        mesh_reports.append({key: source[key] for key in ("id", "semanticId", "storeyId", "kind", "representation", "collisionRole", "materialId", "parentAnchorId")} |
                            {"vertexCount": len(actual_vertices), "triangleCount": len(actual_indices) // 3,
                             "minimum": admission.point_object(admission.vector_min(actual_vertices)), "maximum": admission.point_object(admission.vector_max(actual_vertices)),
                             "maxWorldCoordinateError": error, "geometrySha256": admission.geometry_sha256(actual_vertices, actual_indices),
                             "sourceGeometrySha256": admitted.mesh_stats[source["id"]]["geometrySha256"],
                             "indexSha256": admission.index_sha256(actual_indices),
                             "materialAssignmentsSha256": admission.material_assignments_sha256(actual_materials),
                             "materialTriangleCounts": material_counts, **statistics})
    counts = {"meshes": len(mesh_reports), "anchors": len(anchor_reports), "materials": len(materials),
              "vertices": sum(mesh["vertexCount"] for mesh in mesh_reports), "triangles": sum(mesh["triangleCount"] for mesh in mesh_reports),
              "visualTriangles": sum(mesh["triangleCount"] for mesh in mesh_reports if mesh["representation"] == "visual"),
              "collisionTriangles": sum(mesh["triangleCount"] for mesh in mesh_reports if mesh["representation"] == "collision")}
    if counts["meshes"] + counts["anchors"] > job["limits"]["maxObjects"] or counts["vertices"] > job["limits"]["maxVertices"] or counts["triangles"] > job["limits"]["maxTriangles"]:
        raise ValueError("Actual Blender geometry exceeds declared construction bounds")
    admission.verify_current_sources(admitted, expected_job_hash, bundle_directory)
    blend = output_directory / "building.blend"
    if blend.exists() or blend.is_symlink() or any(output_directory.iterdir()):
        raise ValueError("Construction output was populated before source save")
    saved = bpy.ops.wm.save_as_mainfile(filepath=str(blend), check_existing=False, compress=False)
    if "FINISHED" not in saved:
        raise ValueError("Blender did not finish saving editable source")
    blend = admission.safe_absolute(str(blend), "constructed Blender source")
    if not blend.is_file() or blend.stat().st_size <= 0 or blend.stat().st_size > job["limits"]["maxOutputBytes"]:
        raise ValueError("Actual Blender source exceeds the declared output bytes")
    report = {"schemaVersion": "bubo-floor-plan-construction-report/v1", "jobSha256": expected_job_hash,
              "bundle": job["bundle"], "identities": job["identities"], "blenderVersion": ".".join(str(part) for part in bpy.app.version),
              "blend": {"path": "building.blend", "sha256": admission.hash_file(blend, job["limits"]["maxOutputBytes"]), "bytes": blend.stat().st_size},
              "counts": counts,
              "minimum": admission.point_object(admission.vector_min([admission.point(mesh["minimum"], "actual mesh minimum") for mesh in mesh_reports])),
              "maximum": admission.point_object(admission.vector_max([admission.point(mesh["maximum"], "actual mesh maximum") for mesh in mesh_reports])),
              "maxWorldCoordinateError": maximum_error, "meshes": mesh_reports, "anchors": anchor_reports,
              "materials": source_scene["materials"], "independentReimport": False, "humanModelAccepted": False, "runtimeAccepted": False}
    admission.verify_current_sources(admitted, expected_job_hash, bundle_directory)
    report_path = output_directory / "construction-report.json"
    admission.write_report(report_path, report, job["limits"]["maxOutputBytes"], blend.stat().st_size)
    if {item.name for item in output_directory.iterdir()} != {"building.blend", "construction-report.json"}:
        raise ValueError("Unexpected output appeared during construction")
    if sum(item.stat().st_size for item in output_directory.iterdir()) > job["limits"]["maxOutputBytes"]:
        raise ValueError("Actual source/report package exceeds declared output bytes")
    admission.verify_current_sources(admitted, expected_job_hash, bundle_directory)
    print(json.dumps({"status": "constructed", "blendSha256": report["blend"]["sha256"], "meshes": counts["meshes"]}))
    return report


def main():
    arguments = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    admission, admitted, expected_job_hash, directory = bootstrap(arguments)
    return author(admission, admitted, expected_job_hash, directory)


if __name__ == "__main__":
    main()
