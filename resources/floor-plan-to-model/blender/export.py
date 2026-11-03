"""Fixed source-preserving producer and separate-process GLB verifier.

The verifier never opens a blend or invokes an exporter. Each invocation owns
one absent output. Native orchestration launches visual/collision independently.
"""
import hashlib
from array import array
import importlib
import importlib.machinery
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
FIXED_BUNDLE = {"admission.py", "review.py", "export.py", "export_admission.py", "export_geometry.py", "export_views.py"}
HASH = re.compile(r"[0-9a-f]{64}\Z")
MESH_KEYS = ("id", "semanticId", "storeyId", "kind", "representation", "collisionRole", "materialId", "parentAnchorId")
NODE_KEYS = MESH_KEYS + ("parentId", "position", "axis", "worldMatrix")


def _bootstrap_path(value):
    path = Path(value)
    normalized = str(value).replace("\\", "/")
    if not path.is_absolute() or normalized.startswith("//") or any(part.lower() in (".", "..", ".git") for part in normalized.split("/")):
        raise ValueError("Export bootstrap requires absolute local canonical paths")
    current = Path(path.anchor)
    for part in path.parts[1:]:
        if part.endswith((".", " ")) or ":" in part:
            raise ValueError("Ambiguous export bootstrap path")
        current /= part
        info = current.lstat()
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise ValueError("Linked/reparse export bootstrap path")
    if not path.is_file():
        raise ValueError("Export bootstrap requires regular file")
    return path.resolve(strict=True)


def _pairs(rows):
    value = {}
    for key, item in rows:
        if key in value:
            raise ValueError("Duplicate export bootstrap JSON key")
        value[key] = item
    return value


def bootstrap(arguments, entry="export.py"):
    if len(arguments) != 2 or not isinstance(arguments[1], str) or HASH.fullmatch(arguments[1]) is None:
        raise ValueError("Usage: export.py -- ABSOLUTE_JOB_JSON EXPECTED_JOB_SHA256")
    source = _bootstrap_path(arguments[0])
    if not 2 <= source.stat().st_size <= 1024**2:
        raise ValueError("Missing/oversized export job")
    with source.open("rb") as handle:
        raw = handle.read(1024**2 + 1)
    if len(raw) > 1024**2 or hashlib.sha256(raw).hexdigest() != arguments[1]:
        raise ValueError("Export job exact identity changed")
    depth = 0
    quoted = escaped = False
    for byte in raw:
        if quoted:
            if escaped:
                escaped = False
            elif byte == 92:
                escaped = True
            elif byte == 34:
                quoted = False
        elif byte == 34:
            quoted = True
        elif byte in (123, 91):
            depth += 1
            if depth > 64:
                raise ValueError("Export job JSON nesting exceeds 64")
        elif byte in (125, 93):
            depth -= 1
    job = json.loads(raw.decode("utf-8"), object_pairs_hook=_pairs,
                     parse_constant=lambda value: (_ for _ in ()).throw(ValueError("Nonfinite export JSON")))
    if not isinstance(job, dict) or not isinstance(job.get("bundle"), dict) or set(job["bundle"]) != FIXED_BUNDLE:
        raise ValueError("Exact fixed six-resource export bundle required")
    directory = _bootstrap_path(__file__).parent
    for name, expected in job["bundle"].items():
        resource = _bootstrap_path(directory / name)
        if not isinstance(expected, str) or HASH.fullmatch(expected) is None or resource.stat().st_size > 2 * 1024**2 or hashlib.sha256(resource.read_bytes()).hexdigest() != expected:
            raise ValueError("Fixed audited export resource changed: " + name)
    spec = importlib.util.spec_from_file_location("_bubo_floor_plan_export_admission", directory / "export_admission.py")
    if spec is None or spec.loader is None:
        raise ValueError("Fixed export admission is unavailable")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module, module.bootstrap(arguments, directory, entry)


def require_process(bpy, admitted, admission):
    if tuple(bpy.app.version[:2]) != (4, 5) or not bpy.app.background:
        raise ValueError("Export workers require background Blender 4.5 LTS")
    for flag in ("--factory-startup", "--disable-autoexec", "--offline-mode"):
        if flag not in sys.argv:
            raise ValueError("Export workers require explicit " + flag)
    if bpy.data.filepath:
        raise ValueError("Export workers require an initially unopened factory process")
    if not sys.dont_write_bytecode:
        raise ValueError("Export workers must not write installed module bytecode")
    admission.verify_sources(admitted, bpy)


def material_rows(bpy):
    result = []
    for material in bpy.data.materials:
        if not material.use_nodes or material.node_tree is None or material.animation_data:
            raise ValueError("Imported material must have a static explicit shader")
        nodes = material.node_tree.nodes
        shaders = [node for node in nodes if node.bl_idname == "ShaderNodeBsdfPrincipled"]
        outputs = [node for node in nodes if node.bl_idname == "ShaderNodeOutputMaterial" and node.is_active_output]
        if len(shaders) != 1 or len(outputs) != 1 or any(node.bl_idname not in ("ShaderNodeBsdfPrincipled", "ShaderNodeOutputMaterial") for node in nodes):
            raise ValueError("Unexpected imported scalar-only material graph")
        shader, output = shaders[0], outputs[0]
        links = list(material.node_tree.links)
        if len(links) != 1 or links[0].from_node != shader or links[0].to_node != output or links[0].to_socket != output.inputs["Surface"]:
            raise ValueError("Imported material surface is not its declared Principled BSDF")
        result.append({"id": material.get("bubo_id"), "color": [float(value) for value in shader.inputs["Base Color"].default_value],
                       **{key: float(shader.inputs[socket].default_value) for key, socket in
                          (("roughness", "Roughness"), ("metallic", "Metallic"), ("transmission", "Transmission Weight"), ("ior", "IOR"))}})
    return result


def compare_source_materials(scene, actual_materials, geometry):
    """Source .blend may omit declared materials with no triangle users.

    Every assigned material, including per-triangle overrides, remains
    mandatory. Any retained unused material must still be declared and match
    its full shader semantics. The immutable native palette stays unchanged.
    """
    declared = {row["id"] for row in scene["materials"]}
    required = {identifier for mesh in scene["meshes"]
                for identifier in (mesh.get("triangleMaterialIds") or [mesh["materialId"]])}
    observed = [row["id"] for row in actual_materials]
    if len(set(observed)) != len(observed) or not required <= set(observed) or not set(observed) <= declared:
        raise ValueError("Editable source material inventory omits assigned or adds unknown/duplicate identities")
    palette_scene = dict(scene, meshes=[{"representation": "visual", "materialId": identifier} for identifier in observed])
    return geometry.compare_materials(palette_scene, actual_materials)


def mesh_rows(bpy, admitted, representation, admission):
    rows, vertices, triangles, normal_count, normal_error = [], 0, 0, 0, 0.
    for item in bpy.data.objects:
        if item.type != "MESH":
            continue
        if item.get("bubo_representation") != representation:
            raise ValueError("Unexpected mesh representation in selected import")
        if item.modifiers or item.constraints or item.animation_data or item.data.shape_keys:
            raise ValueError("Unexpected imported geometry evaluation pipeline")
        data = item.data
        admission.remaining_limits(admitted, "imported mesh entry")
        data.calc_loop_triangles()
        vertices += len(data.vertices)
        triangles += len(data.loop_triangles)
        if vertices > admitted.job["limits"]["maxVertices"] or triangles > admitted.job["limits"]["maxTriangles"]:
            raise ValueError("Imported cumulative geometry exceeds approved caps")
        assignments = []
        normal_matrix = item.matrix_world.to_3x3().inverted().transposed()
        angular = admitted.plan["numerics"]["angularToleranceDegrees"]
        for triangle in data.loop_triangles:
            admission.charge_work(admitted, 1, "imported triangle inspection")
            slot = data.polygons[triangle.polygon_index].material_index
            if not 0 <= slot < len(data.materials) or data.materials[slot] is None:
                raise ValueError("Invalid imported triangle material slot")
            assignments.append(data.materials[slot].get("bubo_id"))
            points = [item.matrix_world @ data.vertices[index].co for index in triangle.vertices]
            face = (points[1] - points[0]).cross(points[2] - points[0])
            if not math.isfinite(face.length) or face.length == 0:
                raise ValueError("Collapsed/nonfinite actual imported triangle")
            face.normalize()
            for loop in triangle.loops:
                admission.charge_work(admitted, 1, "imported corner-normal inspection")
                normal = normal_matrix @ data.corner_normals[loop].vector
                if not math.isfinite(normal.length) or normal.length == 0:
                    raise ValueError("Missing/nonfinite actual imported corner normal")
                normal.normalize()
                observed_angle = math.degrees(math.atan2(normal.cross(face).length, normal.dot(face)))
                normal_count += 1
                normal_error = max(normal_error, observed_angle)
                if observed_angle > angular:
                    raise ValueError("Actual imported normal differs from oriented triangle")
        row = {key: item.get("bubo_" + key) for key in MESH_KEYS if key != "parentAnchorId"}
        row["parentAnchorId"] = item.get("bubo_parent_anchor_id") or None
        admission.charge_work(admitted, len(data.vertices), "imported world-vertex inspection")
        row.update(vertices=[[float(value) for value in item.matrix_world @ vertex.co] for vertex in data.vertices],
                   triangles=[int(index) for triangle in data.loop_triangles for index in triangle.vertices], triangleMaterialIds=assignments)
        rows.append(row)
    if not rows:
        raise ValueError("No selected imported geometry")
    admitted.imported_normal_diagnostics = {"cornerNormals": normal_count, "maxAngularErrorDegrees": normal_error,
                                            "angularToleranceDegrees": admitted.plan["numerics"]["angularToleranceDegrees"]}
    return rows


