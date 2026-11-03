"""Closed, bounded data admission for fixed portable-building workers.

No Blender dependency. Historical construction jobs are read-only references:
their scene is admitted without applying a fresh-output rule to the old attempt.
These cooperative filesystem checks do not constitute an OS sandbox.
"""
import hashlib
import importlib.util
import json
import math
import os
from pathlib import Path
import re
import sys

sys.dont_write_bytecode = True
BUNDLE = ("admission.py", "review.py", "export.py", "export_admission.py", "export_geometry.py", "export_views.py")
INPUTS = ("sourceBlend", "constructionJob", "constructionReport", "plan", "package", "glb", "visualVerification", "collisionVerification")
IDENTITIES = ("planSha256", "designFingerprint", "approvalSha256", "runtimeSha256", "constructionJobSha256", "constructionReportSha256", "sourceSha256", "packageSha256")
LIMITS = {"maxSourceBytes": 16 * 1024**3, "maxJobBytes": 256 * 1024**2, "maxOutputBytes": 16 * 1024**3,
          "maxVertices": 2_000_000, "maxTriangles": 4_000_000, "maxNodes": 40_000, "maxMeshes": 20_000,
          "maxMaterials": 4096, "maxAccessors": 200_000, "maxPrimitives": 100_000,
          "maxGeometryChecks": 100_000_000, "maxViews": 1158, "imageSize": 768}
HASH = re.compile(r"[0-9a-f]{64}\Z")
MAX_JOB = 1024 * 1024
MAX_REPORT = 8 * 1024 * 1024
SOURCE_TO_GLTF = (1, 0, 0, 0, 0, 1, 0, -1, 0)


def fail(message):
    raise ValueError("Floor-plan export admission: " + message)


def load_fixed(name, directory):
    if name not in BUNDLE:
        fail("unregistered module")
    spec = importlib.util.spec_from_file_location("_bubo_floor_plan_" + name[:-3], Path(directory) / name)
    if spec is None or spec.loader is None:
        fail("fixed resource unavailable")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def bootstrap(arguments, directory, entry):
    # Entry wrappers verify all six resource bytes before loading this module.
    common = load_fixed("admission.py", directory)
    source = common.safe_absolute(arguments[0], "export job")
    raw = read_bytes(common, source, MAX_JOB)
    if hashlib.sha256(raw).hexdigest() != arguments[1]:
        fail("job bytes changed")
    job = decode_json(common, raw)
    admitted = validate_job(common, job, source, len(raw))
    admitted.common = common
    admitted.directory = Path(directory)
    admitted.expected_job_hash = arguments[1]
    verify_sources(admitted)
    admitted.geometry = load_fixed("export_geometry.py", directory)
    admitted.review = load_fixed("review.py", directory)
    if (entry == "export_views.py") != (job["operation"] == "views"):
        fail("operation is not owned by this fixed entry point")
    return admitted


def read_bytes(common, path, maximum):
    path = common.safe_absolute(str(path), "input")
    if not path.is_file() or not 2 <= path.stat().st_size <= maximum:
        fail("missing or oversized JSON input")
    with path.open("rb") as handle:
        raw = handle.read(maximum + 1)
    if len(raw) > maximum or len(raw) != path.stat().st_size:
        fail("input grew/changed during bounded read")
    return raw


def decode_json(common, raw):
    value = common.decode_json(raw)
    pending = [value]
    while pending:
        item = pending.pop()
        if isinstance(item, dict):
            pending.extend(item.values())
        elif isinstance(item, list):
            pending.extend(item)
        elif isinstance(item, float) and not math.isfinite(item):
            fail("nonfinite JSON number, including overflowed exponent")
    return value


def reference(common, value, root, output, maximum, label):
    common.closed(value, ("path", "sha256", "bytes"), label=label)
    common.hash_value(value["sha256"], label + " sha256")
    common.integer(value["bytes"], label + " bytes", 1, maximum)
    path = common.safe_absolute(value["path"], label)
    if not path.is_relative_to(root) or path.is_relative_to(output) or not path.is_file():
        fail(label + " must be a separate confined regular file")
    if path.stat().st_size != value["bytes"] or common.hash_file(path, maximum) != value["sha256"]:
        fail(label + " exact bytes changed")
    return path


