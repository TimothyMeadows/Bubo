"""Closed, bounded input admission for the fixed independent-part workers.

This module uses only the Python standard library and performs no native work.
The native caller remains responsible for design approval and process ownership.
"""
import hashlib
import json
import math
import os
from pathlib import Path
import re
import stat

BUNDLE = ("parts_admission.py", "parts_author.py", "parts_geometry.py", "parts_verify.py")
HEX = re.compile(r"^[0-9a-f]{64}$")
IDENT = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$")
MAX_JOB = 8 * 1024 * 1024


def require(condition, message):
    if not condition:
        raise ValueError(message)


def closed(value, keys, label):
    require(isinstance(value, dict) and set(value) == set(keys.split()), "Closed " + label + " fields required")
    return value


def number(value, label, low=None, high=None):
    require(type(value) in (int, float) and math.isfinite(value), "Finite " + label + " required")
    require(low is None or value >= low, label + " below minimum")
    require(high is None or value <= high, label + " above maximum")
    return value


def integer(value, label, low, high):
    number(value, label, low, high)
    require(type(value) is int, "Integer " + label + " required")
    return value


def identifier(value):
    require(isinstance(value, str) and IDENT.fullmatch(value) and value not in (".", ".."), "Unsafe identifier")
    # Avoid Windows device names and aliases, even when admitted on another host.
    require(value.split(".")[0].upper() not in {"CON", "PRN", "AUX", "NUL", *["COM" + str(i) for i in range(10)], *["LPT" + str(i) for i in range(10)]}, "Reserved identifier")
    return value


def digest(path):
    hasher = hashlib.sha256()
    with open(path, "rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            hasher.update(chunk)
    return hasher.hexdigest()


def no_links(path):
    path = Path(path)
    require(path.is_absolute(), "Absolute path required")
    require(not any(part in ("..", ".git") for part in path.parts), "Unsafe path component")
    for node in [path, *path.parents]:
        if node.exists() or node.is_symlink():
            info = node.lstat()
            require(not stat.S_ISLNK(info.st_mode) and not getattr(info, "st_file_attributes", 0) & 0x400, "Linked/reparse path rejected")
    return path


def confined(root, value):
    path = no_links(value)
    require(path != root and path.is_relative_to(root), "Path must remain below project root")
    require(path.resolve() == path, "Canonical path required")
    return path


def strict_json(raw):
    def pairs(items):
        result = {}
        for key, value in items:
            require(key not in result, "Duplicate JSON property")
            result[key] = value
        return result
    return json.loads(raw, object_pairs_hook=pairs, parse_constant=lambda value: (_ for _ in ()).throw(ValueError("Nonfinite JSON")))


def vector(value, axes="xyz"):
    closed(value, " ".join(axes), "vector")
    return tuple(number(value[key], "coordinate", -1000000, 1000000) for key in axes)


def frame(value):
    closed(value, "origin rotationDegrees", "frame")
    vector(value["origin"])
    vector(value["rotationDegrees"])


def profile(value):
    require(isinstance(value, list) and 3 <= len(value) <= 4096, "Bounded polygon required")
    points = [vector(item, "xy") for item in value]
    require(len(set(points)) == len(points), "Repeated polygon point")
    # Initial audited lane supports convex CCW polygons only. This rejects
    # self crossings without asking a tessellator to repair the approved shape.
    for i, a in enumerate(points):
        b, c = points[(i + 1) % len(points)], points[(i + 2) % len(points)]
        cross = (b[0] - a[0]) * (c[1] - b[1]) - (b[1] - a[1]) * (c[0] - b[0])
        require(cross > 1e-16, "Native polygon profile requires strictly convex CCW vertices")
    # A star polygon may have consistently positive local turns. Require each
    # nonadjacent point strictly inside every oriented edge half-plane too.
    for i, a in enumerate(points):
        b = points[(i + 1) % len(points)]
        for j, c in enumerate(points):
            if j not in (i, (i + 1) % len(points)):
                require((b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0]) > 1e-16,
                        "Self-intersecting/nonconvex polygon unsupported")
    return points