def node_rows(bpy):
    from mathutils import Vector
    rows = []
    for item in bpy.data.objects:
        if item.type not in ("MESH", "EMPTY") or item.modifiers or item.constraints or item.animation_data:
            raise ValueError("Unexpected portable node/evaluation type")
        row = {key: item.get("bubo_" + key) for key in MESH_KEYS if key != "parentAnchorId"}
        # Empty string is explicit metadata for an inapplicable node field.
        row["parentAnchorId"] = item.get("bubo_parent_anchor_id")
        row.update(parentId=item.parent.get("bubo_id") if item.parent else None,
                   position=[float(value) for value in item.matrix_world.translation],
                   axis=[float(value) for value in item.matrix_world.to_3x3() @ Vector((0, 0, 1))],
                   worldMatrix=[float(value) for row_values in item.matrix_world for value in row_values])
        rows.append(row)
    return rows


def inspect_import(bpy, admitted, admission, representation):
    admission.remaining_limits(admitted, "imported inventory entry")
    admitted.review.require_source_scope(bpy.data)
    if len(bpy.data.scenes) != 1 or bpy.data.libraries or bpy.data.texts or bpy.data.actions or bpy.data.images:
        raise ValueError("Portable import must be self-contained scalar-only static geometry")
    if len(bpy.data.objects) > admitted.job["limits"]["maxNodes"] or len(bpy.data.materials) > admitted.job["limits"]["maxMaterials"]:
        raise ValueError("Imported node/material caps exceeded")
    geometry = admitted.geometry
    actual = mesh_rows(bpy, admitted, representation, admission)
    measured = geometry.compare_meshes(admitted.scene, representation, actual, admission.remaining_limits(admitted, "imported triangle comparison"), admitted.scene["linearTolerance"], admitted.plan["numerics"]["angularToleranceDegrees"])
    admission.charge_work(admitted, measured["geometryChecks"], "imported triangle comparison")
    admission.charge_work(admitted, len(bpy.data.objects) + len(bpy.data.materials), "imported node/material inspection")
    measured["nodes"] = geometry.compare_nodes(admitted.scene, representation, node_rows(bpy), admitted.scene["linearTolerance"], admitted.plan["numerics"]["angularToleranceDegrees"])
    measured["materials"] = geometry.compare_materials(admitted.scene, material_rows(bpy), representation)
    measured["normalDiagnostics"] = admitted.imported_normal_diagnostics
    return measured


def _fixed_import_settings(extension):
    return {"bone_heuristic": "BLENDER", "bone_shape_scale_factor": 1., "disable_bone_shape": True,
            "guess_original_bind_pose": False, "export_import_convert_lighting_mode": "SPEC",
            "import_merge_material_slots": False, "import_pack_images": False, "import_scene_as_collection": False,
            "import_scene_extras": False, "import_select_created_objects": False, "import_shading": "FLAT",
            "import_unused_materials": False, "import_webp_texture": False, "loglevel": 40,
            "merge_vertices": False, "import_user_extensions": [extension]}


def _pinned_import_sdk(admitted):
    root = Path(admitted.job["toolchain"]["moduleRoot"])
    expected = {"io_scene_gltf2.io.imp.gltf2_io_gltf": "io/imp/gltf2_io_gltf.py",
                "io_scene_gltf2.io.imp.gltf2_io_binary": "io/imp/gltf2_io_binary.py",
                "io_scene_gltf2.blender.imp.blender_gltf": "blender/imp/blender_gltf.py"}
    modules = {name: importlib.import_module(name) for name in expected}
    for name, relative in expected.items():
        if admitted.common.safe_absolute(modules[name].__file__, "fixed SDK importer module") != root / relative:
            raise ValueError("Fixed SDK importer module origin differs from the pinned installation")
    _require_loaded_sdk_origins(admitted)
    return (modules["io_scene_gltf2.io.imp.gltf2_io_gltf"].glTFImporter,
            modules["io_scene_gltf2.blender.imp.blender_gltf"].BlenderGlTF,
            modules["io_scene_gltf2.io.imp.gltf2_io_binary"].BinaryData)


def _require_loaded_sdk_origins(admitted):
    root = admitted.common.safe_absolute(admitted.job["toolchain"]["moduleRoot"], "selected SDK module root")
    inventory = {row["path"] for row in admitted.job["toolchain"]["files"]}
    directories = {"/".join(path.split("/")[:index]) for path in inventory for index in range(1, len(path.split("/")))}
    loaded = [(name, module) for name, module in sys.modules.items() if isinstance(name, str) and (name == "io_scene_gltf2" or name.startswith("io_scene_gltf2."))]
    if len(loaded) > len(inventory) + min(1024, len(directories)):
        raise ValueError("Loaded SDK module " + loaded[-1][0][:256] + " rejected: module inventory exceeds pinned files/directories")
    for name, module in loaded:
        def reject(reason):
            raise ValueError("Loaded SDK module " + name[:256] + " rejected: " + reason[:512])
        try:
            filename = getattr(module, "__file__", None)
            specification = getattr(module, "__spec__", None)
            if specification is None or specification.name != name or getattr(module, "__name__", None) != name:
                reject("missing/inconsistent module specification identity")
            if filename is not None:
                if not isinstance(filename, str) or not filename:
                    reject("empty/non-text executable module file origin")
                path = admitted.common.safe_absolute(filename, "loaded fixed SDK module")
                if not path.is_file() or not path.is_relative_to(root) or path.relative_to(root).as_posix() not in inventory:
                    reject("executable module escaped its pinned file inventory")
                if specification.origin is None or admitted.common.safe_absolute(specification.origin, "SDK module spec origin") != path:
                    reject("executable module specification/file origins differ")
                continue
            loader = specification.loader
            if (type(module) is not type(sys) or type(loader) is not importlib.machinery.NamespaceLoader or getattr(module, "__loader__", None) is not loader or
                    specification.origin is not None or specification.has_location):
                reject("missing file origin is not a true namespace package")
            parts = name.split(".")
            if len(parts) < 2 or any(not part.isidentifier() for part in parts):
                reject("namespace fullname has an invalid directory identity")
            expected = root.joinpath(*parts[1:])
            locations = list(specification.submodule_search_locations or ())
            package_paths = list(getattr(module, "__path__", ()))
            if len(locations) != 1 or len(package_paths) != 1:
                reject("namespace must have one exact selected search directory")
            location = admitted.common.safe_absolute(locations[0], "SDK namespace search directory")
            package_path = admitted.common.safe_absolute(package_paths[0], "SDK namespace package path")
            if (location != expected or package_path != expected or not expected.is_dir() or not expected.is_relative_to(root) or
                    (expected / "__init__.py").exists()):
                reject("namespace paths differ from its selected SDK directory")
            relative = expected.relative_to(root).as_posix()
            if relative not in directories or not any(path.startswith(relative + "/") for path in inventory):
                reject("namespace directory has no pinned descendant files")
        except (OSError, RuntimeError, TypeError, AttributeError) as error:
            reject(str(error))
        except ValueError as error:
            if str(error).startswith("Loaded SDK module " + name[:256] + " rejected:"):
                raise
            reject(str(error))


