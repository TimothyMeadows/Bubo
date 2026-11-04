"""Fixed source-preserving canonical and diagnostic GLB producer."""
import hashlib
import math
from pathlib import Path
import struct
import sys
import json

sys.dont_write_bytecode = True
# Verify the exact job and all fixed resources before any repository module is
# imported. Native orchestration repeats these checks at its execution boundary.
if __name__ == "__main__":
    _args = sys.argv[sys.argv.index("--")+1:] if "--" in sys.argv else []
    if len(_args) != 2:
        raise ValueError("Expected exact job path and SHA256")
    _job_path = Path(_args[0])
    if not _job_path.is_absolute() or not _job_path.is_file() or not 2 <= _job_path.stat().st_size <= 8*1024**2:
        raise ValueError("Missing/oversized fixed export job")
    for _entry in [_job_path, *_job_path.parents]:
        _info = _entry.lstat()
        if _entry.is_symlink() or getattr(_info, "st_file_attributes", 0) & 0x400:
            raise ValueError("Linked export bootstrap path")
    _raw = _job_path.read_bytes()
    if hashlib.sha256(_raw).hexdigest() != _args[1]:
        raise ValueError("Fixed export job SHA256 changed")
    _job = json.loads(_raw)
    _names = {"export_admission.py", "export_geometry.py", "export_author.py",
              "export_verify.py", "export_review.py", "assembly_admission.py",
              "assembly_author.py", "assembly_geometry.py", "assembly_motion.py",
              "assembly_verify.py", "parts_admission.py", "parts_geometry.py"}
    if not isinstance(_job, dict) or not isinstance(_job.get("bundle"), dict) or set(_job["bundle"]) != _names:
        raise ValueError("Exact twelve-resource export bundle required")
    for _name, _expected in _job["bundle"].items():
        _resource = Path(__file__).parent/_name
        for _entry in [_resource, *_resource.parents]:
            _info = _entry.lstat()
            if _entry.is_symlink() or getattr(_info, "st_file_attributes", 0) & 0x400:
                raise ValueError("Linked export resource")
        if not _resource.is_file() or _resource.stat().st_size > 2*1024**2 or hashlib.sha256(_resource.read_bytes()).hexdigest() != _expected:
            raise ValueError("Audited export resource SHA256 changed: "+_name)
sys.path.insert(0, str(Path(__file__).resolve().parent))
from export_admission import (admit, process, import_sdk, require_sdk_origins,
                              require, digest)
from export_geometry import read_glb, write_glb
import export_geometry as geo


def actual_source_snapshot(bpy):
    """Observe saved source data before diagnostic evaluation changes frames."""
    return {obj.name: {"local": obj.matrix_local.copy(), "world": obj.matrix_world.copy(),
                       "vertices": [tuple(float(c) for c in vertex.co) for vertex in obj.data.vertices] if obj.type == "MESH" else None,
                       "bones": {bone.name: bone.matrix_local.copy() for bone in obj.data.bones} if obj.type == "ARMATURE" else {}}
            for obj in bpy.data.objects}


def verify_source(admitted, bpy, diagnostic):
    from assembly_geometry import inventory, measure_instances, name
    from assembly_motion import check_actions, check_pose, owners, sample_actions
    source = admitted.motion if diagnostic else admitted.assembly
    key = "sourceMotion" if diagnostic else "sourceAssembly"
    opened = bpy.ops.wm.open_mainfile(filepath=str(admitted.paths[key]), load_ui=False, use_scripts=False)
    require("FINISHED" in opened and Path(bpy.data.filepath) == admitted.paths[key], "exact saved source open")
    inventory(source, bpy)
    require(all(not o.animation_data or o.animation_data.action is None for _, o in owners(source, bpy)), "saved canonical rest actions disabled")
    check_pose(source, bpy, source["plan"]["rest"])
    snapshot = actual_source_snapshot(bpy)
    measured = measure_instances(source, bpy, "rest", verify=True)
    expected = admitted.reports["motionReport" if diagnostic else "assemblyReport"]
    require(measured == expected["instances"], "current measured source differs from saved receipt")
    clips = sample_actions(source, bpy, verify=True) if diagnostic else check_actions(source, bpy)
    require(clips == expected["clips"], "current actual diagnostic actions differ from saved receipt")
    count = sum(i["vertexCount"] + i["triangleCount"] for i in measured)
    admitted.charge(count * (source["plan"]["sampling"]["totalSamples"] if diagnostic else 1))
    return source, snapshot


