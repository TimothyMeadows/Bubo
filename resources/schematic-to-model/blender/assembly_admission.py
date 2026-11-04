"""Closed, offline admission for the audited assembly and diagnostic workers."""
import hashlib
import math
from pathlib import Path
import struct

from parts_admission import (HEX, MAX_JOB, arguments, check_runtime, closed, confined,
                             digest, frame, identifier, integer, no_links, number, require,
                             strict_json, validate_design, vector)

BUNDLE = ("assembly_admission.py", "assembly_author.py", "assembly_geometry.py",
          "assembly_motion.py", "assembly_verify.py", "parts_admission.py", "parts_geometry.py")


def validate_assembly_design(design, limits):
    if design.get("schemaVersion") == "bubo-assembly-design/v1":
        return validate_design(design, limits)
    require(design.get("schemaVersion") == "bubo-assembly-design/v2" and "assemblyProfile" in design, "Explicit v2 assembly profile required")
    profile = closed(design["assemblyProfile"], "schemaVersion samplesPerRange samplesPerCycle maxGeometryChecks minimumSeparation contacts flexibleRigs couplingFits", "assembly profile")
    require(profile["schemaVersion"] == "bubo-assembly-profile/v1", "Unsupported assembly profile")
    integer(profile["samplesPerRange"], "profile range samples", 2, limits["maxMotionSamples"])
    integer(profile["samplesPerCycle"], "profile cycle samples", 2, limits["maxMotionSamples"])
    integer(profile["maxGeometryChecks"], "profile geometry work", 1, limits["maxWorkChecks"])
    number(profile["minimumSeparation"], "profile separation", 0, 1000000)
    for collection in ("contacts", "flexibleRigs", "couplingFits"):
        require(isinstance(profile[collection], list) and len(profile[collection]) <= 131072, "Bounded profile collection required")
    for contact in profile["contacts"]:
        closed(contact, "id firstInstanceId secondInstanceId frame size reason", "approved contact zone")
        for key in ("id", "firstInstanceId", "secondInstanceId"):
            identifier(contact[key])
        frame(contact["frame"])
        require(all(v > 0 for v in vector(contact["size"])) and isinstance(contact["reason"], str), "Positive reasoned contact zone required")
    for rig in profile["flexibleRigs"]:
        closed(rig, "routeId minimumSegmentRatio minimumEndpointDistance maximumEndpointDistance driverJointId travelMetresPerDegree", "flexible policy")
        identifier(rig["routeId"])
        number(rig["minimumSegmentRatio"], "segment compression ratio", 0, 1)
        for key in ("minimumEndpointDistance", "maximumEndpointDistance", "travelMetresPerDegree"):
            if rig[key] is not None:
                number(rig[key], key, 0, 1000000)
        if rig["driverJointId"] is not None:
            identifier(rig["driverJointId"])
    for fit in profile["couplingFits"]:
        closed(fit, "mechanismId driverPitchRadius followerPitchRadius driverToothCount followerToothCount", "coupling fit")
        identifier(fit["mechanismId"])
        number(fit["driverPitchRadius"], "driver pitch radius", 0, 1000000)
        if fit["followerPitchRadius"] is not None:
            number(fit["followerPitchRadius"], "follower pitch radius", 0, 1000000)
        for key in ("driverToothCount", "followerToothCount"):
            if fit[key] is not None:
                integer(fit[key], key, 1, 1000000)
    return validate_design(design, limits)


def pose(value):
    closed(value, "origin x y z", "rigid pose")
    origin, x, y, z = (vector(value[k]) for k in ("origin", "x", "y", "z"))
    dot = lambda a, b: sum(u * v for u, v in zip(a, b))
    for a in (x, y, z):
        require(abs(dot(a, a) - 1) <= 1e-7, "Unit rigid basis required")
    require(all(abs(dot(a, b)) <= 1e-7 for a, b in ((x, y), (x, z), (y, z))), "Orthogonal rigid basis required")
    cross = (x[1]*y[2]-x[2]*y[1], x[2]*y[0]-x[0]*y[2], x[0]*y[1]-x[1]*y[0])
    require(dot(cross, z) >= 1-1e-7, "Right handed rigid basis required")


