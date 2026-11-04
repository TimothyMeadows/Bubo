"""Bounded raw glTF observations and lossless split-vertex evidence.

No bpy dependency and no repair path. Logical vertex attributes identify the
source correspondence; measured positions, triangles and weights stay actual.
"""
import collections
import hashlib
import json
import math
from pathlib import Path
import struct

MAX_JSON = 8 * 1024 * 1024
COMPONENTS = {5120: ("b", 1), 5121: ("B", 1), 5122: ("h", 2),
              5123: ("H", 2), 5125: ("I", 4), 5126: ("f", 4)}
WIDTHS = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT4": 16}
ATTRIBUTE = "_BUBO_VERTEX_INDEX"


def require(value, message):
    if not value:
        raise ValueError("Schematic export geometry: " + message)


def integer(value, low, high, label):
    require(type(value) is int and low <= value <= high, "bounded integer " + label)
    return value


def finite(value):
    require(type(value) in (int, float) and math.isfinite(value), "finite number required")
    return float(value)


def unique_pairs(items):
    result = {}
    for key, value in items:
        require(key not in result, "duplicate JSON property")
        result[key] = value
    return result


def decode_json(raw):
    require(len(raw) <= MAX_JSON, "JSON byte ceiling")
    depth, quoted, escaped = 0, False, False
    for byte in raw:
        if quoted:
            if escaped:
                escaped = False
            elif byte == 92:
                escaped = True
            elif byte == 34:
                quoted = False
        elif byte == 34:
            quoted = True
        elif byte in (123, 91):
            depth += 1
            require(depth <= 64, "JSON depth ceiling")
        elif byte in (125, 93):
            depth -= 1
            require(depth >= 0, "JSON depth")
    require(depth == 0 and not quoted, "complete JSON required")
    result = json.loads(raw.decode("utf-8"), object_pairs_hook=unique_pairs,
                        parse_constant=lambda _: require(False, "nonfinite JSON"))
    pending = [result]
    while pending:
        item = pending.pop()
        if isinstance(item, dict):
            pending.extend(item.values())
        elif isinstance(item, list):
            pending.extend(item)
        elif isinstance(item, float):
            finite(item)
    return result


def read_glb(path, limits):
    path = Path(path)
    size = path.stat().st_size
    require(28 <= size <= limits["maxSourceBytes"], "GLB byte ceiling")
    with path.open("rb") as handle:
        header = handle.read(12)
        magic, version, length = struct.unpack("<4sII", header)
        require(magic == b"glTF" and version == 2 and length == size, "exact GLB2 header")
        chunks = []
        consumed = 12
        while consumed < size:
            require(len(chunks) < 2 and consumed + 8 <= size, "bounded GLB chunk inventory")
            length, kind = struct.unpack("<I4s", handle.read(8))
            require(length % 4 == 0 and consumed + 8 + length <= size, "GLB chunk bounds/alignment")
            require(kind != b"JSON" or length <= MAX_JSON, "raw glTF JSON byte ceiling")
            data = handle.read(length)
            require(len(data) == length, "complete GLB chunk")
            chunks.append((kind, data))
            consumed += 8 + length
    require([kind for kind, _ in chunks] == [b"JSON", b"BIN\0"], "one embedded JSON and BIN required")
    doc, binary = decode_json(chunks[0][1]), chunks[1][1]
    require(isinstance(doc, dict) and doc.get("asset", {}).get("version") == "2.0", "glTF2 asset required")
    require(not doc.get("extensionsRequired") and not doc.get("extensionsUsed"), "extension-free mechanical GLB required")
    require(not doc.get("images") and not doc.get("textures") and not doc.get("cameras"), "external media/cameras forbidden")
    require(len(doc.get("buffers", [])) == 1 and set(doc["buffers"][0]) <= {"byteLength", "name"}, "one embedded buffer required")
    used = integer(doc["buffers"][0].get("byteLength"), 0, len(binary), "buffer bytes")
    require(len(binary)-used <= 3 and not any(binary[used:]), "exact zero binary padding")
    for key, maximum in (("nodes", limits["maxNodes"]), ("meshes", limits["maxNodes"]),
                         ("skins", limits["maxNodes"]), ("accessors", limits["maxAccessors"]),
                         ("bufferViews", limits["maxAccessors"]), ("animations", limits["maxMotionSamples"]),
                         ("materials", 4096)):
        require(isinstance(doc.get(key, []), list) and len(doc.get(key, [])) <= maximum, "bounded " + key)
    require(len(doc.get("scenes", [])) == 1 and doc.get("scene", 0) == 0, "one active scene required")
    require(sum(len(mesh.get("primitives", [])) for mesh in doc.get("meshes", [])) <= limits["maxPrimitives"], "complete primitive inventory ceiling")
    view_bytes = 0
    for view in doc.get("bufferViews", []):
        require(isinstance(view, dict) and view.get("buffer") == 0 and "extensions" not in view, "embedded uncompressed buffer view")
        start = integer(view.get("byteOffset", 0), 0, used, "view offset")
        length = integer(view.get("byteLength"), 1, used, "view length")
        require(start + length <= used, "buffer view bounds")
        if "byteStride" in view:
            integer(view["byteStride"], 4, 252, "view stride")
            require(view["byteStride"] % 4 == 0, "aligned vertex stride")
        view_bytes += length
        require(view_bytes <= limits["maxSourceBytes"] * 4, "overlapping view work ceiling")
    return doc, binary