def prepare_transient(source, snapshot, bpy, diagnostic):
    """Change only export metadata/NLA packaging in the unsaved working scene."""
    from assembly_geometry import name
    from assembly_motion import owners, curves
    from parts_geometry import stable_json
    # Restore measured saved transforms, never expected design coordinates.
    for obj in bpy.data.objects:
        obj.matrix_local = snapshot[obj.name]["local"]
    bpy.context.view_layer.update()
    for scene in bpy.data.scenes:
        for key in list(scene.keys()):
            del scene[key]
    for obj in bpy.data.objects:
        for key in list(obj.keys()):
            if key in ("bubo.rig", "bubo.ringVertexIndices"):
                del obj[key]
        obj.select_set(True)
        if obj.type == "MESH":
            mesh = obj.data
            if "_BUBO_VERTEX_INDEX" not in mesh.attributes:
                attr = mesh.attributes.new("_BUBO_VERTEX_INDEX", "FLOAT", "POINT")
                attr.data.foreach_set("value", list(range(len(mesh.vertices))))
        if obj.type == "ARMATURE":
            for index, bone in enumerate(obj.data.bones):
                bone["bubo.sourceBoneName"] = bone.name
                bone["bubo.sourceBoneIndex"] = index
                bone["bubo.sourceBoneLength"] = float(bone.length)
                bone["bubo.owner"] = obj["bubo.owner"]
    if diagnostic:
        for clip_index, clip in enumerate(source["plan"]["clips"]):
            for owner_index, (_, owner) in enumerate(owners(source, bpy)):
                action = bpy.data.actions[name("action", clip_index, owner_index)]
                # Native source uses frame 1 + 1000*t. Portable clips begin at
                # zero; this explicit time-origin conversion retains every
                # measured key/value and never resamples or edits the source.
                for curve in curves(action):
                    for key in curve.keyframe_points:
                        key.co.x -= 1
                        key.handle_left.x -= 1
                        key.handle_right.x -= 1
                track = owner.animation_data.nla_tracks.new()
                track.name = "bubo.clip."+str(clip_index).zfill(6)
                track.mute = True
                strip = track.strips.new(track.name, 0, action)
                strip.action_slot = action.slots[0]
                strip.mute = False
                strip.blend_type = "REPLACE"
                strip.extrapolation = "NOTHING"
                owner.animation_data.action = None
                owner.animation_data.use_nla = False
    bpy.context.scene.frame_start, bpy.context.scene.frame_end = 0, 1000
    bpy.context.scene.render.fps, bpy.context.scene.render.fps_base = 1000, 1
    bpy.context.scene.frame_set(1)
    bpy.context.view_layer.update()


