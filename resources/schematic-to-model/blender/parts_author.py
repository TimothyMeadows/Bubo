"""Fixed Blender 4.5 independent-definition prototype producer."""
import sys
from pathlib import Path

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parent))
from parts_admission import admit, arguments, check_runtime, no_links, require
from parts_geometry import GeometryWork, canonical_geometry, create_part, geometry_bytes, measured_part, measurements, reject_self_intersections, report, report_bytes, write_new, write_report


def main():
    import bpy
    job_path, job_sha = arguments(sys.argv)
    job = admit(job_path, job_sha)
    check_runtime(job, bpy)
    output = Path(job["output"])
    work = GeometryWork(job)
    parts, vertices_used, triangles_used, bytes_used = [], 0, 0, 0
    materials = {item["id"]: item for item in job["design"]["materials"]}
    quantities = {}
    for instance in job["design"]["instances"]:
        quantities[instance["partId"]] = quantities.get(instance["partId"], 0) + 1
    weighted = 0
    for part in job["design"]["parts"]:
        physical = create_part(bpy, part, materials[part["materialId"]], job["partIdentities"][part["id"]], work)
        vertices, triangles = canonical_geometry(physical, bpy, job["limits"]["maxVertices"] - vertices_used, job["limits"]["maxTriangles"] - triangles_used)
        measurements(vertices, triangles)
        reject_self_intersections(vertices, triangles, work)
        vertices_used += len(vertices)
        triangles_used += len(triangles)
        weighted += len(triangles) * quantities.get(part["id"], 0)
        require(vertices_used <= job["limits"]["maxVertices"] and triangles_used <= job["limits"]["maxTriangles"] and weighted <= min(job["limits"]["maxTriangles"], job["design"]["budgets"]["maxGeneratedTriangles"]), "Cumulative evaluated geometry/occurrence budget")
        blend_path, geometry_path = (output / (part["id"] + suffix) for suffix in (".blend", ".geometry.bin"))
        no_links(output)
        require(not blend_path.exists() and not geometry_path.exists(), "Immutable part output already exists")
        data = geometry_bytes(vertices, triangles)
        require(bytes_used + len(data) <= job["limits"]["maxOutputBytes"], "Geometry output byte budget")
        write_new(geometry_path, data)
        bpy.context.preferences.filepaths.save_version = 0
        bpy.ops.wm.save_as_mainfile(filepath=str(blend_path), check_existing=False, compress=False)
        require(blend_path.is_file(), "Blender did not save the editable part")
        bytes_used += blend_path.stat().st_size + len(data)
        require(bytes_used <= job["limits"]["maxOutputBytes"], "Cumulative editable-source byte budget")
        measured, actual = measured_part(job, part, physical, bpy, blend_path, geometry_path)
        require(actual == data, "Geometry changed during source persistence")
        parts.append(measured)
    receipt = report(job, job_sha, parts, bpy)
    require(len(report_bytes(receipt)) <= 8388608 and bytes_used + 2 * len(report_bytes(receipt)) <= job["limits"]["maxOutputBytes"], "Cumulative payload and two-report byte budget")
    write_report(output / "parts-report.json", receipt)


if __name__ == "__main__":
    main()