def assembly_pose(value, instance_ids, joint_ids):
    closed(value, "jointValues instances closures", "assembly pose")
    require(isinstance(value["instances"], dict) and set(value["instances"]) == instance_ids, "Exact pose instance inventory required")
    for item in value["instances"].values():
        pose(item)
    require(isinstance(value["jointValues"], dict) and set(value["jointValues"]) == joint_ids, "Exact joint values required")
    for item in value["jointValues"].values():
        number(item, "joint value")
    require(isinstance(value["closures"], list) and len(value["closures"]) == len(joint_ids), "Exact closure inventory required")
    seen = set()
    for item in value["closures"]:
        closed(item, "jointId linearError angularErrorDegrees", "joint closure")
        require(item["jointId"] in joint_ids and item["jointId"] not in seen, "Invalid closure identity")
        seen.add(item["jointId"])
        number(item["linearError"], "closure error", 0)
        number(item["angularErrorDegrees"], "closure angle", 0, 180)


def validate_plan(job):
    design, limits, plan = job["design"], job["limits"], job["plan"]
    ids = {i["id"] for i in design["instances"]}
    joints = {j["id"] for j in design["joints"]}
    closed(plan, "schemaVersion rest clips sampling workChecks", "diagnostic plan")
    require(plan["schemaVersion"] == "bubo-schematic-diagnostic-plan/v1", "Unsupported diagnostic plan")
    integer(plan["workChecks"], "plan work", 0, limits["maxWorkChecks"])
    assembly_pose(plan["rest"], ids, joints)
    require(isinstance(plan["clips"], list) and len(plan["clips"]) <= 2*len(joints), "Bounded diagnostic range/cycle clips required")
    clips, samples = {}, 1
    for clip in plan["clips"]:
        closed(clip, "id driverJointId mechanismId completeCycle samples", "diagnostic clip")
        require(clip["id"] not in clips and clip["driverJointId"] in joints, "Invalid clip identity/driver")
        # Native internal IDs preserve a complete approved 80-character joint
        # after a six-character range/cycle prefix. No names or paths use them.
        if clip["id"] not in ("range-"+clip["driverJointId"], "cycle-"+clip["driverJointId"]):
            identifier(clip["id"])
        require(clip["mechanismId"] is None or clip["mechanismId"] in {m["id"] for m in design["mechanisms"]}, "Unknown clip mechanism")
        require(type(clip["completeCycle"]) is bool and isinstance(clip["samples"], list) and len(clip["samples"]) >= 2, "Finite sampled clip required")
        previous = -1
        for sample in clip["samples"]:
            closed(sample, "timeSeconds pose", "diagnostic sample")
            time = number(sample["timeSeconds"], "diagnostic time", 0, 1)
            require(time > previous, "Strictly ordered diagnostic times required")
            previous = time
            assembly_pose(sample["pose"], ids, joints)
        require(clip["samples"][0]["timeSeconds"] == 0 and previous == 1, "Complete normalized diagnostic range required")
        samples += len(clip["samples"])
        require(samples <= min(limits["maxMotionSamples"], design["budgets"]["maxMotionSamples"]), "Diagnostic sample budget")
        clips[clip["id"]] = clip
    sampling = closed(plan["sampling"], "samplesPerRange samplesPerCycle totalSamples drivers", "sampling")
    integer(sampling["samplesPerRange"], "range sample count", 2, limits["maxMotionSamples"])
    integer(sampling["samplesPerCycle"], "cycle sample count", 2, limits["maxMotionSamples"])
    require(sampling["totalSamples"] == samples, "Sampling total mismatch")
    require(isinstance(sampling["drivers"], list) and len(sampling["drivers"]) <= len(joints), "Bounded driver coverage required")
    for driver in sampling["drivers"]:
        closed(driver, "driverJointId cycleApplicable completeCycleSampled cycleStatus", "driver coverage")
        require(driver["driverJointId"] in joints and type(driver["cycleApplicable"]) is bool and type(driver["completeCycleSampled"]) is bool and isinstance(driver["cycleStatus"], str), "Invalid coverage")
    return clips