def _assert_sdk_mesh_mapping(mesh, positions, indices, materials, admission, admitted):
    admission.charge_work(admitted, len(positions) + len(indices) + len(materials), "actual SDK geometry/material mapping")
    if len(mesh.vertices) != len(positions) or len(mesh.loops) != len(indices) or len(mesh.polygons) != len(materials):
        raise ValueError("Actual SDK mesh inventory differs from delivered primitive compaction")
    if any(tuple(float(value) for value in vertex.co) != point for vertex, point in zip(mesh.vertices, positions)):
        raise ValueError("Actual SDK POSITION values differ from the delivered accessor")
    if any(int(loop.vertex_index) != index for loop, index in zip(mesh.loops, indices)):
        raise ValueError("Actual SDK loop indices differ from delivered triangles")
    for index, (polygon, material) in enumerate(zip(mesh.polygons, materials)):
        if tuple(polygon.loop_indices) != (3*index, 3*index+1, 3*index+2) or tuple(polygon.vertices) != tuple(indices[3*index:3*index+3]):
            raise ValueError("Actual SDK triangle order differs from delivered indices")
        slot = polygon.material_index
        if not 0 <= slot < len(mesh.materials) or mesh.materials[slot] is None or mesh.materials[slot].get("bubo_id") != material:
            raise ValueError("Actual SDK triangle material differs from delivered primitive")


