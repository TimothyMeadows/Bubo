"""Fresh-process actual GLB import and complete raw/imported deformation QA.

This entry never opens a blend, calls the exporter, or repairs imported data.
"""
import importlib
import json
import math
from pathlib import Path
import sys
import hashlib

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
import export_geometry as geo


def _matrix(value):
    return [[float(value[i][j]) for j in range(4)] for i in range(4)]


def _pose(value):
    return geo.pose(_matrix(value))


def _file(admitted, name, data):
    row = admitted.write(name, data)
    return row["path"], row["sha256"]


def _material_raw(doc, indices):
    require(indices and len(set(indices)) == 1, "one approved material per physical component")
    index = geo.integer(indices[0], 0, len(doc.get("materials", []))-1, "material index")
    material = doc["materials"][index]
    require(not material.get("extensions") and type(material.get("doubleSided", False)) is bool, "portable static scalar material profile")
    pbr = material.get("pbrMetallicRoughness", {})
    require(set(pbr) <= {"baseColorFactor", "metallicFactor", "roughnessFactor"}, "scalar PBR material required")
    color = pbr.get("baseColorFactor", [1, 1, 1, 1])
    require(len(color) == 4 and color[3] == 1 and all(0 <= c <= 1 for c in color), "opaque bounded material color")
    identifier = material.get("extras", {}).get("bubo.materialId")
    require(isinstance(identifier, str), "actual material identity absent")
    return {"materialId": identifier, "materialColorLinear": dict(zip("xyz", color[:3])),
            "materialRoughness": float(pbr.get("roughnessFactor", 1)), "materialMetallic": float(pbr.get("metallicFactor", 1))}


def _material_imported(obj):
    require(len(obj.data.materials) == 1 and obj.data.materials[0] is not None, "one actual imported material")
    material = obj.data.materials[0]
    require(material.use_nodes and material.node_tree and not material.animation_data and not material.node_tree.animation_data, "static actual imported PBR graph")
    nodes = list(material.node_tree.nodes)
    shaders = [n for n in nodes if n.bl_idname == "ShaderNodeBsdfPrincipled"]
    outputs = [n for n in nodes if n.bl_idname == "ShaderNodeOutputMaterial" and n.is_active_output]
    require(len(shaders) == len(outputs) == 1 and len(nodes) == 2 and len(material.node_tree.links) == 1, "actual scalar material graph")
    shader = shaders[0]
    require(material.node_tree.links[0].from_node == shader and material.node_tree.links[0].to_node == outputs[0], "actual shader surface binding")
    require(all(p.material_index == 0 for p in obj.data.polygons), "complete actual triangle material assignments")
    color = list(shader.inputs["Base Color"].default_value)
    require(color[3] == 1, "opaque imported material")
    return {"materialId": material.get("bubo.materialId"), "materialColorLinear": dict(zip("xyz", color[:3])),
            "materialRoughness": float(shader.inputs["Roughness"].default_value), "materialMetallic": float(shader.inputs["Metallic"].default_value)}


def _node_index(doc):
    return {node["name"]: index for index, node in enumerate(doc["nodes"])}


def _identity(node):
    extras = node.get("extras", {})
    return extras.get("bubo.groupId", extras.get("bubo.instanceId"))


