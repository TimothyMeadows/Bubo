"""Fixed primitive meshes and measured, canonical evaluated-solid receipts.

No repair, proxy, remeshing or decimation path exists. Coordinates are metres.
"""
import json
import math
from pathlib import Path
import struct

from parts_admission import digest, require


def add(a, b):
    return tuple(x + y for x, y in zip(a, b))


def sub(a, b):
    return tuple(x - y for x, y in zip(a, b))


def mul(a, value):
    return tuple(x * value for x in a)


def dot(a, b):
    return sum(x * y for x, y in zip(a, b))


def cross(a, b):
    return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])


def unit(a):
    length = math.sqrt(dot(a, a))
    require(length > 1e-14, "Degenerate native direction")
    return mul(a, 1 / length)


def xyz(value):
    return tuple(value[k] for k in "xyz")


def vec(value):
    return dict(zip("xyz", value))


def transform(frame, point):
    x, y, z = (math.radians(frame["rotationDegrees"][key]) for key in "xyz")
    sx, cx, sy, cy, sz, cz = math.sin(x), math.cos(x), math.sin(y), math.cos(y), math.sin(z), math.cos(z)
    ax, ay, az = point[0], point[1] * cx - point[2] * sx, point[1] * sx + point[2] * cx
    bx, by, bz = ax * cy + az * sy, ay, -ax * sy + az * cy
    return add((bx * cz - by * sz, bx * sz + by * cz, bz), xyz(frame["origin"]))


def ring_solid(rings, closed=False):
    n = len(rings[0])
    vertices = [v for ring in rings for v in ring]
    faces = []
    count = len(rings) if closed else len(rings) - 1
    for j in range(count):
        nxt = (j + 1) % len(rings)
        for i in range(n):
            k = (i + 1) % n
            faces.append((j * n + i, j * n + k, nxt * n + k, nxt * n + i))
    if not closed:
        faces.extend((tuple(reversed(range(n))), tuple((len(rings) - 1) * n + i for i in range(n))))
    return vertices, faces


def transport(normal, before, after):
    axis = cross(before, after)
    sine = math.sqrt(dot(axis, axis))
    cosine = max(-1.0, min(1.0, dot(before, after)))
    if sine < 1e-12:
        require(cosine > 0, "Reversing sweep tangent")
        return normal
    axis = mul(axis, 1 / sine)
    # Rodrigues rotation, followed only by roundoff orthogonalization.
    rotated = add(add(mul(normal, cosine), mul(cross(axis, normal), sine)), mul(axis, dot(axis, normal) * (1 - cosine)))
    return unit(sub(rotated, mul(after, dot(rotated, after))))


def sweep_rings(path, radius, segments):
    closed = path[0] == path[-1]
    points = path[:-1] if closed else path
    directions = [unit(sub(points[(i + 1) % len(points)], points[i])) for i in range(len(points) if closed else len(points) - 1)]
    tangents = []
    for i in range(len(points)):
        if not closed and i == 0:
            tangents.append(directions[0])
        elif not closed and i == len(points) - 1:
            tangents.append(directions[-1])
        else:
            tangents.append(unit(add(directions[i - 1], directions[i % len(directions)])))
    seed = min(((1, 0, 0), (0, 1, 0), (0, 0, 1)), key=lambda axis: abs(dot(axis, tangents[0])))
    normals = [unit(cross(tangents[0], seed))]
    for i in range(1, len(points)):
        normals.append(transport(normals[-1], tangents[i - 1], tangents[i]))
    if closed:
        final = transport(normals[-1], tangents[-1], tangents[0])
        twist = math.atan2(dot(tangents[0], cross(final, normals[0])), dot(final, normals[0]))
        # Distribute closed-loop holonomy over the rings; the seam uses the
        # original first ring exactly, with no duplicated seam vertices.
        for i in range(1, len(normals)):
            angle = twist * i / len(normals)
            normals[i] = add(mul(normals[i], math.cos(angle)), mul(cross(tangents[i], normals[i]), math.sin(angle)))
    rings = []
    for point, tangent, normal in zip(points, tangents, normals):
        binormal = cross(tangent, normal)
        rings.append([add(point, add(mul(normal, radius * math.cos(2 * math.pi * i / segments)),
                                     mul(binormal, radius * math.sin(2 * math.pi * i / segments)))) for i in range(segments)])
    return ring_solid(rings, closed)


