"""Separate physical instances, owned one-hot skin rigs and measured receipts.

No imported executable source, drivers, callbacks, remesh or proxy path exists.
"""
import hashlib
import json
import math
from pathlib import Path
import struct

from assembly_admission import read_geometry
from parts_admission import digest, no_links, require
from parts_geometry import (create_material, feature_mesh, geometry_bytes,
                            material_measurements, measurements, stable_json,
                            transform, vec, write_new, xyz)


def report_bytes(value):
    """Lossless assembly receipts use one deterministic compact UTF-8 line."""
    return (stable_json(value) + "\n").encode("utf-8")


def write_report(path, value):
    write_new(path, report_bytes(value))


def matrix(pose):
    from mathutils import Matrix
    return Matrix([[pose[a][k] for a in ("x", "y", "z", "origin")] for k in "xyz"] + [[0, 0, 0, 1]])


def frame_matrix(frame):
    from mathutils import Matrix
    zero = {"origin": dict.fromkeys("xyz", 0), "rotationDegrees": frame["rotationDegrees"]}
    axes = [transform(zero, v) for v in ((1, 0, 0), (0, 1, 0), (0, 0, 1))]
    return Matrix([[axes[j][i] for j in range(3)] + [frame["origin"]["xyz"[i]]] for i in range(3)] + [[0, 0, 0, 1]])


def pose_of(value):
    return {"origin": vec(tuple(float(value[i][3]) for i in range(3))),
            **{key: vec(tuple(float(value[i][j]) for i in range(3))) for j, key in enumerate("xyz")}}


def author_rigid_basis(obj, value):
    """Author only: store pure rigid channels, discarding roundoff scale.

    Saved-source verification never calls this to fix an imported object.
    """
    location, rotation, _ = value.decompose()
    obj.rotation_mode = "QUATERNION"
    obj.location = location
    obj.rotation_quaternion = rotation.normalized()
    obj.scale = (1, 1, 1)


def near_matrix(actual, expected, tolerance, angular_tolerance=None):
    angular_tolerance = tolerance if angular_tolerance is None else angular_tolerance
    require(all(abs(float(actual[i][3])-float(expected[i][3])) <= tolerance for i in range(3)), "Measured origin differs from admitted pose")
    require(all(abs(float(actual[i][j])-float(expected[i][j])) <= angular_tolerance for i in range(3) for j in range(3)), "Measured basis differs from admitted pose")
    require(all(abs(float(actual[3][j])-float(expected[3][j])) <= 1e-8 for j in range(4)), "Rigid homogeneous matrix required")


def name(kind, index, subindex=None):
    return "bubo." + kind + "." + str(index).zfill(6) + ("." + str(subindex).zfill(6) if subindex is not None else "")


def objects(job, bpy):
    return {item["id"]: bpy.data.objects[name("instance", index)] for index, item in enumerate(job["design"]["instances"])}


def world_frames(job, instance_poses):
    groups = {item["id"]: item for item in job["design"]["subassemblies"]}
    result = {key: matrix(value) for key, value in instance_poses.items()}
    visiting = set()
    def get(key):
        if key in result:
            return result[key]
        require(key in groups and key not in visiting, "Unknown/cyclic assembly parent")
        visiting.add(key)
        group = groups[key]
        result[key] = (get(group["parentId"]) if group["parentId"] is not None else frame_matrix({"origin": dict.fromkeys("xyz", 0), "rotationDegrees": dict.fromkeys("xyz", 0)})) @ frame_matrix(group["frame"])
        visiting.remove(key)
        return result[key]
    for key in groups:
        get(key)
    return result


