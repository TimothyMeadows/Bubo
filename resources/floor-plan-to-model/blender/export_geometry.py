"""Bounded portable building geometry verification; deliberately independent of bpy.

Positions and matrices returned by this module use the native XY/Z-up frame.
Triangle correspondence preserves orientation and multiplicity, but does not
claim preservation of exporter-dependent vertex indexing or engine traversal.
"""

import itertools
import json
import math
from pathlib import Path
import stat
import struct


MATERIAL_EPSILON = 2 ** -20
MAX_GLB_BYTES = 512 * 1024 * 1024
MAX_JSON_BYTES = 64 * 1024 * 1024
MAX_NEIGHBOR_CANDIDATES = 256
MAX_COORDINATE = 1_000_000_000
_IDENTITY = [1., 0., 0., 0., 0., 1., 0., 0., 0., 0., 1., 0., 0., 0., 0., 1.]
_META = ("id", "semanticId", "storeyId", "kind", "representation", "collisionRole", "materialId", "parentAnchorId")
_NODE_KEYS = _META + ("parentId", "position", "axis", "worldMatrix")
_EXTENSIONS = {"KHR_materials_transmission", "KHR_materials_ior"}
_INDEX_MAXIMUM = {5121: 255, 5123: 65535, 5125: 4294967295}
_CAPS = {"maxVertices": 2_000_000, "maxTriangles": 4_000_000, "maxNodes": 100_000,
         "maxMeshes": 20_000, "maxMaterials": 4096, "maxAccessors": 100_000,
         "maxPrimitives": 100_000, "maxGeometryChecks": 2_000_000_000,
         "maxOutputBytes": 16 * 1024 * 1024 * 1024}


def _fail(message):
    raise ValueError("Floor-plan export geometry: " + message)


def _closed(value, required, optional=(), label="object"):
    if not isinstance(value, dict) or not set(required) <= value.keys() or not value.keys() <= set(required) | set(optional):
        _fail(label + " has missing or unknown fields")
    return value


def _integer(value, label, low=0, high=2**63 - 1):
    if type(value) is not int or not low <= value <= high:
        _fail(label + " must be a bounded integer")
    return value


def _number(value, label, low=-MAX_COORDINATE, high=MAX_COORDINATE):
    if type(value) not in (int, float) or not math.isfinite(value) or not low <= value <= high:
        _fail(label + " must be finite and bounded")
    return float(value)


def _array(value, label, maximum, minimum=0):
    if not isinstance(value, list) or not minimum <= len(value) <= maximum:
        _fail(label + " must be a bounded array")
    return value


def _text(value, label, empty=False):
    if (not isinstance(value, str) or not (0 if empty else 1) <= len(value) <= 512 or
            any(ord(c) < 32 or ord(c) == 127 for c in value)):
        _fail(label + " must be bounded text")
    return value


def _optional_names(value):
    for key in ("name", "generator", "copyright"):
        if key in value:
            _text(value[key], key, True)


def _point(value, label):
    if isinstance(value, dict):
        _closed(value, ("x", "y", "z"), label=label)
        value = [value[key] for key in ("x", "y", "z")]
    _array(value, label, 3, 3)
    return tuple(_number(component, label) for component in value)


def _point_object(value):
    return dict(zip(("x", "y", "z"), value))


class _Budget:
    def __init__(self, limits):
        if not isinstance(limits, dict):
            _fail("limits must be an object")
        self.limits = {key: _integer(limits.get(key), key, 1, maximum) for key, maximum in _CAPS.items()}
        self.checks = 0

    def use(self, count=1):
        self.checks += count
        if self.checks > self.limits["maxGeometryChecks"]:
            _fail("geometry work cap exhausted")


def _tolerance(scene, value):
    value = _number(value, "linear tolerance", 1e-12, 1)
    if value != scene.get("linearTolerance"):
        _fail("linear tolerance differs from the admitted native scene")
    return value


def _materials_used(scene, representation):
    return {identifier for mesh in scene["meshes"] if representation is None or mesh["representation"] == representation
            for identifier in (mesh.get("triangleMaterialIds") or [mesh["materialId"]])}


def _srgb(value):
    return value / 12.92 if value <= .04045 else ((value + .055) / 1.055) ** 2.4


def compare_materials(scene, actual_materials, representation=None):
    """Rows: id, linear RGBA color, roughness, metallic, transmission, ior."""
    expected = {m["id"]: m for m in scene["materials"] if m["id"] in _materials_used(scene, representation)}
    if len(actual_materials) != len(expected):
        _fail("material inventory differs from native assignments")
    seen, result = set(), []
    for actual in actual_materials:
        _closed(actual, ("id", "color", "roughness", "metallic", "transmission", "ior"), label="measured material")
        identifier = _text(actual["id"], "material id")
        if identifier in seen or identifier not in expected:
            _fail("unexpected or duplicate material id")
        seen.add(identifier)
        source = expected[identifier]
        color = [_srgb(int(source["color"][i:i + 2], 16) / 255) for i in (1, 3, 5)] + [1.]
        _array(actual["color"], "material color", 4, 4)
        for value, target in zip(actual["color"], color):
            if abs(_number(value, "material color", 0, 1) - target) > MATERIAL_EPSILON:
                _fail("material linear color changed: " + identifier)
        for key in ("roughness", "metallic", "transmission", "ior"):
            if abs(_number(actual[key], "material " + key, 0, 3) - source[key]) > MATERIAL_EPSILON:
                _fail("material " + key + " changed: " + identifier)
        result.append(actual)
    return sorted(result, key=lambda row: row["id"])


