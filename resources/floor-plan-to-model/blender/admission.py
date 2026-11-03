"""Closed data/path admission for the audited floor-plan Blender authorer.

This module has no Blender dependency. It validates a native compiler job; it
does not authenticate human approval, certify navigation or independently
re-import a model. These cooperative checks are not an operating-system sandbox.
"""

import hashlib
import json
import math
import os
from pathlib import Path
import re
import stat
import struct


MAX_JOB_BYTES = 256 * 1024 * 1024
MAX_REPORT_BYTES = 8 * 1024 * 1024
MAX_OBJECTS = 20_000
MAX_MATERIALS = 4096
MAX_VERTICES = 2_000_000
MAX_TRIANGLES = 4_000_000
MAX_OUTPUT_BYTES = 16 * 1024 * 1024 * 1024
MAX_COORDINATE = 1_000_000_000
HASH = re.compile(r"[0-9a-f]{64}\Z")


def fail(message):
    raise ValueError("Floor-plan authoring admission: " + message)


def closed(value, required, optional=(), label="object"):
    if not isinstance(value, dict):
        fail(label + " must be an object")
    required = set(required)
    if not required <= value.keys() or not value.keys() <= required | set(optional):
        fail(label + " has missing or unknown fields")
    return value


def text(value, label, maximum=512):
    if (not isinstance(value, str) or not 1 <= len(value) <= maximum or
            len(value.encode("utf-8")) > maximum * 4 or
            any(ord(character) < 32 or ord(character) == 127 for character in value)):
        fail(label + " must be bounded nonempty text without controls")
    return value


def integer(value, label, lower=0, upper=2**63 - 1):
    if type(value) is not int or not lower <= value <= upper:
        fail(label + " must be a bounded integer")
    return value


def number(value, label, lower=-MAX_COORDINATE, upper=MAX_COORDINATE):
    if type(value) not in (int, float) or not math.isfinite(value) or not lower <= value <= upper:
        fail(label + " must be a finite bounded number")
    return float(value)


def point(value, label):
    closed(value, ("x", "y", "z"), label=label)
    return tuple(number(value[axis], label + "." + axis) for axis in ("x", "y", "z"))


def point_object(value):
    return dict(zip(("x", "y", "z"), value))


def hash_value(value, label):
    if not isinstance(value, str) or HASH.fullmatch(value) is None:
        fail(label + " must be lowercase SHA-256")
    return value


def array(value, label, maximum, minimum=0):
    if not isinstance(value, list) or not minimum <= len(value) <= maximum:
        fail(label + " must be a bounded array")
    return value


def _pairs(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            fail("duplicate JSON property: " + key)
        result[key] = value
    return result


def decode_json(raw):
    if not isinstance(raw, bytes) or not 2 <= len(raw) <= MAX_JOB_BYTES:
        fail("missing or oversized job bytes")
    depth = 0
    quoted = escaped = False
    for value in raw:
        if quoted:
            if escaped:
                escaped = False
            elif value == 92:
                escaped = True
            elif value == 34:
                quoted = False
        elif value == 34:
            quoted = True
        elif value in (123, 91):
            depth += 1
            if depth > 64:
                fail("JSON depth exceeds 64")
        elif value in (125, 93):
            depth -= 1
    try:
        return json.loads(raw.decode("utf-8"), object_pairs_hook=_pairs,
                          parse_constant=lambda value: fail("non-finite JSON constant: " + value))
    except (UnicodeError, RecursionError, json.JSONDecodeError) as error:
        fail("invalid bounded UTF-8 JSON: " + str(error))


def safe_absolute(value, label, must_exist=True):
    text(value, label, 4096)
    normalized = value.replace("\\", "/")
    if normalized.startswith("//") or any(part.lower() in (".", "..", ".git") for part in normalized.split("/")):
        fail(label + " contains a path alias or traversal")
    path = Path(value)
    if not path.is_absolute():
        fail(label + " must be absolute")
    for part in path.parts[1:]:
        if part.endswith((".", " ")) or ":" in part:
            fail(label + " contains an ambiguous component")
        stem = part.split(".", 1)[0].upper()
        if stem in {"CON", "PRN", "AUX", "NUL", *["COM" + str(n) for n in range(1, 10)],
                    *["LPT" + str(n) for n in range(1, 10)]}:
            fail(label + " contains a reserved component")
    current = Path(path.anchor)
    for component in path.parts[1:]:
        current /= component
        try:
            info = current.lstat()
        except FileNotFoundError:
            continue
        if (stat.S_ISLNK(info.st_mode) or
                getattr(info, "st_file_attributes", 0) & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)):
            fail(label + " contains a symbolic link or reparse point")
    try:
        resolved = path.resolve(strict=must_exist)
    except (OSError, RuntimeError) as error:
        fail(label + " cannot be resolved: " + str(error))
    if must_exist and not resolved.exists():
        fail(label + " is missing")
    return resolved