def set_pose(job, bpy, instance_poses):
    from mathutils import Matrix
    worlds = world_frames(job, instance_poses)
    for index, item in enumerate(job["design"]["subassemblies"]):
        obj = bpy.data.objects[name("group", index)]
        obj.matrix_parent_inverse = Matrix.Identity(4)
        author_rigid_basis(obj, (worlds[item["parentId"]].inverted() if item["parentId"] else Matrix.Identity(4)) @ worlds[item["id"]])
    rig_ids = {r["instanceId"] for r in job["rigs"]}
    for index, item in enumerate(job["design"]["instances"]):
        local = worlds[item["parentId"]].inverted() @ worlds[item["id"]]
        for kind in (("instance", "rig") if item["id"] in rig_ids else ("instance",)):
            obj = bpy.data.objects[name(kind, index)]
            obj.matrix_parent_inverse = Matrix.Identity(4)
            author_rigid_basis(obj, local)
    bpy.context.view_layer.update()


def ring_indices(part, prototype_vertices, rig, tolerance):
    authored, _ = feature_mesh(part["features"][0])
    radial, count = rig["radialSegments"], len(rig["restRingFrames"])
    require(len(authored) == len(prototype_vertices) == radial * count, "Sweep mesh ring correspondence is incomplete")
    # Canonical prototypes retain float32 Blender positions. Quantized spatial
    # buckets bound correspondence work, and all 27 neighbors handle cell edges.
    buckets = {}
    def cell(v):
        return tuple(math.floor(c / tolerance) for c in v)
    for index, value in enumerate(prototype_vertices):
        buckets.setdefault(cell(value), []).append(index)
    rings, used = [], set()
    for ring in range(count):
        indices = []
        for expected in authored[ring*radial:(ring+1)*radial]:
            key = cell(expected)
            candidates = []
            for dx in (-1, 0, 1):
                for dy in (-1, 0, 1):
                    for dz in (-1, 0, 1):
                        for candidate in buckets.get((key[0]+dx, key[1]+dy, key[2]+dz), []):
                            if sum((a-b)**2 for a, b in zip(prototype_vertices[candidate], expected)) <= tolerance*tolerance:
                                candidates.append(candidate)
            require(len(candidates) == 1 and candidates[0] not in used, "Ambiguous or missing source sweep vertex")
            used.add(candidates[0])
            indices.append(candidates[0])
        center = tuple(sum(prototype_vertices[i][k] for i in indices)/radial for k in range(3))
        require(sum((center[k]-rig["restRingFrames"][ring]["origin"]["xyz"[k]])**2 for k in range(3)) <= tolerance*tolerance, "Rest ring frame is detached from actual source ring")
        rings.append(sorted(indices))
    return rings


def create_rig(job, bpy, index, obj, part, rig, vertices):
    from mathutils import Vector
    armature = bpy.data.armatures.new(name("armature", index))
    arm = bpy.data.objects.new(name("rig", index), armature)
    bpy.context.scene.collection.objects.link(arm)
    arm.parent = obj.parent
    arm["bubo.owner"] = obj["bubo.instanceId"]
    arm["bubo.rig"] = stable_json(rig)
    armature.pose_position = "POSE"
    bpy.context.view_layer.objects.active = arm
    arm.select_set(True)
    bpy.ops.object.mode_set(mode="EDIT")
    root = armature.edit_bones.new("root")
    root.head, root.tail = (0, 0, 0), (0, rig["boneLength"], 0)
    root.use_deform = False
    for ring, frame in enumerate(rig["restRingFrames"]):
        bone = armature.edit_bones.new(name("ring", ring))
        bone.head = xyz(frame["origin"])
        bone.tail = Vector(bone.head) + Vector(xyz(frame["y"])) * rig["boneLength"]
        bone.align_roll(Vector(xyz(frame["z"])))
        bone.parent = root
        bone.use_connect = False
        bone.use_deform = True
    bpy.ops.object.mode_set(mode="OBJECT")
    arm.select_set(False)
    rings = ring_indices(part, vertices, rig, job["design"]["numerics"]["linearTolerance"])
    for ring, indices in enumerate(rings):
        group = obj.vertex_groups.new(name=name("ring", ring))
        group.add(indices, 1, "REPLACE")
    modifier = obj.modifiers.new("bubo.owned-skin", "ARMATURE")
    modifier.object = arm
    modifier.use_vertex_groups = True
    modifier.use_bone_envelopes = False
    modifier.use_deform_preserve_volume = False
    modifier.show_viewport = True
    modifier.show_render = True
    obj["bubo.ringVertexIndices"] = stable_json(rings)
    return arm