def _raw_skin(admitted, doc, binary, item, mesh_node_index, parents, local, world, prefix):
    mesh_node = doc["nodes"][mesh_node_index]
    if "skin" not in mesh_node:
        require(not any(item["weights"]), "rigid node has skin weights")
        return None
    skin = doc["skins"][geo.integer(mesh_node["skin"], 0, len(doc.get("skins", []))-1, "skin index")]
    joints = skin.get("joints", [])
    require(isinstance(joints, list) and 1 <= len(joints) <= admitted.job["limits"]["maxSkinJoints"] and len(set(joints)) == len(joints), "bounded unique skin joints")
    inverse_binds = geo.accessor(doc, binary, skin.get("inverseBindMatrices"), (16,), admitted.job["limits"]["maxSkinJoints"])
    require(len(inverse_binds) == len(joints), "complete actual inverse bind inventory")
    owner = mesh_node.get("extras", {}).get("bubo.instanceId")
    rigs = [(i, n) for i, n in enumerate(doc["nodes"]) if n.get("extras", {}).get("bubo.owner") == owner and n["name"].startswith("bubo.rig.")]
    require(len(rigs) == 1, "one actual physical skin owner")
    rig_index, rig = rigs[0]
    bone_names = [doc["nodes"][i]["name"] for i in joints]
    bone_parents, bones = [], []
    for index in joints:
        node = doc["nodes"][index]
        require(node.get("extras", {}).get("bubo.owner") == owner, "owned raw skin joint")
        parent = parents.get(index)
        require(parent in joints or parent == rig_index, "raw bone hierarchy escaped owner")
        parent_name = doc["nodes"][parent]["name"] if parent in joints else None
        bone_parents.append(parent_name)
        rest = geo.multiply(geo.inverse(world[rig_index]), world[index])
        pose = geo.pose(geo.convert_matrix(rest))
        length = node.get("extras", {}).get("bubo.sourceBoneLength")
        require(type(length) in (int, float) and math.isfinite(length) and length > 0, "source bone metadata length")
        bones.append({"name": node["name"], "parentName": parent_name, "restPose": pose, "pose": pose, "length": float(length)})
    require(all(j < len(joints) for weights in item["weights"] for j, _ in weights), "raw weights reference owned joint")
    weight_file, weight_sha = _file(admitted, prefix+".weights.bin", geo.skin_bytes(item["weights"]))
    return {"rigNode": rig["name"], "localPose": geo.pose(geo.convert_matrix(local[rig_index])), "worldPose": geo.pose(geo.convert_matrix(world[rig_index])),
            "joints": bone_names, "parents": bone_parents, "inverseBindMatrices": [list(v) for v in inverse_binds],
            "bones": bones, "weightsFile": weight_file, "weightsSha256": weight_sha}


def raw_measurement(admitted, doc, binary, parents, meshes, prefix, animation=None, time=0, rest=True):
    limits = admitted.job["limits"]
    local, world = geo.matrices(doc, binary, parents, limits, animation, time)
    result = []
    for ordinal, (node_index, base) in enumerate(meshes):
        node = doc["nodes"][node_index]
        extras = node.get("extras", {})
        instance_id, part_id = extras.get("bubo.instanceId"), extras.get("bubo.partId")
        require(isinstance(instance_id, str) and isinstance(part_id, str), "actual component semantic identities")
        parent = parents.get(node_index)
        require(parent is not None and isinstance(_identity(doc["nodes"][parent]), str), "actual semantic component parent")
        vertices = list(base["vertices"])
        if "skin" in node:
            skin = doc["skins"][node["skin"]]
            binds = geo.accessor(doc, binary, skin["inverseBindMatrices"], (16,), limits["maxSkinJoints"])
            require(len(binds) == len(skin["joints"]), "actual skin bind count")
            palette = [geo.multiply(geo.multiply(geo.inverse(world[node_index]), world[joint]), geo.column_matrix(bind)) for joint, bind in zip(skin["joints"], binds)]
            require(all(base["weights"]), "every flexible vertex has actual owned weights")
            converted = []
            for point, influences in zip(base["vertices"], base["weights"]):
                raw_point = (point[0], point[2], -point[1])
                transformed = [geo.transform(palette[j], raw_point) for j, _ in influences]
                converted.append(geo.source_point(tuple(sum(p[k]*w for p, (_, w) in zip(transformed, influences)) for k in range(3))))
            vertices = converted
        admitted.charge(len(vertices)+len(base["triangles"]))
        name = prefix+"."+str(ordinal).zfill(6)
        geometry_file, geometry_sha = _file(admitted, name+".geometry.bin", geo.geometry_bytes(vertices, base["logical"], base["triangles"]))
        ports = []
        for port_index, port in enumerate(doc["nodes"]):
            pextras = port.get("extras", {})
            if pextras.get("bubo.owner") == instance_id and pextras.get("bubo.kind") in ("interface", "terminal"):
                require(parents.get(port_index) == node_index, "actual port ownership")
                declaration = json.loads(pextras.get("bubo.port", "null"))
                require(isinstance(declaration, dict) and isinstance(declaration.get("id"), str), "actual port identity")
                ports.append({"portId": declaration["id"], "kind": pextras["bubo.kind"], "objectName": port["name"],
                              "localPose": geo.pose(geo.convert_matrix(local[port_index])), "worldPose": geo.pose(geo.convert_matrix(world[port_index]))})
        result.append({"instanceId": instance_id, "partId": part_id, "node": node["name"], "parentId": _identity(doc["nodes"][parent]),
                       "localPose": geo.pose(geo.convert_matrix(local[node_index])), "worldPose": geo.pose(geo.convert_matrix(world[node_index])),
                       "geometry": {"node": node["name"], "parentNode": doc["nodes"][parent]["name"], "sourceNodeIndex": node_index,
                                    "localPose": geo.pose(geo.convert_matrix(local[node_index])), "worldPose": geo.pose(geo.convert_matrix(world[node_index])), "semanticNodeKind": "mesh"},
                       **_material_raw(doc, base["triangleMaterials"]), "ports": ports,
                       "geometryFile": geometry_file, "geometrySha256": geometry_sha, "vertexCount": len(vertices), "triangleCount": len(base["triangles"]),
                       "skin": _raw_skin(admitted, doc, binary, base, node_index, parents, local, world, name) if rest else None})
    return result