def validate_locations(job, job_path):
    root = safe_absolute(job["workspaceRoot"], "workspaceRoot")
    if not root.is_dir() or root == Path(root.anchor):
        fail("workspaceRoot must be a specific existing project directory")
    source = safe_absolute(str(job_path), "job path")
    if not source.is_relative_to(root) or source.parent == root or source.name != "job.json" or not source.is_file():
        fail("job must be an owned nested job.json inside workspaceRoot")
    output = safe_absolute(job["outputDirectory"], "outputDirectory", must_exist=False)
    if output != source.parent / "output" or not output.is_relative_to(root):
        fail("output must be the same owned attempt's output child")
    if output.exists() or output.is_symlink():
        fail("output already exists; attempts cannot overwrite")
    return root, source, output


def vector_min(values):
    return tuple(min(value[axis] for value in values) for axis in range(3))


def vector_max(values):
    return tuple(max(value[axis] for value in values) for axis in range(3))


def _subtract(left, right):
    return tuple(left[axis] - right[axis] for axis in range(3))


def _cross(left, right):
    return (left[1] * right[2] - left[2] * right[1],
            left[2] * right[0] - left[0] * right[2],
            left[0] * right[1] - left[1] * right[0])


def index_sha256(indices):
    digest = hashlib.sha256()
    for value in indices:
        digest.update(struct.pack("<I", value))
    return digest.hexdigest()


def geometry_sha256(vertices, indices):
    digest = hashlib.sha256()
    for vertex in vertices:
        digest.update(struct.pack("<ddd", *(value if value != 0 else 0.0 for value in vertex)))
    for value in indices:
        digest.update(struct.pack("<I", value))
    return digest.hexdigest()


def material_assignments_sha256(material_ids):
    digest = hashlib.sha256()
    for value in material_ids:
        encoded = value.encode("utf-8")
        digest.update(struct.pack("<I", len(encoded)))
        digest.update(encoded)
    return digest.hexdigest()