def create(job, bpy):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.unit_settings.system, scene.unit_settings.scale_length = "METRIC", 1
    for key in ("design", "plan", "rigs", "identity"):
        scene["bubo." + key] = stable_json(job[key])
    by_id, meshes = {}, {}
    material_defs = {item["id"]: item for item in job["design"]["materials"]}
    materials = {key: create_material(bpy, item) for key, item in material_defs.items()}
    for index, item in enumerate(job["design"]["subassemblies"]):
        obj = bpy.data.objects.new(name("group", index), None)
        scene.collection.objects.link(obj)
        obj["bubo.groupId"], obj["bubo.group"] = item["id"], stable_json(item)
        by_id[item["id"]] = obj
    definitions = {item["id"]: item for item in job["design"]["parts"]}
    prototypes = {item["id"]: item for item in job["parts"]["parts"]}
    rigs = {item["instanceId"]: item for item in job["rigs"]}
    for index, item in enumerate(job["design"]["instances"]):
        part, proto = definitions[item["partId"]], prototypes[item["partId"]]
        vertices, triangles = read_geometry(Path(job["partsDirectory"]) / proto["geometryFile"], job["limits"])
        if part["id"] not in meshes or item["id"] in rigs:
            mesh = bpy.data.meshes.new(name("mesh", index))
            mesh.from_pydata(vertices, [], triangles)
            mesh.update()
            mesh.materials.append(materials[part["materialId"]])
            if item["id"] not in rigs:
                meshes[part["id"]] = mesh
        else:
            mesh = meshes[part["id"]]
        obj = bpy.data.objects.new(name("instance", index), mesh)
        scene.collection.objects.link(obj)
        for key, value in (("instanceId", item["id"]), ("partId", part["id"]), ("instance", stable_json(item)), ("prototypeBlendSha256", proto["blendSha256"]), ("prototypeGeometrySha256", proto["geometrySha256"]), ("definitionSha256", proto["definitionSha256"]), ("joints", stable_json([j for j in job["design"]["joints"] if item["id"] in (j["parentInstanceId"], j["childInstanceId"])]))):
            obj["bubo."+key] = value
        by_id[item["id"]] = obj
        for kind, ports in (("interface", part["interfaces"]), ("terminal", part["terminals"])):
            for port_index, port in enumerate(ports):
                physical = bpy.data.objects.new(name(kind, index, port_index), None)
                scene.collection.objects.link(physical)
                physical.parent = obj
                author_rigid_basis(physical, frame_matrix(port["frame"]))
                physical["bubo.owner"], physical["bubo.port"], physical["bubo.kind"] = item["id"], stable_json(port), kind
                physical.hide_render = True
    for item in [*job["design"]["subassemblies"], *job["design"]["instances"]]:
        by_id[item["id"]].parent = by_id[item["parentId"]] if item["parentId"] else None
    for index, item in enumerate(job["design"]["instances"]):
        if item["id"] in rigs:
            proto = prototypes[item["partId"]]
            vertices, _ = read_geometry(Path(job["partsDirectory"]) / proto["geometryFile"], job["limits"])
            create_rig(job, bpy, index, by_id[item["id"]], definitions[item["partId"]], rigs[item["id"]], vertices)
    set_pose(job, bpy, job["plan"]["rest"]["instances"])