def segment_distance(a, b, c, d):
    # Closest points on closed segments, including parallel/endpoint cases.
    dot = lambda u, v: sum(x * y for x, y in zip(u, v))
    sub = lambda u, v: tuple(x - y for x, y in zip(u, v))
    u, v, w = sub(b, a), sub(d, c), sub(a, c)
    aa, bb, cc, dd, ee = dot(u, u), dot(u, v), dot(v, v), dot(u, w), dot(v, w)
    denom = aa * cc - bb * bb
    s = max(0, min(1, (bb * ee - cc * dd) / denom)) if denom > 1e-24 * aa * cc else 0
    t = (bb * s + ee) / cc
    if t < 0:
        t, s = 0, max(0, min(1, -dd / aa))
    elif t > 1:
        t, s = 1, max(0, min(1, (bb - dd) / aa))
    delta = tuple(wi + s * ui - t * vi for wi, ui, vi in zip(w, u, v))
    return math.sqrt(dot(delta, delta))


def admit_sweep(path, radius):
    points = [vector(p) for p in path]
    closed_path = points[0] == points[-1]
    points = points[:-1] if closed_path else points
    require(len(points) >= (3 if closed_path else 2) and len(set(points)) == len(points), "Repeated/degenerate sweep point")
    dot = lambda u, v: sum(x * y for x, y in zip(u, v))
    sub = lambda u, v: tuple(x - y for x, y in zip(u, v))
    directions, lengths = [], []
    count = len(points) if closed_path else len(points) - 1
    for i in range(count):
        delta = sub(points[(i + 1) % len(points)], points[i])
        length = math.sqrt(dot(delta, delta))
        require(length > 1e-10, "Zero-length sweep edge")
        lengths.append(length)
        directions.append(tuple(x / length for x in delta))
    for i in range(len(points)):
        if not closed_path and i in (0, len(points) - 1):
            continue
        before, after = directions[i - 1], directions[i % count]
        cosine = max(-1, min(1, dot(before, after)))
        require(cosine > -0.5, "Sharp/reversing sweep bend unsupported")
        sine = math.sqrt(max(0, 1 - cosine * cosine))
        if sine > 1e-12:
            a, b = lengths[i - 1], lengths[i % count]
            chord = math.sqrt(a * a + b * b + 2 * a * b * cosine)
            require(chord / (2 * sine) > radius * (1 + 1e-8), "Sweep bend radius does not clear tube radius")
    tangents = []
    for i in range(len(points)):
        if not closed_path and i == 0:
            tangent = directions[0]
        elif not closed_path and i == len(points) - 1:
            tangent = directions[-1]
        else:
            summed = tuple(a + b for a, b in zip(directions[i - 1], directions[i % count]))
            magnitude = math.sqrt(dot(summed, summed))
            tangent = tuple(value / magnitude for value in summed)
        tangents.append(tangent)
    # Each adjacent ring must lie strictly on its own side of the shared ring's
    # plane. Thus adjacent strips/caps meet only at their owned shared ring.
    for i in range(len(points)):
        for step in (-1, 1):
            neighbor = i + step
            if not closed_path and not 0 <= neighbor < len(points):
                continue
            neighbor %= len(points)
            projection = step * dot(sub(points[neighbor], points[i]), tangents[i])
            cosine = max(-1, min(1, dot(tangents[i], tangents[neighbor])))
            radial_projection = radius * math.sqrt(max(0, 1 - cosine * cosine))
            require(projection > radial_projection * (1 + 1e-8) + 1e-12, "Adjacent sweep rings cross an owned section plane")
    for i in range(count):
        for j in range(i + 1, count):
            if j == i + 1 or (closed_path and i == 0 and j == count - 1):
                continue
            require(segment_distance(points[i], points[(i + 1) % len(points)], points[j], points[(j + 1) % len(points)]) > 2 * radius * (1 + 1e-8), "Nonlocal sweep tube intersection/clearance unsupported")
    return len(points)