def feature_mesh(feature):
    p = feature["parameters"]
    kind = feature["kind"]
    if kind == "box":
        w, d, h = p["width"], p["depth"], p["height"]
        vertices = [(0, 0, 0), (w, 0, 0), (w, d, 0), (0, d, 0), (0, 0, h), (w, 0, h), (w, d, h), (0, d, h)]
        faces = [(0, 3, 2, 1), (4, 5, 6, 7), (0, 1, 5, 4), (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)]
    elif kind == "cylinder":
        n = int(p["segments"])
        vertices, faces = ring_solid([[(p["radius"] * math.cos(2 * math.pi * i / n), p["radius"] * math.sin(2 * math.pi * i / n), z) for i in range(n)] for z in (0, p["length"])])
    elif kind == "extrude":
        vertices, faces = ring_solid([[(q["x"], q["y"], z) for q in feature["profile"]] for z in (0, p["depth"])])
    elif kind == "loft":
        vertices, faces = ring_solid([[transform(section["frame"], (q["x"], q["y"], 0)) for q in section["profile"]] for section in feature["sections"]])
    elif kind == "sweep":
        vertices, faces = sweep_rings([xyz(point) for point in feature["path"]], p["radius"], int(p["segments"]))
    elif kind == "revolve":
        n, angle = int(p["segments"]), math.radians(p["angleDegrees"])
        closed = p["angleDegrees"] == 360
        count = n if closed else n + 1
        profile = feature["profile"]
        # Weld axis vertices across all angular stations rather than producing
        # coincident zero-area strips at the axis of an otherwise valid solid.
        vertices, lookup, rings = [], {}, []
        for j in range(count):
            row = []
            for i, q in enumerate(profile):
                key = (i, 0 if q["x"] == 0 else j)
                if key not in lookup:
                    lookup[key] = len(vertices)
                    vertices.append((q["x"] * math.cos(angle * j / n), q["x"] * math.sin(angle * j / n), q["y"]))
                row.append(lookup[key])
            rings.append(row)
        faces = []
        for j in range(n):
            nxt = (j + 1) % count
            for i in range(len(profile)):
                k = (i + 1) % len(profile)
                face = list(dict.fromkeys((rings[j][i], rings[j][k], rings[nxt][k], rings[nxt][i])))
                if len(face) >= 3:
                    faces.append(tuple(face))
        if not closed:
            faces.extend((tuple(reversed(rings[0])), tuple(rings[-1])))
    else:
        raise ValueError("Unadmitted feature kind")
    return [transform(feature["frame"], point) for point in vertices], faces


def require_feature_precision(obj, authored_vertices, linear_tolerance):
    require(len(obj.data.vertices) == len(authored_vertices), "Authored feature vertex correspondence changed")
    for vertex, authored in zip(obj.data.vertices, authored_vertices):
        delta = sub(tuple(float(c) for c in vertex.co), authored)
        require(math.sqrt(dot(delta, delta)) <= linear_tolerance,
                "Selected Blender cannot represent authored feature coordinates within approved linear tolerance")


def mesh_object(bpy, name, vertices, faces, collection, linear_tolerance):
    import bmesh
    mesh = bpy.data.meshes.new(name + ".mesh")
    mesh.from_pydata(vertices, [], faces)
    mesh.update()
    bm = bmesh.new()
    try:
        bm.from_mesh(mesh)
        bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
        require(all(edge.is_manifold for edge in bm.edges), "Feature is not a closed manifold")
        require(bm.calc_volume(signed=True) > 0, "Feature has no positive solid volume")
        bm.to_mesh(mesh)
    finally:
        bm.free()
    obj = bpy.data.objects.new(name, mesh)
    collection.objects.link(obj)
    require_feature_precision(obj, vertices, linear_tolerance)
    return obj


def boolean(bpy, physical, operand, operation):
    modifier = physical.modifiers.new("exact." + operand.name, "BOOLEAN")
    modifier.operation = "UNION" if operation == "add" else "DIFFERENCE"
    modifier.solver = "EXACT"
    modifier.object = operand
    bpy.context.view_layer.objects.active = physical
    physical.hide_set(False)
    physical.select_set(True)
    bpy.ops.object.modifier_apply(modifier=modifier.name)
    physical.select_set(False)


