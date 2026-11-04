"""Independent saved-source reopen and actual diagnostic evaluation.

The verifier writes only its receipt; it never resaves or repairs payloads.
"""
import sys
from pathlib import Path

sys.dont_write_bytecode = True
sys.path.insert(0,str(Path(__file__).resolve().parent))
from assembly_admission import admit, arguments, check_runtime, digest, require, strict_json
from assembly_geometry import inventory, measure_instances, receipt, write_report
from assembly_motion import check_actions, check_pose, owners, sample_actions


def main():
    import bpy
    job_path,job_sha = arguments(sys.argv)
    job = admit(job_path,job_sha,verify=True)
    check_runtime(job,bpy)
    output = Path(job["output"])
    stage = "assembly" if job["operation"] == "assemble" else "motion"
    source = output/("assembly.blend" if stage == "assembly" else "diagnostic.blend")
    report_path = output/(stage+"-report.json")
    require(report_path.stat().st_size <= 8388608,"Independent assembly report byte budget")
    report = strict_json(report_path.read_bytes())
    before = {p.name:digest(p) for p in output.iterdir()}
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.wm.open_mainfile(filepath=str(source),load_ui=False,use_scripts=False)
    check_runtime(job,bpy)
    inventory(job,bpy)
    require(all(not o.animation_data or o.animation_data.action is None for _,o in owners(job,bpy)),"Saved canonical source must retain rest with actions disabled")
    check_pose(job,bpy,job["plan"]["rest"])
    instances = measure_instances(job,bpy,"rest",verify=True)
    clips = check_actions(job,bpy) if stage == "assembly" else sample_actions(job,bpy,verify=True)
    verified = receipt(job,job_sha,bpy,instances,clips)
    require(verified == report,"Independent complete actual assembly report differs from author receipt")
    require({p.name:digest(p) for p in output.iterdir()} == before,"Independent verifier changed immutable assembly payload")
    write_report(output/(stage+"-verification.json"),verified)


if __name__ == "__main__":
    main()