def write_glb(path, doc, binary):
    raw = json.dumps(doc, ensure_ascii=True, separators=(",", ":"), allow_nan=False).encode("utf-8")
    require(len(raw) <= MAX_JSON, "raw glTF JSON byte ceiling")
    raw += b" " * (-len(raw) % 4)
    binary += b"\0" * (-len(binary) % 4)
    data = struct.pack("<4sII", b"glTF", 2, 28+len(raw)+len(binary))
    data += struct.pack("<I4s", len(raw), b"JSON") + raw
    data += struct.pack("<I4s", len(binary), b"BIN\0") + binary
    # The caller owns the newly produced file; replacement never touches source.
    Path(path).write_bytes(data)


def accessor(doc, binary, index, widths=None, maximum=2_000_000):
    integer(index, 0, len(doc.get("accessors", []))-1, "accessor index")
    item = doc["accessors"][index]
    require(isinstance(item, dict) and not item.get("sparse") and "extensions" not in item, "dense accessor required")
    require(item.get("componentType") in COMPONENTS and item.get("type") in WIDTHS, "supported accessor type")
    width = WIDTHS[item["type"]]
    require(widths is None or width in widths, "accessor width")
    count = integer(item.get("count"), 1, maximum, "accessor count")
    view_index = integer(item.get("bufferView"), 0, len(doc.get("bufferViews", []))-1, "buffer view")
    view = doc["bufferViews"][view_index]
    fmt, size = COMPONENTS[item["componentType"]]
    stride = view.get("byteStride", width*size)
    offset = integer(item.get("byteOffset", 0), 0, view["byteLength"], "accessor offset")
    require(offset % size == 0 and stride >= width*size and (count-1)*stride+width*size <= view["byteLength"]-offset, "accessor storage bounds")
    start = view.get("byteOffset", 0)+offset
    normalized = item.get("normalized", False)
    require(type(normalized) is bool and (not normalized or fmt in ("b", "B", "h", "H")), "valid normalized integer accessor")
    result = []
    divisor = {"b": 127, "B": 255, "h": 32767, "H": 65535}.get(fmt)
    for row in range(count):
        values = struct.unpack_from("<"+fmt*width, binary, start+row*stride)
        if normalized:
            values = tuple(max(-1, v/divisor) for v in values)
        require(all(math.isfinite(v) for v in values), "finite accessor data")
        result.append(tuple(values))
    return result


def source_point(point):
    return (float(point[0]), -float(point[2]), float(point[1]))