def export_selected(admitted, bpy, path, diagnostic):
    # glTF JSON has an enforced 8 MiB ceiling. Every possible triangle corner
    # bounds split vertices; the fixed attributes plus normalized copies and
    # indices consume at most 80 bytes/corner. Animation stores original and
    # normalized 3/4-component streams at the original finite sample times.
    meshes = [obj for obj in bpy.data.objects if obj.type == "MESH"]
    corners = sum(3*len(obj.data.loop_triangles) for obj in meshes)
    joints = sum(len(obj.data.bones) for obj in bpy.data.objects if obj.type == "ARMATURE")
    owners = len(meshes)+sum(1+max(0, len(obj.data.bones)-1) for obj in bpy.data.objects if obj.type == "ARMATURE")
    samples = sum(len(c["samples"]) for c in admitted.motion["plan"]["clips"]) if diagnostic else 0
    accessors = 10*len(meshes)+2*joints+6*owners*(len(admitted.motion["plan"]["clips"]) if diagnostic else 0)
    reserve = 8*1024**2+80*corners+128*joints+64*owners*samples+4*accessors+4096
    current = admitted.output_files()
    require(len(current)+2 <= admitted.job["limits"]["maxFiles"] and sum(r["bytes"] for r in current)+reserve <= admitted.job["limits"]["maxOutputBytes"], "insufficient output reservation before fixed GLB write")
    import_sdk(admitted)
    options = {"filepath": str(path), "check_existing": False, "export_format": "GLB", "use_selection": True,
               "use_visible": False, "use_renderable": False, "use_active_collection": False,
               "export_extras": True, "export_yup": True, "export_apply": False, "export_materials": "EXPORT",
               "export_animations": diagnostic, "export_skins": True, "export_morph": False,
               "export_cameras": False, "export_lights": False, "export_normals": True,
               "export_attributes": True, "export_tangents": False, "export_texcoords": False,
               "export_vertex_color": "NONE", "export_all_vertex_colors": False,
               "export_active_vertex_color_when_no_material": False, "export_draco_mesh_compression_enable": False,
               "export_use_gltfpack": False, "export_gpu_instances": False,
               "export_hierarchy_full_collections": False, "export_hierarchy_flatten_objs": False,
               "export_hierarchy_flatten_bones": False, "export_gn_mesh": False,
               "export_armature_object_remove": False, "export_def_bones": False,
               "export_leaf_bone": False, "export_influence_nb": 4, "will_save_settings": False,
               "export_animation_mode": "ACTIONS", "export_merge_animation": "NLA_TRACK",
               "export_force_sampling": False, "export_frame_range": False,
               "export_optimize_animation_size": False, "export_anim_slide_to_zero": True,
               "export_anim_single_armature": False, "export_bake_animation": False,
               "export_pointer_animation": False}
    properties = {p.identifier for p in bpy.ops.export_scene.gltf.get_rna_type().properties}
    require(set(options) <= properties, "pinned glTF exporter lacks explicit options: "+",".join(sorted(set(options)-properties)))
    require(not path.exists(), "owned export file already exists")
    require("FINISHED" in bpy.ops.export_scene.gltf(**options), "builtin glTF export failed")
    require(path.stat().st_size <= reserve, "fixed GLB exceeds its conservative preflight reservation")
    require_sdk_origins(admitted.job)


def _rows(matrix):
    return [[float(matrix[i][j]) for j in range(4)] for i in range(4)]


def _to_gltf(matrix):
    c = [[1., 0., 0., 0.], [0., 0., 1., 0.], [0., -1., 0., 0.], [0., 0., 0., 1.]]
    return geo.multiply(geo.multiply(c, _rows(matrix)), geo.inverse(c))


def _portable_trs(matrix):
    location, rotation, scale = matrix.decompose()
    require(all(abs(float(v)-1) <= 1e-5 for v in scale), "actual portable source frame must remain rigid")
    rotation.normalize()
    return (tuple(float(location[i]) for i in (0, 2))+(-float(location[1]),),
            (float(rotation.x), float(rotation.z), -float(rotation.y), float(rotation.w)))


def _set_trs(node, matrix):
    translation, rotation = _portable_trs(matrix)
    node.pop("matrix", None)
    node.pop("scale", None)
    node["translation"], node["rotation"] = list(translation), list(rotation)


def _append_accessor(doc, binary, rows, kind):
    require(rows and kind in ("VEC3", "VEC4", "MAT4"), "bounded portable accessor rows")
    width = {"VEC3": 3, "VEC4": 4, "MAT4": 16}[kind]
    require(all(len(row) == width and all(math.isfinite(v) for v in row) for row in rows), "finite actual portable source values")
    binary.extend(b"\0"*(-len(binary)%4))
    offset = len(binary)
    for row in rows:
        binary.extend(struct.pack("<"+"f"*width, *row))
    view = len(doc.setdefault("bufferViews", []))
    doc["bufferViews"].append({"buffer": 0, "byteOffset": offset, "byteLength": len(rows)*width*4})
    accessor = {"bufferView": view, "componentType": 5126, "count": len(rows), "type": kind}
    if kind == "VEC3":
        accessor["min"] = [min(row[i] for row in rows) for i in range(3)]
        accessor["max"] = [max(row[i] for row in rows) for i in range(3)]
    index = len(doc.setdefault("accessors", []))
    doc["accessors"].append(accessor)
    doc["buffers"][0]["byteLength"] = len(binary)
    return index