def validate_rigs(job, clips):
    from parts_geometry import transform, xyz
    parts = {p["id"]: p for p in job["design"]["parts"]}
    flexible = {i["id"]: parts[i["partId"]] for i in job["design"]["instances"] if parts[i["partId"]]["kind"] != "rigid"}
    require(isinstance(job["rigs"], list) and len(job["rigs"]) == len(flexible), "Exact owned flexible rig inventory required")
    seen, work = set(), 0
    for rig in job["rigs"]:
        closed(rig, "instanceId closedPath radialSegments restRingFrames boneLength clips", "rig program")
        number(rig["boneLength"], "owned bone length", 1e-8, 1000000)
        key = rig["instanceId"]
        require(key in flexible and key not in seen, "Unknown/duplicate flexible rig")
        seen.add(key)
        part = flexible[key]
        require(part["flexible"] is not None and len(part["features"]) == 1, "Flexible mesh requires one approved sweep")
        feature = part["features"][0]
        require(feature["kind"] == "sweep" and feature["operation"] == "add", "Flexible mesh requires additive sweep")
        route = [r for r in job["design"]["routes"] if r["instanceId"] == key]
        require(len(route) == 1 and type(rig["closedPath"]) is bool and rig["closedPath"] == route[0]["closed"], "Exact flexible route required")
        require((feature["path"][0] == feature["path"][-1]) == rig["closedPath"], "Sweep seam and declared route closure differ")
        count = len(feature["path"]) - (1 if rig["closedPath"] else 0)
        require(rig["radialSegments"] == feature["parameters"]["segments"] and part["flexible"]["skinSegments"] == count - (0 if rig["closedPath"] else 1), "Skin segmentation must match approved sweep route")
        require(isinstance(rig["restRingFrames"], list) and len(rig["restRingFrames"]) == count, "One rest frame per actual sweep ring required")
        for frame in rig["restRingFrames"]:
            pose(frame)
        tolerance = job["design"]["numerics"]["linearTolerance"]
        for index, frame in enumerate(rig["restRingFrames"]):
            expected = transform(feature["frame"], xyz(feature["path"][index]))
            require(math.dist(expected, vector(frame["origin"])) <= tolerance, "Rest ring frame must match approved source sweep center")
        require(isinstance(rig["clips"], list) and len(rig["clips"]) == len(clips), "Exact rig clip inventory required")
        seen_clips = set()
        for clip in rig["clips"]:
            closed(clip, "clipId samples", "rig clip")
            require(clip["clipId"] in clips and clip["clipId"] not in seen_clips, "Duplicate/unknown rig clip")
            seen_clips.add(clip["clipId"])
            require(isinstance(clip["samples"], list) and len(clip["samples"]) == len(clips[clip["clipId"]]["samples"]), "Exact rig sample inventory required")
            for index, sample in enumerate(clip["samples"]):
                closed(sample, "sampleIndex ringFrames", "rig sample")
                require(sample["sampleIndex"] == index and isinstance(sample["ringFrames"], list) and len(sample["ringFrames"]) == count, "Exact ring frame sample required")
                for frame in sample["ringFrames"]:
                    pose(frame)
                work += count
                require(work <= job["limits"]["maxWorkChecks"], "Ring program work budget")