def animation_records(admitted, doc, binary):
    diagnostic = admitted.job["representation"] == "diagnostic"
    expected = admitted.motion["plan"]["clips"] if diagnostic else []
    animations = doc.get("animations", [])
    require(len(animations) == len(expected), "complete portable clip inventory")
    by_name = {a.get("name"): a for a in animations}
    require(len(by_name) == len(animations), "unique actual portable animation names")
    result = []
    for index, clip in enumerate(expected):
        name = "bubo.clip."+str(index).zfill(6)
        require(name in by_name, "grouped portable clip missing")
        animation = by_name[name]
        require(animation.get("extras") == {"bubo.clipId": clip["id"], "bubo.role": "diagnostic-readiness-only"}, "semantic diagnostic clip mapping changed")
        channels, seen = [], set()
        require(len(animation.get("channels", [])) <= admitted.job["limits"]["maxChannels"], "bounded channels")
        for channel in animation.get("channels", []):
            node_index = geo.integer(channel["target"]["node"], 0, len(doc["nodes"])-1, "target node")
            path = channel["target"]["path"]
            require(path in ("translation", "rotation", "scale") and (node_index, path) not in seen, "unique transform channels")
            seen.add((node_index, path))
            sampler = animation["samplers"][geo.integer(channel["sampler"], 0, len(animation["samplers"])-1, "sampler")]
            require(sampler.get("interpolation", "LINEAR") == "LINEAR", "linear diagnostic channels")
            times = [v[0] for v in geo.accessor(doc, binary, sampler["input"], (1,), admitted.job["limits"]["maxMotionSamples"])]
            require(len(times) == len(clip["samples"]) and all(abs(t-s["timeSeconds"]) <= 1e-6 for t, s in zip(times, clip["samples"])), "every actual declared key retained")
            channels.append({"node": doc["nodes"][node_index]["name"], "path": path, "times": times})
        result.append((clip, name, animation, channels))
    return result


class _StaticBindImport:
    """Fixed pinned-SDK hook: create clips without automatically applying one.

    A range's first key can differ from the canonical rest pose. Initial bind
    evidence must measure the actual static GLB TRS, before explicit selection.
    This hook changes no node, joint, transform, skin or animation data.
    """
    def gather_import_animations(self, animations, options, gltf):
        options.restore_first_anim = False


