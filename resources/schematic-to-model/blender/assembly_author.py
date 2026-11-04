"""Audited Blender 4.5 assembly construction from verified part binaries."""
import sys
from pathlib import Path

sys.dont_write_bytecode = True
sys.path.insert(0,str(Path(__file__).resolve().parent))
from assembly_admission import admit, arguments, check_runtime, require
from assembly_geometry import create, inventory, measure_instances, receipt, write_report
from assembly_motion import author_actions, check_actions, check_pose, owners


def main():
    import bpy
    job_path,job_sha = arguments(sys.argv)
    job = admit(job_path,job_sha)
    require(job["operation"] == "assemble", "Assembly author requires assemble")
    check_runtime(job,bpy)
    create(job,bpy)
    author_actions(job,bpy)
    inventory(job,bpy)
    check_pose(job,bpy,job["plan"]["rest"])
    require(all(not o.animation_data or o.animation_data.action is None for _,o in owners(job,bpy)), "Canonical source actions must be disabled")
    instances,clips = measure_instances(job,bpy,"rest"),check_actions(job,bpy)
    output = Path(job["output"])
    bpy.context.preferences.filepaths.save_version = 0
    bpy.ops.wm.save_as_mainfile(filepath=str(output/"assembly.blend"),check_existing=False,compress=False)
    write_report(output/"assembly-report.json",receipt(job,job_sha,bpy,instances,clips))


if __name__ == "__main__":
    main()
