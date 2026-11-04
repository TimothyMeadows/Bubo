"""Fixed diagnostic action authoring and actual evaluated-skin sampling."""
import math
from pathlib import Path
import sys

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parent))
from assembly_admission import admit, arguments, check_runtime, digest, require
from assembly_geometry import (inventory, matrix, measure_instances, name,
                               near_matrix, receipt, set_pose, write_report)
from parts_geometry import stable_json


def owners(job, bpy):
    rigs = {r["instanceId"] for r in job["rigs"]}
    return [(item["id"], bpy.data.objects[name(kind, index)])
            for index, item in enumerate(job["design"]["instances"])
            for kind in (("instance", "rig") if item["id"] in rigs else ("instance",))]


def curves(action):
    require(len(action.layers) == 1 and len(action.slots) == 1, "One owned action layer and slot required")
    for layer in action.layers:
        require(len(layer.strips) == 1, "One bounded action strip required")
        for strip in layer.strips:
            require(strip.type == "KEYFRAME" and len(strip.channelbags) == 1, "Only owned keyframe action channels allowed")
            yield from strip.channelbags[0].fcurves


def assign(owner, action):
    owner.animation_data_create()
    owner.animation_data.use_nla = False
    owner.animation_data.action = action
    require(len(action.slots) == 1, "One action owner slot required")
    owner.animation_data.action_slot = action.slots[0]


def reset(job, bpy):
    from mathutils import Matrix
    for _, owner in owners(job, bpy):
        if owner.animation_data:
            owner.animation_data.action = None
            owner.animation_data.use_nla = False
        if owner.type == "ARMATURE":
            for bone in owner.pose.bones:
                bone.matrix_basis = Matrix.Identity(4)
                bone.scale = (1,1,1)
    set_pose(job, bpy, job["plan"]["rest"]["instances"])
    bpy.context.scene.frame_set(1)
    bpy.context.view_layer.update()


def set_rings(job, bpy, clip_id, sample_index):
    indices = {i["id"]: index for index, i in enumerate(job["design"]["instances"])}
    for rig in job["rigs"]:
        arm = bpy.data.objects[name("rig", indices[rig["instanceId"]])]
        sample = next(c for c in rig["clips"] if c["clipId"] == clip_id)["samples"][sample_index]
        for index, frame in enumerate(sample["ringFrames"]):
            bone = arm.pose.bones[name("ring", index)]
            bone.matrix = matrix(frame)
            bone.scale = (1,1,1)
            bone.rotation_quaternion.normalize()
    bpy.context.view_layer.update()


def author_actions(job, bpy):
    scene = bpy.context.scene
    scene.render.fps, scene.render.fps_base = 1000, 1
    for clip_index, clip in enumerate(job["plan"]["clips"]):
        reset(job, bpy)
        previous_rotations = {}
        for owner_index, (owner_id, owner) in enumerate(owners(job, bpy)):
            action = bpy.data.actions.new(name("action", clip_index, owner_index))
            action.use_fake_user = True
            action["bubo.ownerId"], action["bubo.ownerName"], action["bubo.clipId"] = owner_id, owner.name, clip["id"]
            action.slots.new(id_type="OBJECT", name=owner.name)
            assign(owner, action)
            owner.rotation_mode = "QUATERNION"
            if owner.type == "ARMATURE":
                for bone in owner.pose.bones:
                    bone.rotation_mode = "QUATERNION"
        for index, sample in enumerate(clip["samples"]):
            frame = 1+sample["timeSeconds"]*1000
            set_pose(job, bpy, sample["pose"]["instances"])
            set_rings(job, bpy, clip["id"], index)
            for _, owner in owners(job, bpy):
                quaternion = owner.rotation_quaternion.copy()
                if owner.name in previous_rotations and quaternion.dot(previous_rotations[owner.name]) < 0:
                    quaternion.negate()
                    owner.rotation_quaternion = quaternion
                previous_rotations[owner.name] = quaternion
                for path in ("location", "rotation_quaternion"):
                    owner.keyframe_insert(data_path=path, frame=frame)
                if owner.type == "ARMATURE":
                    for bone in owner.pose.bones:
                        if bone.name != "root":
                            key = owner.name+"/"+bone.name
                            quaternion = bone.rotation_quaternion.copy()
                            if key in previous_rotations and quaternion.dot(previous_rotations[key]) < 0:
                                quaternion.negate()
                                bone.rotation_quaternion = quaternion
                            previous_rotations[key] = quaternion
                            for path in ("location", "rotation_quaternion"):
                                bone.keyframe_insert(data_path=path, frame=frame)
        for _, owner in owners(job, bpy):
            for curve in curves(owner.animation_data.action):
                for key in curve.keyframe_points:
                    key.interpolation = "LINEAR"
    reset(job, bpy)