class FixedDeliveredNormalImport:
    """One audited SDK hook decoding actual delivered NORMAL fields.

    The SDK owns geometry/material/node import in FLAT mode. This fixed hook
    avoids its lossy short2 normal path, without deriving or replacing values
    from construction/source face normals. SDK hook exceptions are swallowed;
    our bounded ledger must therefore be asserted after BlenderGlTF.create.
    """
    def __init__(self, admission, admitted, binary_data, glb_sha256):
        self.admission, self.admitted, self.binary_data = admission, admitted, binary_data
        self.glb_sha256 = glb_sha256
        self.importer, self.lookup, self.owners = None, {}, {}
        self.calls, self.completed, self.failures = {}, set(), []
        self.vertices = self.corners = self.primitives = self.prior_mismatches = 0
        self.max_prior_error = 0.

    def bind(self, importer, raw):
        if importer.import_user_extensions != [self] or importer.import_settings != _fixed_import_settings(self):
            raise ValueError("Fixed SDK import settings/extensions changed")
        self.importer = importer
        meshes, nodes = importer.data.meshes, importer.data.nodes
        if len(meshes) != raw["rawCounts"]["meshes"] or not 1 <= len(meshes) <= self.admitted.job["limits"]["maxMeshes"]:
            raise ValueError("SDK document mesh count differs from strict raw admission")
        self.admission.charge_work(self.admitted, len(meshes) + len(nodes), "fixed SDK hook coverage admission")
        self.lookup = {id(mesh): index for index, mesh in enumerate(meshes)}
        if len(self.lookup) != len(meshes):
            raise ValueError("SDK mesh objects are aliased")
        for node in nodes:
            if node.mesh is not None:
                if node.mesh in self.owners or not isinstance(node.extras, dict) or not isinstance(node.extras.get("bubo_id"), str):
                    raise ValueError("SDK mesh has missing/ambiguous delivered semantic ownership")
                self.owners[node.mesh] = node.extras["bubo_id"]
        if set(self.owners) != set(range(len(meshes))):
            raise ValueError("SDK mesh ownership differs from the strict raw inventory")

    def _decode(self, gltf, index, width):
        accessor = gltf.data.accessors[index]
        count = accessor.count
        if type(count) is not int or not 1 <= count <= (3*self.admitted.job["limits"]["maxTriangles"] if width == 1 else self.admitted.job["limits"]["maxVertices"]):
            raise ValueError("Delivered accessor count exceeds fixed decode limits")
        self.admission.charge_work(self.admitted, count, "delivered accessor decode reservation")
        values = self.binary_data.decode_accessor(gltf, index, cache=False).copy()
        if len(values) != count or any(len(row) != width for row in values):
            raise ValueError("SDK accessor decoder changed the delivered count/width")
        return values

    def gather_import_mesh_after_hook(self, pymesh, mesh, gltf):
        if self.failures:
            return
        try:
            self._import_mesh(pymesh, mesh, gltf)
        except Exception as error:
            self.failures.append(str(error)[:2048])
            raise

    def _import_mesh(self, pymesh, mesh, gltf):
        if gltf is not self.importer or id(pymesh) not in self.lookup:
            raise ValueError("Fixed SDK hook received an unbound importer/mesh")
        index = self.lookup[id(pymesh)]
        self.calls[index] = self.calls.get(index, 0) + 1
        if self.calls[index] != 1:
            raise ValueError("Fixed SDK mesh hook was called more than once")
        positions, indices, materials, normals = [], [], [], []
        limits = self.admitted.job["limits"]
        for primitive in pymesh.primitives:
            self.primitives += 1
            if self.primitives > limits["maxPrimitives"] or primitive.mode not in (None, 4) or set(primitive.attributes) != {"POSITION", "NORMAL"}:
                raise ValueError("Fixed normal decoder requires admitted POSITION/NORMAL triangles")
            source_positions = self._decode(gltf, primitive.attributes["POSITION"], 3)
            source_normals = self._decode(gltf, primitive.attributes["NORMAL"], 3)
            if len(source_positions) != len(source_normals):
                raise ValueError("Delivered NORMAL/POSITION counts differ")
            if primitive.indices is None:
                source_indices = list(range(len(source_positions)))
            else:
                source_indices = [int(row[0]) for row in self._decode(gltf, primitive.indices, 1)]
            if len(source_indices) < 3 or len(source_indices) % 3 or any(not 0 <= value < len(source_positions) for value in source_indices):
                raise ValueError("Delivered triangle index inventory changed")
            self.admission.charge_work(self.admitted, len(source_indices), "delivered normal corner expansion")
            self.corners += len(source_indices)
            if self.corners > 3*limits["maxTriangles"]:
                raise ValueError("Delivered cumulative corner count exceeds approval")
            gltf.locs_batch_gltf_to_blender(source_positions)
            gltf.normals_batch_gltf_to_blender(source_normals)
            self.admission.charge_work(self.admitted, len(source_indices) * (len(source_indices).bit_length() + 1),
                                       "SDK primitive sorting/compaction reservation")
            unique = sorted(set(source_indices))
            self.vertices += len(unique)
            if self.vertices > limits["maxVertices"]:
                raise ValueError("SDK compacted vertex count exceeds approval")
            base = len(positions)
            remap = {value: base + offset for offset, value in enumerate(unique)}
            positions.extend(tuple(float(value) for value in source_positions[vertex]) for vertex in unique)
            indices.extend(remap[value] for value in source_indices)
            normals.extend(tuple(float(value) for value in source_normals[vertex]) for vertex in source_indices)
            if primitive.material is None:
                raise ValueError("Delivered triangle primitive has no material")
            material = gltf.data.materials[primitive.material].extras["bubo_id"]
            materials.extend([material] * (len(source_indices)//3))
        _assert_sdk_mesh_mapping(mesh, positions, indices, materials, self.admission, self.admitted)
        if len(mesh.corner_normals) != len(normals):
            raise ValueError("Preexisting SDK generated-normal inventory differs from corners")
        for loop, normal in enumerate(normals):
            self.admission.charge_work(self.admitted, 1, "preexisting SDK generated-normal diagnostic")
            previous = tuple(float(value) for value in mesh.corner_normals[loop].vector)
            if not all(math.isfinite(value) for value in previous) or math.hypot(*previous) == 0:
                raise ValueError("SDK generated a nonfinite/empty preexisting corner normal")
            error = _normal_angle_degrees(previous, normal)
            self.max_prior_error = max(self.max_prior_error, error)
            if error > self.admitted.plan["numerics"]["angularToleranceDegrees"]:
                self.prior_mismatches += 1
        attributes = mesh.attributes
        if (len(attributes) > 32 or mesh.has_custom_normals or attributes.get("custom_normal") is not None or
                any(attribute.name.casefold() == "custom_normal" or attribute.name.casefold().startswith("custom_normal.") for attribute in attributes)):
            raise ValueError("SDK FLAT mesh has unexpected preexisting custom normals")
        self.admission.charge_work(self.admitted, len(attributes) + len(normals) + len(mesh.polygons), "delivered float-corner normal assignment")
        for polygon in mesh.polygons:
            polygon.use_smooth = True
        attribute = attributes.new(name="custom_normal", type="FLOAT_VECTOR", domain="CORNER")
        if (attribute.name != "custom_normal" or attribute.data_type != "FLOAT_VECTOR" or attribute.domain != "CORNER" or
                len(attribute.data) != len(normals) or not callable(getattr(attribute.data, "foreach_set", None))):
            raise ValueError("SDK hook could not create exact delivered normal attribute")
        attribute.data.foreach_set("vector", array("f", (component for normal in normals for component in normal)))
        # Direct float-vector storage has no normal-space encoding. Invalidate
        # the generated-normal cache read above, then measure actual storage.
        mesh.update()
        if not mesh.has_custom_normals or len(mesh.corner_normals) != len(normals):
            raise ValueError("SDK hook did not retain delivered corner normals")
        for loop, expected in enumerate(normals):
            self.admission.charge_work(self.admitted, 1, "delivered normal exact component readback")
            if tuple(float(value) for value in mesh.corner_normals[loop].vector) != expected:
                raise ValueError("Actual imported corner normal differs from delivered accessor components")
        _assert_sdk_mesh_mapping(mesh, positions, indices, materials, self.admission, self.admitted)
        self.completed.add(index)

    def require_complete(self):
        if self.importer is None or not self.owners:
            raise ValueError("Fixed SDK delivered-normal hook was not bound")
        if self.failures:
            raise ValueError("Fixed SDK delivered-normal hook failed: " + self.failures[0])
        if self.completed != set(self.owners) or self.calls != {index: 1 for index in self.owners}:
            raise ValueError("Fixed SDK delivered-normal hook coverage is incomplete")
        diagnostic = {"diagnostic": "fixed-sdk-delivered-normal-import", "method": "official-after-mesh-hook-exact-accessor-to-float-corner",
                      "importShading": "FLAT", "rawGlbSha256": self.glb_sha256, "meshes": len(self.completed),
                      "primitives": self.primitives, "vertices": self.vertices, "corners": self.corners,
                      "preexistingGeneratedNormalMismatchCount": self.prior_mismatches,
                      "maxPreexistingGeneratedToDeliveredAngleDegrees": self.max_prior_error, "sourceDerivedNormalValues": False}
        print(json.dumps(diagnostic, sort_keys=True, separators=(",", ":")))
        return diagnostic


def import_visual_or_collision(bpy, admitted, admission, representation, path, raw):
    admission.remaining_limits(admitted, "actual GLB import entry")
    # The strict raw reader has already bounded this exact input. Reserve its
    # decode/triangle workload before entering the native importer, then charge
    # each actual inspection step separately below.
    admission.charge_work(admitted, raw["observedVertexCount"] + raw["observedTriangleCount"], "actual GLB import reservation")
    admission.remaining_limits(admitted, "actual GLB import")
    admission.verify_sources(admitted, bpy)
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.context.scene.sequence_editor_clear()
    bpy.context.scene.render.use_sequencer = False
    bpy.context.scene.render.use_compositing = False
    if bpy.app.debug_value != 0 or bpy.context.scene.unit_settings.scale_length != 1:
        raise ValueError("Fixed SDK import requires normal axis conversion and metre scale")
    importer_type, blender_import, binary_data = _pinned_import_sdk(admitted)
    admission.charge_work(admitted, (path.stat().st_size + 65535)//65536, "delivered GLB import identity scan")
    glb_sha256 = admitted.common.hash_file(path, admitted.job["limits"]["maxSourceBytes"])
    extension = FixedDeliveredNormalImport(admission, admitted, binary_data, glb_sha256)
    importer = importer_type(str(path), _fixed_import_settings(extension))
    primary_error = None
    try:
        importer.read()
        importer.checks()
        extension.bind(importer, raw)
        blender_import.create(importer)
        if importer.import_user_extensions != [extension] or importer.import_settings != _fixed_import_settings(extension):
            raise ValueError("Fixed SDK import settings/extensions changed during create")
        extension.require_complete()
        _require_loaded_sdk_origins(admitted)
    except BaseException as error:
        primary_error = error
        raise
    finally:
        try:
            importer.log.flush()
        except Exception as error:
            if primary_error is None:
                raise
            # A diagnostic must not replace the original import/hook failure.
            try:
                print(json.dumps({"diagnostic": "sdk-log-flush-failed-after-import-error", "message": str(error)[:512]}, separators=(",", ":")))
            except Exception:
                pass
    admission.verify_sources(admitted, bpy)
    bpy.context.view_layer.update()
    return inspect_import(bpy, admitted, admission, representation)


def _source_inventory(bpy, admitted, admission):
    common, source = admitted.common, admitted.scene
    admission.remaining_limits(admitted, "source inventory entry")
    admission.charge_work(admitted, source["metrics"]["vertexCount"] + source["metrics"]["triangleCount"], "source indexed-solid inventory reservation")
    admission.remaining_limits(admitted, "source indexed-solid inventory")
    admitted.review.require_source_scope(bpy.data)
    inventory, objects = admitted.review.inventory(bpy, common, admitted.plan["storeys"],
        {"maxObjects": admitted.job["limits"]["maxNodes"], **admitted.job["limits"]})
    scene = bpy.context.scene
    if (scene.get("bubo_schema_version") != source["schemaVersion"] or scene.get("bubo_plan_id") != source["planId"] or
            scene.get("bubo_job_sha256") != admitted.job["identities"]["constructionJobSha256"] or
            any(scene.get("bubo_" + key) != admitted.job["identities"][key] for key in ("planSha256", "designFingerprint", "runtimeSha256"))):
        raise ValueError("Editable source lacks the exact trusted construction ancestry")
    if set(objects) != {row["id"] for row in source["meshes"]} or {row["id"] for row in inventory["anchors"]} != {row["id"] for row in source["anchors"]}:
        raise ValueError("Editable source object inventory differs from native scene")
    for anchor in source["anchors"]:
        actual = next(row for row in inventory["anchors"] if row["id"] == anchor["id"])
        if any(actual[key] != anchor[key] for key in ("semanticId", "storeyId", "kind")) or math.dist(common.point(actual["position"], "anchor"), common.point(anchor["position"], "expected anchor")) > source["linearTolerance"] or common.point(actual["axis"], "axis") != (0, 0, 1) or actual["rotationRadians"] != 0:
            raise ValueError("Editable source pivot differs from native scene")
    # Source inventory uses exact native indexed-shell checks separately from
    # the seam-tolerant oriented triangle comparison used for the export.
    measured = {}
    for representation in ("visual", "collision"):
        subset = [row for row in source["meshes"] if row["representation"] == representation]
        if not subset:
            raise ValueError("Both visual and collision geometry are required")
        all_rows = []
        for row in subset:
            admission.remaining_limits(admitted, "source mesh entry")
            item = objects[row["id"]]
            data = item.data
            data.calc_loop_triangles()
            admission.charge_work(admitted, len(data.vertices) + len(data.loop_triangles), "source world-geometry inspection")
            observed = {key: item.get("bubo_" + key) for key in MESH_KEYS if key != "parentAnchorId"}
            observed["parentAnchorId"] = item.parent.get("bubo_id") if item.parent else None
            observed.update(vertices=[[float(value) for value in item.matrix_world @ vertex.co] for vertex in data.vertices],
                            triangles=[int(index) for triangle in data.loop_triangles for index in triangle.vertices],
                            triangleMaterialIds=[data.materials[data.polygons[triangle.polygon_index].material_index].get("bubo_id") for triangle in data.loop_triangles])
            all_rows.append(observed)
        measured[representation] = admitted.geometry.compare_meshes(source, representation, all_rows,
            admission.remaining_limits(admitted, "source " + representation + " comparison"), source["linearTolerance"], admitted.plan["numerics"]["angularToleranceDegrees"])
        admission.charge_work(admitted, measured[representation]["geometryChecks"], "source " + representation + " comparison")
    # Blender saves the assigned palette, dropping zero-user materials unless
    # explicitly retained. Provenance still carries every source declaration.
    admission.charge_work(admitted, len(bpy.data.materials), "source material inspection")
    compare_source_materials(source, material_rows(bpy), admitted.geometry)
    return inventory, measured


def clearance_preservation(scene, plan, raw, imported, representation):
    """Conservative perturbation bounds on native evidence, not path finding.

    Complete oriented triangle correspondence bounds every surface point by
    its matched vertex error. Distances between two perturbed bodies can fall
    by the sum of their bounds. Hinge-frame drift is included for every angle
    of the source-approved door sweep, including its final-open pose.
    """
    tolerance = scene["linearTolerance"]
    error = max(raw["maxWorldCoordinateError"], imported["maxWorldCoordinateError"])
    if not math.isfinite(error) or not 0 <= error <= tolerance:
        raise ValueError("Clearance preservation requires finite complete-triangle error bounds")
    routes = []
    for route in scene["clearance"]["routes"]:
        margin = route["minimumClearance"]
        lower = margin - error
        if lower < -tolerance:
            raise ValueError("Exported route margin cannot be preserved within approved tolerance")
        routes.append({"roomId": route["roomId"], "fromId": route["fromId"], "toId": route["toId"],
                       "sourceMinimumClearance": margin, "triangleErrorBound": error, "conservativeClearanceLowerBound": lower})
    expected_anchors = {row["id"]: row for row in scene["anchors"]}
    observed_sets = (raw["nodes"], imported["nodes"]["nodes"])
    source_meshes = [row for row in scene["meshes"] if row["representation"] == representation]
    sweeps = []
    for sweep in scene["clearance"]["doorSweeps"]:
        anchor = expected_anchors[sweep["anchorId"]]
        hinge = tuple(anchor["position"][key] for key in ("x", "y", "z"))
        leaves = [row for row in source_meshes if row["parentAnchorId"] == anchor["id"] and row["kind"] == "door-leaf"]
        if not leaves:
            raise ValueError("Door sweep lacks a measured owned leaf in selected representation")
        radius = max(math.dist(tuple(point[key] for key in ("x", "y", "z")), hinge) for leaf in leaves for point in leaf["vertices"])
        hinge_error = frame_error = 0.
        for rows in observed_sets:
            observed = next((row for row in rows if row["id"] == anchor["id"]), None)
            if observed is None:
                raise ValueError("Door sweep lacks independently measured imported hinge")
            hinge_error = max(hinge_error, math.dist(observed["position"], hinge))
            matrix = observed["worldMatrix"]
            frame_error = max(frame_error, math.sqrt(math.fsum((matrix[4*i+j] - (1 if i == j else 0))**2 for i in range(3) for j in range(3))))
        # Matrix admission allows only tiny numerical unit-frame drift. Use
        # the Neumann bound ||A^-1||<=1/(1-delta), delta=||A-I||_F<1,
        # rather than assuming that an approximately unit frame is exact.
        if frame_error >= 1:
            raise ValueError("Door hinge frame has no bounded inverse")
        amplification = (1 + frame_error) / (1 - frame_error)
        dynamic_bound = amplification * error + (amplification + 1) * hinge_error + 2 * frame_error / (1 - frame_error) * radius
        lower = sweep["minimumClearance"] - dynamic_bound - error
        if not math.isfinite(lower) or lower < -tolerance:
            raise ValueError("Exported complete door sweep margin cannot be bounded within approved tolerance")
        sweeps.append({"openingId": sweep["openingId"], "anchorId": anchor["id"], "angleDegrees": sweep["angleDegrees"],
                       "sourceMinimumClearance": sweep["minimumClearance"], "leafLeverArm": radius,
                       "hingePositionErrorBound": hinge_error, "hingeFrameFrobeniusErrorBound": frame_error,
                       "frameConditionAmplificationBound": amplification,
                       "movingLeafErrorBound": dynamic_bound, "staticSurfaceErrorBound": error,
                       "conservativeClearanceLowerBound": lower, "completeSweepAndFinalOpenCovered": True})
    return {"method": "native-clearance-minus-measured-surface-and-hinge-perturbation", "routes": routes, "doorSweeps": sweeps,
            "maxSurfacePointErrorBound": error, "newNavigationMeasurement": False,
            "sourceApertureIds": sorted(opening["id"] for floor in plan["storeys"] for wall in floor.get("walls", []) for opening in wall["openings"]),
            "sourceSlabOpeningIds": sorted(opening["id"] for floor in plan["storeys"] for opening in floor.get("slabOpenings", [])),
            "sourceStairIds": sorted(stair["id"] for stair in plan["stairs"]),
            "featureBasis": "complete-triangle-preservation-within-approved-tolerance"}


def _portable_hierarchy(bpy, admitted):
    # Parent explicitly in memory, preserving source world coordinates. Never
    # save the blend: collections remain the editable source's original layout.
    objects = {item.get("bubo_id"): item for item in bpy.data.objects}
    for representation in ("visual", "collision"):
        for row in admitted.geometry.expected_nodes(admitted.scene, representation):
            identifier = row["id"]
            item = objects.get(identifier)
            if item is None:
                item = bpy.data.objects.new("portable-" + hashlib.sha256(identifier.encode()).hexdigest()[:32], None)
                bpy.context.scene.collection.objects.link(item)
                objects[identifier] = item
            for key in MESH_KEYS:
                item["bubo_parent_anchor_id" if key == "parentAnchorId" else "bubo_" + key] = row[key] or ""
            parent = objects.get(row["parentId"])
            if row["parentId"] is not None and parent is None:
                raise ValueError("Expected portable hierarchy must be parent-first")
            world = item.matrix_world.copy()
            item.parent = parent
            item.matrix_world = world
    bpy.context.view_layer.update()
    return objects


def clear_transient_source_scene_metadata(scene, admitted, admission):
    """Remove only validated source-only Scene ID props in export memory.

    The exact saved blend and construction receipts retain these identities.
    Portable object/material extras are independent and remain untouched.
    """
    admission.remaining_limits(admitted, "transient scene metadata entry")
    expected = {"bubo_schema_version": admitted.scene["schemaVersion"], "bubo_plan_id": admitted.scene["planId"],
                "bubo_job_sha256": admitted.job["identities"]["constructionJobSha256"],
                **{"bubo_" + key: admitted.job["identities"][key] for key in ("planSha256", "designFingerprint", "runtimeSha256")},
                "bubo_independent_reimport": False, "bubo_human_model_accepted": False, "bubo_runtime_accepted": False}
    keys = scene.keys()
    if len(keys) != len(expected) or set(keys) != set(expected):
        raise ValueError("Source scene has missing/unknown transient identity fields")
    for key, value in expected.items():
        actual = scene[key]
        if type(actual) is not type(value) or actual != value:
            raise ValueError("Source scene has changed identity or acceptance metadata: " + key)
    admission.charge_work(admitted, len(expected), "transient source scene identity validation")
    for key in expected:
        del scene[key]


def _unit_triangle_normal(points):
    if len(points) != 3 or any(len(point) != 3 or not all(math.isfinite(float(value)) for value in point) for point in points):
        raise ValueError("Flat corner normals require three finite local positions")
    a, b, c = (tuple(float(value) for value in point) for point in points)
    u, v = [b[i] - a[i] for i in range(3)], [c[i] - a[i] for i in range(3)]
    normal = (math.fsum((u[1] * v[2], -u[2] * v[1])),
              math.fsum((u[2] * v[0], -u[0] * v[2])),
              math.fsum((u[0] * v[1], -u[1] * v[0])))
    size = math.hypot(*normal)
    if not math.isfinite(size) or size == 0:
        raise ValueError("Flat corner normals cannot repair collapsed/nonfinite geometry")
    return tuple(value / size for value in normal)


def _normal_angle_degrees(left, right):
    cross = (left[1]*right[2] - left[2]*right[1], left[2]*right[0] - left[0]*right[2], left[0]*right[1] - left[1]*right[0])
    return math.degrees(math.atan2(math.hypot(*cross), math.fsum(a*b for a, b in zip(left, right))))


def flat_corner_normals(vertices, faces, face_loops, loop_count, admission, admitted):
    """Double-precision local face normals, indexed by independent corners."""
    admission.remaining_limits(admitted, "flat corner-normal entry")
    limits = admitted.job["limits"]
    if (not 1 <= len(vertices) <= limits["maxVertices"] or not 1 <= len(faces) <= limits["maxTriangles"] or
            len(face_loops) != len(faces) or type(loop_count) is not int or loop_count != len(faces) * 3):
        raise ValueError("Flat normal authoring requires bounded native triangle/corner inventory")
    admission.charge_work(admitted, len(vertices), "flat normal local-position inspection")
    normals = [None] * loop_count
    for face, loops in zip(faces, face_loops):
        admission.charge_work(admitted, 1, "flat normal triangle inspection")
        if (len(face) != 3 or len(loops) != 3 or len(set(face)) != 3 or
                any(type(index) is not int or not 0 <= index < len(vertices) for index in face)):
            raise ValueError("Flat normal authoring cannot retriangulate or repair faces")
        normal = _unit_triangle_normal([vertices[index] for index in face])
        for loop in loops:
            admission.charge_work(admitted, 1, "flat normal corner assignment")
            if type(loop) is not int or not 0 <= loop < loop_count or normals[loop] is not None:
                raise ValueError("Flat normal corner ownership is incomplete or duplicated")
            normals[loop] = normal
    if any(normal is None for normal in normals):
        raise ValueError("Flat normal authoring left unowned corners")
    return normals


def _normal_storage_failure(reason, identifier, face, loop, expected, observed, error, angle, positions, **extra):
    """One bounded, exact counterexample; never dump complete source meshes."""
    def finite(value):
        return float(value) if math.isfinite(float(value)) else None
    context = {"diagnostic": "transient-flat-normal-rejection", "reason": reason, "meshId": identifier,
               "faceIndex": face, "loopIndex": loop, "expected": [finite(value) for value in expected],
               "observed": [finite(value) for value in observed], "angularErrorDegrees": finite(error) if error is not None else None,
               "angularToleranceDegrees": angle, "localFacePositions": positions, **extra}
    encoded = json.dumps(context, allow_nan=False, sort_keys=True, separators=(",", ":"))
    print(encoded)
    raise ValueError("Blender custom normal storage rejected: " + encoded)


def prepare_transient_flat_normals(bpy, admitted, admission):
    """Author only normal attributes/smooth flags on transient mesh copies.

    Blender 4.5's bundled exporter reads corner_normals and then rounds to
    four decimals. We neither change that installed code nor its precision;
    actual raw and imported angular gates remain authoritative.
    """
    admission.remaining_limits(admitted, "transient flat-normal preparation")
    originals = [item for item in bpy.data.objects if item.type == "MESH"]
    if (len(originals) != len(admitted.scene["meshes"]) or len({item.get("bubo_id") for item in originals}) != len(originals) or
            {item.get("bubo_id") for item in originals} != {row["id"] for row in admitted.scene["meshes"]}):
        raise ValueError("Transient normal authoring requires exact native mesh identities")
    triangles = corners = 0
    maximum = 0.
    angle = admitted.plan["numerics"]["angularToleranceDegrees"]
    for item in originals:
        original = item.data
        attributes = getattr(original, "attributes", None)
        if attributes is None or not callable(getattr(attributes, "new", None)) or not callable(getattr(attributes, "get", None)):
            raise ValueError("Selected Blender lacks required float-vector corner-attribute API")
        if len(attributes) > 32:
            raise ValueError("Source mesh attribute inventory exceeds the fixed constructor ceiling")
        admission.charge_work(admitted, len(attributes), "source normal-attribute admission")
        if (getattr(original, "has_custom_normals", False) or attributes.get("custom_normal") is not None or
                any(attribute.name.casefold() == "custom_normal" or attribute.name.casefold().startswith("custom_normal.") for attribute in attributes)):
            raise ValueError("Source mesh has existing/ambiguous custom normal attributes")
        if not 1 <= len(original.edges) <= len(original.loops):
            raise ValueError("Transient flat normals require bounded native triangle-edge inventory")
        admission.charge_work(admitted, len(original.vertices) + len(original.polygons) + len(original.loops) + len(original.edges), "transient normal mesh-copy reservation")
        vertices = [tuple(float(value) for value in vertex.co) for vertex in original.vertices]
        faces = [tuple(int(index) for index in polygon.vertices) for polygon in original.polygons]
        loops = [tuple(int(index) for index in polygon.loop_indices) for polygon in original.polygons]
        before_geometry = admitted.common.geometry_sha256(vertices, [index for face in faces for index in face])
        assignments = [polygon.material_index for polygon in original.polygons]
        slots = list(original.materials)
        edges = [tuple(int(index) for index in edge.vertices) for edge in original.edges]
        normals = flat_corner_normals(vertices, faces, loops, len(original.loops), admission, admitted)
        working = original.copy()
        item.data = working
        for polygon in working.polygons:
            polygon.use_smooth = True
        # Original architecture is flat shaded. Preserve that isolation while
        # enabling custom corner vectors: no shared smooth fan may average two
        # independently approved face directions under Blender's wider epsilon.
        for edge in working.edges:
            admission.charge_work(admitted, 1, "transient flat-face edge isolation")
            if not hasattr(edge, "use_edge_sharp"):
                raise ValueError("Selected Blender lacks required sharp-edge shading API")
            edge.use_edge_sharp = True
        # Finalize shading before the one explicit FLOAT_VECTOR/CORNER path.
        # Blender 4.5 returns this attribute directly as corner_normals; the
        # short2 setter cannot reliably encode the approved near-auto angles.
        working.update()
        admission.charge_work(admitted, len(normals), "float-vector corner-normal attribute write")
        attribute = working.attributes.new(name="custom_normal", type="FLOAT_VECTOR", domain="CORNER")
        if (attribute.name != "custom_normal" or attribute.data_type != "FLOAT_VECTOR" or attribute.domain != "CORNER" or
                len(attribute.data) != len(normals) or not callable(getattr(attribute.data, "foreach_set", None))):
            raise ValueError("Blender did not create the exact float-vector corner-normal attribute")
        values = array("f", (component for normal in normals for component in normal))
        if len(values) != len(normals) * 3:
            raise ValueError("Float-vector corner-normal component inventory changed")
        attribute.data.foreach_set("vector", values)
        admission.charge_work(admitted, len(edges), "transient sharp-edge retention inspection")
        if len(working.edges) != len(edges) or any(not edge.use_edge_sharp for edge in working.edges):
            raise ValueError("Blender did not retain isolated flat-face normal fans")
        if not getattr(working, "has_custom_normals", False) or len(working.corner_normals) != len(normals):
            raise ValueError("Blender did not retain explicit custom corner normals")
        for face_index, face_loops in enumerate(loops):
            actual = []
            positions = [vertices[index] for index in faces[face_index]]
            for loop in face_loops:
                admission.charge_work(admitted, 1, "stored custom corner-normal inspection")
                observed = tuple(float(value) for value in working.corner_normals[loop].vector)
                length = math.hypot(*observed)
                if not math.isfinite(length) or abs(length - 1) > 1e-6:
                    _normal_storage_failure("stored corner is not a finite unit direction", item.get("bubo_id"), face_index, loop,
                                            normals[loop], observed, None, angle, positions, observedLength=length if math.isfinite(length) else None)
                error = _normal_angle_degrees(observed, normals[loop])
                maximum = max(maximum, error)
                if error > angle:
                    _normal_storage_failure("stored corner exceeds approved angular tolerance", item.get("bubo_id"), face_index, loop,
                                            normals[loop], observed, error, angle, positions)
                actual.append(observed)
            for left, right in ((0, 1), (1, 2), (2, 0)):
                error = _normal_angle_degrees(actual[left], actual[right])
                if error > angle:
                    _normal_storage_failure("stored corners do not preserve flat face appearance", item.get("bubo_id"), face_index, face_loops[left],
                                            normals[face_loops[left]], actual[left], error, angle, positions,
                                            otherLoopIndex=face_loops[right], otherObserved=list(actual[right]))
        admission.charge_work(admitted, len(vertices) + len(faces), "transient normal geometry-preservation inspection")
        after_vertices = [tuple(float(value) for value in vertex.co) for vertex in working.vertices]
        after_faces = [tuple(int(index) for index in polygon.vertices) for polygon in working.polygons]
        if (admitted.common.geometry_sha256(after_vertices, [index for face in after_faces for index in face]) != before_geometry or
                [polygon.material_index for polygon in working.polygons] != assignments or list(working.materials) != slots or
                [tuple(int(index) for index in edge.vertices) for edge in working.edges] != edges):
            raise ValueError("Custom normal authoring changed geometry or material assignment")
        triangles += len(faces); corners += len(normals)
    print(json.dumps({"diagnostic": "transient-double-flat-corner-normals", "meshes": len(originals), "triangles": triangles,
                      "corners": corners, "storage": "FLOAT_VECTOR/CORNER custom_normal", "maxStoredAngularErrorDegrees": maximum,
                      "angularToleranceDegrees": angle}, sort_keys=True, separators=(",", ":")))


def _export_selected(bpy, path):
    options = {"filepath": str(path), "check_existing": False, "export_format": "GLB", "use_selection": True,
               "use_visible": False, "use_renderable": False, "use_active_collection": False,
               "export_extras": True, "export_yup": True, "export_apply": False, "export_materials": "EXPORT",
               "export_animations": False, "export_skins": False, "export_morph": False, "export_cameras": False,
               "export_lights": False, "export_normals": True, "export_attributes": False, "export_tangents": False, "export_texcoords": False,
               "export_draco_mesh_compression_enable": False, "export_use_gltfpack": False,
               "export_gpu_instances": False, "export_hierarchy_full_collections": False,
               "export_hierarchy_flatten_objs": False, "export_gn_mesh": False, "will_save_settings": False}
    properties = {prop.identifier for prop in bpy.ops.export_scene.gltf.get_rna_type().properties}
    if not set(options) <= properties:
        raise ValueError("Pinned glTF operator lacks a required explicit preservation option: " + ",".join(sorted(set(options) - properties)))
    if path.exists() or path.is_symlink():
        raise ValueError("GLB destination already exists")
    if "FINISHED" not in bpy.ops.export_scene.gltf(**options):
        raise ValueError("Pinned Blender GLB exporter did not finish")


def inspect_raw(admission, admitted, path, representation):
    measured = admitted.geometry.inspect_glb(path, admitted.scene, representation,
        admission.remaining_limits(admitted, "raw " + representation + " inspection"),
        admitted.scene["linearTolerance"], admitted.plan["numerics"]["angularToleranceDegrees"])
    admission.charge_work(admitted, measured["geometryChecks"], "raw " + representation + " inspection")
    return measured


def _emit_material_iors(admission, admitted, document, representation):
    """Complete explicit source IOR serialization; all other scalars must match."""
    c = admitted.common
    c.closed(document, ("asset", "scene", "scenes", "nodes", "meshes", "materials", "accessors", "bufferViews", "buffers"),
             ("extensionsUsed", "extensionsRequired"), label="emitted GLB document")
    allowed = {"KHR_materials_ior", "KHR_materials_transmission"}
    used = c.array(document.get("extensionsUsed", []), "emitted extensionsUsed", len(allowed))
    required = c.array(document.get("extensionsRequired", []), "emitted extensionsRequired", len(allowed))
    if (any(type(value) is not str or value not in allowed for value in used + required) or
            len(set(used)) != len(used) or len(set(required)) != len(required) or not set(required) <= set(used)):
        raise ValueError("Unexpected/duplicate emitted material extension declarations")
    pending = [document]
    while pending:
        value = pending.pop()
        admission.charge_work(admitted, 1, "emitted GLB JSON inventory")
        if isinstance(value, dict):
            if "uri" in value:
                raise ValueError("Emitted GLB external/data resource URI is forbidden")
            if "extensions" in value:
                c.closed(value["extensions"], (), allowed, label="emitted extensions")
            pending.extend(value.values())
        elif isinstance(value, list):
            pending.extend(value)
    sources = {material["id"]: material for material in admitted.scene["materials"]}
    materials = c.array(document["materials"], "emitted materials", admitted.job["limits"]["maxMaterials"], 1)
    rows, observed_extensions = [], set()
    for material in materials:
        admission.charge_work(admitted, 1, "explicit source material IOR emission")
        c.closed(material, ("extras",), ("name", "pbrMetallicRoughness", "extensions", "doubleSided", "alphaMode", "emissiveFactor"), label="emitted material")
        extras = c.closed(material["extras"], ("bubo_id", "bubo_name", "bubo_color"), label="emitted material identity")
        identifier = c.text(extras["bubo_id"], "emitted material id")
        source = sources.get(identifier)
        if source is None or extras["bubo_name"] != source["name"] or extras["bubo_color"] != source["color"]:
            raise ValueError("Emitted material has unknown/changed source semantics")
        pbr = c.closed(material.get("pbrMetallicRoughness", {}), (), ("baseColorFactor", "roughnessFactor", "metallicFactor"), label="emitted scalar PBR")
        extensions = c.closed(material.get("extensions", {}), (), allowed, label="emitted material extensions")
        observed_extensions.update(extensions)
        if "KHR_materials_transmission" in extensions:
            c.closed(extensions["KHR_materials_transmission"], ("transmissionFactor",), label="emitted transmission")
        existing_ior = source["ior"]
        if "KHR_materials_ior" in extensions:
            value = c.closed(extensions["KHR_materials_ior"], (), ("ior",), label="emitted IOR")
            existing_ior = value.get("ior", 1.5)
        rows.append({"id": identifier, "color": pbr.get("baseColorFactor", [1, 1, 1, 1]),
                     "roughness": pbr.get("roughnessFactor", 1), "metallic": pbr.get("metallicFactor", 1),
                     "transmission": extensions.get("KHR_materials_transmission", {}).get("transmissionFactor", 0), "ior": existing_ior})
    if observed_extensions != set(used):
        raise ValueError("Emitted material extensions disagree with their declarations")
    # Missing opaque IOR is the installed exporter's documented omission, not
    # evidence that the source scalar changed. Any existing scalar and every
    # other PBR property must already match before explicit format emission.
    admitted.geometry.compare_materials(admitted.scene, rows, representation)
    for material in materials:
        source = sources[material["extras"]["bubo_id"]]
        material.setdefault("extensions", {})["KHR_materials_ior"] = {"ior": source["ior"]}
    document["extensionsUsed"] = sorted(observed_extensions | {"KHR_materials_ior"})


def preserve_material_iors(admission, admitted, path, representation):
    """Deterministic owned-output GLB serialization, retaining BIN verbatim.

    This fixed step runs on every export, before authoritative raw/import
    checks. Its temporary file can replace only this invocation's exact newly
    exported output after rechecking its bytes. Failures retain intermediates.
    """
    admission.remaining_limits(admitted, "source material format-emission entry")
    c = admitted.common
    path = c.safe_absolute(str(path), "newly exported GLB")
    if path != admitted.output / (representation + ".glb") or representation not in ("visual", "collision"):
        raise ValueError("Material emission requires the selected owned GLB output")
    maximum = min(admitted.job["limits"]["maxOutputBytes"], 2**32 - 1)
    size = path.stat().st_size
    if not path.is_file() or not 32 <= size <= maximum:
        raise ValueError("Missing/oversized emitted GLB container")
    block_size = 65536
    def hash_current():
        admission.charge_work(admitted, (size + block_size - 1) // block_size, "emitted GLB identity scan")
        if path.stat().st_size != size:
            raise ValueError("Emitted GLB size changed during format emission")
        return c.hash_file(c.safe_absolute(str(path), "owned emitted GLB"), maximum)
    before_hash = hash_current()
    json_cap = min(8 * 1024**2, admitted.job["limits"]["maxJobBytes"], maximum)
    with path.open("rb") as source:
        if source.read(12) != struct.pack("<4sII", b"glTF", 2, size):
            raise ValueError("Invalid emitted GLB header/declared total length")
        json_header = source.read(8)
        if len(json_header) != 8:
            raise ValueError("Missing emitted GLB JSON chunk")
        json_size, json_type = struct.unpack("<II", json_header)
        if json_type != 0x4e4f534a or json_size % 4 or not 4 <= json_size <= min(json_cap, size - 28):
            raise ValueError("Invalid/oversized emitted GLB JSON chunk")
        admission.charge_work(admitted, (json_size + block_size - 1) // block_size, "emitted GLB JSON parse reservation")
        raw_json = source.read(json_size)
        if len(raw_json) != json_size:
            raise ValueError("Truncated emitted GLB JSON")
        document = admission.decode_json(c, raw_json)
        binary_header = source.read(8)
        if len(binary_header) != 8:
            raise ValueError("Missing emitted GLB BIN chunk")
        binary_size, binary_type = struct.unpack("<II", binary_header)
        if binary_type != 0x004e4942 or binary_size % 4 or binary_size < 4 or 28 + json_size + binary_size != size:
            raise ValueError("Invalid emitted GLB BIN length/layout or extra chunks")
        buffers = c.array(document.get("buffers"), "emitted GLB buffers", 1, 1)
        c.closed(buffers[0], ("byteLength",), label="emitted GLB internal buffer")
        declared_binary = c.integer(buffers[0]["byteLength"], "emitted BIN bytes", 1, binary_size)
        if binary_size - declared_binary > 3:
            raise ValueError("Emitted BIN padding exceeds the format allowance")
        _emit_material_iors(admission, admitted, document, representation)
        encoded = bytearray()
        for fragment in json.JSONEncoder(ensure_ascii=False, allow_nan=False, sort_keys=True, separators=(",", ":")).iterencode(document):
            raw = fragment.encode("utf-8")
            if len(encoded) + len(raw) > json_cap:
                raise ValueError("Explicit material JSON exceeds its bounded allowance")
            encoded.extend(raw)
        encoded.extend(b" " * (-len(encoded) % 4))
        if len(encoded) > json_cap:
            raise ValueError("Padded explicit material JSON exceeds its bounded allowance")
        new_size = 28 + len(encoded) + binary_size
        files = admission.output_files(admitted)
        # Account for both retained raw bytes and the exclusive temporary copy,
        # including any representation already completed in this invocation.
        if new_size > maximum or sum(row["bytes"] for row in files.values()) + new_size > maximum:
            raise ValueError("Material emission exceeds cumulative output/intermediate bytes")
        partial = path.with_name(path.name + ".partial")
        descriptor = os.open(partial, os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0), 0o600)
        binary_hash, copied, padding = hashlib.sha256(), 0, bytearray()
        with os.fdopen(descriptor, "wb") as target:
            target.write(struct.pack("<4sII", b"glTF", 2, new_size))
            target.write(struct.pack("<II", len(encoded), 0x4e4f534a))
            target.write(encoded)
            target.write(binary_header)
            while copied < binary_size:
                admission.charge_work(admitted, 1, "emitted GLB BIN copy")
                data = source.read(min(block_size, binary_size - copied))
                if not data:
                    raise ValueError("Emitted GLB BIN truncated during bounded copy")
                start = copied
                copied += len(data)
                if copied > declared_binary:
                    padding.extend(data[max(0, declared_binary - start):])
                binary_hash.update(data)
                target.write(data)
            if source.read(1) or any(padding):
                raise ValueError("Emitted GLB has extra bytes or nonzero BIN padding")
            target.flush()
            os.fsync(target.fileno())
    if hash_current() != before_hash:
        raise ValueError("Newly exported GLB changed before explicit material publication")
    checked_partial = c.safe_absolute(str(partial), "explicit material GLB partial")
    if checked_partial != partial or checked_partial.stat().st_size != new_size:
        raise ValueError("Explicit material temporary output changed")
    os.replace(partial, path)
    admission.charge_work(admitted, (new_size + block_size - 1) // block_size, "explicit material GLB final identity")
    final_hash = c.hash_file(c.safe_absolute(str(path), "explicit material GLB"), maximum)
    diagnostic = {"diagnostic": "source-material-ior-serialization", "representation": representation,
                  "beforeSha256": before_hash, "sha256": final_hash, "binSha256": binary_hash.hexdigest(),
                  "beforeBytes": size, "bytes": new_size, "materials": len(document["materials"])}
    print(json.dumps(diagnostic, sort_keys=True, separators=(",", ":")))
    return diagnostic


def produce(admission, admitted):
    import bpy
    require_process(bpy, admitted, admission)
    output = admitted.common.create_output(admitted)
    opened = bpy.ops.wm.open_mainfile(filepath=str(admitted.paths["sourceBlend"]), load_ui=False, use_scripts=False)
    if "FINISHED" not in opened or Path(bpy.data.filepath).resolve(strict=True) != admitted.paths["sourceBlend"]:
        raise ValueError("Blender did not open exact trusted source")
    # Guard inherited evaluator features before explicit dependency updates.
    admitted.review.require_source_scope(bpy.data)
    bpy.context.view_layer.update()
    inventory, source_measured = _source_inventory(bpy, admitted, admission)
    prepare_transient_flat_normals(bpy, admitted, admission)
    clear_transient_source_scene_metadata(bpy.context.scene, admitted, admission)
    admission.remaining_limits(admitted, "portable hierarchy entry")
    admission.charge_work(admitted, len(admitted.scene["meshes"]) + len(admitted.scene["anchors"]) + 1 + 4 * len(admitted.plan["storeys"]), "portable hierarchy construction")
    objects = _portable_hierarchy(bpy, admitted)
    measured = {}
    for representation in ("visual", "collision"):
        admission.remaining_limits(admitted, "produce " + representation + " entry")
        admission.verify_sources(admitted, bpy)
        expected = admitted.geometry.expected_nodes(admitted.scene, representation)
        selected = {row["id"] for row in expected}
        for identifier, item in objects.items():
            item.hide_set(False)
            item.hide_viewport = False
            item.select_set(identifier in selected)
        bpy.context.view_layer.objects.active = objects[expected[0]["id"]]
        path = output / (representation + ".glb")
        selected_meshes = [row for row in admitted.scene["meshes"] if row["representation"] == representation]
        admission.charge_work(admitted, sum(len(row["vertices"]) + len(row["triangles"]) // 3 for row in selected_meshes), "native GLB export reservation")
        _export_selected(bpy, path)
        preserve_material_iors(admission, admitted, path, representation)
        admission.output_files(admitted)
        # Raw observation here checks producer output immediately. It does not
        # substitute for the fresh verifier's actual independent import.
        measured[representation] = inspect_raw(admission, admitted, path, representation)
        admission.verify_sources(admitted, bpy)
    files = admission.output_files(admitted, {"visual.glb", "collision.glb"})
    work = admitted.geometry_work[0]
    report = admission.report_envelope(admitted, "bubo-floor-plan-export-producer/v1",
        {"passed": True, "source": source_measured, "visual": measured["visual"], "collision": measured["collision"], "sourceUnchanged": True, "geometryChecks": work},
        [files[name] for name in sorted(files)], blenderVersion=".".join(map(str, bpy.app.version)))
    admission.write_report(admitted, "producer-report.json", report)
    admission.output_files(admitted, {"visual.glb", "collision.glb", "producer-report.json"})
    admission.verify_sources(admitted, bpy)
    return report


def verify(admission, admitted):
    import bpy
    require_process(bpy, admitted, admission)
    admitted.common.create_output(admitted)
    representation = admitted.representation
    path = admitted.paths["glb"]
    raw = inspect_raw(admission, admitted, path, representation)
    imported = import_visual_or_collision(bpy, admitted, admission, representation, path, raw)
    if raw["sourceTriangleCount"] != imported["sourceTriangleCount"] or raw["observedTriangleCount"] != imported["observedTriangleCount"]:
        raise ValueError("Raw GLB and actual import triangle measurements disagree")
    admission.remaining_limits(admitted, "clearance-preservation entry")
    clearance_work = len(admitted.scene["clearance"]["routes"]) + sum(
        1 + sum(len(mesh["vertices"]) for mesh in admitted.scene["meshes"] if mesh["representation"] == representation and mesh["parentAnchorId"] == sweep["anchorId"])
        for sweep in admitted.scene["clearance"]["doorSweeps"])
    admission.charge_work(admitted, clearance_work, "clearance-preservation inspection")
    clearance = clearance_preservation(admitted.scene, admitted.plan, raw, imported, representation)
    measured = {"passed": True, "raw": raw, "imported": imported,
                "geometryChecks": admitted.geometry_work[0],
                "clearancePreservation": clearance,
                "clearanceBasis": "complete-oriented-triangle-correspondence-to-native-measured-construction",
                "sourceClearanceEvidence": admitted.scene["clearance"], "newNavigationMeasurement": False,
                "nativeIndexedTopologyBasis": "source-construction-only", "cleanProcessImport": True}
    report = admission.report_envelope(admitted, "bubo-floor-plan-export-verification/v1", measured, [],
        representation=representation, blenderVersion=".".join(map(str, bpy.app.version)))
    admission.write_report(admitted, "verification.json", report)
    admission.output_files(admitted, {"verification.json"})
    admission.verify_sources(admitted, bpy)
    return report


def main():
    arguments = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    admission, admitted = bootstrap(arguments)
    report = produce(admission, admitted) if admitted.job["operation"] == "produce" else verify(admission, admitted)
    print(json.dumps({"status": "export-" + report["operation"], "runtimeAccepted": False}))
    return report


if __name__ == "__main__":
    main()