def geometry(doc, binary, mesh_index, limits):
    integer(mesh_index, 0, len(doc.get("meshes", []))-1, "mesh index")
    mesh = doc["meshes"][mesh_index]
    require(isinstance(mesh.get("primitives"), list) and 1 <= len(mesh["primitives"]) <= 4096, "bounded mesh primitives")
    vertices, logical, triangles, weights, materials = [], [], [], [], []
    for primitive in mesh["primitives"]:
        require(primitive.get("mode", 4) == 4 and not primitive.get("targets") and not primitive.get("extensions"), "uncompressed triangle primitive required")
        attrs = primitive.get("attributes", {})
        require(isinstance(attrs, dict) and "POSITION" in attrs and ATTRIBUTE in attrs, "positions and immutable logical correspondence required")
        require(set(attrs) <= {"POSITION", "NORMAL", ATTRIBUTE, "JOINTS_0", "WEIGHTS_0"}, "unexpected vertex attribute")
        positions = accessor(doc, binary, attrs["POSITION"], (3,), limits["maxVertices"])
        require("NORMAL" in attrs, "actual surface normals required")
        normals = accessor(doc, binary, attrs["NORMAL"], (3,), limits["maxVertices"])
        require(len(normals) == len(positions) and all(abs(sum(c*c for c in normal)-1) <= 1e-4 for normal in normals), "finite unit actual surface normals")
        identifiers = accessor(doc, binary, attrs[ATTRIBUTE], (1,), limits["maxVertices"])
        require(len(identifiers) == len(positions), "logical vertex count")
        ids = [integer(int(v[0]), 0, 2_000_000, "logical vertex") for v in identifiers]
        require(all(v[0] == i for v, i in zip(identifiers, ids)), "integral actual logical vertex values")
        indices = [v[0] for v in accessor(doc, binary, primitive.get("indices"), (1,), limits["maxTriangles"]*3)]
        require(len(indices) % 3 == 0 and all(type(v) is int and 0 <= v < len(positions) for v in indices), "indexed triangle bounds")
        require(set(indices) == set(range(len(positions))), "unused raw GLB vertices forbidden")
        start = len(vertices)
        vertices.extend(source_point(p) for p in positions)
        logical.extend(ids)
        tris = [tuple(start+v for v in indices[i:i+3]) for i in range(0, len(indices), 3)]
        require(all(len(set(t)) == 3 for t in tris), "degenerate indexed triangle")
        triangles.extend(tris)
        materials.extend([primitive.get("material")] * len(tris))
        if "JOINTS_0" in attrs or "WEIGHTS_0" in attrs:
            require("JOINTS_0" in attrs and "WEIGHTS_0" in attrs, "paired skin attributes required")
            joints = accessor(doc, binary, attrs["JOINTS_0"], (4,), limits["maxVertices"])
            values = accessor(doc, binary, attrs["WEIGHTS_0"], (4,), limits["maxVertices"])
            require(len(joints) == len(values) == len(positions), "actual skin attribute counts")
            for joint, value in zip(joints, values):
                pairs = []
                for j, w in zip(joint, value):
                    require(type(j) is int and j >= 0 and 0 <= w <= 1, "bounded actual skin influence")
                    if w:
                        pairs.append((j, float(w)))
                require(pairs and abs(sum(w for _, w in pairs)-1) <= 1e-6 and len({j for j, _ in pairs}) == len(pairs), "normalized unique owned skin weights")
                weights.append(pairs)
        else:
            weights.extend([[] for _ in positions])
        require(len(vertices) <= limits["maxVertices"] and len(triangles) <= limits["maxTriangles"], "raw geometry cumulative ceiling")
    return {"vertices": vertices, "logical": logical, "triangles": triangles,
            "weights": weights, "triangleMaterials": materials}


