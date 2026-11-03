"""Fresh-process independent reopen, primitive/boolean replay and measurement.

This verifier never resaves the authoring source or modifies a payload file.
"""
import sys
from pathlib import Path

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parent))
from parts_admission import admit, arguments, check_runtime, digest, require, strict_json
from parts_geometry import GeometryWork, canonical_geometry, independently_replay, measured_part, reject_self_intersections, report, report_bytes, semantic_inventory, write_report


def main():
    import bpy
    job_path, job_sha = arguments(sys.argv)
    job = admit(job_path, job_sha, verify=True)
    check_runtime(job, bpy)
    output = Path(job["output"])
    work = GeometryWork(job)
    require((output / "parts-report.json").stat().st_size <= 8388608, "Independent source report byte budget")
    source = strict_json((output / "parts-report.json").read_bytes())
    parts = []
    for part in job["design"]["parts"]:
        blend_path, geometry_path = (output / (part["id"] + suffix) for suffix in (".blend", ".geometry.bin"))
        blend_sha, geometry_sha = digest(blend_path), digest(geometry_path)
        bpy.ops.wm.read_factory_settings(use_empty=True)
        bpy.ops.wm.open_mainfile(filepath=str(blend_path), load_ui=False, use_scripts=False)
        bpy.context.view_layer.update()
        check_runtime(job, bpy)
        physical = semantic_inventory(bpy, part, job["partIdentities"][part["id"]], work)
        independently_replay(bpy, part, physical)
        reject_self_intersections(*canonical_geometry(physical, bpy), work)
        measured, actual = measured_part(job, part, physical, bpy, blend_path, geometry_path)
        require(actual == geometry_path.read_bytes(), "Independent evaluated source differs from native geometry payload")
        require(digest(blend_path) == blend_sha and digest(geometry_path) == geometry_sha, "Verifier changed immutable part payload")
        parts.append(measured)
    verified = report(job, job_sha, parts, bpy)
    require(source == verified, "Independent complete part report differs from author receipt")
    require(verified["totalBytes"] + (output / "parts-report.json").stat().st_size + len(report_bytes(verified)) <= job["limits"]["maxOutputBytes"], "Cumulative independent output byte budget")
    write_report(output / "parts-verification.json", verified)


if __name__ == "__main__":
    main()