def _translation(position):
    result = _IDENTITY.copy()
    result[3], result[7], result[11] = position
    return result


def expected_nodes(scene, representation):
    """Full measured-node row contract, also used by the fixed producer adapter."""
    if representation not in ("visual", "collision"):
        _fail("unknown representation")
    rows = []

    def add(identifier, kind, parent, storey="", role="", source=None, position=(0., 0., 0.)):
        row = {key: "" for key in _META}
        row.update(id=identifier, kind=kind, parentId=parent, storeyId=storey, representation=role,
                   position=list(position), axis=[0., 0., 1.], worldMatrix=_translation(position))
        if source is not None:
            for key in _META:
                if key in source:
                    row[key] = source[key] if source[key] is not None else ""
        rows.append(row)

    root = "building:" + scene["planId"]
    add(root, "building", None)
    storeys = sorted({m["storeyId"] for m in scene["meshes"] + scene["anchors"]})
    for storey in storeys:
        floor = "storey:" + storey
        add(floor, "storey", root, storey)
        roles = sorted({m["representation"] if representation == "visual" else "collision-" + m["collisionRole"]
                        for m in scene["meshes"] if m["representation"] == representation and m["storeyId"] == storey})
        for role in roles:
            add("representation:" + role + ":" + storey, "representation", floor, storey, role)
    anchors = {anchor["id"]: anchor for anchor in scene["anchors"]}
    for anchor in scene["anchors"]:
        add(anchor["id"], anchor["kind"], "storey:" + anchor["storeyId"], source=anchor,
            position=_point(anchor["position"], "source anchor"))
    for mesh in scene["meshes"]:
        if mesh["representation"] != representation:
            continue
        role = "visual" if representation == "visual" else "collision-" + mesh["collisionRole"]
        parent = mesh["parentAnchorId"] or "representation:" + role + ":" + mesh["storeyId"]
        position = _point(anchors[mesh["parentAnchorId"]]["position"], "source anchor") if mesh["parentAnchorId"] else (0., 0., 0.)
        add(mesh["id"], mesh["kind"], parent, source=mesh, position=position)
    if len({row["id"] for row in rows}) != len(rows):
        _fail("native and structural node IDs collide")
    return rows


def compare_nodes(scene, representation, actual_nodes, linear_tolerance, angular_tolerance_degrees=.001):
    """Compare exact metadata/parents and full source-frame world transforms."""
    tolerance = _tolerance(scene, linear_tolerance)
    angle = _number(angular_tolerance_degrees, "angular tolerance degrees", 1e-9, 1)
    expected = {row["id"]: row for row in expected_nodes(scene, representation)}
    if len(actual_nodes) != len(expected):
        _fail("node inventory differs from native hierarchy")
    seen, maximum = set(), 0.
    for row in actual_nodes:
        _closed(row, _NODE_KEYS, label="measured node")
        identifier = _text(row["id"], "node id")
        if identifier in seen or identifier not in expected:
            _fail("unexpected or duplicate node id")
        seen.add(identifier)
        source = expected[identifier]
        if any(row[key] != source[key] for key in _META + ("parentId",)):
            _fail("node semantic metadata or parent changed: " + identifier)
        matrix = [_number(v, "node matrix") for v in _array(row["worldMatrix"], "node matrix", 16, 16)]
        if matrix[12:] != [0., 0., 0., 1.]:
            _fail("non-affine node world transform")
        # All approved construction frames are unit, unreflected and initially
        # unrotated. Translation and angular tolerances have separate units.
        columns = [tuple(matrix[r * 4 + c] for r in range(3)) for c in range(3)]
        for column in columns:
            if abs(math.hypot(*column) - 1) > 1e-6:
                _fail("node scale changed")
        for left, right in ((0, 1), (0, 2), (1, 2)):
            if abs(sum(a * b for a, b in zip(columns[left], columns[right]))) > 1e-6:
                _fail("node shear changed")
        for index, column in enumerate(columns):
            dot = max(-1., min(1., column[index] / math.hypot(*column)))
            if math.degrees(math.acos(dot)) > angle:
                _fail("node orientation or reflection changed")
        position = tuple(matrix[i] for i in (3, 7, 11))
        error = math.dist(position, source["position"])
        maximum = max(maximum, error)
        if error > tolerance or math.dist(_point(row["position"], "node position"), position) > tolerance:
            _fail("node pivot/world position changed")
        axis = _point(row["axis"], "node axis")
        if math.dist(axis, columns[2]) > 1e-6:
            _fail("node measured axis disagrees with world matrix")
    return {"nodes": sorted(actual_nodes, key=lambda row: row["id"]), "maxNodePositionError": maximum}