def geometry_bytes(vertices, logical, triangles):
    require(len(vertices) == len(logical), "split vertex correspondence count")
    data = bytearray(struct.pack("<8sII", b"BUBOEXP1", len(vertices), len(triangles)))
    for vertex in vertices:
        data.extend(struct.pack("<ddd", *(finite(v) for v in vertex)))
    for value in logical:
        data.extend(struct.pack("<I", integer(value, 0, 2_000_000, "logical vertex")))
    for triangle in triangles:
        require(len(triangle) == 3 and len(set(triangle)) == 3, "triangle arity")
        data.extend(struct.pack("<III", *(integer(v, 0, len(vertices)-1, "triangle vertex") for v in triangle)))
    return bytes(data)


def skin_bytes(weights):
    data = bytearray(struct.pack("<8sI", b"BUBOSKX1", len(weights)))
    for row in weights:
        data.extend(struct.pack("<I", integer(len(row), 0, 4, "skin influences")))
        for joint, weight in row:
            data.extend(struct.pack("<Id", integer(joint, 0, 100000, "skin joint"), finite(weight)))
    return bytes(data)


def triangle_key(triangle):
    return min(tuple(triangle[i:]+triangle[:i]) for i in range(3))


def compare_geometry(actual, expected_vertices, expected_triangles, tolerance):
    """Validate every actual split occurrence without reconstructing geometry."""
    require(set(actual["logical"]) == set(range(len(expected_vertices))), "complete source vertex inventory")
    for point, logical in zip(actual["vertices"], actual["logical"]):
        require(logical < len(expected_vertices) and math.dist(point, expected_vertices[logical]) <= tolerance, "actual local geometry differs from approved source")
    observed = collections.Counter(triangle_key([actual["logical"][i] for i in t]) for t in actual["triangles"])
    expected = collections.Counter(triangle_key(list(t)) for t in expected_triangles)
    require(observed == expected, "oriented triangle multiplicity differs from approved source")


def file_row(path):
    path = Path(path)
    return {"file": path.name, "sha256": hashlib.sha256(path.read_bytes()).hexdigest(), "bytes": path.stat().st_size}


def identity():
    return [[float(i == j) for j in range(4)] for i in range(4)]


def multiply(a, b):
    return [[sum(a[i][k]*b[k][j] for k in range(4)) for j in range(4)] for i in range(4)]


def inverse(a):
    # Real affine inversion retains small local hardware in large assemblies.
    rows = [list(map(float, a[i]))+identity()[i] for i in range(4)]
    for column in range(4):
        pivot = max(range(column, 4), key=lambda row: abs(rows[row][column]))
        require(abs(rows[pivot][column]) > 1e-14, "invertible affine matrix required")
        rows[column], rows[pivot] = rows[pivot], rows[column]
        divisor = rows[column][column]
        rows[column] = [v/divisor for v in rows[column]]
        for row in range(4):
            if row != column:
                factor = rows[row][column]
                rows[row] = [a-factor*b for a, b in zip(rows[row], rows[column])]
    return [row[4:] for row in rows]


def transform(matrix, point):
    return tuple(sum(matrix[i][k]*point[k] for k in range(3))+matrix[i][3] for i in range(3))


def column_matrix(values):
    require(len(values) == 16, "matrix width")
    return [[finite(values[4*j+i]) for j in range(4)] for i in range(4)]


def convert_matrix(value):
    c = [[1., 0., 0., 0.], [0., 0., -1., 0.], [0., 1., 0., 0.], [0., 0., 0., 1.]]
    return multiply(multiply(c, value), inverse(c))


def pose(value):
    return {"origin": dict(zip("xyz", [value[i][3] for i in range(3)])),
            **{axis: dict(zip("xyz", [value[i][j] for i in range(3)])) for j, axis in enumerate("xyz")}}


def trs(translation=(0, 0, 0), rotation=(0, 0, 0, 1), scale=(1, 1, 1)):
    x, y, z, w = map(finite, rotation)
    require(abs(x*x+y*y+z*z+w*w-1) <= 1e-5, "unit raw quaternion required")
    result = [[1-2*y*y-2*z*z, 2*x*y-2*z*w, 2*x*z+2*y*w, finite(translation[0])],
              [2*x*y+2*z*w, 1-2*x*x-2*z*z, 2*y*z-2*x*w, finite(translation[1])],
              [2*x*z-2*y*w, 2*y*z+2*x*w, 1-2*x*x-2*y*y, finite(translation[2])], [0., 0., 0., 1.]]
    require(len(scale) == 3 and all(math.isfinite(v) and v > 0 for v in scale), "positive finite raw scale")
    for i in range(3):
        for j in range(3):
            result[i][j] *= scale[j]
    return result