def read_geometry(path, limits):
    require(path.stat().st_size <= limits["maxOutputBytes"], "Prototype geometry byte budget")
    with open(path, "rb") as stream:
        header = stream.read(16)
        require(len(header) == 16, "Truncated prototype geometry")
        magic, nv, nt = struct.unpack("<8sII", header)
        require(magic == b"BUBOPRT1" and 4 <= nv <= limits["maxVertices"] and 4 <= nt <= limits["maxTriangles"], "Invalid prototype geometry header/budget")
        require(path.stat().st_size == 16 + nv*24 + nt*12, "Exact binary geometry length required")
        vertices = [struct.unpack("<ddd", stream.read(24)) for _ in range(nv)]
        triangles = [struct.unpack("<III", stream.read(12)) for _ in range(nt)]
    require(all(math.isfinite(c) for v in vertices for c in v) and len(set(vertices)) == nv, "Invalid prototype vertices")
    require(all(len(set(t)) == 3 and max(t) < nv for t in triangles), "Invalid prototype triangles")
    return vertices, triangles


def admit(job_path, job_sha, bundle_dir=None, verify=False):
    require(isinstance(job_sha, str) and HEX.fullmatch(job_sha), "Exact job SHA required")
    path = no_links(job_path)
    require(path.is_file() and 2 <= path.stat().st_size <= MAX_JOB, "Missing/oversized assembly job")
    raw = path.read_bytes()
    require(hashlib.sha256(raw).hexdigest() == job_sha, "Assembly job SHA mismatch")
    job = closed(strict_json(raw), "schemaVersion operation projectRoot output design partsDirectory partsReportSha256 partsVerificationSha256 parts identity bundle limits plan rigs sourceAssembly sourceAssemblySha256", "assembly job")
    require(job["schemaVersion"] == "bubo-schematic-assembly-job/v1" and job["operation"] in ("assemble", "prove-motion"), "Unsupported assembly operation")
    root = no_links(job["projectRoot"])
    require(root.is_dir() and root.resolve() == root, "Canonical existing project root required")
    confined(root, path)
    output = confined(root, job["output"])
    require(output.is_dir(), "Existing owned assembly output required")
    closed(job["identity"], "designSha256 designFingerprint runtimeBuildSha256 toolSha256 partsJobSha256 partsReportSha256 partsVerificationSha256", "assembly identity")
    for value in job["identity"].values():
        require(isinstance(value, str) and HEX.fullmatch(value), "Exact assembly identity required")
    limits = closed(job["limits"], "maxVertices maxTriangles maxOutputBytes maxJobBytes maxFiles maxMotionSamples maxWorkChecks", "assembly limits")
    for key, cap in (("maxVertices", 2000000), ("maxTriangles", 2000000), ("maxOutputBytes", 17179869184), ("maxJobBytes", MAX_JOB), ("maxFiles", 16384), ("maxMotionSamples", 1000000), ("maxWorkChecks", 100000000)):
        integer(limits[key], key, 1, cap)
    require(len(raw) <= limits["maxJobBytes"], "Declared assembly job byte budget")
    require(isinstance(job["bundle"], dict) and set(job["bundle"]) == set(BUNDLE), "Exact seven-file assembly bundle required")
    directory = Path(bundle_dir) if bundle_dir else Path(__file__).parent
    for name in BUNDLE:
        require(isinstance(job["bundle"][name], str) and HEX.fullmatch(job["bundle"][name]) and digest(no_links(directory / name)) == job["bundle"][name], "Fixed assembly bundle SHA mismatch: " + name)
    ids = validate_assembly_design(job["design"], limits)
    hierarchy = {i["id"]: i["parentId"] for i in [*job["design"]["subassemblies"], *job["design"]["instances"]]}
    require(len(hierarchy) == len(job["design"]["subassemblies"])+len(job["design"]["instances"]), "Unique physical/group identities required")
    for key in hierarchy:
        visited, current = set(), key
        while current is not None:
            require(current in hierarchy and current not in visited, "Unknown/cyclic assembly hierarchy")
            visited.add(current)
            current = hierarchy[current]
            require(len(visited) <= min(len(hierarchy), 256), "Bounded assembly hierarchy depth required")
    clips = validate_plan(job)
    validate_rigs(job, clips)
    parts_dir = confined(root, job["partsDirectory"])
    require(parts_dir.is_dir() and parts_dir != output and not output.is_relative_to(parts_dir) and not parts_dir.is_relative_to(output), "Separated immutable parts and assembly paths required")
    for name, key in (("parts-report.json", "partsReportSha256"), ("parts-verification.json", "partsVerificationSha256")):
        report = no_links(parts_dir / name)
        require(report.is_file() and report.stat().st_size <= MAX_JOB and isinstance(job[key], str) and HEX.fullmatch(job[key]) and digest(report) == job[key], "Independent parts report identity changed")
        require(strict_json(report.read_bytes()) == job["parts"], "Transported parts report differs from immutable independently verified report")
    require(job["parts"]["schemaVersion"] == "bubo-schematic-parts/v1" and {p["id"] for p in job["parts"]["parts"]} == ids and len(job["parts"]["parts"]) == len(ids), "Exact independently verified prototype inventory required")
    require(job["identity"]["partsReportSha256"] == job["partsReportSha256"] and job["identity"]["partsVerificationSha256"] == job["partsVerificationSha256"] and job["identity"]["partsJobSha256"] == job["parts"]["jobSha256"], "Exact native parts ancestry required")
    geometry_counts = {}
    for part in job["parts"]["parts"]:
        for file_key, hash_key, suffix in (("blendFile", "blendSha256", ".blend"), ("geometryFile", "geometrySha256", ".geometry.bin")):
            require(part[file_key] == part["id"] + suffix, "Unsafe prototype payload filename")
            file = no_links(parts_dir / part[file_key])
            require(file.is_file() and file.stat().st_size <= limits["maxOutputBytes"] and digest(file) == part[hash_key], "Immutable prototype payload changed")
        vertices, triangles = read_geometry(parts_dir / part["geometryFile"], limits)
        geometry_counts[part["id"]] = (len(vertices), len(triangles), (parts_dir/part["geometryFile"]).stat().st_size)
    physical_vertices = sum(geometry_counts[i["partId"]][0] for i in job["design"]["instances"])
    physical_triangles = sum(geometry_counts[i["partId"]][1] for i in job["design"]["instances"])
    physical_bytes = sum(geometry_counts[i["partId"]][2] for i in job["design"]["instances"])
    require(physical_vertices <= limits["maxVertices"] and physical_triangles <= min(limits["maxTriangles"],job["design"]["budgets"]["maxGeneratedTriangles"]), "Physical per-pose prototype allocation budget")
    poses_written = 1 if job["operation"] == "assemble" else job["plan"]["sampling"]["totalSamples"]
    require(3+len(job["design"]["instances"])*poses_written <= limits["maxFiles"] and physical_bytes*poses_written <= limits["maxOutputBytes"], "Complete diagnostic file/geometry byte budget")
    require((physical_vertices+physical_triangles)*poses_written+job["plan"]["workChecks"] <= limits["maxWorkChecks"], "Complete diagnostic geometry work budget")
    if job["operation"] == "assemble":
        require(job["sourceAssembly"] is None and job["sourceAssemblySha256"] is None, "Assembly cannot import another source")
    else:
        source = confined(root, job["sourceAssembly"])
        require(source.is_file() and source.suffix == ".blend" and source.stat().st_size <= limits["maxOutputBytes"] and source.parent != output, "Exact separate source assembly required")
        require(isinstance(job["sourceAssemblySha256"], str) and HEX.fullmatch(job["sourceAssemblySha256"]) and digest(source) == job["sourceAssemblySha256"], "Source assembly SHA mismatch")
    if verify:
        require(any(output.iterdir()), "Independent verification requires a saved payload")
    else:
        require(not any(output.iterdir()), "Immutable assembly output must be empty")
    total = 0
    for child in output.iterdir():
        no_links(child)
        require(child.is_file(), "Only regular assembly payload files allowed")
        total += child.stat().st_size
    require(total <= limits["maxOutputBytes"] and len(list(output.iterdir())) <= limits["maxFiles"], "Existing assembly output budget")
    return job