def import_actual(admitted, bpy, path):
    import_sdk(admitted)
    settings = {"bone_heuristic": "BLENDER", "bone_shape_scale_factor": 1., "disable_bone_shape": True,
                "guess_original_bind_pose": False, "export_import_convert_lighting_mode": "SPEC",
                "import_merge_material_slots": False, "import_pack_images": False, "import_scene_as_collection": False,
                "import_scene_extras": False, "import_select_created_objects": False, "import_shading": "FLAT",
                "import_unused_materials": False, "import_webp_texture": False, "loglevel": 40,
                "merge_vertices": False, "import_user_extensions": [_StaticBindImport()]}
    importer_type = importlib.import_module("io_scene_gltf2.io.imp.gltf2_io_gltf").glTFImporter
    creator = importlib.import_module("io_scene_gltf2.blender.imp.blender_gltf").BlenderGlTF
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.context.scene.render.fps, bpy.context.scene.render.fps_base = 1000, 1
    importer = importer_type(str(path), settings)
    importer.read()
    importer.checks()
    creator.create(importer)
    require_sdk_origins(admitted.job)
    require(not bpy.data.libraries and not bpy.data.texts and not bpy.data.images and not bpy.data.sounds, "independent import contains unexpected dependencies")
    require(all(not obj.animation_data or obj.animation_data.action is None and all(track.mute for track in obj.animation_data.nla_tracks) for obj in bpy.data.objects), "static bind import must precede all clip activation")
    return importer


def imported_skin(admitted, bpy, obj, raw_skin, prefix):
    if raw_skin is None:
        require(not obj.modifiers and not obj.vertex_groups, "actual imported rigid component acquired a skin")
        return None
    require(len(obj.modifiers) == 1 and obj.modifiers[0].type == "ARMATURE", "actual imported skin modifier absent")
    mod = obj.modifiers[0]
    arm = mod.object
    require(arm is not None and arm.name == raw_skin["rigNode"] and arm.type == "ARMATURE" and mod.show_viewport and mod.show_render and mod.use_vertex_groups and not mod.use_bone_envelopes and arm.data.pose_position == "POSE", "actual imported owned skin is inactive or wrong")
    joints = raw_skin["joints"]
    require(set(b.name for b in arm.data.bones) == set(joints), "actual imported bone inventory")
    weights = []
    for vertex in obj.data.vertices:
        row = []
        for group in vertex.groups:
            require(0 <= group.group < len(obj.vertex_groups), "actual vertex group index")
            bone = obj.vertex_groups[group.group].name
            require(bone in joints and math.isfinite(group.weight) and group.weight > 0, "actual owned imported vertex weight")
            row.append((joints.index(bone), float(group.weight)))
        require(row and abs(sum(w for _, w in row)-1) <= 1e-6, "actual imported normalized skin weights")
        weights.append(row)
    weight_file, weight_sha = _file(admitted, prefix+".weights.bin", geo.skin_bytes(weights))
    bones = [{"name": bone.name, "parentName": bone.parent.name if bone.parent else None,
              "restPose": _pose(bone.matrix_local), "pose": _pose(arm.pose.bones[bone.name].matrix), "length": float(bone.length)} for bone in arm.data.bones]
    return {"rigNode": arm.name, "localPose": _pose(arm.matrix_local), "worldPose": _pose(arm.matrix_world), "joints": joints,
            "parents": [arm.data.bones[n].parent.name if arm.data.bones[n].parent else None for n in joints],
            # Actual GLB inverse binds belong to the portable skin; Blender
            # represents them in its independently measured edit-bone matrices.
            "inverseBindMatrices": raw_skin["inverseBindMatrices"], "bones": bones, "weightsFile": weight_file, "weightsSha256": weight_sha}