def local_geometry(obj, bpy, limits):
    evaluated = obj.evaluated_get(bpy.context.evaluated_depsgraph_get())
    mesh = evaluated.to_mesh()
    try:
        require(4 <= len(mesh.vertices) <= limits["maxVertices"], "Evaluated local vertex budget")
        require(sum(len(p.vertices)-2 for p in mesh.polygons) <= limits["maxTriangles"], "Evaluated local triangle budget")
        mesh.calc_loop_triangles()
        original = [tuple(float(c) for c in v.co) for v in mesh.vertices]
        require(all(math.isfinite(c) for v in original for c in v) and len(set(original)) == len(original), "Nonfinite/coincident deformed geometry")
        # Armature deformation preserves topology and vertex indices. Retain
        # those exact indices for native independent skin/ring/LBS checks.
        triangles = [tuple(t.vertices) for t in mesh.loop_triangles]
        obj.data.calc_loop_triangles()
        require(triangles == [tuple(t.vertices) for t in obj.data.loop_triangles], "Owned skin changed approved prototype topology")
        measurements(original, triangles)
        return original, triangles, original
    finally:
        evaluated.to_mesh_clear()


def active_skin(obj, arm):
    """Reject inactive rigs before geometry evaluation, including rest poses."""
    require(len(obj.modifiers) == 1 and obj.modifiers[0].type == "ARMATURE" and obj.modifiers[0].object == arm and obj.modifiers[0].use_vertex_groups and not obj.modifiers[0].use_bone_envelopes and not obj.modifiers[0].use_deform_preserve_volume and obj.modifiers[0].show_viewport and obj.modifiers[0].show_render, "Active actual owned armature modifier required")
    require(arm.data.pose_position == "POSE" and not arm.hide_get() and not arm.hide_render, "Owned armature must remain active in pose mode")
    require(not arm.data.bones["root"].use_deform, "Owned root must not weight/deform vertices")
    require(all(b.use_deform for b in arm.data.bones if b.name != "root"), "Root/ring bone active deformation flags changed")


def skin_measurement(job, bpy, index, obj, rig, original):
    arm = bpy.data.objects[name("rig", index)]
    rings = json.loads(obj["bubo.ringVertexIndices"])
    require(len(arm.data.bones) == len(rings)+1 and len(obj.vertex_groups) == len(rings), "Owned skin bone/group inventory mismatch")
    active_skin(obj, arm)
    require(arm.parent == obj.parent and arm.data.users == 1 and obj.data.users == 1, "Flexible mesh and rig must be independently owned")
    owners = {}
    for ring, indices in enumerate(rings):
        require(obj.vertex_groups[ring].name == name("ring", ring), "Stable ring group name required")
        for vertex in indices:
            require(vertex not in owners and 0 <= vertex < len(obj.data.vertices), "Invalid ring skin correspondence")
            owners[vertex] = ring
    require(len(owners) == len(original), "Every actual vertex must belong to one ring")
    weights = bytearray(struct.pack("<8sI", b"BUBOSKN1", len(original)))
    for vertex in obj.data.vertices:
        require(len(vertex.groups) == 1 and vertex.groups[0].group == owners[vertex.index] and vertex.groups[0].weight == 1.0, "One-hot actual ring skin weights required")
        weights.extend(struct.pack("<Id", owners[vertex.index], 1.0))
    centers = [tuple(sum(original[i][k] for i in ring)/len(ring) for k in range(3)) for ring in rings]
    lengths = [math.dist(centers[i], centers[(i+1)%len(centers)]) for i in range(len(centers) if rig["closedPath"] else len(centers)-1)]
    angles = []
    for i in range(len(centers)):
        if not rig["closedPath"] and i in (0, len(centers)-1):
            angles.append(0.0)
            continue
        before = tuple(centers[i][k]-centers[i-1][k] for k in range(3))
        after = tuple(centers[(i+1)%len(centers)][k]-centers[i][k] for k in range(3))
        denominator = math.sqrt(sum(c*c for c in before)*sum(c*c for c in after))
        require(denominator > 1e-20, "Collapsed deformed ring segment")
        angles.append(math.degrees(math.acos(max(-1, min(1, sum(a*b for a,b in zip(before,after))/denominator)))))
    bones = []
    for bone_index, bone_name in enumerate(["root"]+[name("ring", i) for i in range(len(rings))]):
        bone, posed = arm.data.bones[bone_name], arm.pose.bones[bone_name]
        require(not posed.constraints and (bone.parent.name if bone.parent else None) == ("root" if bone_index else None), "Unexpected owned bone behavior/hierarchy")
        require(bone.use_deform == (bone_index != 0), "Root/ring bone active deformation flags changed")
        require(tuple(posed.scale) == (1,1,1), "Exact identity pose-bone scale required")
        bones.append({"name": bone_name, "parentName": bone.parent.name if bone.parent else None, "restPose": pose_of(bone.matrix_local), "pose": pose_of(posed.matrix), "length": float(bone.length)})
    return {"armatureName": arm.name, "armatureLocalPose": pose_of(arm.matrix_local), "armatureWorldPose": pose_of(arm.matrix_world), "bones": bones, "ringVertexIndices": rings, "weightsSha256": hashlib.sha256(weights).hexdigest(), "ringCenters": [vec(v) for v in centers], "segmentLengths": lengths, "bendAnglesDegrees": angles}