def _canonical(triangle):
    return min(triangle, triangle[1:] + triangle[:1], triangle[2:] + triangle[:2])


def _normal(triangle):
    a, b, c = triangle
    u = tuple(b[i] - a[i] for i in range(3))
    v = tuple(c[i] - a[i] for i in range(3))
    normal = (u[1]*v[2] - u[2]*v[1], u[2]*v[0] - u[0]*v[2], u[0]*v[1] - u[1]*v[0])
    length = math.hypot(*normal)
    if not math.isfinite(length) or length == 0:
        _fail("degenerate or non-finite measured triangle")
    return tuple(value / length for value in normal)


def _cell(triangle, tolerance):
    centroid = [sum(point[i] / 3 for point in triangle) for i in range(3)]
    # A cell spans twice the tolerance so floating point values on cell
    # boundaries cannot turn an admitted one-tolerance displacement into a
    # skipped neighbor. Final matching still uses the original tolerance.
    scaled = [value / (2 * tolerance) for value in centroid]
    if any(not math.isfinite(value) or abs(value) > 2**52 for value in scaled):
        _fail("coordinate/tolerance cannot support reliable bounded neighborhood indexing")
    return tuple(math.floor(value) for value in scaled)


def _triangles(row, budget, source=False):
    vertices = [_point(value, "triangle vertex") for value in _array(row["vertices"], "vertices", budget.limits["maxVertices"], 3)]
    indices = _array(row["triangles"], "triangles", budget.limits["maxTriangles"] * 3, 3)
    if len(indices) % 3:
        _fail("triangle index count is not divisible by three")
    materials = row.get("triangleMaterialIds")
    if materials is None:
        materials = [row["materialId"]] * (len(indices) // 3)
    _array(materials, "triangle material ids", len(indices) // 3, len(indices) // 3)
    used, result = set(), []
    for offset in range(0, len(indices), 3):
        budget.use()
        ids = [_integer(i, "triangle index", 0, len(vertices) - 1) for i in indices[offset:offset + 3]]
        used.update(ids)
        triangle = tuple(vertices[i] for i in ids)
        _normal(triangle)
        result.append((_text(materials[offset // 3], "triangle material id"), triangle))
    if len(used) != len(vertices):
        _fail("unreferenced geometry vertices")
    return vertices, result


def _compare(scene, representation, actual_meshes, budget, tolerance, angular_tolerance_degrees):
    angular_tolerance = _number(angular_tolerance_degrees, "angular tolerance degrees", .001, 1)
    if representation not in ("visual", "collision"):
        _fail("unknown representation")
    expected = {m["id"]: m for m in scene["meshes"] if m["representation"] == representation}
    _array(actual_meshes, "measured meshes", budget.limits["maxMeshes"])
    if len(actual_meshes) != len(expected):
        _fail("mesh inventory differs from selected native representation")
    seen, summaries = set(), []
    vertex_total = triangle_total = source_total = 0
    maximum = maximum_angle = 0.
    for row in actual_meshes:
        _closed(row, _META + ("vertices", "triangles"), ("triangleMaterialIds",), "measured mesh")
        identifier = _text(row["id"], "mesh id")
        if identifier not in expected or identifier in seen:
            _fail("unexpected or duplicate mesh id")
        seen.add(identifier)
        source = expected[identifier]
        if any(row[key] != source[key] for key in _META):
            _fail("mesh semantic identity changed: " + identifier)
        vertices, actual = _triangles(row, budget)
        _, target = _triangles(source, budget, True)
        vertex_total += len(vertices)
        triangle_total += len(actual)
        source_total += len(target)
        if vertex_total > budget.limits["maxVertices"] or max(triangle_total, source_total) > budget.limits["maxTriangles"]:
            _fail("cumulative vertex/triangle cap exceeded")
        if len(actual) != len(target):
            _fail("triangle multiplicity/count changed: " + identifier)
        # Cell keys select candidates only. Exact per-vertex Euclidean distance
        # checks below establish the result; rounded coordinates never do.
        groups, cells = {}, {}
        for material, triangle in target:
            budget.use()
            key = (material, _canonical(triangle))
            if key in groups:
                groups[key][1] += 1
                continue
            groups[key] = [triangle, 1, _normal(triangle)]
            cells.setdefault((material, _cell(triangle, tolerance)), []).append(key)
        mesh_error, mesh_angle, material_counts = 0., 0., {}
        for material, triangle in actual:
            origin, candidates = _cell(triangle, tolerance), []
            for offset in itertools.product((-1, 0, 1), repeat=3):
                budget.use()
                values = cells.get((material, tuple(origin[i] + offset[i] for i in range(3))), ())
                if len(candidates) + len(values) > MAX_NEIGHBOR_CANDIDATES:
                    _fail("triangle neighbor candidate cap exhausted")
                candidates.extend(values)
            matches = []
            normal = _normal(triangle)
            for key in candidates:
                budget.use()
                target_triangle, _, target_normal = groups[key]
                dot = sum(a*b for a, b in zip(normal, target_normal))
                if dot <= 0:
                    continue
                error = min(max(math.dist(triangle[i], target_triangle[(i + shift) % 3]) for i in range(3)) for shift in range(3))
                if error <= tolerance:
                    cross = (normal[1]*target_normal[2] - normal[2]*target_normal[1],
                             normal[2]*target_normal[0] - normal[0]*target_normal[2],
                             normal[0]*target_normal[1] - normal[1]*target_normal[0])
                    # atan2 retains angular resolution near identical normals;
                    # acos(dot) loses precision precisely where tolerances are
                    # tight. Spatial and angular admission remain independent.
                    angle = math.degrees(math.atan2(math.hypot(*cross), dot))
                    matches.append((key, error, angle))
            if len(matches) > 1:
                _fail("ambiguous tolerance-aware triangle correspondence")
            if not matches or groups[matches[0][0]][1] == 0:
                _fail("oriented triangle/material correspondence failed: " + identifier)
            key, error, angle = matches[0]
            if angle > angular_tolerance:
                _fail("source-face angular tolerance exceeded: " + identifier)
            groups[key][1] -= 1
            mesh_error = max(mesh_error, error)
            mesh_angle = max(mesh_angle, angle)
            material_counts[material] = material_counts.get(material, 0) + 1
        if any(group[1] for group in groups.values()):
            _fail("source triangle multiplicity was not preserved")
        maximum = max(maximum, mesh_error)
        maximum_angle = max(maximum_angle, mesh_angle)
        summaries.append({"id": identifier, "sourceTriangleCount": len(target), "observedTriangleCount": len(actual),
                          "observedVertexCount": len(vertices), "materialTriangleCounts": material_counts,
                          "minimum": _point_object([min(v[i] for v in vertices) for i in range(3)]),
                          "maximum": _point_object([max(v[i] for v in vertices) for i in range(3)]),
                          "maxWorldCoordinateError": mesh_error, "maxSourceFaceAngularErrorDegrees": mesh_angle})
    return {"meshes": sorted(summaries, key=lambda row: row["id"]), "sourceTriangleCount": source_total,
            "observedTriangleCount": triangle_total, "observedVertexCount": vertex_total,
            "maxWorldCoordinateError": maximum, "maxSourceFaceAngularErrorDegrees": maximum_angle,
            "geometryChecks": budget.checks}


def compare_meshes(scene, representation, actual_meshes, limits, linear_tolerance, angular_tolerance_degrees=.001):
    """Compare measured world triangles, tolerating cyclic order and seam splits.

    Rows contain the eight native identity fields and ``vertices`` (XYZ arrays
    or point objects), flat ``triangles``, optional ``triangleMaterialIds``.
    Exact duplicate oriented triangles are counted. Distinct source triangles
    admitting multiple tolerance matches fail rather than choosing arbitrarily.
    Production callers pass the approved angular policy explicitly. Returned
    source-face angular errors are degrees, separate from positional errors.
    """
    return _compare(scene, representation, actual_meshes, _Budget(limits), _tolerance(scene, linear_tolerance), angular_tolerance_degrees)


def _pairs(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            _fail("duplicate GLB JSON property: " + key)
        result[key] = value
    return result


def _json(raw):
    if len(raw) > MAX_JSON_BYTES:
        _fail("GLB JSON exceeds hard byte cap")
    depth, quoted, escaped = 0, False, False
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
        elif value in (91, 123):
            depth += 1
            if depth > 48:
                _fail("GLB JSON nesting depth exceeds cap")
        elif value in (93, 125):
            depth -= 1
    def decimal(value):
        parsed = float(value)
        if not math.isfinite(parsed):
            _fail("non-finite GLB JSON number")
        return parsed
    def integer(value):
        if len(value) > 20:
            _fail("oversized GLB JSON integer")
        return int(value)
    try:
        return json.loads(raw.decode("utf-8"), object_pairs_hook=_pairs, parse_float=decimal, parse_int=integer,
                          parse_constant=lambda value: _fail("non-finite GLB JSON constant"))
    except (UnicodeError, RecursionError, json.JSONDecodeError) as error:
        _fail("invalid GLB UTF-8 JSON: " + str(error))


def _container(path, budget):
    path = Path(path)
    info = path.lstat()
    if not stat.S_ISREG(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
        _fail("GLB must be a regular non-link file")
    maximum = min(MAX_GLB_BYTES, budget.limits["maxOutputBytes"])
    if not 28 <= info.st_size <= maximum:
        _fail("GLB length exceeds byte admission")
    with path.open("rb") as handle:
        raw = handle.read(maximum + 1)
    if len(raw) != info.st_size:
        _fail("GLB changed while reading or exceeds byte cap")
    magic, version, total = struct.unpack_from("<III", raw)
    if magic != 0x46546c67 or version != 2 or total != len(raw):
        _fail("invalid GLB magic/version/declared total length")
    offset, chunks = 12, []
    while offset < total:
        if len(chunks) == 2 or offset + 8 > total:
            _fail("GLB requires exactly one JSON and one BIN chunk")
        length, kind = struct.unpack_from("<II", raw, offset)
        offset += 8
        if length % 4 or offset + length > total:
            _fail("invalid GLB chunk alignment or range")
        chunks.append((kind, raw[offset:offset + length]))
        offset += length
    if len(chunks) != 2 or [c[0] for c in chunks] != [0x4e4f534a, 0x004e4942]:
        _fail("GLB chunk layout must be JSON then BIN")
    return _json(chunks[0][1]), chunks[1][1]


def _multiply(left, right):
    result = [sum(left[r*4+k] * right[k*4+c] for k in range(4)) for r in range(4) for c in range(4)]
    if any(not math.isfinite(value) or abs(value) > MAX_COORDINATE for value in result):
        _fail("non-finite or unbounded composed node transform")
    return result


def _local(node):
    if "matrix" in node:
        if any(key in node for key in ("translation", "rotation", "scale")):
            _fail("node mixes matrix and TRS")
        values = [_number(v, "node matrix") for v in _array(node["matrix"], "matrix", 16, 16)]
        result = [values[c*4+r] for r in range(4) for c in range(4)]
        if result[12:] != [0., 0., 0., 1.]:
            _fail("node matrix must be affine")
        return result
    t = _point(node.get("translation", [0., 0., 0.]), "node translation")
    s = _point(node.get("scale", [1., 1., 1.]), "node scale")
    q = [_number(v, "node quaternion", -1, 1) for v in _array(node.get("rotation", [0., 0., 0., 1.]), "rotation", 4, 4)]
    if abs(sum(v*v for v in q) - 1) > 1e-6:
        _fail("node quaternion must be normalized")
    x, y, z, w = q
    rotation = [1-2*(y*y+z*z), 2*(x*y-z*w), 2*(x*z+y*w), 0.,
                2*(x*y+z*w), 1-2*(x*x+z*z), 2*(y*z-x*w), 0.,
                2*(x*z-y*w), 2*(y*z+x*w), 1-2*(x*x+y*y), 0., 0., 0., 0., 1.]
    for r in range(3):
        for c in range(3):
            rotation[r*4+c] *= s[c]
        rotation[r*4+3] = t[r]
    return rotation


def _source_matrix(matrix):
    # p_glTF = C p_native; hence native world transform = C^-1 W C.
    c = [1.,0.,0.,0., 0.,0.,1.,0., 0.,-1.,0.,0., 0.,0.,0.,1.]
    inverse = [1.,0.,0.,0., 0.,0.,-1.,0., 0.,1.,0.,0., 0.,0.,0.,1.]
    return _multiply(_multiply(inverse, matrix), c)


def _transform(matrix, point):
    result = [sum(matrix[r*4+c]*point[c] for c in range(3)) + matrix[r*4+3] for r in range(3)]
    return _point(result, "world vertex")


def _extras(node):
    required = {"bubo_" + key for key in _META if key != "parentAnchorId"} | {"bubo_parent_anchor_id"}
    extras = _closed(node.get("extras"), required, label="semantic node extras")
    return {key: _text(extras["bubo_parent_anchor_id" if key == "parentAnchorId" else "bubo_" + key], key, True) for key in _META}


def inspect_glb(path, scene, representation, limits, linear_tolerance, angular_tolerance_degrees=.001):
    """Strictly decode raw GLB world triangles and compare to ConstructionScene."""
    budget, tolerance = _Budget(limits), _tolerance(scene, linear_tolerance)
    doc, binary = _container(path, budget)
    _closed(doc, ("asset", "scene", "scenes", "nodes", "meshes", "materials", "accessors", "bufferViews", "buffers"),
            ("extensionsUsed", "extensionsRequired"), "GLB document")
    _closed(doc["asset"], ("version",), ("generator", "copyright", "minVersion"), "asset")
    _optional_names(doc["asset"])
    if doc["asset"]["version"] != "2.0" or doc["asset"].get("minVersion", "2.0") != "2.0":
        _fail("unsupported glTF asset version")
    used_extensions = _array(doc.get("extensionsUsed", []), "extensionsUsed", len(_EXTENSIONS))
    required_extensions = _array(doc.get("extensionsRequired", []), "extensionsRequired", len(_EXTENSIONS))
    if (any(type(v) is not str or v not in _EXTENSIONS for v in used_extensions + required_extensions) or
            len(set(used_extensions)) != len(used_extensions) or len(set(required_extensions)) != len(required_extensions) or
            not set(required_extensions) <= set(used_extensions)):
        _fail("unknown/duplicate/undeclared glTF extension")
    buffers = _array(doc["buffers"], "buffers", 1, 1)
    _closed(buffers[0], ("byteLength",), label="buffer (external URIs forbidden)")
    bin_length = _integer(buffers[0]["byteLength"], "buffer byteLength", 1, len(binary))
    if not 0 <= len(binary) - bin_length <= 3 or any(binary[bin_length:]):
        _fail("BIN chunk length/padding differs from declared buffer")
    views = _array(doc["bufferViews"], "bufferViews", budget.limits["maxAccessors"], 1)
    for view in views:
        _closed(view, ("buffer", "byteLength"), ("byteOffset", "byteStride", "target", "name"), "bufferView")
        _optional_names(view)
        if _integer(view["buffer"], "buffer index") != 0:
            _fail("bufferView refers outside sole embedded buffer")
        offset = _integer(view.get("byteOffset", 0), "bufferView offset", 0, bin_length)
        length = _integer(view["byteLength"], "bufferView length", 1, bin_length)
        if offset % 4 or offset + length > bin_length:
            _fail("bufferView alignment/range invalid")
        if "byteStride" in view and (_integer(view["byteStride"], "byteStride", 4, 252) % 4):
            _fail("bufferView stride must be a multiple of four")
        if "target" in view and view["target"] not in (34962, 34963):
            _fail("unsupported bufferView target")
    accessors = _array(doc["accessors"], "accessors", budget.limits["maxAccessors"], 1)
    layouts, view_used = [], set()
    decoded_vector_count = decoded_index_count = 0
    for accessor in accessors:
        _closed(accessor, ("bufferView", "componentType", "count", "type"), ("byteOffset", "normalized", "min", "max", "name"), "accessor")
        _optional_names(accessor)
        view_id = _integer(accessor["bufferView"], "accessor view", 0, len(views)-1)
        view_used.add(view_id)
        view = views[view_id]
        component = accessor["componentType"]
        if (type(component) is not int or component not in (5121, 5123, 5125, 5126) or
                not isinstance(accessor["type"],str) or accessor["type"] not in ("SCALAR", "VEC3")):
            _fail("unsupported accessor type/component")
        size, code = {5121: (1,"B"), 5123: (2,"H"), 5125: (4,"I"), 5126: (4,"f")}[component]
        width = 1 if accessor["type"] == "SCALAR" else 3
        maximum = budget.limits["maxTriangles"]*3 if width == 1 else budget.limits["maxVertices"]
        count = _integer(accessor["count"], "accessor count", 1, maximum)
        if width == 3:
            decoded_vector_count += count
            if decoded_vector_count > budget.limits["maxVertices"] * 2:
                _fail("cumulative decoded vertex/normal cap exceeded")
        else:
            decoded_index_count += count
            if decoded_index_count > budget.limits["maxTriangles"] * 3:
                _fail("cumulative decoded index cap exceeded")
        offset = _integer(accessor.get("byteOffset", 0), "accessor offset", 0, view["byteLength"])
        stride = view.get("byteStride", size*width)
        if stride < size*width or stride % size or offset % size or (view.get("byteOffset",0)+offset) % size:
            _fail("accessor alignment/stride invalid")
        if offset + (count-1)*stride + size*width > view["byteLength"]:
            _fail("accessor range exceeds bufferView")
        if accessor.get("normalized", False) is not False:
            _fail("normalized attributes are unsupported")
        for key in ("min", "max"):
            if key in accessor:
                values = _array(accessor[key], "accessor " + key, width, width)
                for v in values:
                    _number(v, "accessor bound", 0 if component != 5126 else -MAX_COORDINATE,
                            2**32-1 if component != 5126 else MAX_COORDINATE)
        if "min" in accessor and "max" in accessor and any(a>b for a,b in zip(accessor["min"],accessor["max"])):
            _fail("accessor min/max inverted")
        layouts.append((view.get("byteOffset",0)+offset, stride, count, "<"+code*width, component, width, view))
    if len(view_used) != len(views):
        _fail("unreferenced bufferView")
    accessed, decoded, roles, view_roles = set(), {}, {}, {}
    role_counts = {"POSITION": 0, "NORMAL": 0, "indices": 0}

    def decode(index, role):
        index = _integer(index, role + " accessor", 0, len(layouts)-1)
        offset, stride, count, fmt, component, width, view = layouts[index]
        view_id = accessors[index]["bufferView"]
        view_role = "indices" if role == "indices" else "vertex attributes"
        if view_id in view_roles and view_roles[view_id] != view_role:
            _fail("bufferView mixes vertex attributes and indices")
        view_roles[view_id] = view_role
        if index in roles and roles[index] != role:
            _fail("one accessor has conflicting geometry roles")
        if index not in roles:
            roles[index] = role
            role_counts[role] += count
            if role_counts[role] > (budget.limits["maxTriangles"]*3 if role == "indices" else budget.limits["maxVertices"]):
                _fail("cumulative decoded attribute cap exceeded")
        if role in ("POSITION", "NORMAL"):
            if component != 5126 or width != 3 or (offset % 4) or view.get("target",34962) != 34962:
                _fail("vertex attribute requires aligned FLOAT VEC3")
            if role == "POSITION" and not {"min", "max"} <= accessors[index].keys():
                _fail("POSITION accessor requires bounds")
        elif width != 1 or component == 5126 or "byteStride" in view or view.get("target",34963) != 34963:
            _fail("indices require unstrided unsigned SCALAR")
        accessed.add(index)
        if index not in decoded:
            budget.use(count)
            values = [struct.unpack_from(fmt, binary, offset+i*stride) for i in range(count)]
            for row in values:
                for value in row:
                    _number(value, "decoded accessor component", -MAX_COORDINATE if component == 5126 else 0,
                            MAX_COORDINATE if component == 5126 else 2**32-1)
            if role == "indices" and any(row[0] == _INDEX_MAXIMUM[component] for row in values):
                _fail("index accessor uses reserved unsigned restart-sentinel maximum")
            for key, operation in (("min", min), ("max", max)):
                if key in accessors[index]:
                    actual = [operation(row[axis] for row in values) for axis in range(width)]
                    requested = accessors[index][key]
                    if component == 5126:
                        requested = [struct.unpack("<f", struct.pack("<f", v))[0] for v in requested]
                    if requested != actual:
                        _fail("declared accessor bounds differ from decoded data")
            decoded[index] = values
        return decoded[index]

    materials = _array(doc["materials"], "materials", budget.limits["maxMaterials"], 1)
    material_rows, actual_extensions = [], set()
    for material in materials:
        _closed(material, ("extras",), ("name", "pbrMetallicRoughness", "extensions", "doubleSided", "alphaMode", "emissiveFactor"), "material")
        _optional_names(material)
        extras = _closed(material["extras"], ("bubo_id", "bubo_name", "bubo_color"), label="material extras")
        source = next((m for m in scene["materials"] if m["id"] == extras["bubo_id"]), None)
        if source is None or extras["bubo_name"] != source["name"] or extras["bubo_color"] != source["color"]:
            _fail("material semantic extras changed")
        if material.get("alphaMode", "OPAQUE") != "OPAQUE":
            _fail("unsupported material alpha/emission")
        for component in _array(material.get("emissiveFactor",[0,0,0]), "emissiveFactor", 3, 3):
            if _number(component, "emissiveFactor", 0, 1) != 0:
                _fail("unsupported material emission")
        if type(material.get("doubleSided",False)) is not bool:
            _fail("invalid material doubleSided")
        pbr = _closed(material.get("pbrMetallicRoughness",{}), (), ("baseColorFactor", "metallicFactor", "roughnessFactor"), "PBR material")
        extensions = _closed(material.get("extensions",{}), (), _EXTENSIONS, "material extensions")
        actual_extensions.update(extensions)
        for key, field in (("KHR_materials_transmission","transmissionFactor"), ("KHR_materials_ior","ior")):
            if key in extensions:
                _closed(extensions[key], (field,), label=key)
        material_rows.append({"id": extras["bubo_id"], "color": pbr.get("baseColorFactor",[1,1,1,1]),
                              "roughness": pbr.get("roughnessFactor",1), "metallic": pbr.get("metallicFactor",1),
                              "transmission": extensions.get("KHR_materials_transmission",{}).get("transmissionFactor",0),
                              "ior": extensions.get("KHR_materials_ior",{}).get("ior",1.5)})
    if actual_extensions != set(used_extensions):
        _fail("extension declarations differ from actual material extensions")
    material_rows = compare_materials(scene, material_rows, representation)
    meshes = _array(doc["meshes"], "meshes", budget.limits["maxMeshes"], 1)
    mesh_primitives, primitive_total, primitive_triangles = [], 0, 0
    position_references = {}
    for mesh in meshes:
        _closed(mesh, ("primitives",), ("name", "extras"), "mesh")
        _optional_names(mesh)
        if "extras" in mesh:
            _closed(mesh["extras"], (), label="mesh-data extras")
        primitives = _array(mesh["primitives"], "primitives", budget.limits["maxPrimitives"], 1)
        primitive_total += len(primitives)
        if primitive_total > budget.limits["maxPrimitives"]:
            _fail("cumulative primitive cap exceeded")
        rows = []
        for primitive in primitives:
            _closed(primitive, ("attributes", "material"), ("indices", "mode"), "primitive")
            if type(primitive.get("mode",4)) is not int or primitive.get("mode",4) != 4:
                _fail("only TRIANGLES primitives are supported")
            attrs = _closed(primitive["attributes"], ("POSITION",), ("NORMAL",), "primitive attributes")
            positions = decode(attrs["POSITION"], "POSITION")
            normals = None
            if "NORMAL" in attrs:
                normals = decode(attrs["NORMAL"], "NORMAL")
                if len(normals) != len(positions) or any(abs(math.hypot(*n)-1) > 1e-4 for n in normals):
                    _fail("normal count/normalization differs from positions")
            if "indices" in primitive:
                indices = [v[0] for v in decode(primitive["indices"], "indices")]
            else:
                indices = list(range(len(positions)))
            if len(indices) % 3 or len(indices)//3 > budget.limits["maxTriangles"] or any(i>=len(positions) for i in indices):
                _fail("triangle index count/range invalid")
            position_references.setdefault(attrs["POSITION"], set()).update(indices)
            primitive_triangles += len(indices)//3
            if primitive_triangles > budget.limits["maxTriangles"]:
                _fail("cumulative primitive triangle cap exceeded")
            if normals is not None:
                angular = _number(angular_tolerance_degrees, "angular tolerance degrees", 1e-9, 1)
                for first in range(0,len(indices),3):
                    budget.use()
                    face = _normal(tuple(positions[i] for i in indices[first:first+3]))
                    for vertex in indices[first:first+3]:
                        dot = sum(a*b for a,b in zip(face,normals[vertex])) / math.hypot(*normals[vertex])
                        if math.degrees(math.acos(max(-1.,min(1.,dot)))) > angular:
                            _fail("exported normal differs from oriented flat construction triangle")
            material = _integer(primitive["material"], "primitive material", 0, len(materials)-1)
            rows.append((positions, indices, materials[material]["extras"]["bubo_id"]))
        mesh_primitives.append(rows)
    if len(accessed) != len(accessors):
        _fail("unreferenced accessor")
    for index, references in position_references.items():
        if len(references) != len(decoded[index]):
            _fail("unreferenced POSITION vertices")
    nodes = _array(doc["nodes"], "nodes", budget.limits["maxNodes"], 1)
    parents, node_meta, locals_ = {}, [], []
    for index, node in enumerate(nodes):
        budget.use()
        _closed(node, ("extras",), ("name", "children", "matrix", "translation", "rotation", "scale", "mesh"), "node")
        _optional_names(node)
        node_meta.append(_extras(node))
        locals_.append(_local(node))
        children = _array(node.get("children", []), "node children", budget.limits["maxNodes"])
        for child in children:
            _integer(child, "child node", 0, len(nodes)-1)
            if child in parents or child == index:
                _fail("node has duplicate/multiple parents or self-cycle")
            parents[child] = index
    scenes = _array(doc["scenes"], "scenes", 1, 1)
    if _integer(doc["scene"], "scene index") != 0:
        _fail("exactly one default scene is required")
    _closed(scenes[0], ("nodes",), ("name",), "scene")
    _optional_names(scenes[0])
    roots = _array(scenes[0]["nodes"], "scene roots", budget.limits["maxNodes"], 1)
    for root in roots:
        _integer(root, "root node", 0, len(nodes)-1)
    if len(set(roots)) != len(roots) or set(roots) != set(range(len(nodes))) - parents.keys():
        _fail("scene roots differ from unique parent graph")
    worlds, pending = {}, [(root, _IDENTITY, 0) for root in roots]
    while pending:
        index, parent_world, depth = pending.pop()
        budget.use()
        if depth > 64 or index in worlds:
            _fail("node hierarchy cycle or depth cap")
        worlds[index] = _multiply(parent_world, locals_[index])
        pending.extend((child, worlds[index], depth+1) for child in nodes[index].get("children", []))
    if len(worlds) != len(nodes):
        _fail("unreachable/cyclic hidden nodes")
    actual_nodes, actual_meshes, mesh_used = [], [], set()
    for index, node in enumerate(nodes):
        meta = node_meta[index]
        world = _source_matrix(worlds[index])
        actual_nodes.append(dict(meta, parentId=node_meta[parents[index]]["id"] if index in parents else None,
                                 position=[world[i] for i in (3,7,11)], axis=[world[i] for i in (2,6,10)], worldMatrix=world))
        if "mesh" not in node:
            continue
        mesh_index = _integer(node["mesh"], "node mesh", 0, len(meshes)-1)
        if mesh_index in mesh_used:
            _fail("mesh instancing is forbidden")
        mesh_used.add(mesh_index)
        row = dict(meta, parentAnchorId=meta["parentAnchorId"] or None, vertices=[], triangles=[], triangleMaterialIds=[])
        vertex_map = {}
        for positions, indices, material in mesh_primitives[mesh_index]:
            for vertex in indices:
                budget.use()
                key = (id(positions), vertex)
                if key not in vertex_map:
                    transformed = _transform(worlds[index], positions[vertex])
                    vertex_map[key] = len(row["vertices"])
                    row["vertices"].append([transformed[0], -transformed[2], transformed[1]])
                row["triangles"].append(vertex_map[key])
            row["triangleMaterialIds"].extend([material]*(len(indices)//3))
        actual_meshes.append(row)
    if len(mesh_used) != len(meshes):
        _fail("hidden/unreferenced mesh")
    node_result = compare_nodes(scene, representation, actual_nodes, tolerance, angular_tolerance_degrees)
    result = _compare(scene, representation, actual_meshes, budget, tolerance, angular_tolerance_degrees)
    result.update(node_result)
    result["materials"] = material_rows
    result["rawCounts"] = {"nodes": len(nodes), "meshes": len(meshes), "materials": len(materials),
                           "accessors": len(accessors), "primitives": primitive_total,
                           "decodedVertices": sum(layout[2] for layout in layouts if layout[5] == 3)}
    if result["rawCounts"]["decodedVertices"] > budget.limits["maxVertices"] * 2:
        _fail("cumulative decoded vertex/normal cap exceeded")
    return result