def normalize_source_frames(admitted, doc, binary, source, snapshot, bpy, diagnostic):
    """Convert exporter world-baked skins to measured part-local frames.

    The pinned exporter bakes skin POSITION into world space and uses Blender
    bone-axis conventions. Use actual verified source geometry/frames/actions
    to produce equivalent glTF component coordinates before independent QA.
    No expected plan position, deformation, weight or topology repairs data.
    """
    from assembly_geometry import name
    from assembly_motion import owners, assign
    data = bytearray(binary)
    instances = {item["id"]: index for index, item in enumerate(source["design"]["instances"])}
    nodes_by_name = {node["name"]: node for node in doc["nodes"]}
    physical_nodes, bone_nodes = {}, {}
    for node in doc["nodes"]:
        extras = node.get("extras", {})
        if node["name"] in snapshot:
            _set_trs(node, snapshot[node["name"]]["local"])
        if "bubo.instanceId" in extras:
            owner = extras["bubo.instanceId"]
            physical_nodes[owner] = node
            _set_trs(node, snapshot[name("instance", instances[owner])]["local"])
        if "bubo.sourceBoneName" in extras:
            owner, bone_name = extras["bubo.owner"], extras["bubo.sourceBoneName"]
            arm = bpy.data.objects[name("rig", instances[owner])]
            rest = snapshot[arm.name]["bones"][bone_name]
            parent = arm.data.bones[bone_name].parent
            relative = snapshot[arm.name]["bones"][parent.name].inverted()@rest if parent else rest
            _set_trs(node, relative)
            bone_nodes[(owner, bone_name)] = node
    for owner, node in physical_nodes.items():
        if "skin" not in node:
            continue
        index = instances[owner]
        source_obj, source_arm = name("instance", index), name("rig", index)
        mesh_world = _to_gltf(snapshot[source_obj]["world"])
        skin = doc["skins"][node["skin"]]
        inverse_binds = []
        for joint_index in skin["joints"]:
            bone_name = doc["nodes"][joint_index]["extras"]["bubo.sourceBoneName"]
            joint_world = _to_gltf(geo.multiply(_rows(snapshot[source_arm]["world"]), _rows(snapshot[source_arm]["bones"][bone_name])))
            bind = geo.multiply(geo.inverse(joint_world), mesh_world)
            inverse_binds.append(tuple(bind[row][column] for column in range(4) for row in range(4)))
        skin["inverseBindMatrices"] = _append_accessor(doc, data, inverse_binds, "MAT4")
        for primitive in doc["meshes"][node["mesh"]]["primitives"]:
            logical = geo.accessor(doc, data, primitive["attributes"][geo.ATTRIBUTE], (1,), admitted.job["limits"]["maxVertices"])
            positions = []
            for value in logical:
                original = value[0]
                require(type(original) in (int, float) and int(original) == original and 0 <= original < len(snapshot[source_obj]["vertices"]), "actual source logical correspondence")
                point = snapshot[source_obj]["vertices"][int(original)]
                positions.append((point[0], point[2], -point[1]))
            primitive["attributes"]["POSITION"] = _append_accessor(doc, data, positions, "VEC3")
            normals = geo.accessor(doc, data, primitive["attributes"]["NORMAL"], (3,), admitted.job["limits"]["maxVertices"])
            inverse_world = geo.inverse(_rows(snapshot[source_obj]["world"]))
            local_normals = []
            for normal in normals:
                source_normal = geo.source_point(normal)
                local = tuple(sum(inverse_world[i][j]*source_normal[j] for j in range(3)) for i in range(3))
                length = math.sqrt(sum(c*c for c in local))
                require(length > 1e-8, "nonzero actual source surface normal")
                local_normals.append((local[0]/length, local[2]/length, -local[1]/length))
            primitive["attributes"]["NORMAL"] = _append_accessor(doc, data, local_normals, "VEC3")
    if diagnostic:
        animations = {a["name"]: a for a in doc["animations"]}
        for clip_index, clip in enumerate(source["plan"]["clips"]):
            animation = animations["bubo.clip."+str(clip_index).zfill(6)]
            observed = {}
            for owner_index, (_, owner) in enumerate(owners(source, bpy)):
                assign(owner, bpy.data.actions[name("action", clip_index, owner_index)])
            for sample in clip["samples"]:
                frame = 1000*sample["timeSeconds"]
                bpy.context.scene.frame_set(math.floor(frame), subframe=frame-math.floor(frame))
                bpy.context.view_layer.update()
                for _, owner in owners(source, bpy):
                    translation, rotation = _portable_trs(owner.matrix_local)
                    observed.setdefault((owner.name, "translation"), []).append(translation)
                    observed.setdefault((owner.name, "rotation"), []).append(rotation)
                    if owner.type == "ARMATURE":
                        for bone in owner.pose.bones:
                            if bone.name == "root":
                                continue
                            relative = bone.parent.matrix.inverted()@bone.matrix if bone.parent else bone.matrix
                            translation, rotation = _portable_trs(relative)
                            portable = bone_nodes[(owner["bubo.owner"], bone.name)]["name"]
                            observed.setdefault((portable, "translation"), []).append(translation)
                            observed.setdefault((portable, "rotation"), []).append(rotation)
            for channel in animation["channels"]:
                target = doc["nodes"][channel["target"]["node"]]["name"], channel["target"]["path"]
                require(target in observed, "actual source-owned diagnostic channel mapping")
                values = observed[target]
                if target[1] == "rotation":
                    for index in range(1, len(values)):
                        if sum(a*b for a, b in zip(values[index-1], values[index])) < 0:
                            values[index] = tuple(-v for v in values[index])
                sampler = animation["samplers"][channel["sampler"]]
                sampler["output"] = _append_accessor(doc, data, values, "VEC4" if target[1] == "rotation" else "VEC3")
                admitted.charge(len(values))
    return bytes(data)