def measure_instances(job, bpy, prefix, verify=False):
    result, nv, nt = [], 0, 0
    definitions = {p["id"]: p for p in job["design"]["parts"]}
    protos = {p["id"]: p for p in job["parts"]["parts"]}
    rigs = {r["instanceId"]: r for r in job["rigs"]}
    for index, item in enumerate(job["design"]["instances"]):
        obj, part, proto = bpy.data.objects[name("instance", index)], definitions[item["partId"]], protos[item["partId"]]
        vertices, triangles, original = local_geometry(obj, bpy, job["limits"])
        nv, nt = nv+len(vertices), nt+len(triangles)
        require(nv <= job["limits"]["maxVertices"] and nt <= min(job["limits"]["maxTriangles"], job["design"]["budgets"]["maxGeneratedTriangles"]), "Cumulative per-pose physical geometry budget")
        geometry_file = prefix + "." + str(index).zfill(6) + ".geometry.bin"
        data, path = geometry_bytes(vertices, triangles), Path(job["output"]) / geometry_file
        if verify:
            require(path.is_file() and path.read_bytes() == data, "Independent actual geometry differs from binary payload")
        else:
            no_links(Path(job["output"]))
            current = list(Path(job["output"]).iterdir())
            require(len(current)+3 <= job["limits"]["maxFiles"] and sum(p.stat().st_size for p in current)+len(data) <= job["limits"]["maxOutputBytes"], "Incremental geometry output file/byte budget")
            write_new(path, data)
        material = next(m for m in job["design"]["materials"] if m["id"] == part["materialId"])
        measured_material = material_measurements(obj, material)
        ports = []
        for kind, declarations in (("interface", part["interfaces"]), ("terminal", part["terminals"])):
            for port_index, port in enumerate(declarations):
                physical = bpy.data.objects[name(kind, index, port_index)]
                ports.append({"portId": port["id"], "kind": kind, "objectName": physical.name, "localPose": pose_of(physical.matrix_local), "worldPose": pose_of(physical.matrix_world)})
        result.append({"instanceId": item["id"], "partId": part["id"], "objectName": obj.name, "parentId": item["parentId"], "localPose": pose_of(obj.matrix_local), "worldPose": pose_of(obj.matrix_world), "materialId": part["materialId"], "materialColorLinear": measured_material["materialColorLinear"], "materialRoughness": measured_material["materialRoughness"], "materialMetallic": measured_material["materialMetallic"], "ports": ports, "prototypeBlendSha256": proto["blendSha256"], "prototypeGeometrySha256": proto["geometrySha256"], "geometryFile": geometry_file, "geometrySha256": hashlib.sha256(data).hexdigest(), "vertexCount": len(vertices), "triangleCount": len(triangles), "rig": skin_measurement(job, bpy, index, obj, rigs[item["id"]], original) if item["id"] in rigs else None})
    return result