def validate_job(common, job, job_path, raw_length):
    common.closed(job, ("schemaVersion", "operation", "workspaceRoot", "outputDirectory", "bundle", "toolchain", "identities", "limits", "inputs"), label="export job")
    if job["schemaVersion"] != "bubo-floor-plan-export-job/v1" or job["operation"] not in ("produce", "verify", "views"):
        fail("unsupported job schema/operation")
    common.closed(job["bundle"], BUNDLE, label="fixed export bundle")
    for value in job["bundle"].values():
        common.hash_value(value, "bundle hash")
    limits = common.closed(job["limits"], LIMITS, label="export limits")
    for key, cap in LIMITS.items():
        common.integer(limits[key], key, 1, cap)
    if limits["imageSize"] != 768:
        fail("fixed CPU diagnostic imageSize is 768")
    common.integer(raw_length, "export job bytes", 2, min(MAX_JOB, limits["maxJobBytes"]))
    root, source, output = common.validate_locations(job, job_path)
    common.closed(job["identities"], IDENTITIES, label="export identities")
    for key, value in job["identities"].items():
        if key == "packageSha256" and job["operation"] == "produce" and value is None:
            continue
        common.hash_value(value, key)
    if job["operation"] == "produce" and job["identities"]["packageSha256"] is not None:
        fail("produce cannot predict a package hash")
    common.closed(job["inputs"], INPUTS, label="export inputs")
    required = {"produce": {"sourceBlend", "constructionJob", "constructionReport", "plan"},
                "verify": {"constructionJob", "plan", "package", "glb"},
                "views": {"constructionJob", "plan", "package", "visualVerification", "collisionVerification"}}[job["operation"]]
    paths = {}
    for key, value in job["inputs"].items():
        if (value is not None) != (key in required):
            fail("wrong null/input inventory for " + key)
        if value is not None:
            maximum = limits["maxJobBytes"] if key == "constructionJob" else limits["maxSourceBytes"]
            paths[key] = reference(common, value, root, output, maximum, key)
    if len(set(paths.values())) != len(paths):
        fail("inputs alias each other")
    identity_inputs = {"sourceBlend": "sourceSha256", "constructionJob": "constructionJobSha256", "constructionReport": "constructionReportSha256", "plan": "planSha256", "package": "packageSha256"}
    for key, identity in identity_inputs.items():
        if key in paths and job["inputs"][key]["sha256"] != job["identities"][identity]:
            fail("input identity mismatch: " + key)
    result = common.AdmittedJob(job, root, source, output, {})
    result.common, result.paths = common, paths
    result.geometry_work = [0]
    result.scene, result.constructor_bundle = admit_construction_reference(common, paths["constructionJob"], job, result)
    report = None
    if "constructionReport" in paths:
        report = decode_json(common, read_bytes(common, paths["constructionReport"], MAX_REPORT))
        common.closed(report, ("schemaVersion", "jobSha256", "bundle", "identities", "blenderVersion", "blend", "counts", "minimum", "maximum", "maxWorldCoordinateError", "meshes", "anchors", "materials", "independentReimport", "humanModelAccepted", "runtimeAccepted"), label="construction report")
        if (report["schemaVersion"] != "bubo-floor-plan-construction-report/v1" or report["jobSha256"] != job["identities"]["constructionJobSha256"] or
                report["blend"] != {"path": "building.blend", "sha256": job["identities"]["sourceSha256"], "bytes": job["inputs"]["sourceBlend"]["bytes"]} or
                report["identities"] != {key: job["identities"][key] for key in ("planSha256", "designFingerprint", "runtimeSha256")} or
                report["materials"] != result.scene["materials"]):
            fail("construction report does not bind the exact trusted source")
        if report["bundle"] != result.constructor_bundle or report["runtimeAccepted"] is not False or report["humanModelAccepted"] is not False or report["independentReimport"] is not False:
            fail("construction receipt altered source bundle/acceptance claims")
    result.plan = decode_json(common, read_bytes(common, paths["plan"], 8 * 1024**2))
    plan = result.plan
    if (not isinstance(plan, dict) or plan.get("schemaVersion") != "bubo-building-plan/v2" or
            plan.get("id") != result.scene["planId"] or plan.get("coordinateSystem") != "xy-metres-z-up"):
        fail("exact current v2 source plan required")
    numerics = common.closed(plan.get("numerics"), ("linearTolerance", "angularToleranceDegrees", "maxCoordinateMagnitude", "minimumFeatureSize"), label="source numerics")
    if numerics["linearTolerance"] != result.scene["linearTolerance"]:
        fail("source scene tolerance differs from approved plan")
    common.number(numerics["angularToleranceDegrees"], "angular tolerance", .001, 1)
    if report is not None:
        validate_report_anchors(common, report["anchors"], result.scene["anchors"],
                                numerics["linearTolerance"], numerics["angularToleranceDegrees"], result)
    for key, plan_key, container in (("maxTriangles", "maxGeneratedTriangles", "budgets"), ("maxVertices", "maxConstructionVertices", "construction"), ("maxGeometryChecks", "maxGeometryChecks", "budgets"), ("maxOutputBytes", "maxExportBytes", "budgets")):
        approved = plan.get(container, {}).get(plan_key)
        common.integer(approved, "approved " + plan_key, 1)
        if limits[key] > approved:
            fail(key + " exceeds approved source allowance")
    for key in ("storeys", "stairs"):
        rows = common.array(plan.get(key), key, 1024, 1 if key == "storeys" else 0)
        ids = [common.text(row.get("id"), key + " id") for row in rows if isinstance(row, dict)]
        if len(ids) != len(rows) or len(set(ids)) != len(ids):
            fail("invalid/duplicate source " + key)
    if {row["id"] for row in plan["storeys"]} != {row["storeyId"] for row in result.scene["meshes"]}:
        fail("source floor inventory differs from scene")
    if len(plan["storeys"]) + len(plan["stairs"]) + 6 > limits["maxViews"]:
        fail("complete required view set exceeds approved allowance")
    validate_toolchain(common, job["toolchain"])
    result.package = None
    result.representation = None
    if "package" in paths:
        result.package, result.representation = admit_package(result)
    return result