def imported_measurement(admitted, bpy, raw_instances, prefix, importer, rest=True):
    result = []
    geometry_owners = {}
    for index, node in enumerate(importer.data.nodes):
        if node.mesh is None:
            continue
        candidates = [v for v in importer.vnodes.values() if v.mesh_node_idx == index]
        require(len(candidates) == 1 and candidates[0].blender_object is not None and candidates[0].blender_object.type == "MESH", "actual pinned SDK raw-node/mesh mapping")
        geometry_owners[node.name] = (candidates[0].blender_object, index)
    require(set(geometry_owners) == {row["node"] for row in raw_instances} and {o.name for o in bpy.data.objects if o.type == "MESH"} == {o.name for o, _ in geometry_owners.values()}, "complete separate actual imported physical occurrences")
    for ordinal, raw in enumerate(raw_instances):
        semantic = bpy.data.objects.get(raw["node"])
        obj, source_node_index = geometry_owners[raw["node"]]
        require(semantic is not None and semantic.type in ("MESH", "EMPTY") and semantic.get("bubo.instanceId") == raw["instanceId"] and semantic.get("bubo.partId") == raw["partId"], "actual imported semantic identity")
        require(semantic.parent is not None, "actual imported physical hierarchy")
        parent_id = semantic.parent.get("bubo.groupId", semantic.parent.get("bubo.instanceId"))
        if parent_id is None and semantic.parent.type == "ARMATURE":
            # Pinned importer owns this explicit skin wrapper. Report its
            # actual parent and local transform; never repair the hierarchy.
            parent_id = semantic.parent.name
        require(isinstance(parent_id, str), "actual imported semantic parent")
        attr = obj.data.attributes.get(geo.ATTRIBUTE)
        require(attr is not None and attr.domain == "POINT" and attr.data_type in ("FLOAT", "INT"), "actual imported source correspondence absent")
        logical = [int(v.value) for v in attr.data]
        require(all(v.value == i for v, i in zip(attr.data, logical)), "actual imported integral logical indices")
        evaluated = obj.evaluated_get(bpy.context.evaluated_depsgraph_get())
        mesh = evaluated.to_mesh()
        try:
            mesh.calc_loop_triangles()
            vertices = [tuple(float(c) for c in v.co) for v in mesh.vertices]
            triangles = [tuple(t.vertices) for t in mesh.loop_triangles]
            require(len(vertices) == len(logical) and len(vertices) <= admitted.job["limits"]["maxVertices"] and len(triangles) <= admitted.job["limits"]["maxTriangles"], "actual imported evaluated topology/limits")
            admitted.charge(len(vertices)+len(triangles))
            name = prefix+"."+str(ordinal).zfill(6)
            geometry_file, geometry_sha = _file(admitted, name+".geometry.bin", geo.geometry_bytes(vertices, logical, triangles))
        finally:
            evaluated.to_mesh_clear()
        ports = []
        for port in bpy.data.objects:
            if port.get("bubo.owner") == raw["instanceId"] and port.get("bubo.kind") in ("interface", "terminal"):
                require(port.type == "EMPTY" and port.parent == semantic, "actual imported port ownership")
                declared = json.loads(port.get("bubo.port", "null"))
                require(isinstance(declared, dict) and isinstance(declared.get("id"), str), "actual imported port identity")
                ports.append({"portId": declared["id"], "kind": port["bubo.kind"], "objectName": port.name, "localPose": _pose(port.matrix_local), "worldPose": _pose(port.matrix_world)})
        result.append({"instanceId": semantic["bubo.instanceId"], "partId": semantic["bubo.partId"], "node": semantic.name, "parentId": parent_id,
                       "localPose": _pose(semantic.matrix_local), "worldPose": _pose(semantic.matrix_world), **_material_imported(obj), "ports": ports,
                       "geometry": {"node": obj.name, "parentNode": obj.parent.name if obj.parent else None, "sourceNodeIndex": source_node_index,
                                    "localPose": _pose(obj.matrix_local), "worldPose": _pose(obj.matrix_world), "semanticNodeKind": semantic.type.lower()},
                       "geometryFile": geometry_file, "geometrySha256": geometry_sha, "vertexCount": len(vertices), "triangleCount": len(triangles),
                       "skin": imported_skin(admitted, bpy, obj, raw["skin"], name) if rest else None})
    return result