def canonical_geometry(obj, bpy, max_vertices=2000000, max_triangles=2000000):
    depsgraph = bpy.context.evaluated_depsgraph_get()
    evaluated = obj.evaluated_get(depsgraph)
    mesh = evaluated.to_mesh()
    try:
        require(0 < len(mesh.vertices) <= max_vertices, "Evaluated vertex allocation budget")
        triangle_count = sum(len(poly.vertices) - 2 for poly in mesh.polygons)
        require(0 < triangle_count <= max_triangles, "Evaluated triangle allocation budget")
        mesh.calc_loop_triangles()
        vertices = [tuple(float(c) for c in obj.matrix_world @ vertex.co) for vertex in mesh.vertices]
        require(vertices and all(math.isfinite(c) for v in vertices for c in v), "Nonfinite/empty evaluated mesh")
        require(len(set(vertices)) == len(vertices), "Coincident evaluated vertices rejected")
        order = sorted(range(len(vertices)), key=lambda i: vertices[i])
        indices = {old: new for new, old in enumerate(order)}
        vertices = [vertices[i] for i in order]
        triangles = []
        for triangle in mesh.loop_triangles:
            face = tuple(indices[i] for i in triangle.vertices)
            start = face.index(min(face))
            triangles.append(face[start:] + face[:start])
        triangles.sort()
        require(len(set(triangles)) == len(triangles), "Repeated evaluated triangle")
        return vertices, triangles
    finally:
        evaluated.to_mesh_clear()


def geometry_bytes(vertices, triangles):
    data = bytearray(struct.pack("<8sII", b"BUBOPRT1", len(vertices), len(triangles)))
    for vertex in vertices:
        data.extend(struct.pack("<ddd", *vertex))
    for triangle in triangles:
        data.extend(struct.pack("<III", *triangle))
    return bytes(data)


def measurements(vertices, triangles):
    require(len(vertices) >= 4 and len(triangles) >= 4, "Solid mesh required")
    edges, adjacency, used = {}, [[] for _ in vertices], set()
    vertex_links = [[] for _ in vertices]
    volume, area = 0.0, 0.0
    for a, b, c in triangles:
        require(len({a, b, c}) == 3 and max(a, b, c) < len(vertices), "Invalid solid triangle")
        pa, pb, pc = vertices[a], vertices[b], vertices[c]
        normal = cross(sub(pb, pa), sub(pc, pa))
        triangle_area = math.sqrt(dot(normal, normal)) * 0.5
        require(triangle_area > 1e-18, "Degenerate solid triangle")
        area += triangle_area
        volume += dot(pa, cross(pb, pc)) / 6
        used.update((a, b, c))
        vertex_links[a].append((b, c))
        vertex_links[b].append((c, a))
        vertex_links[c].append((a, b))
        for left, right in ((a, b), (b, c), (c, a)):
            edge = (min(left, right), max(left, right))
            edges.setdefault(edge, []).append(1 if left < right else -1)
            adjacency[left].append(right)
            adjacency[right].append(left)
    require(len(used) == len(vertices), "Loose solid vertices rejected")
    require(all(len(directions) == 2 and sum(directions) == 0 for directions in edges.values()), "Solid must be consistently oriented closed manifold")
    for link in vertex_links:
        graph = {}
        for left, right in link:
            graph.setdefault(left, set()).add(right)
            graph.setdefault(right, set()).add(left)
        require(graph and all(len(neighbors) == 2 for neighbors in graph.values()), "Solid vertex link must be a cycle")
        reached, stack = set(), [next(iter(graph))]
        while stack:
            node = stack.pop()
            if node not in reached:
                reached.add(node)
                stack.extend(graph[node] - reached)
        require(len(reached) == len(graph), "Nonmanifold bow-tie vertex link rejected")
    component_index, components = {}, 0
    for start in range(len(vertices)):
        if start in component_index:
            continue
        stack = [start]
        component_index[start] = components
        while stack:
            for other in adjacency[stack.pop()]:
                if other not in component_index:
                    component_index[other] = components
                    stack.append(other)
        components += 1
    component_volumes = [0.0] * components
    for a, b, c in triangles:
        component_volumes[component_index[a]] += dot(vertices[a], cross(vertices[b], vertices[c])) / 6
    require(all(value > 1e-18 for value in component_volumes), "Each solid component requires positive outward volume")
    require(components == 1, "One connected physical solid required; material islands and sealed inner boundary shells unsupported")
    return {"vertexCount": len(vertices), "triangleCount": len(triangles), "minimum": vec(tuple(min(p[i] for p in vertices) for i in range(3))),
            "maximum": vec(tuple(max(p[i] for p in vertices) for i in range(3))), "volume": volume, "surfaceArea": area,
            "components": components, "manifold": True}