def action_records(job, bpy, clip_index):
    clip = job["plan"]["clips"][clip_index]
    result = []
    for owner_index, (owner_id, owner) in enumerate(owners(job, bpy)):
        action = bpy.data.actions[name("action", clip_index, owner_index)]
        require(action.get("bubo.ownerId") == owner_id and action.get("bubo.ownerName") == owner.name and action.get("bubo.clipId") == clip["id"] and action.use_fake_user, "Diagnostic action stable mapping changed")
        all_curves = list(curves(action))
        expected_paths = {("location", i) for i in range(3)} | {("rotation_quaternion", i) for i in range(4)}
        if owner.type == "ARMATURE":
            for bone in owner.pose.bones:
                if bone.name != "root":
                    expected_paths |= {(bone.path_from_id("location"), i) for i in range(3)} | {(bone.path_from_id("rotation_quaternion"), i) for i in range(4)}
        require(len(all_curves) == len(expected_paths) and {(c.data_path,c.array_index) for c in all_curves} == expected_paths, "Actual diagnostic transform/skin channels changed")
        for curve in all_curves:
            require(not curve.modifiers and len(curve.keyframe_points) == len(clip["samples"]), "Diagnostic channel key/modifier inventory changed")
            for key, sample in zip(curve.keyframe_points, clip["samples"]):
                require(abs(key.co.x-(1+1000*sample["timeSeconds"])) <= .0001 and math.isfinite(key.co.y) and key.interpolation == "LINEAR", "Diagnostic sample timing/interpolation changed")
        result.append({"ownerId": owner_id, "ownerName": owner.name, "actionName": action.name, "fcurveCount": len(all_curves), "keyframeCount": sum(len(c.keyframe_points) for c in all_curves)})
    return result


def check_actions(job, bpy):
    expected = {name("action", ci, oi) for ci in range(len(job["plan"]["clips"])) for oi in range(len(owners(job,bpy)))}
    require(set(a.name for a in bpy.data.actions) == expected, "Exact diagnostic action inventory required")
    return [{"id": c["id"], "actions": action_records(job,bpy,ci), "samples": []} for ci,c in enumerate(job["plan"]["clips"])]


def check_pose(job, bpy, expected, rig_clip=None, sample_index=None):
    from mathutils import Matrix
    tolerance = job["design"]["numerics"]["linearTolerance"]
    angular = math.radians(job["design"]["numerics"]["angularToleranceDegrees"])
    for index, item in enumerate(job["design"]["instances"]):
        near_matrix(bpy.data.objects[name("instance",index)].matrix_world, matrix(expected["instances"][item["id"]]), tolerance, angular)
    for rig in job["rigs"]:
        index = next(i for i,item in enumerate(job["design"]["instances"]) if item["id"] == rig["instanceId"])
        arm, obj = bpy.data.objects[name("rig",index)], bpy.data.objects[name("instance",index)]
        near_matrix(arm.matrix_world,obj.matrix_world,tolerance,angular)
        near_matrix(arm.data.bones["root"].matrix_local,Matrix.Identity(4),tolerance,angular)
        near_matrix(arm.pose.bones["root"].matrix,Matrix.Identity(4),tolerance,angular)
        require(not arm.data.bones["root"].use_deform, "Owned root must not weight/deform vertices")
        frames = rig["restRingFrames"] if rig_clip is None else next(c for c in rig["clips"] if c["clipId"] == rig_clip)["samples"][sample_index]["ringFrames"]
        for bone_index, frame in enumerate(frames):
            near_matrix(arm.pose.bones[name("ring",bone_index)].matrix,matrix(frame),tolerance,angular)
            near_matrix(arm.data.bones[name("ring",bone_index)].matrix_local,matrix(rig["restRingFrames"][bone_index]),tolerance,angular)


def sample_actions(job, bpy, verify=False):
    clips = check_actions(job,bpy)
    for clip_index, clip in enumerate(job["plan"]["clips"]):
        reset(job,bpy)
        for owner_index, (_, owner) in enumerate(owners(job,bpy)):
            assign(owner,bpy.data.actions[name("action",clip_index,owner_index)])
        for index, sample in enumerate(clip["samples"]):
            frame = 1+1000*sample["timeSeconds"]
            bpy.context.scene.frame_set(math.floor(frame),subframe=frame-math.floor(frame))
            bpy.context.view_layer.update()
            check_pose(job,bpy,sample["pose"],clip["id"],index)
            inventory(job,bpy)
            measured = measure_instances(job,bpy,"clip."+str(clip_index).zfill(6)+".sample."+str(index).zfill(6),verify)
            clips[clip_index]["samples"].append({"index":index,"timeSeconds":sample["timeSeconds"],"instances":measured})
    reset(job,bpy)
    return clips


def main():
    import bpy
    job_path, job_sha = arguments(sys.argv)
    job = admit(job_path,job_sha)
    require(job["operation"] == "prove-motion", "Motion worker requires prove-motion")
    check_runtime(job,bpy)
    source = Path(job["sourceAssembly"])
    bpy.ops.wm.open_mainfile(filepath=str(source),load_ui=False,use_scripts=False)
    check_runtime(job,bpy)
    inventory(job,bpy)
    check_actions(job,bpy)
    require(all(not owner.animation_data or owner.animation_data.action is None for _,owner in owners(job,bpy)), "Canonical assembly must retain disabled actions at rest")
    check_pose(job,bpy,job["plan"]["rest"])
    instances = measure_instances(job,bpy,"rest")
    clips = sample_actions(job,bpy)
    output = Path(job["output"])
    bpy.context.preferences.filepaths.save_version = 0
    bpy.ops.wm.save_as_mainfile(filepath=str(output/"diagnostic.blend"),check_existing=False,compress=False)
    require(digest(source) == job["sourceAssemblySha256"], "Motion proof changed its immutable source")
    write_report(output/"motion-report.json",receipt(job,job_sha,bpy,instances,clips))


if __name__ == "__main__":
    main()