def feature(value):
    closed(value, "id kind operation frame parameters profile path sections", "feature")
    identifier(value["id"])
    require(value["operation"] in ("add", "subtract"), "Unknown feature operation")
    frame(value["frame"])
    kind = value["kind"]
    keys = {"box": "width depth height", "cylinder": "radius length segments", "extrude": "depth",
            "revolve": "angleDegrees segments", "sweep": "radius segments", "loft": ""}
    require(kind in keys, "Unsupported feature kind")
    params = closed(value["parameters"], keys[kind], "feature parameters")
    for key, val in params.items():
        number(val, key, 0, 1000000)
        require(val > 0, "Positive feature parameter required")
    if "segments" in params:
        require(params["segments"] == int(params["segments"]) and 3 <= params["segments"] <= 1024, "Bounded integral segments required")
    require(isinstance(value["profile"], list) and isinstance(value["path"], list) and isinstance(value["sections"], list), "Feature arrays required")
    points = profile(value["profile"]) if kind in ("extrude", "revolve") else []
    require(kind in ("extrude", "revolve") or not value["profile"], "Unused profile rejected")
    require(kind == "sweep" or not value["path"], "Unused path rejected")
    require(kind == "loft" or not value["sections"], "Unused sections rejected")
    if kind == "revolve":
        require(params["angleDegrees"] <= 360 and all(p[0] >= 0 for p in points), "Unsupported revolution")
        require(max(p[0] for p in points) > 0, "Zero-radius revolution")
    if kind == "sweep":
        require(2 <= len(value["path"]) <= 4096, "Bounded sweep path required")
        admit_sweep(value["path"], params["radius"])
    if kind == "loft":
        sections = value["sections"]
        require(2 <= len(sections) <= 1024, "Bounded loft sections required")
        first = None
        previous = None
        for section in sections:
            closed(section, "frame profile", "loft section")
            frame(section["frame"])
            section_points = profile(section["profile"])
            rotation = vector(section["frame"]["rotationDegrees"])
            origin = vector(section["frame"]["origin"])
            if first is None:
                first = (rotation, len(section_points), origin)
            require(rotation == first[0] and len(section_points) == first[1], "Native loft requires parallel consistently indexed sections")
            # Dot displacement with the section local +Z normal (Rz*Ry*Rx).
            x, y, z = (math.radians(v) for v in rotation)
            normal = (math.cos(z) * math.sin(y) * math.cos(x) + math.sin(z) * math.sin(x),
                      math.sin(z) * math.sin(y) * math.cos(x) - math.cos(z) * math.sin(x), math.cos(y) * math.cos(x))
            offset = sum((a - b) * n for a, b, n in zip(origin, first[2], normal))
            require(previous is None or offset > previous + 1e-12, "Native loft requires increasing axial sections")
            previous = offset
        # Sections must be translated versions with positive diagonal XY scale.
        # Every intermediate axial section then remains a convex diagonal
        # transform of the original, preventing twisted/intersecting sidewalls.
        base = [vector(p, "xy") for p in sections[0]["profile"]]
        for section in sections[1:]:
            pts = [vector(p, "xy") for p in section["profile"]]
            a, c = base[0], pts[0]
            for k in range(2):
                index = max(range(len(base)), key=lambda i: abs(base[i][k] - a[k]))
                require(abs(base[index][k] - a[k]) > 1e-12, "Degenerate loft profile axis")
                scale = (pts[index][k] - c[k]) / (base[index][k] - a[k])
                require(scale > 0, "Loft correspondence reverses")
                require(all(abs((p[k] - c[k]) - scale * (q[k] - a[k])) <= 1e-10 for p, q in zip(pts, base)), "Native loft requires translated positive diagonal-scaled profiles")
    n = len(points)
    rings = len(value["sections"])
    count = {"box": 8, "cylinder": 2 * int(params.get("segments", 0)), "extrude": 2 * n,
             "revolve": n * (int(params.get("segments", 0)) + 1), "sweep": len(value["path"]) * int(params.get("segments", 0)),
             "loft": rings * (len(value["sections"][0]["profile"]) if rings else 0)}[kind]
    return count, max(12, count * 4)


