"""Complete diagnostic CPU views of the delivered, independently verified GLB.

Sections clip actual imported triangles against recorded world-space planes.
They never decide visibility from an object's center. Clipping is render-only,
leaves section edges open, and never writes back to the package or source.
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
FIXED_BUNDLE = {"admission.py", "review.py", "export.py", "export_admission.py", "export_geometry.py", "export_views.py"}
HASH = re.compile(r"[0-9a-f]{64}\Z")


def _pairs(rows):
    result = {}
    for key, value in rows:
        if key in result:
            raise ValueError("Duplicate view bootstrap JSON property")
        result[key] = value
    return result


def _path(value):
    path = Path(value)
    normalized = str(value).replace("\\", "/")
    if not path.is_absolute() or normalized.startswith("//") or any(part.lower() in (".", "..", ".git") for part in normalized.split("/")):
        raise ValueError("View bootstrap requires absolute local paths")
    current = Path(path.anchor)
    for component in path.parts[1:]:
        if component.lower() in (".", "..", ".git") or component.endswith((".", " ")) or ":" in component:
            raise ValueError("Ambiguous view bootstrap path")
        current /= component
        info = current.lstat()
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise ValueError("Linked/reparse view bootstrap path")
    if not path.is_file():
        raise ValueError("View bootstrap requires regular file")
    return path.resolve(strict=True)


def bootstrap(arguments):
    if len(arguments) != 2 or not isinstance(arguments[1], str) or HASH.fullmatch(arguments[1]) is None:
        raise ValueError("Usage: export_views.py -- ABSOLUTE_JOB_JSON EXPECTED_JOB_SHA256")
    source = _path(arguments[0])
    if not 2 <= source.stat().st_size <= 1024**2:
        raise ValueError("Missing/oversized package-view job")
    with source.open("rb") as handle:
        raw = handle.read(1024**2 + 1)
    if len(raw) > 1024**2 or hashlib.sha256(raw).hexdigest() != arguments[1]:
        raise ValueError("Package-view job bytes changed")
    job = json.loads(raw.decode("utf-8"), object_pairs_hook=_pairs,
                     parse_constant=lambda value: (_ for _ in ()).throw(ValueError("Nonfinite view JSON")))
    if not isinstance(job, dict) or not isinstance(job.get("bundle"), dict) or set(job["bundle"]) != FIXED_BUNDLE:
        raise ValueError("Exact fixed six-resource view bundle required")
    directory = _path(__file__).parent
    for name, expected in job["bundle"].items():
        resource = _path(directory / name)
        if not isinstance(expected, str) or HASH.fullmatch(expected) is None or resource.stat().st_size > 2 * 1024**2 or hashlib.sha256(resource.read_bytes()).hexdigest() != expected:
            raise ValueError("Fixed view bundle changed: " + name)
    spec = importlib.util.spec_from_file_location("_bubo_floor_plan_export", directory / "export.py")
    if spec is None or spec.loader is None:
        raise ValueError("Fixed export adapter unavailable")
    exporter = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = exporter
    spec.loader.exec_module(exporter)
    admission, admitted = exporter.bootstrap(arguments, "export_views.py")
    return exporter, admission, admitted


def _point(value):
    if isinstance(value, dict):
        return tuple(float(value[key]) for key in ("x", "y", "z"))
    return tuple(float(component) for component in value)


def view_specs(plan, scene):
    """Exact source-derived inventory; all cameras and clip planes are explicit."""
    low, high = _point(scene["metrics"]["minimum"]), _point(scene["metrics"]["maximum"])
    center = tuple((a + b) / 2 for a, b in zip(low, high))
    rows = [{"kind": "exterior-" + name, "sourceId": None, "direction": direction, "clipPlanes": []}
            for name, direction in (("north", [0, 1, 0]), ("east", [1, 0, 0]), ("south", [0, -1, 0]), ("west", [-1, 0, 0]))]
    rows.append({"kind": "roof-plan", "sourceId": None, "direction": [0, 0, 1], "clipPlanes": []})
    for floor in sorted(plan["storeys"], key=lambda row: (row["elevation"], row["id"])):
        cut = floor["elevation"] + min(1.2, floor["height"] / 2)
        bottom = floor["elevation"] - floor["slabThickness"]
        rows.append({"kind": "floor-plan", "sourceId": floor["id"], "direction": [0, 0, 1],
                     "clipPlanes": [{"point": [0, 0, cut], "normal": [0, 0, 1]},
                                    {"point": [0, 0, bottom], "normal": [0, 0, -1]}]})
    rows.append({"kind": "principal-section", "sourceId": None, "direction": [0, -1, 0],
                 "clipPlanes": [{"point": list(center), "normal": [0, -1, 0]}]})
    for stair in sorted(plan["stairs"], key=lambda row: row["id"]):
        start, end = stair["start"], stair["end"]
        dx, dy = end["x"] - start["x"], end["y"] - start["y"]
        length = math.hypot(dx, dy)
        if not math.isfinite(length) or length <= 0:
            raise ValueError("Stair section requires a finite nonzero source run")
        normal = [-dy / length, dx / length, 0]
        rows.append({"kind": "stair-section", "sourceId": stair["id"], "direction": normal,
                     "clipPlanes": [{"point": [(start["x"] + end["x"]) / 2, (start["y"] + end["y"]) / 2, 0], "normal": normal}]})
    if len(rows) != len(plan["storeys"]) + len(plan["stairs"]) + 6:
        raise ValueError("Incomplete required package view inventory")
    return rows


def clip_polygon(points, plane):
    """Retain the closed negative half-space; true plane/edge intersections."""
    if not points:
        return []
    origin, normal = plane["point"], plane["normal"]
    def distance(point):
        return math.fsum((point[i] - origin[i]) * normal[i] for i in range(3))
    result = []
    previous, previous_distance = points[-1], distance(points[-1])
    for current in points:
        current_distance = distance(current)
        if (previous_distance <= 0) != (current_distance <= 0):
            fraction = previous_distance / (previous_distance - current_distance)
            intersection = tuple(previous[i] + fraction * (current[i] - previous[i]) for i in range(3))
            if not all(math.isfinite(value) for value in intersection):
                raise ValueError("Nonfinite clipping intersection")
            result.append(intersection)
        if current_distance <= 0:
            result.append(tuple(current))
        previous, previous_distance = current, current_distance
    # Exact endpoint crossings can duplicate a polygon endpoint. Remove only
    # identical adjacent positions; no welding/epsilon/simplification policy.
    clean = []
    for point in result:
        if not clean or point != clean[-1]:
            clean.append(point)
    if len(clean) > 1 and clean[0] == clean[-1]:
        clean.pop()
    return clean


def _capture(bpy, admitted, admission):
    result = []
    count = 0
    for item in sorted((item for item in bpy.data.objects if item.type == "MESH"), key=lambda item: item.get("bubo_id", "")):
        data = item.data
        admission.remaining_limits(admitted, "view source capture entry")
        admission.charge_work(admitted, len(data.vertices) + len(data.polygons), "view source geometry capture")
        data.calc_loop_triangles()
        vertices = [tuple(float(value) for value in item.matrix_world @ vertex.co) for vertex in data.vertices]
        triangles = [(tuple(vertices[index] for index in triangle.vertices), data.polygons[triangle.polygon_index].material_index) for triangle in data.loop_triangles]
        count += len(triangles)
        if count > admitted.job["limits"]["maxTriangles"]:
            raise ValueError("Imported view triangles exceed approved allowance")
        result.append({"id": item.get("bubo_id"), "storeyId": item.get("bubo_storeyId"), "triangles": triangles,
                       "materials": list(data.materials), "item": item,
                       "minimum": tuple(min(point[i] for point in vertices) for i in range(3)),
                       "maximum": tuple(max(point[i] for point in vertices) for i in range(3))})
        item.hide_render = True
    return result


def _clipped_view(bpy, admitted, captured, spec, work):
    cap = admitted.job["limits"]["maxGeometryChecks"]
    def charge(count):
        if type(work[0]) is not int or work[0] < 0 or work[0] + count > cap:
            raise ValueError("Complete-view cumulative geometry work exceeded approved cap")
        work[0] += count
    if type(work[0]) is not int or not 0 <= work[0] < cap:
        raise ValueError("Required view has no remaining geometry-work allowance")
    charge(1)  # Every required view enters the shared allowance, even no-plane views.
    vertices_total = triangles_total = intersections = 0
    generated, visible = [], []
    for source in captured:
        vertices, faces, slots = [], [], []
        for triangle, material in source["triangles"]:
            charge(1)  # Cardinal/roof triangle copies cannot bypass accounting.
            polygon = list(triangle)
            for plane in spec["clipPlanes"]:
                charge(len(polygon))
                before = polygon
                polygon = clip_polygon(polygon, plane)
                intersections += sum(point not in before for point in polygon)
            if len(polygon) < 3:
                continue
            base = len(vertices)
            vertices.extend(polygon)
            for index in range(1, len(polygon) - 1):
                a, b, c = polygon[0], polygon[index], polygon[index + 1]
                ab, ac = [b[i]-a[i] for i in range(3)], [c[i]-a[i] for i in range(3)]
                cross = (ab[1]*ac[2]-ab[2]*ac[1], ab[2]*ac[0]-ab[0]*ac[2], ab[0]*ac[1]-ab[1]*ac[0])
                if not any(cross):
                    continue
                faces.append((base, base + index, base + index + 1)); slots.append(material)
        vertices_total += len(vertices); triangles_total += len(faces)
        if vertices_total > admitted.job["limits"]["maxVertices"] or triangles_total > admitted.job["limits"]["maxTriangles"]:
            raise ValueError("Actual clipped-view geometry exceeded approved caps")
        if not faces:
            continue
        name = "diagnostic-" + hashlib.sha256(source["id"].encode()).hexdigest()[:24]
        data = bpy.data.meshes.new(name)
        data.from_pydata(vertices, [], faces)
        data.update()
        for material in source["materials"]:
            data.materials.append(material)
        for polygon, material in zip(data.polygons, slots):
            polygon.material_index = material
        item = bpy.data.objects.new(name, data)
        bpy.context.scene.collection.objects.link(item)
        generated.append(item)
        visible.append(source["id"])
    if not generated:
        raise ValueError("Required source-derived clipped view contains no geometry")
    return generated, visible, {"vertices": vertices_total, "triangles": triangles_total, "planeIntersections": intersections}


def _bounds(captured, spec):
    active = captured
    if spec["kind"] == "floor-plan":
        active = [row for row in captured if row["storeyId"] == spec["sourceId"]]
    if not active:
        raise ValueError("Required view source inventory is empty")
    return tuple(min(row["minimum"][i] for row in active) for i in range(3)), tuple(max(row["maximum"][i] for row in active) for i in range(3))


def _configure(bpy, admitted, captured):
    scene = bpy.context.scene
    admitted.review.fixed_render_policy(scene, apply=True)
    scene.render.engine = "CYCLES"
    scene.cycles.device = "CPU"
    scene.cycles.samples = 8
    scene.cycles.use_adaptive_sampling = False
    scene.cycles.use_denoising = False
    scene.render.threads_mode = "FIXED"
    scene.render.threads = 2
    scene.render.resolution_x = scene.render.resolution_y = admitted.job["limits"]["imageSize"]
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = "PNG"
    scene.render.image_settings.color_mode = "RGB"
    scene.render.image_settings.color_depth = "8"
    scene.render.use_file_extension = True
    scene.render.film_transparent = False
    scene.render.use_stamp = False
    scene.view_settings.view_transform = "Standard"
    scene.view_settings.look = "None"
    scene.view_settings.exposure = 0
    scene.view_settings.gamma = 1
    world = bpy.data.worlds.new("package-qa-world")
    world.use_nodes = True
    world.node_tree.nodes["Background"].inputs["Color"].default_value = (.8, .8, .8, 1)
    world.node_tree.nodes["Background"].inputs["Strength"].default_value = .8
    scene.world = world
    low, high = _bounds(captured, {"kind": "roof-plan"})
    center = tuple((a+b)/2 for a, b in zip(low, high))
    distance = max(max(b-a for a, b in zip(low, high)), 1)
    for index, offset in enumerate(((-1.5, -1.5, 2), (1.5, .5, 1.5), (0, 1.5, 1.5))):
        position = tuple(center[i] + offset[i]*distance for i in range(3))
        admitted.review._light(bpy, scene, "package-qa-light-" + str(index), position, center, max(100, 60 * distance**2), distance)
    return scene


def render(exporter, admission, admitted):
    import bpy
    exporter.require_process(bpy, admitted, admission)
    admitted.common.create_output(admitted)
    path = admitted.paths["package"].parent / "visual.glb"
    raw = exporter.inspect_raw(admission, admitted, path, "visual")
    imported = exporter.import_visual_or_collision(bpy, admitted, admission, "visual", path, raw)
    captured = _capture(bpy, admitted, admission)
    admission.remaining_limits(admitted, "complete-view render configuration")
    scene = _configure(bpy, admitted, captured)
    specs = view_specs(admitted.plan, admitted.scene)
    if len(specs) > admitted.job["limits"]["maxViews"]:
        raise ValueError("Complete view inventory exceeds approved maxViews")
    views, work = [], admitted.geometry_work
    for index, spec in enumerate(specs):
        admission.remaining_limits(admitted, "required view entry")
        admission.verify_sources(admitted, bpy)
        generated, visible, diagnostics = _clipped_view(bpy, admitted, captured, spec, work)
        low, high = _bounds(captured, spec)
        camera, target, scale = admitted.review._camera(bpy, scene, low, high, spec["direction"])
        bpy.context.view_layer.update()
        filename = "view-" + str(index).zfill(3) + ".png"
        image = admitted.output / filename
        if image.exists() or image.is_symlink():
            raise ValueError("View destination already exists")
        scene.render.filepath = str(image)
        admitted.review.fixed_render_policy(scene)
        admission.remaining_limits(admitted, "required view rendering")
        if scene.render.engine != "CYCLES" or scene.cycles.device != "CPU" or scene.cycles.samples != 8 or scene.render.threads != 2:
            raise ValueError("Fixed CPU view settings changed")
        if "FINISHED" not in bpy.ops.render.render(write_still=True):
            raise ValueError("Fixed CPU renderer did not finish required view")
        admitted.review._png(image, 768, admitted.job["limits"]["maxOutputBytes"])
        inventory = admission.output_files(admitted)
        row = inventory[filename]
        views.append({"index": index, "kind": spec["kind"], "sourceId": spec["sourceId"], **row,
                      "camera": {"projection": "ORTHO", "position": [float(value) for value in camera.location], "target": [float(value) for value in target], "orthoScale": float(scale), "direction": spec["direction"]},
                      "clipPlanes": spec["clipPlanes"], "meshIds": sorted(visible),
                      "diagnostics": {"method": "world-triangle-half-space-clipping", "capSectionFaces": False, **diagnostics}})
        camera_data = camera.data
        bpy.data.objects.remove(camera, do_unlink=True)
        bpy.data.cameras.remove(camera_data)
        for item in generated:
            data = item.data
            bpy.data.objects.remove(item, do_unlink=True)
            bpy.data.meshes.remove(data)
    admission.verify_sources(admitted, bpy)
    files = admission.output_files(admitted, {row["path"] for row in views})
    measured = {"passed": True, "requiredViewCount": len(specs), "views": views, "geometryChecks": work[0],
                "importedVisual": imported, "rawVisual": raw,
                "collisionCheck": {"method": "separate-complete-triangle-import-verification", "sha256": admitted.job["inputs"]["collisionVerification"]["sha256"]},
                "renderSettings": {"engine": "CYCLES", "device": "CPU", "samples": 8, "threads": 2, "imageSize": 768, "viewTransform": "Standard", "denoising": False, "adaptiveSampling": False}}
    report = admission.report_envelope(admitted, "bubo-floor-plan-export-views/v1", measured, [files[name] for name in sorted(files)],
        blenderVersion=".".join(map(str, bpy.app.version)))
    admission.write_report(admitted, "views.json", report)
    admission.output_files(admitted, {row["path"] for row in views} | {"views.json"})
    admission.verify_sources(admitted, bpy)
    return report


def main():
    arguments = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    exporter, admission, admitted = bootstrap(arguments)
    report = render(exporter, admission, admitted)
    print(json.dumps({"status": "package-views", "views": report["measured"]["requiredViewCount"], "runtimeAccepted": False}))
    return report


if __name__ == "__main__":
    main()