def validate_report_anchors(c, measured, sources, linear_tolerance, angular_tolerance_degrees, work_owner):
    """Admit actual float32 measurements without replacing their values.

    Semantic identity and the inventory are exact. Position error uses metres;
    axis and initial-pose errors use the approved angle in degrees. Axis unit
    length has the same fixed dimensionless 1e-6 guard as portable-node frames.
    """
    expected = {row["id"]: row for row in sources}
    rows = c.array(measured, "measured construction anchors", len(sources), len(sources))
    seen = set()
    for row in rows:
        charge_work(work_owner, 1, "measured construction-anchor admission")
        c.closed(row, ("id", "semanticId", "storeyId", "kind", "position", "axis", "rotationRadians"), label="measured construction anchor")
        identifier = c.text(row["id"], "measured anchor id")
        if identifier in seen or identifier not in expected:
            fail("construction report has unexpected/duplicate anchor identity")
        seen.add(identifier)
        source = expected[identifier]
        if any(row[key] != source[key] for key in ("semanticId", "storeyId", "kind")):
            fail("construction report changed anchor semantic identity")
        actual_position = c.point(row["position"], "measured anchor position")
        source_position = c.point(source["position"], "source anchor position")
        if math.dist(actual_position, source_position) > linear_tolerance:
            fail("construction report anchor position exceeds approved linear tolerance")
        actual_axis, source_axis = c.point(row["axis"], "measured anchor axis"), c.point(source["axis"], "source anchor axis")
        if abs(math.hypot(*actual_axis) - 1) > 1e-6:
            fail("construction report anchor axis is not a unit direction")
        cross = (actual_axis[1] * source_axis[2] - actual_axis[2] * source_axis[1],
                 actual_axis[2] * source_axis[0] - actual_axis[0] * source_axis[2],
                 actual_axis[0] * source_axis[1] - actual_axis[1] * source_axis[0])
        dot = math.fsum(left * right for left, right in zip(actual_axis, source_axis))
        axis_error = math.degrees(math.atan2(math.hypot(*cross), dot))
        rotation_error = math.degrees(abs(c.number(row["rotationRadians"], "measured anchor rotation") - source["rotationRadians"]))
        if axis_error > angular_tolerance_degrees or rotation_error > angular_tolerance_degrees:
            fail("construction report anchor pose exceeds approved angular tolerance")