def portable_metadata(admitted, path, source, snapshot, bpy, diagnostic):
    """Semantic renaming changes no geometry, matrices, skin or channels."""
    doc, binary = read_glb(path, admitted.job["limits"])
    ids = {i["id"]: index for index, i in enumerate(source["design"]["instances"])}
    joint_nodes = set()
    for skin in doc.get("skins", []):
        for index in skin.get("joints", []):
            require(index not in joint_nodes, "a joint belongs to multiple physical owners")
            joint_nodes.add(index)
            node = doc["nodes"][index]
            extras = node.get("extras", {})
            owner, bone = extras.get("bubo.owner"), extras.get("bubo.sourceBoneIndex")
            require(owner in ids and type(bone) is int and bone >= 0, "source-owned raw bone mapping absent")
            node["name"] = "bubo.bone."+str(ids[owner]).zfill(6)+"."+str(bone).zfill(6)
    animations = doc.get("animations", [])
    require(len(animations) == (len(source["plan"]["clips"]) if diagnostic else 0), "exact grouped portable animation count")
    by_name = {a.get("name"): a for a in animations}
    require(len(by_name) == len(animations), "unique grouped portable animation names")
    for index, clip in enumerate(source["plan"]["clips"] if diagnostic else []):
        name = "bubo.clip."+str(index).zfill(6)
        require(name in by_name, "all-owner NLA merge missing: "+name)
        by_name[name]["extras"] = {"bubo.clipId": clip["id"], "bubo.role": "diagnostic-readiness-only"}
    binary = normalize_source_frames(admitted, doc, binary, source, snapshot, bpy, diagnostic)
    require(len({n.get("name") for n in doc.get("nodes", [])}) == len(doc.get("nodes", [])), "portable node names must be globally unique")
    raw_json = json.dumps(doc, ensure_ascii=True, separators=(",", ":"), allow_nan=False).encode("utf-8")
    require(len(raw_json) <= 8*1024**2, "normalized GLB JSON byte ceiling")
    final_bytes = 28+len(raw_json)+(-len(raw_json)%4)+len(binary)+(-len(binary)%4)
    current = admitted.output_files()
    require(sum(r["bytes"] for r in current if r["path"] != path.name)+final_bytes <= admitted.job["limits"]["maxOutputBytes"], "exact normalized GLB output reservation")
    write_glb(path, doc, binary)
    require(path.stat().st_size <= admitted.job["limits"]["maxOutputBytes"], "complete GLB byte limit")


def produce(admitted):
    import bpy
    process(admitted, bpy)
    for diagnostic, filename in ((False, "assembly.glb"), (True, "diagnostic.glb")):
        source, snapshot = verify_source(admitted, bpy, diagnostic)
        admitted.unchanged(bpy)
        prepare_transient(source, snapshot, bpy, diagnostic)
        export_selected(admitted, bpy, admitted.output/filename, diagnostic)
        portable_metadata(admitted, admitted.output/filename, source, snapshot, bpy, diagnostic)
        admitted.output_files()
        admitted.unchanged(bpy)
    return admitted.report(bpy)


def main():
    args = sys.argv[sys.argv.index("--")+1:] if "--" in sys.argv else []
    return produce(admit(args, "produce"))


if __name__ == "__main__":
    main()