def inventory(job, bpy):
    from mathutils import Matrix
    require(not bpy.data.libraries and not bpy.data.texts and not bpy.app.handlers.frame_change_pre and not bpy.app.handlers.frame_change_post, "External code or callback forbidden")
    require(not bpy.data.images and not bpy.data.sounds and not bpy.data.movieclips and not bpy.data.cache_files, "External media dependencies forbidden")
    require(len(bpy.data.scenes) == 1 and not bpy.data.collections, "Exact single-scene assembly collection inventory required")
    expected_materials = {m["id"] for m in job["design"]["materials"]}
    require(len(bpy.data.materials) == len(expected_materials) and {m.get("bubo.materialId") for m in bpy.data.materials} == expected_materials, "Unexpected material datablock inventory")
    definition_kinds = {p["id"]:p["kind"] for p in job["design"]["parts"]}
    mesh_count = len({i["partId"] for i in job["design"]["instances"] if definition_kinds[i["partId"]] == "rigid"})+len(job["rigs"])
    require(len(bpy.data.meshes) == mesh_count and len(bpy.data.armatures) == len(job["rigs"]), "Unexpected mesh/armature datablock inventory")
    for block in [*bpy.data.meshes, *bpy.data.armatures, *bpy.data.materials, *bpy.data.scenes, *bpy.data.node_groups]:
        require(not block.animation_data, "Unexpected datablock animation/drivers forbidden")
    for material in bpy.data.materials:
        require(not material.node_tree or not material.node_tree.animation_data, "Material node drivers forbidden")
    for key in ("design", "plan", "rigs", "identity"):
        require(bpy.context.scene.get("bubo."+key) == stable_json(job[key]), "Assembly source ancestry changed")
    expected, by_id = set(), {}
    for kind, items, id_key in (("group", job["design"]["subassemblies"], "groupId"), ("instance", job["design"]["instances"], "instanceId")):
        for index, item in enumerate(items):
            obj = bpy.data.objects[name(kind, index)]
            expected.add(obj.name)
            require(obj.get("bubo."+id_key) == item["id"] and obj.type == ("MESH" if kind == "instance" else "EMPTY"), "Separate physical identity changed")
            require(obj.get("bubo."+("instance" if kind == "instance" else "group")) == stable_json(item), "Physical hierarchy ancestry changed")
            by_id[item["id"]] = obj
    definitions = {p["id"]: p for p in job["design"]["parts"]}
    prototypes = {p["id"]: p for p in job["parts"]["parts"]}
    rigs = {r["instanceId"]: r for r in job["rigs"]}
    for index, item in enumerate(job["design"]["instances"]):
        obj, part, proto = by_id[item["id"]], definitions[item["partId"]], prototypes[item["partId"]]
        require(obj.data.shape_keys is None, "Undeclared shape-key deformation forbidden")
        require(obj.parent == by_id[item["parentId"]] and not obj.hide_render and not obj.hide_get(), "Physical parent/visibility changed")
        for key in ("partId", "prototypeBlendSha256", "prototypeGeometrySha256", "definitionSha256"):
            expected_value = {"partId": part["id"], "prototypeBlendSha256": proto["blendSha256"], "prototypeGeometrySha256": proto["geometrySha256"], "definitionSha256": proto["definitionSha256"]}[key]
            require(obj.get("bubo."+key) == expected_value, "Independent prototype ancestry changed")
        require(obj.get("bubo.joints") == stable_json([j for j in job["design"]["joints"] if item["id"] in (j["parentInstanceId"], j["childInstanceId"])]), "Joint axis/pivot metadata changed")
        raw, faces = read_geometry(Path(job["partsDirectory"]) / proto["geometryFile"], job["limits"])
        require(len(obj.data.vertices) == len(raw) and all(math.dist(tuple(v.co), point) <= job["design"]["numerics"]["linearTolerance"] for v, point in zip(obj.data.vertices, raw)), "Physical rest mesh differs from independently verified prototype")
        obj.data.calc_loop_triangles()
        require([tuple(t.vertices) for t in obj.data.loop_triangles] == faces, "Prototype triangle topology changed")
        if item["id"] in rigs:
            arm = bpy.data.objects[name("rig", index)]
            expected.add(arm.name)
            require(arm.type == "ARMATURE" and arm.get("bubo.owner") == item["id"] and arm.get("bubo.rig") == stable_json(rigs[item["id"]]), "Owned rig identity changed")
            active_skin(obj, arm)
            require(json.loads(obj["bubo.ringVertexIndices"]) == ring_indices(part, raw, rigs[item["id"]], job["design"]["numerics"]["linearTolerance"]), "Actual ring ownership changed")
        else:
            require(not obj.modifiers and not obj.vertex_groups, "Rigid physical mesh acquired deformation")
        material_measurements(obj, next(m for m in job["design"]["materials"] if m["id"] == part["materialId"]))
        for kind, ports in (("interface", part["interfaces"]), ("terminal", part["terminals"])):
            for port_index, port in enumerate(ports):
                physical = bpy.data.objects[name(kind, index, port_index)]
                expected.add(physical.name)
                require(physical.type == "EMPTY" and physical.parent == obj and physical.get("bubo.owner") == item["id"] and physical.get("bubo.port") == stable_json(port) and physical.get("bubo.kind") == kind and physical.hide_render and not physical.animation_data, "Owned interface/terminal changed")
                near_matrix(physical.matrix_local, frame_matrix(port["frame"]), job["design"]["numerics"]["linearTolerance"], math.radians(job["design"]["numerics"]["angularToleranceDegrees"]))
    for item in job["design"]["subassemblies"]:
        require(by_id[item["id"]].parent == (by_id[item["parentId"]] if item["parentId"] else None), "Subassembly hierarchy changed")
    require(set(o.name for o in bpy.data.objects) == expected, "Unexpected/fused assembly object inventory")
    for obj in bpy.data.objects:
        require(not obj.library and not obj.is_instancer and obj.instance_type == "NONE", "Linked/instanced object dependencies forbidden")
        require(not obj.constraints and (not obj.animation_data or not obj.animation_data.drivers and not obj.animation_data.nla_tracks), "Constraints/drivers/NLA forbidden")
        near_matrix(obj.matrix_parent_inverse, Matrix.Identity(4), 1e-8)
        require(tuple(obj.scale) == (1,1,1) and tuple(obj.delta_location) == (0,0,0) and tuple(obj.delta_scale) == (1,1,1) and tuple(obj.delta_rotation_euler) == (0,0,0) and tuple(obj.delta_rotation_quaternion) == (1,0,0,0), "Exact identity scale/delta transforms required")
        if obj.type == "ARMATURE":
            require(all(tuple(b.scale) == (1,1,1) for b in obj.pose.bones), "Exact identity pose-bone scale required")