def admit_construction_reference(c, path, export_job, work_owner):
    remaining_limits(work_owner, "historical source-scene admission entry")
    raw = read_bytes(c, path, export_job["limits"]["maxJobBytes"])
    job = decode_json(c, raw)
    c.closed(job, ("schemaVersion", "operation", "workspaceRoot", "outputDirectory", "scene", "identities", "bundle", "limits"), label="historical construction job")
    if job["schemaVersion"] != "bubo-floor-plan-blender-job/v1" or job["operation"] != "construct":
        fail("trusted constructor job required")
    original_root = c.safe_absolute(job["workspaceRoot"], "construction workspace")
    original_output = c.safe_absolute(job["outputDirectory"], "completed construction output")
    if (original_root != c.safe_absolute(export_job["workspaceRoot"], "export workspace") or
            not path.is_relative_to(original_root) or path.name != "job.json" or path.parent == original_root or
            original_output != path.parent / "output" or not original_output.is_dir()):
        fail("construction reference is not an owned completed attempt")
    if export_job["operation"] == "produce" and any(Path(export_job["inputs"][key]["path"]) != original_output / name for key, name in (("sourceBlend", "building.blend"), ("constructionReport", "construction-report.json"))):
        fail("producer must consume the original completed constructor outputs")
    c.closed(job["identities"], ("planSha256", "designFingerprint", "runtimeSha256"), label="source identities")
    if any(value != export_job["identities"][key] for key, value in job["identities"].items()):
        fail("construction identities differ from export")
    c.closed(job["bundle"], ("author.py", "admission.py"), label="source constructor bundle")
    for value in job["bundle"].values():
        c.hash_value(value, "constructor resource hash")
    if job["bundle"]["admission.py"] != export_job["bundle"]["admission.py"]:
        fail("source constructor was admitted by a different pure source contract")
    source_limits = c.closed(job["limits"], ("maxTriangles", "maxVertices", "maxObjects", "maxOutputBytes", "maxJobBytes"), label="source constructor limits")
    for key, cap in (("maxTriangles", c.MAX_TRIANGLES), ("maxVertices", c.MAX_VERTICES), ("maxObjects", c.MAX_OBJECTS), ("maxOutputBytes", c.MAX_OUTPUT_BYTES), ("maxJobBytes", c.MAX_JOB_BYTES)):
        c.integer(source_limits[key], "source " + key, 1, cap)
    c.integer(len(raw), "source job length", 2, source_limits["maxJobBytes"])
    # Pure historical-scene admission deliberately never calls validate_job:
    # that constructor entry point owns a new, absent output directory.
    scene = c.closed(job["scene"], ("schemaVersion", "planId", "linearTolerance", "materials", "meshes", "anchors", "metrics", "clearance"), label="source scene")
    if scene["schemaVersion"] != "bubo-floor-plan-construction-scene/v1":
        fail("unsupported source scene")
    c.text(scene["planId"], "source plan id")
    tolerance = c.number(scene["linearTolerance"], "source tolerance", 1e-12, 1)
    palette, anchors, mesh_ids = {}, {}, set()
    for row in c.array(scene["materials"], "source materials", c.MAX_MATERIALS, 1):
        charge_work(work_owner, 1, "source material admission")
        c.closed(row, ("id", "name", "color", "roughness", "metallic", "transmission", "ior"), label="source material")
        identifier = c.text(row["id"], "material id")
        if identifier in palette or not isinstance(row["color"], str) or re.fullmatch(r"#[0-9a-fA-F]{6}", row["color"]) is None:
            fail("invalid/duplicate source material")
        c.text(row["name"], "material name")
        for scalar in ("roughness", "metallic", "transmission"):
            c.number(row[scalar], scalar, 0, 1)
        c.number(row["ior"], "ior", 1, 3)
        palette[identifier] = row
    for row in c.array(scene["anchors"], "source anchors", c.MAX_OBJECTS):
        charge_work(work_owner, 1, "source anchor admission")
        c.closed(row, ("id", "semanticId", "storeyId", "kind", "position", "axis", "rotationRadians"), label="source anchor")
        for key in ("id", "semanticId", "storeyId", "kind"):
            c.text(row[key], "anchor " + key)
        if row["id"] in anchors or c.point(row["axis"], "anchor axis") != (0, 0, 1) or c.number(row["rotationRadians"], "anchor angle") != 0:
            fail("invalid source anchor")
        c.point(row["position"], "anchor position")
        anchors[row["id"]] = row
    vertices = triangles = visual = collision = 0
    lows, highs = [], []
    for row in c.array(scene["meshes"], "source meshes", c.MAX_OBJECTS, 1):
        c.closed(row, ("id", "semanticId", "storeyId", "kind", "representation", "collisionRole", "materialId", "vertices", "triangles", "parentAnchorId"), ("triangleMaterialIds",), label="source mesh")
        for key in ("id", "semanticId", "storeyId", "kind", "materialId"):
            c.text(row[key], "mesh " + key)
        if row["id"] in mesh_ids or row["id"] in anchors or row["materialId"] not in palette:
            fail("invalid/duplicate mesh/material identity")
        mesh_ids.add(row["id"])
        if row["representation"] not in ("visual", "collision") or row["collisionRole"] not in ("none", "static", "dynamic") or row["representation"] == "collision" and row["collisionRole"] == "none":
            fail("invalid source representation/collision ownership")
        parent = row["parentAnchorId"]
        if parent is not None and (parent not in anchors or anchors[parent]["storeyId"] != row["storeyId"]):
            fail("invalid source pivot parent")
        raw_vertices = c.array(row["vertices"], "source vertices", export_job["limits"]["maxVertices"], 4)
        indices = c.array(row["triangles"], "source indices", export_job["limits"]["maxTriangles"] * 3, 12)
        vertices += len(raw_vertices)
        triangles += len(indices) // 3
        if vertices > export_job["limits"]["maxVertices"] or triangles > export_job["limits"]["maxTriangles"]:
            fail("cumulative source geometry cap exceeded")
        charge_work(work_owner, len(raw_vertices) + len(indices) // 3, "source indexed-solid admission")
        points = [c.point(point, "source vertex") for point in raw_vertices]
        c.inspect_triangle_solid(points, indices)
        overrides = row.get("triangleMaterialIds")
        if overrides is not None and (len(c.array(overrides, "triangle material IDs", export_job["limits"]["maxTriangles"])) != len(indices) // 3 or any(value not in palette for value in overrides)):
            fail("invalid per-triangle source materials")
        lows.append(c.vector_min(points)); highs.append(c.vector_max(points))
        if row["representation"] == "visual":
            visual += len(indices) // 3
        else:
            collision += len(indices) // 3
    metrics = c.closed(scene["metrics"], ("meshCount", "vertexCount", "triangleCount", "visualTriangleCount", "collisionTriangleCount", "geometryChecks", "minimum", "maximum"), label="source metrics")
    expected_metrics = {"meshCount": len(mesh_ids), "vertexCount": vertices, "triangleCount": triangles, "visualTriangleCount": visual, "collisionTriangleCount": collision}
    if any(c.integer(metrics[key], key) != value for key, value in expected_metrics.items()) or c.point(metrics["minimum"], "source minimum") != c.vector_min(lows) or c.point(metrics["maximum"], "source maximum") != c.vector_max(highs):
        fail("stale native source metrics")
    if vertices > source_limits["maxVertices"] or triangles > source_limits["maxTriangles"] or len(mesh_ids) + len(anchors) > source_limits["maxObjects"]:
        fail("source scene exceeds its original constructor limits")
    c.integer(metrics["geometryChecks"], "source geometry checks", 0, export_job["limits"]["maxGeometryChecks"])
    clearance = c.closed(scene["clearance"], ("routes", "doorSweeps", "minimumRouteClearance"), label="source clearance")
    for route in c.array(clearance["routes"], "source routes", c.MAX_OBJECTS):
        c.closed(route, ("roomId", "fromId", "toId", "points", "radius", "height", "minimumClearance"), label="source route")
        charge_work(work_owner, 1 + len(c.array(route["points"], "route points", c.MAX_VERTICES, 1)), "source route evidence admission")
    charge_work(work_owner, len(c.array(clearance["doorSweeps"], "source door sweeps", c.MAX_OBJECTS)), "source sweep evidence admission")
    c.validate_clearance(clearance, anchors, tolerance)
    if (len(scene["meshes"]) > export_job["limits"]["maxMeshes"] or
            len(scene["materials"]) > export_job["limits"]["maxMaterials"] or
            scene["metrics"]["vertexCount"] > export_job["limits"]["maxVertices"] or
            scene["metrics"]["triangleCount"] > export_job["limits"]["maxTriangles"]):
        fail("source scene exceeds export allowance")
    return scene, job["bundle"]


def relative_path(c, value):
    c.text(value, "relative inventory path", 4096)
    if "\\" in value or ":" in value or value.startswith("/") or any(part in ("", ".", "..", ".git") or part.endswith((".", " ")) for part in value.split("/")):
        fail("noncanonical relative inventory path")
    return value


def validate_toolchain(c, value, bpy=None):
    c.closed(value, ("blenderPath", "blenderSha256", "moduleRoot", "files", "fingerprint"), label="toolchain")
    c.hash_value(value["blenderSha256"], "Blender hash")
    c.hash_value(value["fingerprint"], "toolchain fingerprint")
    blender = c.safe_absolute(value["blenderPath"], "selected Blender")
    root = c.safe_absolute(value["moduleRoot"], "bundled glTF module")
    expected_root = blender.parent / "4.5/scripts/addons_core/io_scene_gltf2"
    if root != expected_root or not root.is_dir() or not blender.is_file():
        fail("toolchain is not selected Blender's fixed bundled module")
    if c.hash_file(blender, 1024**3) != value["blenderSha256"]:
        fail("selected Blender executable changed")
    expected, total = {}, 0
    previous = None
    for row in c.array(value["files"], "toolchain files", 4096, 1):
        c.closed(row, ("path", "sha256", "bytes"), label="toolchain file")
        name = relative_path(c, row["path"])
        c.hash_value(row["sha256"], "module file hash")
        c.integer(row["bytes"], "module file bytes", 0, 16 * 1024**2)
        if previous is not None and name <= previous or name.casefold() in expected:
            fail("toolchain files must be uniquely sorted")
        previous = name
        total += row["bytes"]
        if total > 128 * 1024**2:
            fail("toolchain cumulative byte cap exceeded")
        expected[name.casefold()] = row
    if "__init__.py" not in expected:
        fail("missing bundled module entry")
    seen, pending, directories = set(), [root], 1
    while pending:
        directory = pending.pop()
        for candidate in directory.iterdir():
            path = c.safe_absolute(str(candidate), "installed module entry")
            if not path.is_relative_to(root):
                fail("module entry escaped selected root")
            if path.is_dir():
                directories += 1
                if directories > 1024:
                    fail("toolchain directory cap exceeded")
                pending.append(path)
                continue
            name = path.relative_to(root).as_posix()
            key = name.casefold()
            if key in seen or key not in expected or len(seen) >= 4096:
                fail("unexpected/duplicate installed module file")
            row = expected[key]
            if name != row["path"] or path.stat().st_size != row["bytes"] or c.hash_file(path, 16 * 1024**2) != row["sha256"]:
                fail("installed module bytes changed")
            seen.add(key)
    if seen != set(expected):
        fail("installed module inventory is incomplete")
    fingerprint_text = "\n".join([value["blenderPath"], value["blenderSha256"], value["moduleRoot"]] +
                                  [row["path"] + "\t" + row["sha256"] + "\t" + str(row["bytes"]) for row in value["files"]]) + "\n"
    if hashlib.sha256(fingerprint_text.encode("utf-8")).hexdigest() != value["fingerprint"]:
        fail("toolchain fingerprint does not bind its full inventory")
    if bpy is not None:
        if Path(bpy.app.binary_path).resolve(strict=True) != blender:
            fail("running Blender differs from selected executable")
        spec = importlib.util.find_spec("io_scene_gltf2")
        if spec is None or spec.origin is None or c.safe_absolute(spec.origin, "glTF loader origin") != root / "__init__.py":
            fail("actual glTF loader origin differs from pinned bundled code")
        module = sys.modules.get("io_scene_gltf2")
        if module is not None and c.safe_absolute(module.__file__, "loaded glTF module origin") != root / "__init__.py":
            fail("loaded glTF module origin differs from pinned bundled code")


def admit_package(admitted):
    c, job, paths = admitted.common, admitted.job, admitted.paths
    package = decode_json(c, read_bytes(c, paths["package"], MAX_REPORT))
    c.closed(package, ("schemaVersion", "task", "asset", "version", "ancestry", "coordinates", "numerics", "files"), label="sealed package")
    if package["schemaVersion"] != "bubo-floor-plan-export-package/v1":
        fail("unsupported sealed package")
    ancestry = c.closed(package["ancestry"], ("planSha256", "designFingerprint", "approvalSha256", "runtimeSha256", "catalogSha256", "constructionAttempt", "constructionJobSha256", "constructionReportSha256", "sourceSha256", "bundle", "toolchain", "modelAuthor"), label="portable ancestry")
    if any(ancestry.get(key) != value for key, value in job["identities"].items() if key != "packageSha256"):
        fail("package source ancestry differs from current exact inputs")
    c.hash_value(ancestry["catalogSha256"], "catalog hash")
    relative_path(c, ancestry["constructionAttempt"])
    c.text(ancestry["modelAuthor"], "model author", 128)
    portable_tool = c.closed(ancestry["toolchain"], ("blenderSha256", "blenderVersion", "gltfFiles"), label="portable toolchain")
    c.text(portable_tool["blenderVersion"], "Blender version", 128)
    if (ancestry["bundle"] != job["bundle"] or portable_tool["blenderSha256"] != job["toolchain"]["blenderSha256"] or
            portable_tool["gltfFiles"] != job["toolchain"]["files"]):
        fail("package script/tool/module identities differ from current worker")
    coordinates = c.closed(package["coordinates"], ("source", "target", "sourceToGltf"), label="coordinates")
    if coordinates != {"source": "xy-metres-z-up", "target": "gltf-metres-y-up", "sourceToGltf": list(SOURCE_TO_GLTF)}:
        fail("unsupported package coordinate mapping")
    if package["numerics"] != admitted.plan["numerics"]:
        fail("package numerical policy differs from approved plan")
    inventory, total, previous = {}, 0, None
    for row in c.array(package["files"], "payload inventory", 2048, 1):
        c.closed(row, ("path", "sha256", "bytes"), label="payload file")
        name = relative_path(c, row["path"])
        if name == "package.json" or previous is not None and name <= previous or name.casefold() in inventory:
            fail("payload inventory must be sorted/unique and excludes itself")
        previous = name
        c.hash_value(row["sha256"], "payload hash")
        c.integer(row["bytes"], "payload bytes", 1, job["limits"]["maxSourceBytes"])
        total += row["bytes"]
        if total > admitted.plan["budgets"]["maxExportBytes"]:
            fail("sealed payload exceeds approved cumulative bytes")
        payload = paths["package"].parent / name
        reference(c, {"path": str(payload), "sha256": row["sha256"], "bytes": row["bytes"]}, admitted.workspace, admitted.output, job["limits"]["maxSourceBytes"], "payload")
        inventory[name.casefold()] = row
    expected = {"building.blend", "visual.glb", "collision.glb", "building-plan.json", "semantics.json", "provenance/construction-report.json", "provenance/design-preview.json", "provenance/design-approval.json"}
    expected.update("drawings/" + name for name in ("roof-plan.svg", "elevation-north.svg", "elevation-south.svg", "elevation-east.svg", "elevation-west.svg", "section-principal.svg", "construction-details.svg"))
    expected.update("drawings/floor-" + str(index).zfill(2) + ".svg" for index in range(1, len(admitted.plan["storeys"]) + 1))
    expected.update("drawings/section-stair-" + str(index).zfill(2) + ".svg" for index in range(1, len(admitted.plan["stairs"]) + 1))
    if set(inventory) != expected:
        fail("wrong sealed payload inventory")
    actual, pending, directories = set(), [paths["package"].parent], 1
    while pending:
        for item in pending.pop().iterdir():
            item = c.safe_absolute(str(item), "payload entry")
            name = item.relative_to(paths["package"].parent).as_posix()
            if item.is_dir():
                directories += 1
                if name not in ("drawings", "provenance") or directories > 3:
                    fail("unexpected payload directory")
                pending.append(item)
            elif name in actual or len(actual) > 8192:
                fail("duplicate/oversized payload inventory")
            else:
                actual.add(name)
    if actual != {row["path"] for row in inventory.values()} | {"package.json"}:
        fail("unlisted/missing sealed payload files")
    for name, identity in (("building.blend", "sourceSha256"), ("building-plan.json", "planSha256"), ("provenance/construction-report.json", "constructionReportSha256"), ("provenance/design-approval.json", "approvalSha256")):
        if inventory[name]["sha256"] != job["identities"][identity]:
            fail("payload identity mismatch: " + name)
    rep = None
    if "glb" in paths:
        candidates = [kind for kind in ("visual", "collision") if paths["glb"] == paths["package"].parent / (kind + ".glb") and job["inputs"]["glb"] == {"path": str(paths["glb"]), "sha256": inventory[kind + ".glb"]["sha256"], "bytes": inventory[kind + ".glb"]["bytes"]}]
        if len(candidates) != 1:
            fail("verify GLB must be one exact sealed representation")
        rep = candidates[0]
    if job["operation"] == "views":
        for kind in ("visual", "collision"):
            evidence = decode_json(c, read_bytes(c, paths[kind + "Verification"], MAX_REPORT))
            c.closed(evidence, ("schemaVersion", "operation", "jobSha256", "identities", "bundle", "toolchain", "inputs", "limits", "outputs", "measured", "humanModelAccepted", "runtimeAccepted", "representation", "blenderVersion"), label="verification evidence")
            c.hash_value(evidence["jobSha256"], "verification job hash")
            if (evidence.get("schemaVersion") != "bubo-floor-plan-export-verification/v1" or evidence.get("operation") != "verify" or
                    evidence.get("representation") != kind or evidence.get("identities") != job["identities"] or
                    evidence.get("toolchain") != job["toolchain"] or evidence.get("bundle") != job["bundle"] or
                    evidence.get("measured", {}).get("passed") is not True or evidence["humanModelAccepted"] is not False or evidence["runtimeAccepted"] is not False or evidence["outputs"] != []):
                fail("views require both exact current successful verifications")
    return package, rep


def verify_sources(admitted, bpy=None):
    c, job = admitted.common, admitted.job
    if c.hash_file(c.safe_absolute(str(admitted.source), "job"), min(MAX_JOB, job["limits"]["maxJobBytes"])) != admitted.expected_job_hash:
        fail("job changed during operation")
    for name, expected in job["bundle"].items():
        path = c.safe_absolute(str(admitted.directory / name), "fixed bundle resource")
        if c.hash_file(path, 2 * 1024**2) != expected:
            fail("fixed bundle changed during operation")
    for name, path in admitted.paths.items():
        value = job["inputs"][name]
        reference(c, value, admitted.workspace, admitted.output, job["limits"]["maxJobBytes"] if name == "constructionJob" else job["limits"]["maxSourceBytes"], name)
    validate_toolchain(c, job["toolchain"], bpy)
    if admitted.package is not None:
        admit_package(admitted)
        if bpy is not None and admitted.package["ancestry"]["toolchain"]["blenderVersion"] != ".".join(map(str, bpy.app.version)):
            fail("package Blender version differs from actual worker")
    c.safe_absolute(str(admitted.output.parent), "output parent")
    current = c.safe_absolute(str(admitted.output), "output", must_exist=False)
    if current != admitted.output or current.exists() and not current.is_dir():
        fail("owned output identity changed")


def write_report(admitted, filename, report):
    c, output = admitted.common, admitted.output
    if filename not in ("producer-report.json", "verification.json", "views.json"):
        fail("unregistered report name")
    existing = output_files(admitted)
    path = output / filename
    if filename in existing:
        fail("report output already exists")
    maximum = min(MAX_REPORT, admitted.job["limits"]["maxOutputBytes"] - sum(row["bytes"] for row in existing.values()))
    partial = path.with_name(filename + ".partial")
    descriptor = os.open(partial, os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0), 0o600)
    count = 0
    with os.fdopen(descriptor, "wb") as handle:
        for fragment in json.JSONEncoder(ensure_ascii=False, allow_nan=False, sort_keys=True, separators=(",", ":")).iterencode(report):
            data = fragment.encode("utf-8")
            count += len(data)
            if count + 1 > maximum:
                fail("report exceeds cumulative output allowance")
            handle.write(data)
        handle.write(b"\n")
        handle.flush()
        os.fsync(handle.fileno())
    os.link(partial, path)
    partial.unlink()
    return c.hash_file(path, MAX_REPORT)


def output_files(admitted, expected=None):
    c, rows, total = admitted.common, {}, 0
    for item in admitted.output.iterdir():
        path = c.safe_absolute(str(item), "owned output file")
        if not path.is_file() or path.name in rows or len(rows) >= admitted.job["limits"]["maxViews"] + 3:
            fail("unexpected output type/count")
        size = path.stat().st_size
        total += size
        if size <= 0 or total > admitted.job["limits"]["maxOutputBytes"]:
            fail("actual cumulative output bytes exceeded")
        rows[path.name] = {"path": path.name, "sha256": c.hash_file(path, admitted.job["limits"]["maxOutputBytes"]), "bytes": size}
    if expected is not None and set(rows) != set(expected):
        fail("wrong exact output inventory")
    return rows


def report_envelope(admitted, schema, measured, outputs, **extra):
    job = admitted.job
    return {"schemaVersion": schema, "operation": job["operation"], "jobSha256": admitted.expected_job_hash,
            "identities": job["identities"], "bundle": job["bundle"], "toolchain": job["toolchain"],
            "inputs": job["inputs"], "limits": job["limits"], "outputs": outputs, "measured": measured,
            "humanModelAccepted": False, "runtimeAccepted": False, **extra}


def remaining_limits(admitted, label):
    """Admit each helper against only the invocation's remaining allowance."""
    cap, used = admitted.job["limits"]["maxGeometryChecks"], admitted.geometry_work[0]
    if type(used) is not int or not 0 <= used < cap:
        fail(label + " has no remaining geometry-work allowance")
    return {**admitted.job["limits"], "maxGeometryChecks": cap - used}


def charge_work(admitted, count, label):
    """Charge before local work, or debit a bounded helper's measured work."""
    cap, used = admitted.job["limits"]["maxGeometryChecks"], admitted.geometry_work[0]
    if type(count) is not int or count < 0 or type(used) is not int or used < 0 or used + count > cap:
        fail(label + " exceeds the remaining geometry-work allowance")
    admitted.geometry_work[0] += count