def inspect_triangle_solid(vertices, indices):
    """Check index-manifold oriented shells without welding touching cells."""
    if len(vertices) < 4 or len(indices) < 12 or len(indices) % 3:
        fail("mesh must contain closed triangle solids")
    edges = {}
    faces = set()
    used = set()
    adjacency = [[] for _ in range(len(indices) // 3)]
    vertex_faces = [[] for _ in vertices]
    for offset in range(0, len(indices), 3):
        triangle = indices[offset:offset + 3]
        face_index = offset // 3
        for vertex in triangle:
            integer(vertex, "triangle index", 0, len(vertices) - 1)
            used.add(vertex)
            vertex_faces[vertex].append(face_index)
        key = tuple(sorted(triangle))
        if len(set(triangle)) != 3 or key in faces:
            fail("duplicate or collapsed triangle")
        faces.add(key)
        a, b, c = (vertices[index] for index in triangle)
        normal = _cross(_subtract(b, a), _subtract(c, a))
        if not any(component != 0 for component in normal):
            fail("zero-area triangle")
        for start, end in zip(triangle, triangle[1:] + triangle[:1]):
            edge = (min(start, end), max(start, end))
            incidence = edges.setdefault(edge, [])
            incidence.append((face_index, start < end))
            if len(incidence) > 2:
                fail("non-manifold triangle edge")
    if len(used) != len(vertices):
        fail("unreferenced geometry vertex")
    for incidence in edges.values():
        if len(incidence) != 2 or incidence[0][1] == incidence[1][1]:
            fail("open or inconsistently oriented triangle shell")
        left, right = incidence[0][0], incidence[1][0]
        adjacency[left].append(right)
        adjacency[right].append(left)
    for incidence in vertex_faces:
        face_set = set(incidence)
        pending, visited_faces = [incidence[0]], set()
        while pending:
            current = pending.pop()
            if current in visited_faces:
                continue
            visited_faces.add(current)
            neighbors = [neighbor for neighbor in adjacency[current] if neighbor in face_set]
            if len(neighbors) != 2:
                fail("non-manifold vertex fan")
            pending.extend(neighbor for neighbor in neighbors if neighbor not in visited_faces)
        if visited_faces != face_set:
            fail("disconnected vertex fan")
    visited = set()
    volumes = []
    for first in range(len(adjacency)):
        if first in visited:
            continue
        origin = vertices[indices[first * 3]]
        pending = [first]
        volume_terms = []
        while pending:
            current = pending.pop()
            if current in visited:
                continue
            visited.add(current)
            a, b, c = (vertices[index] for index in indices[current * 3:current * 3 + 3])
            a, b, c = (_subtract(vertex, origin) for vertex in (a, b, c))
            cross = _cross(b, c)
            volume_terms.append(sum(a[axis] * cross[axis] for axis in range(3)) / 6)
            pending.extend(neighbor for neighbor in adjacency[current] if neighbor not in visited)
        volume = math.fsum(volume_terms)
        if not math.isfinite(volume) or volume <= 0:
            fail("triangle shell has no positive oriented enclosed volume")
        volumes.append(volume)
    return {"componentCount": len(volumes), "minimumComponentVolume": min(volumes), "manifold": True}


def validate_clearance(value, anchor_ids, tolerance):
    closed(value, ("routes", "doorSweeps", "minimumRouteClearance"), label="clearance")
    number(value["minimumRouteClearance"], "minimumRouteClearance", -tolerance)
    for route in array(value["routes"], "routes", MAX_OBJECTS):
        closed(route, ("roomId", "fromId", "toId", "points", "radius", "height", "minimumClearance"), label="route")
        for key in ("roomId", "fromId", "toId"):
            text(route[key], "route." + key)
        for vertex in array(route["points"], "route.points", MAX_VERTICES, 1):
            point(vertex, "route point")
        number(route["radius"], "route radius", 1e-12)
        number(route["height"], "route height", 1e-12)
        number(route["minimumClearance"], "route minimumClearance", -tolerance)
    for sweep in array(value["doorSweeps"], "doorSweeps", MAX_OBJECTS):
        closed(sweep, ("openingId", "anchorId", "angleDegrees", "minimumClearance", "boundsMinimum", "boundsMaximum"), label="door sweep")
        text(sweep["openingId"], "sweep openingId")
        text(sweep["anchorId"], "sweep anchorId")
        if sweep["anchorId"] not in anchor_ids:
            fail("door sweep refers to an unknown anchor")
        angle = number(sweep["angleDegrees"], "door open angle", -180, 180)
        if abs(angle) < 90:
            fail("door sweep magnitude must be between 90 and 180 degrees")
        number(sweep["minimumClearance"], "door minimumClearance", -tolerance)
        lower, upper = point(sweep["boundsMinimum"], "sweep minimum"), point(sweep["boundsMaximum"], "sweep maximum")
        if any(left > right for left, right in zip(lower, upper)):
            fail("door sweep bounds are inverted")


class AdmittedJob:
    def __init__(self, job, root, source, output, mesh_stats):
        self.job = job
        self.workspace = root
        self.source = source
        self.output = output
        self.mesh_stats = mesh_stats


def validate_job(job, job_path, raw_length):
    closed(job, ("schemaVersion", "operation", "workspaceRoot", "outputDirectory", "scene", "identities", "bundle", "limits"), label="job")
    if job["schemaVersion"] != "bubo-floor-plan-blender-job/v1" or job["operation"] != "construct":
        fail("unsupported construction job schema or operation")
    closed(job["identities"], ("planSha256", "designFingerprint", "runtimeSha256"), label="identities")
    for key, value in job["identities"].items():
        hash_value(value, key)
    closed(job["bundle"], ("author.py", "admission.py"), label="bundle")
    for key, value in job["bundle"].items():
        hash_value(value, "bundle " + key)
    limits = closed(job["limits"], ("maxTriangles", "maxVertices", "maxObjects", "maxOutputBytes", "maxJobBytes"), label="limits")
    for key, cap in (("maxTriangles", MAX_TRIANGLES), ("maxVertices", MAX_VERTICES), ("maxObjects", MAX_OBJECTS),
                     ("maxOutputBytes", MAX_OUTPUT_BYTES), ("maxJobBytes", MAX_JOB_BYTES)):
        integer(limits[key], key, 1, cap)
    integer(raw_length, "raw job length", 2, limits["maxJobBytes"])
    root, source, output = validate_locations(job, job_path)
    scene = closed(job["scene"], ("schemaVersion", "planId", "linearTolerance", "materials", "meshes", "anchors", "metrics", "clearance"), label="scene")
    if scene["schemaVersion"] != "bubo-floor-plan-construction-scene/v1":
        fail("unsupported construction scene schema")
    text(scene["planId"], "planId")
    tolerance = number(scene["linearTolerance"], "linearTolerance", 1e-12, 1)
    materials = {}
    for material in array(scene["materials"], "materials", MAX_MATERIALS, 1):
        closed(material, ("id", "name", "color", "roughness", "metallic", "transmission", "ior"), label="material")
        identifier = text(material["id"], "material id")
        if identifier in materials:
            fail("duplicate material id")
        materials[identifier] = material
        text(material["name"], "material name")
        if not isinstance(material["color"], str) or re.fullmatch(r"#[0-9a-fA-F]{6}", material["color"]) is None:
            fail("material color must be #RRGGBB")
        for key in ("roughness", "metallic", "transmission"):
            number(material[key], "material " + key, 0, 1)
        number(material["ior"], "material ior", 1, 3)
    anchors = {}
    for anchor in array(scene["anchors"], "anchors", limits["maxObjects"]):
        closed(anchor, ("id", "semanticId", "storeyId", "kind", "position", "axis", "rotationRadians"), label="anchor")
        identifier = text(anchor["id"], "anchor id")
        if identifier in anchors:
            fail("duplicate anchor id")
        anchors[identifier] = anchor
        for key in ("semanticId", "storeyId", "kind"):
            text(anchor[key], "anchor " + key)
        point(anchor["position"], "anchor position")
        if point(anchor["axis"], "anchor axis") != (0, 0, 1) or number(anchor["rotationRadians"], "anchor rotation") != 0:
            fail("construction anchors require initial zero rotation about world Z")
    meshes = array(scene["meshes"], "meshes", limits["maxObjects"], 1)
    if len(meshes) + len(anchors) > limits["maxObjects"]:
        fail("mesh and anchor count exceeds maxObjects")
    mesh_stats = {}
    vertex_count = triangle_count = visual_count = collision_count = 0
    minima, maxima = [], []
    for mesh in meshes:
        closed(mesh, ("id", "semanticId", "storeyId", "kind", "representation", "collisionRole", "materialId", "vertices", "triangles", "parentAnchorId"), ("triangleMaterialIds",), label="mesh")
        identifier = text(mesh["id"], "mesh id")
        if identifier in mesh_stats or identifier in anchors:
            fail("duplicate mesh/anchor identity")
        for key in ("semanticId", "storeyId", "kind"):
            text(mesh[key], "mesh " + key)
        if mesh["representation"] not in ("visual", "collision") or mesh["collisionRole"] not in ("static", "dynamic", "none"):
            fail("unknown mesh representation/collision role")
        if mesh["representation"] == "collision" and mesh["collisionRole"] == "none":
            fail("collision mesh must declare its static or dynamic role")
        text(mesh["materialId"], "mesh materialId")
        if mesh["materialId"] not in materials:
            fail("mesh refers to an unknown material")
        parent = mesh["parentAnchorId"]
        if parent is not None:
            text(parent, "mesh parentAnchorId")
        if parent is not None and (parent not in anchors or anchors[parent]["storeyId"] != mesh["storeyId"]):
            fail("mesh parent must be a declared anchor in its storey")
        raw_vertices = array(mesh["vertices"], "vertices", limits["maxVertices"], 4)
        indices = array(mesh["triangles"], "triangles", limits["maxTriangles"] * 3, 12)
        vertex_count += len(raw_vertices)
        triangle_count += len(indices) // 3
        if vertex_count > limits["maxVertices"] or triangle_count > limits["maxTriangles"]:
            fail("scene exceeds approved vertex/triangle bounds")
        vertices = [point(value, "mesh vertex") for value in raw_vertices]
        statistics = inspect_triangle_solid(vertices, indices)
        overrides = mesh.get("triangleMaterialIds")
        if overrides is not None:
            array(overrides, "triangleMaterialIds", limits["maxTriangles"])
            for value in overrides:
                text(value, "triangle material id")
            if len(overrides) != len(indices) // 3 or any(value not in materials for value in overrides):
                fail("triangle material overrides must match every triangle and declared material")
        assigned_materials = overrides if overrides is not None else [mesh["materialId"]] * (len(indices) // 3)
        lower, upper = vector_min(vertices), vector_max(vertices)
        statistics.update(minimum=lower, maximum=upper,
                          geometrySha256=geometry_sha256(vertices, indices), indexSha256=index_sha256(indices),
                          materialAssignmentsSha256=material_assignments_sha256(assigned_materials))
        mesh_stats[identifier] = statistics
        minima.append(lower)
        maxima.append(upper)
        if mesh["representation"] == "visual":
            visual_count += len(indices) // 3
        else:
            collision_count += len(indices) // 3
    metrics = closed(scene["metrics"], ("meshCount", "vertexCount", "triangleCount", "visualTriangleCount", "collisionTriangleCount", "geometryChecks", "minimum", "maximum"), label="metrics")
    for key, expected in (("meshCount", len(meshes)), ("vertexCount", vertex_count), ("triangleCount", triangle_count),
                          ("visualTriangleCount", visual_count), ("collisionTriangleCount", collision_count)):
        if integer(metrics[key], key) != expected:
            fail("stale native metric: " + key)
    integer(metrics["geometryChecks"], "geometryChecks")
    if point(metrics["minimum"], "metrics minimum") != vector_min(minima) or point(metrics["maximum"], "metrics maximum") != vector_max(maxima):
        fail("stale native scene bounds")
    validate_clearance(scene["clearance"], anchors, tolerance)
    return AdmittedJob(job, root, source, output, mesh_stats)


def hash_file(path, maximum=MAX_OUTPUT_BYTES):
    digest = hashlib.sha256()
    total = 0
    with Path(path).open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            total += len(chunk)
            if total > maximum:
                fail("file exceeds bounded hash admission")
            digest.update(chunk)
    return digest.hexdigest()


def verify_current_sources(admitted, expected_job_hash, bundle_directory):
    source = safe_absolute(str(admitted.source), "job path")
    if not source.is_relative_to(admitted.workspace) or hash_file(source, admitted.job["limits"]["maxJobBytes"]) != expected_job_hash:
        fail("job bytes changed during construction")
    if source.stat().st_size > admitted.job["limits"]["maxJobBytes"]:
        fail("job grew beyond approved bytes")
    for name, expected in admitted.job["bundle"].items():
        path = safe_absolute(str(Path(bundle_directory) / name), "audited bundle resource")
        if not path.is_file() or hash_file(path, 2 * 1024 * 1024) != expected:
            fail("audited authoring bundle changed")
    safe_absolute(str(admitted.output.parent), "output parent")
    current_output = safe_absolute(str(admitted.output), "output directory", must_exist=False)
    if current_output != admitted.output or (current_output.exists() and not current_output.is_dir()):
        fail("output directory identity/type changed")


def create_output(admitted):
    """Exclusive directory creation after complete data admission; never clean/reuse."""
    safe_absolute(str(admitted.output.parent), "output parent")
    if admitted.output.exists() or admitted.output.is_symlink():
        fail("output appeared before creation")
    admitted.output.mkdir(mode=0o700)
    return safe_absolute(str(admitted.output), "created output")


def write_report(path, report, output_budget, blend_bytes):
    """Bound the actual UTF-8 report while encoding; retain failures, never truncate."""
    path = Path(path)
    if path.name != "construction-report.json" or path.exists() or path.is_symlink():
        fail("invalid or existing report destination")
    safe_absolute(str(path.parent), "report parent")
    partial = path.with_name("construction-report.json.partial")
    maximum = min(MAX_REPORT_BYTES, output_budget - blend_bytes)
    if maximum <= 0:
        fail("Blender source consumes the output byte allowance")
    total = 0
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0)
    descriptor = os.open(partial, flags, 0o600)
    with os.fdopen(descriptor, "wb") as handle:
        for fragment in json.JSONEncoder(ensure_ascii=False, allow_nan=False, sort_keys=True, separators=(",", ":")).iterencode(report):
            encoded = fragment.encode("utf-8")
            total += len(encoded)
            if total + 1 > maximum:
                fail("actual report exceeds its bounded UTF-8/output allowance")
            handle.write(encoded)
        handle.write(b"\n")
        handle.flush()
        os.fsync(handle.fileno())
    if path.exists() or path.is_symlink():
        fail("report destination appeared during construction")
    # An exclusive hard-link publication cannot replace a destination which
    # appeared after the existence check (unlike POSIX rename).
    os.link(partial, path)
    partial.unlink()
    return total + 1