def validate_design(design, limits):
    closed(design, "schemaVersion id name purpose styleSha256 coordinateSystem numerics budgets observations materials parts subassemblies instances joints mechanisms routes connections sections", "design")
    require(design["schemaVersion"] == "bubo-assembly-design/v1" and design["coordinateSystem"] == "xy-metres-z-up", "Unsupported design schema/coordinates")
    identifier(design["id"])
    require(isinstance(design["styleSha256"], str) and HEX.fullmatch(design["styleSha256"]), "Style identity required")
    closed(design["numerics"], "linearTolerance angularToleranceDegrees minimumFeatureSize maxCoordinateMagnitude", "numerics")
    for val in design["numerics"].values():
        number(val, "numerical policy", 0, 1000000)
        require(val > 0, "Positive numerical policy required")
    budgets = closed(design["budgets"], "maxPartDefinitions maxInstances maxFeatures maxRoutePoints maxDrawings maxValidationChecks maxPreviewBytes maxExportBytes maxGeneratedTriangles maxMotionSamples", "design budgets")
    budget_caps = {"maxPartDefinitions": 4096, "maxInstances": 32768, "maxFeatures": 131072,
                   "maxRoutePoints": 262144, "maxDrawings": 8192, "maxValidationChecks": 10000000,
                   "maxPreviewBytes": 67108864, "maxExportBytes": 17179869184,
                   "maxGeneratedTriangles": 50000000, "maxMotionSamples": 1000000}
    for key, val in budgets.items():
        integer(val, "design budget " + key, 2 if key in ("maxRoutePoints", "maxMotionSamples") else 1, budget_caps[key])
    parts = design["parts"]
    require(isinstance(parts, list) and 1 <= len(parts) <= min(4096, budgets["maxPartDefinitions"]), "Part count budget")
    require(isinstance(design["instances"], list) and 1 <= len(design["instances"]) <= min(131072, budgets["maxInstances"]), "Instance count budget")
    materials = {}
    for mat in design["materials"]:
        closed(mat, "id name color roughness metallic", "material")
        key = identifier(mat["id"])
        require(key not in materials and re.fullmatch(r"#[0-9a-fA-F]{6}", mat["color"]), "Invalid/duplicate material")
        number(mat["roughness"], "roughness", 0, 1)
        number(mat["metallic"], "metallic", 0, 1)
        materials[key] = mat
    ids, source_vertices, source_triangles, feature_count, work = set(), 0, 0, 0, 0
    minimum_triangles = {}
    for part in parts:
        closed(part, "id name purpose detail materialId kind size features interfaces terminals flexible", "part")
        key = identifier(part["id"])
        require(key not in ids and part["materialId"] in materials, "Duplicate part or missing material")
        ids.add(key)
        require(part["kind"] in ("rigid", "wire", "hose", "spring", "belt"), "Unsupported part kind")
        require(all(v > 0 for v in vector(part["size"])), "Positive part envelope required")
        require(isinstance(part["features"], list) and 1 <= len(part["features"]) <= 4096, "Bounded feature list required")
        require(part["features"][0]["operation"] == "add", "First feature must add solid")
        feature_ids = set()
        for item in part["features"]:
            require(item["id"] not in feature_ids, "Duplicate feature")
            feature_ids.add(item["id"])
            # Charge quadratic polygon admission before walking its edges.
            n = len(item.get("profile", []))
            work += n * n + len(item.get("path", [])) ** 2 + sum(len(s.get("profile", [])) ** 2 for s in item.get("sections", [])) + 1
            require(work <= min(budgets["maxValidationChecks"], 5000000), "Native feature work budget")
            v, t = feature(item)
            source_vertices += v
            source_triangles += t
            feature_count += 1
            require(source_vertices <= limits["maxVertices"] and source_triangles <= limits["maxTriangles"] * 4 and feature_count <= budgets["maxFeatures"], "Cumulative source geometry budget")
        minimum_triangles[part["id"]] = 12 if len(part["features"]) == 1 and part["features"][0]["kind"] == "box" else 4
        port_ids = set()
        for collection, terminal in ((part["interfaces"], False), (part["terminals"], True)):
            require(isinstance(collection, list) and len(collection) <= 4096, "Port count budget")
            for port in collection:
                closed(port, "id name kind frame" if terminal else "id name frame", "port")
                key = identifier(port["id"])
                require(key not in port_ids, "Duplicate port")
                port_ids.add(key)
                frame(port["frame"])
                if terminal:
                    require(port["kind"] in ("power", "signal", "ground"), "Unknown terminal")
        if part["flexible"] is not None:
            closed(part["flexible"], "restLength minimumLength maximumLength minimumBendRadius maximumStretchRatio skinSegments", "flexible declaration")
            for key, value in part["flexible"].items():
                if key == "skinSegments":
                    integer(value, key, 2, 4096)
                elif key == "maximumStretchRatio":
                    number(value, key, 1, 100)
                else:
                    number(value, "flexible limit", 0, 1000000)
                    require(value > 0, "Positive flexible limit required")
    instance_ids = set()
    weighted_minimum = 0
    for instance in design["instances"]:
        closed(instance, "id partId parentId frame explodeOffset", "instance")
        key = identifier(instance["id"])
        require(key not in instance_ids and instance["partId"] in ids, "Duplicate instance or unknown part")
        instance_ids.add(key)
        identifier(instance["parentId"])
        frame(instance["frame"])
        vector(instance["explodeOffset"])
        weighted_minimum += minimum_triangles[instance["partId"]]
    require(weighted_minimum <= min(limits["maxTriangles"], budgets["maxGeneratedTriangles"]), "Minimum weighted solid triangle budget")
    validate_non_geometry(design)
    require(len(parts) * 2 + 2 <= limits["maxFiles"], "Output file count budget")
    return ids