def hierarchy(doc, limits):
    nodes = doc.get("nodes", [])
    require(nodes and len(nodes) <= limits["maxNodes"], "bounded nonempty node tree")
    names = [n.get("name") for n in nodes]
    require(all(isinstance(n, str) and n and len(n) <= 63 for n in names) and len(set(names)) == len(names), "short globally unique actual node names")
    parents = {}
    for index, node in enumerate(nodes):
        require(not node.get("extensions"), "node extensions forbidden")
        children = node.get("children", [])
        require(isinstance(children, list) and len(set(children)) == len(children), "unique node children")
        for child in children:
            integer(child, 0, len(nodes)-1, "child node")
            require(child not in parents and child != index, "single-parent acyclic nodes required")
            parents[child] = index
    roots = doc["scenes"][0].get("nodes", [])
    require(isinstance(roots, list) and len(set(roots)) == len(roots) and set(roots) == set(range(len(nodes)))-set(parents), "all node roots in active scene")
    visited, pending = set(), [(root, 0) for root in roots]
    while pending:
        node, depth = pending.pop()
        require(node not in visited and depth <= 256, "bounded acyclic scene hierarchy")
        visited.add(node)
        pending.extend((child, depth+1) for child in nodes[node].get("children", []))
    require(len(visited) == len(nodes), "all raw nodes reachable")
    return parents


def matrices(doc, binary, parents, limits, animation=None, time=0):
    overrides = {}
    if animation is not None:
        require(len(animation.get("channels", [])) <= limits["maxChannels"], "channel ceiling")
        for channel in animation.get("channels", []):
            target = channel.get("target", {})
            node, path = target.get("node"), target.get("path")
            integer(node, 0, len(doc["nodes"])-1, "channel node")
            require(path in ("translation", "rotation", "scale") and (node, path) not in overrides, "owned unique transform channels")
            sampler_index = integer(channel.get("sampler"), 0, len(animation.get("samplers", []))-1, "animation sampler")
            sampler = animation["samplers"][sampler_index]
            require(sampler.get("interpolation", "LINEAR") == "LINEAR", "linear diagnostic sampling required")
            times = [r[0] for r in accessor(doc, binary, sampler.get("input"), (1,), limits["maxMotionSamples"])]
            values = accessor(doc, binary, sampler.get("output"), (4,) if path == "rotation" else (3,), limits["maxMotionSamples"])
            require(len(times) == len(values) and len(times) >= 2 and all(a < b for a, b in zip(times, times[1:])), "ordered complete diagnostic keys")
            matches = [i for i, t in enumerate(times) if abs(t-time) <= 1e-6]
            require(len(matches) == 1, "declared sample missing actual portable key")
            overrides[(node, path)] = values[matches[0]]
    local, worlds = [], {}
    for index, node in enumerate(doc["nodes"]):
        require(not ("matrix" in node and any(k in node for k in ("translation", "rotation", "scale"))), "exclusive matrix or TRS")
        if "matrix" in node:
            require(not any(n == index for n, _ in overrides), "animated matrix forbidden")
            local.append(column_matrix(node["matrix"]))
        else:
            local.append(trs(*(overrides.get((index, key), node.get(key, default)) for key, default in
                               (("translation", (0, 0, 0)), ("rotation", (0, 0, 0, 1)), ("scale", (1, 1, 1))))))
    def world(index):
        if index not in worlds:
            worlds[index] = multiply(world(parents[index]), local[index]) if index in parents else local[index]
        return worlds[index]
    for index in range(len(local)):
        world(index)
    return local, worlds