def imported_channels(bpy):
    """Observe actual selected slots and component curves, not raw expectations."""
    result = []
    for owner in bpy.data.objects:
        data = owner.animation_data
        if data is None or data.action is None:
            continue
        require(data.action_slot is not None and not data.drivers, "actual imported active owned slot")
        action, slot = data.action, data.action_slot
        require(len(action.layers) == 1 and len(action.layers[0].strips) == 1, "actual bounded imported keyframe layer")
        bags = [bag for bag in action.layers[0].strips[0].channelbags if bag.slot_handle == slot.handle]
        require(len(bags) == 1, "actual imported owner channelbag")
        paths = {"location": (owner.name, "translation", 3), "rotation_quaternion": (owner.name, "rotation", 4), "scale": (owner.name, "scale", 3)}
        if owner.type == "ARMATURE":
            for bone in owner.pose.bones:
                for field, path, width in (("location", "translation", 3), ("rotation_quaternion", "rotation", 4), ("scale", "scale", 3)):
                    paths[bone.path_from_id(field)] = (bone.name, path, width)
        grouped = {}
        for curve in bags[0].fcurves:
            require(curve.data_path in paths and not curve.modifiers, "actual imported curve target/modifier")
            node, path, width = paths[curve.data_path]
            require(0 <= curve.array_index < width, "actual curve component")
            grouped.setdefault((node, path, width), []).append(curve)
        for (node, path, width), curves in grouped.items():
            require(len(curves) == width and {c.array_index for c in curves} == set(range(width)), "complete actual transform curve components")
            times = None
            for curve in curves:
                require(all(k.interpolation == "LINEAR" and math.isfinite(k.co.x) and math.isfinite(k.co.y) for k in curve.keyframe_points), "actual linear finite imported keys")
                actual = [float(k.co.x)/1000 for k in curve.keyframe_points]
                require(times is None or actual == times, "actual component key timing agreement")
                times = actual
            result.append({"node": node, "path": path, "times": times})
    return result


def verify(admitted):
    import bpy
    process(admitted, bpy)
    doc, binary = geo.read_glb(admitted.paths["glb"], admitted.job["limits"])
    parents = geo.hierarchy(doc, admitted.job["limits"])
    meshes = [(index, geo.geometry(doc, binary, node["mesh"], admitted.job["limits"])) for index, node in enumerate(doc["nodes"]) if "mesh" in node]
    require(meshes and len({node for node, _ in meshes}) == len(admitted.assembly["design"]["instances"]), "all approved physical occurrences exported")
    raw_instances = raw_measurement(admitted, doc, binary, parents, meshes, "raw.rest")
    records = animation_records(admitted, doc, binary)
    raw_clips = []
    for clip_index, (clip, portable, animation, channels) in enumerate(records):
        samples = []
        for index, sample in enumerate(clip["samples"]):
            prefix = "raw.clip."+str(clip_index).zfill(6)+".sample."+str(index).zfill(6)
            samples.append({"index": index, "timeSeconds": sample["timeSeconds"], "instances": raw_measurement(admitted, doc, binary, parents, meshes, prefix, animation, sample["timeSeconds"], False)})
        raw_clips.append({"id": clip["id"], "portableAnimationName": portable, "channels": channels, "samples": samples})
    importer = import_actual(admitted, bpy, admitted.paths["glb"])
    bpy.context.scene.frame_set(0)
    bpy.context.view_layer.update()
    imported_instances = imported_measurement(admitted, bpy, raw_instances, "imported.rest", importer)
    restorer = importlib.import_module("io_scene_gltf2.blender.imp.animation").BlenderAnimation
    imported_clips = []
    for clip_index, (clip, portable, animation, channels) in enumerate(records):
        actual = [a for a in importer.data.animations if a.name == portable]
        require(len(actual) == 1, "actual importer animation mapping")
        restorer.restore_animation(importer, actual[0].track_name)
        actual_channels = imported_channels(bpy)
        samples = []
        for index, sample in enumerate(clip["samples"]):
            frame = sample["timeSeconds"]*1000
            bpy.context.scene.frame_set(math.floor(frame), subframe=frame-math.floor(frame))
            bpy.context.view_layer.update()
            prefix = "imported.clip."+str(clip_index).zfill(6)+".sample."+str(index).zfill(6)
            samples.append({"index": index, "timeSeconds": sample["timeSeconds"], "instances": imported_measurement(admitted, bpy, raw_instances, prefix, importer, False)})
        imported_clips.append({"id": clip["id"], "portableAnimationName": portable, "channels": actual_channels, "samples": samples})
    counts = {"nodeCount": len(doc["nodes"]), "accessorCount": len(doc.get("accessors", [])), "primitiveCount": sum(len(m["primitives"]) for m in doc["meshes"])}
    admitted.unchanged(bpy)
    return admitted.report(bpy, {"instances": raw_instances, "clips": raw_clips, **counts}, {"instances": imported_instances, "clips": imported_clips, **counts})


def main():
    args = sys.argv[sys.argv.index("--")+1:] if "--" in sys.argv else []
    return verify(admit(args, "verify"))


if __name__ == "__main__":
    main()