def receipt(job, job_sha, bpy, instances, clips):
    output = Path(job["output"])
    payloads = [p for p in output.iterdir() if p.suffix != ".json"]
    source_file = "assembly.blend" if job["operation"] == "assemble" else "diagnostic.blend"
    stage = "assembly" if job["operation"] == "assemble" else "motion"
    require({p.name for p in output.iterdir() if p.suffix == ".json"} <= {stage+"-report.json"}, "Unknown or preexisting assembly receipt forbidden")
    all_instances = instances + [i for c in clips for s in c["samples"] for i in s["instances"]]
    total = sum(p.stat().st_size for p in payloads)
    require(len(payloads)+2 <= job["limits"]["maxFiles"] and total <= job["limits"]["maxOutputBytes"], "Assembly payload file/byte budget")
    result = {"schemaVersion": "bubo-schematic-assembly/v1", "jobSha256": job_sha, "identity": job["identity"], "blenderVersion": bpy.app.version_string, "operation": job["operation"], "sourceFile": source_file, "sourceSha256": digest(output/source_file), "instances": instances, "clips": clips, "totalVertices": sum(i["vertexCount"] for i in all_instances), "totalTriangles": sum(i["triangleCount"] for i in all_instances), "totalBytes": total}
    encoded = report_bytes(result)
    require(len(encoded) <= 8388608 and total + 2*len(encoded) <= job["limits"]["maxOutputBytes"], "Cumulative two-report byte budget")
    expected_files = {source_file} | {i["geometryFile"] for i in all_instances}
    require({p.name for p in payloads} == expected_files, "Exact assembly payload inventory required")
    return result