def validate_non_geometry(design):
    # The entire transported design stays data-only and closed, including fields
    # unused by independent prototypes. Native Domain validation owns semantics.
    shapes = {
        "observations": "id kind description source", "subassemblies": "id name parentId frame",
        "joints": "id kind parentInstanceId childInstanceId parentInterfaceId childInterfaceId axis minimum maximum rest pitch clearance",
        "mechanisms": "id kind jointIds parameters branch", "routes": "id instanceId points closed from to",
        "connections": "id kind from to routeId", "sections": "id axis offset"}
    enums = {"observations": {"observed", "assumed", "unresolved", "simplified"},
             "joints": {"fixed", "hinge", "rotary", "slider", "screw"},
             "mechanisms": {"spurGears", "rackAndPinion", "pulleyBelt", "sliderCrank", "fourBar"},
             "connections": {"power", "signal", "ground"}}
    for name, fields in shapes.items():
        require(isinstance(design[name], list) and len(design[name]) <= 131072, "Bounded design collection required")
        for value in design[name]:
            closed(value, fields, name)
            identifier(value["id"])
            if name in enums:
                require(value["kind"] in enums[name], "Unknown design enum")
            if "frame" in value:
                frame(value["frame"])
            if "axis" in value:
                if name == "sections":
                    require(value["axis"] in ("x", "y", "z"), "Unknown section axis")
                    number(value["offset"], "section offset")
                else:
                    vector(value["axis"])
                    for key in ("minimum", "maximum", "rest", "pitch", "clearance"):
                        number(value[key], key)
            if name == "mechanisms":
                require(value["branch"] in ("open", "crossed") and isinstance(value["jointIds"], list) and 2 <= len(value["jointIds"]) <= 4, "Unknown mechanism branch/joints")
                keys = {"spurGears": "ratio", "rackAndPinion": "pitchRadius", "pulleyBelt": "ratio", "sliderCrank": "crankLength rodLength offset", "fourBar": "groundLength inputLength couplerLength outputLength"}
                closed(value["parameters"], keys[value["kind"]], "mechanism parameters")
                for val in value["parameters"].values():
                    number(val, "mechanism parameter")
            if name == "routes":
                require(type(value["closed"]) is bool and isinstance(value["points"], list) and 2 <= len(value["points"]) <= 262144, "Invalid route data")
                for point in value["points"]:
                    vector(point)
            for endpoint in ("from", "to"):
                if endpoint in value and value[endpoint] is not None:
                    closed(value[endpoint], "instanceId portId", "endpoint")
                    identifier(value[endpoint]["instanceId"])
                    identifier(value[endpoint]["portId"])