class GeometryWork:
    """One cumulative finite shape/mesh proof allowance per worker process."""
    def __init__(self, job):
        self.numerics = job["design"]["numerics"]
        self.maximum = min(job["design"]["budgets"]["maxValidationChecks"], 5000000)
        self.used = sum(len(f["profile"]) ** 2 + len(f["path"]) ** 2 +
                        sum(len(s["profile"]) ** 2 for s in f["sections"]) + 1
                        for p in job["design"]["parts"] for f in p["features"])

    def charge(self, count):
        self.used += count
        require(self.used <= self.maximum, "Cumulative exact geometry proof work budget")


def integer_vertices(vertices):
    # Every finite binary floating coordinate is an exact dyadic rational.
    # A common power-of-two denominator makes all following predicates integer
    # exact, including coplanar contact and intersections near shared vertices.
    ratios = [[value.as_integer_ratio() for value in point] for point in vertices]
    denominator = max(pair[1] for point in ratios for pair in point)
    return [tuple(numerator * (denominator // divisor) for numerator, divisor in point) for point in ratios]


def intersection_allowed(point, denominator, common, vertices):
    if not common:
        return False
    a = vertices[common[0]]
    delta = sub(point, mul(a, denominator))
    if len(common) == 1:
        return delta == (0, 0, 0)
    b = vertices[common[1]]
    edge = sub(b, a)
    return cross(delta, edge) == (0, 0, 0) and 0 <= dot(delta, edge) <= denominator * dot(edge, edge)


def inside_triangle(point, denominator, triangle, vertices, normal):
    for i in range(3):
        a, b = vertices[triangle[i]], vertices[triangle[(i + 1) % 3]]
        if dot(cross(sub(b, a), sub(point, mul(a, denominator))), normal) < 0:
            return False
    return True


def planar_segment_points(a, b, c, d, normal):
    axis = max(range(3), key=lambda k: abs(normal[k]))
    indices = [k for k in range(3) if k != axis]
    orient = lambda u, v: u[indices[0]] * v[indices[1]] - u[indices[1]] * v[indices[0]]
    r, s, offset = sub(b, a), sub(d, c), sub(c, a)
    denominator = orient(r, s)
    if denominator:
        t, u = orient(offset, s), orient(offset, r)
        if denominator < 0:
            denominator, t, u = -denominator, -t, -u
        if 0 <= t <= denominator and 0 <= u <= denominator:
            yield add(mul(a, denominator), mul(r, t)), denominator
    elif orient(offset, r) == 0:
        for point, left, right in ((a, c, d), (b, c, d), (c, a, b), (d, a, b)):
            edge, delta = sub(right, left), sub(point, left)
            if 0 <= dot(delta, edge) <= dot(edge, edge):
                yield point, 1


def triangle_pair_intersects(vertices, left, right):
    common = list(set(left).intersection(right))
    require(len(common) < 3, "Repeated geometric triangle")
    a, b = vertices[left[0]], vertices[right[0]]
    na = cross(sub(vertices[left[1]], a), sub(vertices[left[2]], a))
    nb = cross(sub(vertices[right[1]], b), sub(vertices[right[2]], b))
    distances_b = [dot(na, sub(vertices[i], a)) for i in right]
    distances_a = [dot(nb, sub(vertices[i], b)) for i in left]
    if min(distances_b) > 0 or max(distances_b) < 0 or min(distances_a) > 0 or max(distances_a) < 0:
        return False
    coplanar = all(value == 0 for value in distances_b)
    if len(common) == 2:
        if not coplanar:
            # Distinct planes intersect only along their actual shared edge.
            return False
        u, v = (vertices[i] for i in common)
        third_a = vertices[next(i for i in left if i not in common)]
        third_b = vertices[next(i for i in right if i not in common)]
        return dot(cross(sub(v, u), sub(third_a, u)), cross(sub(v, u), sub(third_b, u))) >= 0
    if coplanar:
        for triangle, other, normal in ((left, right, nb), (right, left, na)):
            for index in triangle:
                if inside_triangle(vertices[index], 1, other, vertices, normal) and not intersection_allowed(vertices[index], 1, common, vertices):
                    return True
        for i in range(3):
            for j in range(3):
                for point, denominator in planar_segment_points(vertices[left[i]], vertices[left[(i + 1) % 3]], vertices[right[j]], vertices[right[(j + 1) % 3]], na):
                    if not intersection_allowed(point, denominator, common, vertices):
                        return True
        return False
    for triangle, other, distances, normal in ((left, right, distances_a, nb), (right, left, distances_b, na)):
        for i in range(3):
            j = (i + 1) % 3
            p, q, dp, dq = vertices[triangle[i]], vertices[triangle[j]], distances[i], distances[j]
            if dp * dq > 0:
                continue
            if dp == 0 and dq == 0:
                for point in (p, q):
                    if inside_triangle(point, 1, other, vertices, normal) and not intersection_allowed(point, 1, common, vertices):
                        return True
                for k in range(3):
                    for point, denominator in planar_segment_points(p, q, vertices[other[k]], vertices[other[(k + 1) % 3]], normal):
                        if not intersection_allowed(point, denominator, common, vertices):
                            return True
            else:
                denominator = dp - dq
                point = sub(mul(q, dp), mul(p, dq))
                if denominator < 0:
                    point, denominator = mul(point, -1), -denominator
                if inside_triangle(point, denominator, other, vertices, normal) and not intersection_allowed(point, denominator, common, vertices):
                    return True
    return False


def reject_self_intersections(vertices, triangles, work):
    """Streaming AABB broad phase; no quadratic candidate list is allocated."""
    work.charge(len(vertices) + len(triangles) * max(1, len(triangles).bit_length()))
    integral = integer_vertices(vertices)
    bounds = [(tuple(min(vertices[i][k] for i in triangle) for k in range(3)),
               tuple(max(vertices[i][k] for i in triangle) for k in range(3))) for triangle in triangles]
    axis = max(range(3), key=lambda k: max(b[1][k] for b in bounds) - min(b[0][k] for b in bounds))
    order = sorted(range(len(triangles)), key=lambda i: (bounds[i][0][axis], i))
    active = []
    for index in order:
        lo, hi = bounds[index]
        work.charge(len(active))
        retained = []
        for other in active:
            other_lo, other_hi = bounds[other]
            if other_hi[axis] < lo[axis]:
                continue
            retained.append(other)
            if all(lo[k] <= other_hi[k] and other_lo[k] <= hi[k] for k in range(3)):
                require(not triangle_pair_intersects(integral, triangles[index], triangles[other]), "Actual solid triangle self-intersection/contact rejected")
        retained.append(index)
        active = retained


def linear_color(hex_color):
    channels = [int(hex_color[i:i + 2], 16) / 255 for i in (1, 3, 5)]
    return tuple(c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4 for c in channels)


def create_material(bpy, spec):
    material = bpy.data.materials.new("material." + spec["id"])
    material.use_nodes = True
    material["bubo.materialId"] = spec["id"]
    material.node_tree.nodes.clear()
    shader = material.node_tree.nodes.new("ShaderNodeBsdfPrincipled")
    output = material.node_tree.nodes.new("ShaderNodeOutputMaterial")
    material.node_tree.links.new(shader.outputs["BSDF"], output.inputs["Surface"])
    shader.inputs["Base Color"].default_value = (*linear_color(spec["color"]), 1)
    shader.inputs["Roughness"].default_value = spec["roughness"]
    shader.inputs["Metallic"].default_value = spec["metallic"]
    material.diffuse_color = (*linear_color(spec["color"]), 1)
    material.roughness, material.metallic = spec["roughness"], spec["metallic"]
    return material


def material_measurements(obj, spec):
    require(len(obj.data.materials) == 1 and obj.data.materials[0] is not None, "One actual owned material required")
    require(all(poly.material_index == 0 for poly in obj.data.polygons), "Unexpected face material")
    material = obj.data.materials[0]
    require(material.get("bubo.materialId") == spec["id"] and material.use_nodes, "Material identity missing")
    nodes = material.node_tree.nodes
    require(len(nodes) == 2 and len(material.node_tree.links) == 1, "Unexpected material shader graph")
    shaders = [node for node in nodes if node.type == "BSDF_PRINCIPLED"]
    outputs = [node for node in nodes if node.type == "OUTPUT_MATERIAL"]
    require(len(shaders) == 1 and len(outputs) == 1, "Bounded material graph required")
    shader = shaders[0]
    link = next(iter(material.node_tree.links))
    require(link.from_node == shader and link.to_node == outputs[0] and link.to_socket.name == "Surface", "Unexpected material connection")
    color = tuple(float(c) for c in shader.inputs["Base Color"].default_value[:3])
    rough, metal = float(shader.inputs["Roughness"].default_value), float(shader.inputs["Metallic"].default_value)
    require(all(abs(a - b) <= 2e-7 for a, b in zip(color, linear_color(spec["color"]))) and abs(rough - spec["roughness"]) <= 2e-7 and abs(metal - spec["metallic"]) <= 2e-7, "Actual material differs from approved design")
    require(abs(shader.inputs["Base Color"].default_value[3] - 1) <= 1e-7, "Unexpected material transparency")
    return {"materialColorLinear": vec(color), "materialRoughness": rough, "materialMetallic": metal}


def stable_json(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), allow_nan=False)


def object_name(kind, index=0):
    # Blender datablock name limits never truncate identity: names are bounded
    # ordinals; exact approved IDs stay in owned custom properties/receipts.
    return "part" if kind == "part" else kind + "." + format(index, "06d")


def check_port_frame(obj, port, numerics):
    require(tuple(obj.delta_location) == (0.0, 0.0, 0.0)
            and tuple(obj.delta_scale) == (1.0, 1.0, 1.0)
            and tuple(obj.delta_rotation_euler) == (0.0, 0.0, 0.0)
            and tuple(obj.delta_rotation_quaternion) == (1.0, 0.0, 0.0, 0.0),
            "Port delta transforms must remain identity")
    expected = port["frame"]
    origin = tuple(float(obj.matrix_world[i][3]) for i in range(3))
    delta = sub(origin, xyz(expected["origin"]))
    require(math.sqrt(dot(delta, delta)) <= numerics["linearTolerance"],
            "Selected Blender cannot represent port origin within approved linear tolerance")
    require(tuple(obj.scale) == (1.0, 1.0, 1.0) and obj.rotation_mode == "XYZ", "Port frame must remain a pure XYZ rotation/translation")
    from mathutils import Vector
    rotation_only = {"origin": {"x": 0, "y": 0, "z": 0}, "rotationDegrees": expected["rotationDegrees"]}
    for axis in ((1, 0, 0), (0, 1, 0), (0, 0, 1)):
        measured_axis = tuple(float(c) for c in obj.matrix_world.to_3x3() @ Vector(axis))
        authored_axis = transform(rotation_only, axis)
        a, b = unit(measured_axis), unit(authored_axis)
        sine = math.sqrt(dot(cross(a, b), cross(a, b)))
        angle = math.degrees(math.atan2(sine, max(-1.0, min(1.0, dot(a, b)))))
        require(angle <= numerics["angularToleranceDegrees"],
                "Selected Blender cannot represent port rotation within approved angular tolerance")


def create_part(bpy, part, material, definition_sha, work):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.unit_settings.system = "METRIC"
    scene.unit_settings.scale_length = 1
    physical_collection = bpy.data.collections.new("physical")
    editable = bpy.data.collections.new("editable-features")
    ports = bpy.data.collections.new("ports")
    for collection in (physical_collection, editable, ports):
        scene.collection.children.link(collection)
    mat = create_material(bpy, material)
    physical = None
    for index, feature in enumerate(part["features"]):
        vertices, faces = feature_mesh(feature)
        operand = mesh_object(bpy, object_name("feature", index), vertices, faces, editable, work.numerics["linearTolerance"])
        operand_vertices, operand_triangles = canonical_geometry(operand, bpy)
        measurements(operand_vertices, operand_triangles)
        reject_self_intersections(operand_vertices, operand_triangles, work)
        operand["bubo.owner"] = part["id"]
        operand["bubo.feature"] = stable_json(feature)
        operand.display_type = "WIRE"
        operand.hide_render = True
        if physical is None:
            physical = bpy.data.objects.new(object_name("part"), operand.data.copy())
            physical_collection.objects.link(physical)
            physical.data.materials.clear()
            physical.data.materials.append(mat)
            physical["bubo.definitionSha256"] = definition_sha
            physical["bubo.partId"] = part["id"]
        else:
            boolean(bpy, physical, operand, feature["operation"])
        operand.hide_set(True)
    editable.hide_render = True
    for kind, collection in (("interface", part["interfaces"]), ("terminal", part["terminals"])):
        for index, port in enumerate(collection):
            obj = bpy.data.objects.new(object_name(kind, index), None)
            ports.objects.link(obj)
            obj.empty_display_type = "ARROWS"
            obj.location = xyz(port["frame"]["origin"])
            obj.rotation_mode = "XYZ"
            obj.rotation_euler = tuple(math.radians(v) for v in xyz(port["frame"]["rotationDegrees"]))
            obj["bubo.owner"], obj["bubo.port"] = part["id"], stable_json(port)
            obj.hide_render = True
    physical["bubo.part"] = stable_json(part)
    bpy.context.view_layer.update()
    for kind, collection in (("interface", part["interfaces"]), ("terminal", part["terminals"])):
        for index, port in enumerate(collection):
            check_port_frame(bpy.data.objects[object_name(kind, index)], port, work.numerics)
    return physical


def semantic_inventory(bpy, part, definition_sha, work):
    expected = {object_name("part"), *[object_name("feature", i) for i in range(len(part["features"]))],
                *[object_name("interface", i) for i in range(len(part["interfaces"]))], *[object_name("terminal", i) for i in range(len(part["terminals"]))]}
    require(set(obj.name for obj in bpy.data.objects) == expected, "Unexpected semantic object inventory")
    require(set(c.name for c in bpy.data.collections) == {"physical", "editable-features", "ports"}, "Unexpected collection inventory")
    require(not bpy.data.libraries and not bpy.data.texts, "External libraries/embedded scripts forbidden")
    physical = bpy.data.objects[object_name("part")]
    require(physical.type == "MESH" and not physical.modifiers and not physical.constraints and not physical.animation_data and physical.parent is None, "Unexpected physical object behavior")
    require(physical.get("bubo.partId") == part["id"] and physical.get("bubo.definitionSha256") == definition_sha and physical.get("bubo.part") == stable_json(part), "Part ancestry mismatch")
    require(list(physical.users_collection) == [bpy.data.collections["physical"]] and not physical.hide_render and not physical.hide_get(), "Physical ownership/visibility mismatch")
    require(all(abs(physical.matrix_world[i][j] - (1 if i == j else 0)) <= 1e-8 for i in range(4) for j in range(4)), "Physical transform must remain part-local identity")
    for index, feature in enumerate(part["features"]):
        operand = bpy.data.objects[object_name("feature", index)]
        require(operand.type == "MESH" and operand.get("bubo.owner") == part["id"] and operand.get("bubo.feature") == stable_json(feature), "Feature identity mismatch")
        require(list(operand.users_collection) == [bpy.data.collections["editable-features"]] and operand.hide_render and operand.hide_get() and not operand.modifiers and not operand.constraints and not operand.animation_data and operand.parent is None, "Operand ownership/behavior mismatch")
        # Rebuild the admitted primitive in a transient mesh and compare actual
        # geometry, rather than trusting the editable operand's custom fields.
        verts, faces = feature_mesh(feature)
        require_feature_precision(operand, verts, work.numerics["linearTolerance"])
        temporary = mesh_object(bpy, "verification-primitive", verts, faces, bpy.context.scene.collection, work.numerics["linearTolerance"])
        try:
            actual = canonical_geometry(operand, bpy)
            require(actual == canonical_geometry(temporary, bpy), "Editable operand geometry differs from design")
            measurements(*actual)
            reject_self_intersections(*actual, work)
        finally:
            mesh = temporary.data
            bpy.data.objects.remove(temporary, do_unlink=True)
            bpy.data.meshes.remove(mesh)
    for kind, ports in (("interface", part["interfaces"]), ("terminal", part["terminals"])):
        for index, port in enumerate(ports):
            obj = bpy.data.objects[object_name(kind, index)]
            require(obj.type == "EMPTY" and obj.get("bubo.owner") == part["id"] and obj.get("bubo.port") == stable_json(port), "Port ancestry mismatch")
            require(list(obj.users_collection) == [bpy.data.collections["ports"]] and obj.parent is None and not obj.constraints and not obj.animation_data and obj.hide_render, "Port ownership/behavior mismatch")
            check_port_frame(obj, port, work.numerics)
    return physical


def independently_replay(bpy, part, physical):
    first = bpy.data.objects[object_name("feature", 0)]
    replay = bpy.data.objects.new("verification-replay", first.data.copy())
    bpy.context.scene.collection.objects.link(replay)
    try:
        for index, feature in enumerate(part["features"][1:], 1):
            boolean(bpy, replay, bpy.data.objects[object_name("feature", index)], feature["operation"])
        require(canonical_geometry(replay, bpy) == canonical_geometry(physical, bpy), "Physical solid differs from independent exact-boolean replay")
    finally:
        mesh = replay.data
        bpy.data.objects.remove(replay, do_unlink=True)
        bpy.data.meshes.remove(mesh)


def measured_part(job, part, physical, bpy, blend_path, geometry_path):
    vertices, triangles = canonical_geometry(physical, bpy, job["limits"]["maxVertices"], job["limits"]["maxTriangles"])
    require(len(vertices) <= job["limits"]["maxVertices"] and len(triangles) <= job["limits"]["maxTriangles"], "Evaluated mesh budget")
    measured = measurements(vertices, triangles)
    tolerance = job["design"]["numerics"]["linearTolerance"]
    require(all(measured["minimum"][key] >= -tolerance and measured["maximum"][key] <= part["size"][key] + tolerance for key in "xyz"), "Evaluated solid exceeds approved part envelope")
    material = next(item for item in job["design"]["materials"] if item["id"] == part["materialId"])
    measured.update(material_measurements(physical, material))
    measured.update({"id": part["id"], "definitionSha256": job["partIdentities"][part["id"]], "materialId": part["materialId"],
                     "blendFile": blend_path.name, "blendSha256": digest(blend_path),
                     "geometryFile": geometry_path.name, "geometrySha256": digest(geometry_path)})
    return measured, geometry_bytes(vertices, triangles)


def report(job, job_sha, parts, bpy):
    quantities = {}
    for instance in job["design"]["instances"]:
        quantities[instance["partId"]] = quantities.get(instance["partId"], 0) + 1
    weighted = sum(part["triangleCount"] * quantities.get(part["id"], 0) for part in parts)
    total = sum((Path(job["output"]) / part[key]).stat().st_size for part in parts for key in ("blendFile", "geometryFile"))
    require(sum(part["vertexCount"] for part in parts) <= job["limits"]["maxVertices"], "Cumulative evaluated vertex budget")
    require(sum(part["triangleCount"] for part in parts) <= job["limits"]["maxTriangles"] and weighted <= min(job["limits"]["maxTriangles"], job["design"]["budgets"]["maxGeneratedTriangles"]), "Cumulative/weighted evaluated triangle budget")
    require(total <= job["limits"]["maxOutputBytes"], "Cumulative output byte budget")
    return {"schemaVersion": "bubo-schematic-parts/v1", "jobSha256": job_sha, "identity": job["identity"], "blenderVersion": bpy.app.version_string,
            "parts": parts, "instances": job["design"]["instances"], "weightedTriangles": weighted, "totalBytes": total}


def write_new(path, data):
    with open(path, "xb") as stream:
        stream.write(data)


def write_report(path, value):
    write_new(path, report_bytes(value))


def report_bytes(value):
    return (json.dumps(value, indent=2, allow_nan=False) + "\n").encode("utf-8")