def admit(job_path, job_sha, bundle_dir=None, verify=False):
    require(isinstance(job_sha, str) and HEX.fullmatch(job_sha), "Exact job SHA required")
    path = no_links(job_path)
    require(path.is_file() and 2 <= path.stat().st_size <= MAX_JOB, "Missing/oversized job")
    raw = path.read_bytes()
    require(hashlib.sha256(raw).hexdigest() == job_sha, "Job SHA mismatch")
    job = closed(strict_json(raw), "schemaVersion operation projectRoot output design partIdentities identity bundle limits", "job")
    require(job["schemaVersion"] == "bubo-schematic-part-job/v1" and job["operation"] == "build-parts", "Unsupported part job")
    root = no_links(job["projectRoot"])
    require(root.is_dir() and root.resolve() == root, "Canonical existing project root required")
    confined(root, path)
    output = confined(root, job["output"])
    require(output.is_dir(), "Existing owned output directory required")
    closed(job["identity"], "designSha256 designFingerprint runtimeBuildSha256 toolSha256", "identity")
    for value in job["identity"].values():
        require(isinstance(value, str) and HEX.fullmatch(value), "Exact native identity required")
    limits = closed(job["limits"], "maxVertices maxTriangles maxOutputBytes maxJobBytes maxFiles", "limits")
    integer(limits["maxVertices"], "vertex limit", 1, 2000000)
    integer(limits["maxTriangles"], "triangle limit", 1, 2000000)
    integer(limits["maxOutputBytes"], "byte limit", 1, 16 * 1024 * 1024 * 1024)
    integer(limits["maxJobBytes"], "job limit", 2, MAX_JOB)
    integer(limits["maxFiles"], "file limit", 4, 16384)
    require(len(raw) <= limits["maxJobBytes"], "Declared job byte budget")
    require(isinstance(job["bundle"], dict) and set(job["bundle"]) == set(BUNDLE), "Exact four-file bundle required")
    directory = Path(bundle_dir) if bundle_dir else Path(__file__).parent
    for name in BUNDLE:
        expected = job["bundle"][name]
        require(isinstance(expected, str) and HEX.fullmatch(expected), "Invalid bundle digest")
        file = no_links(directory / name)
        require(file.is_file() and digest(file) == expected, "Fixed bundle SHA mismatch: " + name)
    ids = validate_design(job["design"], limits)
    require(isinstance(job["partIdentities"], dict) and set(job["partIdentities"]) == ids, "Exact part identity inventory required")
    require(all(isinstance(value, str) and HEX.fullmatch(value) for value in job["partIdentities"].values()), "Part identity hash required")
    allowed = {part + suffix for part in ids for suffix in (".blend", ".geometry.bin")}
    if verify:
        allowed.add("parts-report.json")
        require(set(p.name for p in output.iterdir()) == allowed, "Verification requires exact immutable payload inventory")
    else:
        require(not any(output.iterdir()), "Part output directory must be empty")
    output_bytes = 0
    for child in output.iterdir():
        no_links(child)
        require(child.is_file(), "Only regular payload files allowed")
        output_bytes += child.stat().st_size
        require(output_bytes <= limits["maxOutputBytes"], "Existing payload byte budget")
    return job


def check_runtime(job, bpy):
    require(tuple(bpy.app.version[:2]) == (4, 5), "Blender 4.5 LTS required")
    executable = no_links(bpy.app.binary_path)
    require(digest(executable) == job["identity"]["toolSha256"], "Selected Blender executable changed")
    require(not bpy.app.autoexec_fail, "Disabled autoexecution required")
    require(not bpy.context.preferences.filepaths.use_scripts_auto_execute, "Blender script autoexecution must be disabled")
    require(not bpy.app.online_access, "Blender offline mode required")


def arguments(argv):
    require("--" in argv, "Fixed job arguments required")
    values = argv[argv.index("--") + 1:]
    require(len(values) == 2, "Expected absolute job path and job SHA")
    return values
