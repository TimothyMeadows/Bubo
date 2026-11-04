"""Preserve-only mixed rigid/owned-flexible assembly admission and identity."""
import json
import math
import struct
import os
import sys
from pathlib import Path
from collections import deque
from itertools import product as combinations

from core import armatures, digest, identity, meshes, objects, text


SDK_ROOT = None
SDK_FILES = None


def verify_sdk_origins():
    import types
    import importlib.machinery
    if SDK_ROOT is None or SDK_FILES is None:raise ValueError("Mechanical SDK has not been verified")
    for name,module in list(sys.modules.items()):
        if name=="io_scene_gltf2" or name.startswith("io_scene_gltf2."):
            if type(module) is not types.ModuleType or type(getattr(module,"__spec__",None)) is not importlib.machinery.ModuleSpec:
                raise ValueError("Mechanical SDK loaded module/spec has unsupported identity")
            path=getattr(module,"__file__",None)
            if path is not None:
                actual=Path(path).resolve();spec=getattr(module,"__spec__",None)
                if not actual.is_relative_to(SDK_ROOT) or actual.relative_to(SDK_ROOT).as_posix() not in SDK_FILES or spec is None or Path(spec.origin).resolve()!=actual:
                    raise ValueError("Mechanical SDK module origin differs from reviewed installation")
                if spec.submodule_search_locations is not None:
                    expected=SDK_ROOT.joinpath(*name.split(".")[1:])
                    if [Path(value).resolve() for value in getattr(module,"__path__",())]!=[expected] or [Path(value).resolve() for value in spec.submodule_search_locations]!=[expected]:
                        raise ValueError("Mechanical SDK package search path differs from reviewed installation")
            else:
                expected=SDK_ROOT.joinpath(*name.split(".")[1:])
                origins=list(getattr(module,"__path__",()))
                if name=="io_scene_gltf2" or len(origins)!=1 or Path(origins[0]).resolve()!=expected or not expected.is_dir() or (expected/"__init__.py").exists():
                    raise ValueError("Mechanical SDK namespace origin differs from reviewed installation")


def preflight_sdk_import():
    """find_spec for the top-level package executes no package source."""
    import importlib.util
    import importlib.machinery
    if SDK_ROOT is None:raise ValueError("Mechanical SDK was not verified before import")
    verify_sdk_origins()
    spec=importlib.util.find_spec("io_scene_gltf2")
    if spec is None or spec.origin is None or Path(spec.origin).resolve()!=SDK_ROOT/"__init__.py" or [Path(value).resolve() for value in spec.submodule_search_locations or ()]!=[SDK_ROOT]:
        raise ValueError("Mechanical SDK import resolution differs from reviewed installation")
    if type(spec.loader) is not importlib.machinery.SourceFileLoader or spec.loader.name!="io_scene_gltf2" or Path(spec.loader.path).resolve()!=SDK_ROOT/"__init__.py":
        raise ValueError("Mechanical SDK import loader differs from reviewed source file")


def verify_sdk(job):
    """Verify exact reviewed installed bytes before importing SDK code and after work."""
    global SDK_ROOT,SDK_FILES
    import bpy
    selected=job.get("mechanicalToolchain")
    if not isinstance(selected,dict) or set(selected)!={"blenderPath","blenderSha256","gltfFiles"}:
        raise ValueError("Mechanical native job requires its immutable reviewed glTF SDK inventory")
    blender=Path(selected["blenderPath"])
    if not blender.is_absolute() or os.path.normcase(str(blender))!=os.path.normcase(bpy.app.binary_path) or digest(blender)!=selected["blenderSha256"]:
        raise ValueError("Selected mechanical Blender executable changed")
    root=blender.parent/"4.5/scripts/addons_core/io_scene_gltf2"
    def no_links(path):
        cursor=path
        while True:
            if cursor.is_symlink() or hasattr(os.path,"isjunction") and os.path.isjunction(cursor):raise ValueError("Linked mechanical SDK path")
            if cursor==blender.parent:break
            cursor=cursor.parent
    no_links(root)
    files=selected["gltfFiles"]
    if not isinstance(files,list) or not 1<=len(files)<=4096:raise ValueError("Bounded mechanical SDK inventory required")
    declared={};total=0
    for file in files:
        name=file["path"];count=file["bytes"]
        if not isinstance(name,str) or not name or "\\" in name or ":" in name or name.startswith("/") or any(part in ("",".","..") for part in name.split("/")) or any(ord(c)<32 for c in name) or type(count)is not int or not 0<=count<=16_777_216 or count>134_217_728-total:
            raise ValueError("Invalid immutable mechanical SDK entry")
        key=os.path.normcase(name)
        if key in declared:raise ValueError("Ambiguous mechanical SDK inventory")
        declared[key]=file;total+=count
    if os.path.normcase("__init__.py") not in declared:raise ValueError("Mechanical SDK package root missing")
    queue=deque([root]);observed=set();directories=1
    while queue:
        parent=queue.popleft()
        for path in parent.iterdir():
            no_links(path)
            if path.is_dir():
                directories+=1
                if directories>1024:raise ValueError("Mechanical SDK directory bound exceeded")
                queue.append(path);continue
            key=os.path.normcase(path.relative_to(root).as_posix())
            if key not in declared or key in observed or len(observed)>=4096:raise ValueError("Unexpected mechanical SDK file")
            file=declared[key]
            if not path.is_file() or path.stat().st_size!=file["bytes"] or digest(path)!=file["sha256"]:raise ValueError("Mechanical glTF SDK bytes changed")
            observed.add(key)
    if observed!=set(declared):raise ValueError("Mechanical SDK is incomplete")
    SDK_ROOT=root.resolve()
    SDK_FILES={file["path"] for file in files}
    verify_sdk_origins()
    preflight_sdk_import()


def import_static_gltf(source):
    """Pinned SDK hook creates every clip without applying its first key as bind rest."""
    import bpy
    import importlib
    preflight_sdk_import()
    class StaticBind:
        def gather_import_animations(self, animations, options, gltf):
            options.restore_first_anim = False
    settings = {"bone_heuristic":"BLENDER","bone_shape_scale_factor":1.,"disable_bone_shape":True,
                "guess_original_bind_pose":False,"export_import_convert_lighting_mode":"SPEC",
                "import_merge_material_slots":False,"import_pack_images":True,"import_scene_as_collection":False,
                "import_scene_extras":False,"import_select_created_objects":False,"import_shading":"NORMALS",
                "import_unused_materials":False,"import_webp_texture":False,"loglevel":40,
                "merge_vertices":False,"import_user_extensions":[StaticBind()]}
    importer_type=importlib.import_module("io_scene_gltf2.io.imp.gltf2_io_gltf").glTFImporter
    creator=importlib.import_module("io_scene_gltf2.blender.imp.blender_gltf").BlenderGlTF
    verify_sdk_origins()
    importer=importer_type(str(source),settings)
    importer.read();importer.checks();creator.create(importer)
    if any(obj.animation_data and (obj.animation_data.action is not None or any(not track.mute for track in obj.animation_data.nla_tracks)) for obj in bpy.context.scene.objects):
        raise ValueError("Mechanical static bind import must precede all explicit clip activation")


def approved_numerics(root, handoff, metadata=None):
    from core import confined
    def read(name):
        reference = handoff[name]
        path = confined(root, reference["path"])
        if not path.is_file() or path.stat().st_size > 8 * 1024 * 1024 or digest(path) != reference["sha256"]:
            raise ValueError("Changed/oversized approved mechanical " + name)
        return json.loads(path.read_text(encoding="utf-8-sig"))
    design, bound_metadata = read("design"), read("metadata")
    values = design.get("numerics")
    if not isinstance(values, dict) or set(values) != {"linearTolerance", "angularToleranceDegrees", "minimumFeatureSize", "maxCoordinateMagnitude"} or values != bound_metadata.get("numerics") or metadata is not None and values != metadata.get("numerics"):
        raise ValueError("Mechanical metadata numerical policy differs from the bound approved design")
    if any(type(value) not in (int, float) or not math.isfinite(value) or value <= 0 for value in values.values()) or not values["linearTolerance"] < values["minimumFeatureSize"] <= values["maxCoordinateMagnitude"] <= 1_000_000 or values["angularToleranceDegrees"] > 1:
        raise ValueError("Unsupported approved mechanical numerical policy")
    angular = math.radians(values["angularToleranceDegrees"])
    checks = design.get("assemblyProfile",{}).get("maxGeometryChecks")
    if type(checks) is not int or not 1 <= checks <= 50_000_000:
        raise ValueError("Mechanical comparison requires its approved finite geometry-check budget")
    return {"linear": values["linearTolerance"], "angular": angular, "scale": 1e-6, "orthogonality": min(1e-6,math.sin(angular)), "maxChecks":checks}


def distance(first, second):
    return math.sqrt(sum((a-b)**2 for a,b in zip(first, second)))


def node_matrix(node):
    if "matrix" in node:
        matrix = node["matrix"]
    else:
        x,y,z,w = node.get("rotation", [0,0,0,1])
        length = math.sqrt(x*x+y*y+z*z+w*w)
        if not math.isfinite(length) or abs(length-1)>1e-6:
            raise ValueError("Unsupported non-unit mechanical rotation")
        x,y,z,w = (value/length for value in (x,y,z,w))
        a,b,c = node.get("scale", [1,1,1])
        t = node.get("translation", [0,0,0])
        matrix = [(1-2*y*y-2*z*z)*a,(2*x*y+2*z*w)*a,(2*x*z-2*y*w)*a,0,
                  (2*x*y-2*z*w)*b,(1-2*x*x-2*z*z)*b,(2*y*z+2*x*w)*b,0,
                  (2*x*z+2*y*w)*c,(2*y*z-2*x*w)*c,(1-2*x*x-2*y*y)*c,0,*t,1]
    if len(matrix) != 16 or any(not math.isfinite(value) for value in matrix):
        raise ValueError("Invalid portable mechanical matrix")
    return matrix


def transformed(matrix, point):
    return tuple(sum(matrix[column*4+row]*point[column] for column in range(3))+matrix[12+row] for row in range(3))


def product(first, second):
    return [sum(first[k*4+row]*second[column*4+k] for k in range(4)) for column in range(4) for row in range(4)]


def transform_matches(first, second, n):
    if distance(first[12:15], second[12:15]) > n["linear"] or any(first[index] != 0 or second[index] != 0 for index in (3,7,11)) or first[15] != 1 or second[15] != 1:
        return False
    normalized = []
    for matrix in (first, second):
        columns = [matrix[index*4:index*4+3] for index in range(3)]
        scales = [math.sqrt(sum(value*value for value in column)) for column in columns]
        if any(scale <= 0 for scale in scales):
            return False
        basis = [tuple(value/scale for value in column) for column,scale in zip(columns,scales)]
        if any(abs(sum(a*b for a,b in zip(basis[i],basis[j]))) > n["orthogonality"] for i,j in ((0,1),(0,2),(1,2))):
            return False
        normalized.append((basis,scales))
    if any(abs(a-b) > n["scale"] for a,b in zip(normalized[0][1],normalized[1][1])):
        return False
    trace = sum(sum(a*b for a,b in zip(first_axis,second_axis)) for first_axis,second_axis in zip(normalized[0][0],normalized[1][0]))
    return math.acos(max(-1,min(1,(trace-1)/2))) <= n["angular"]


def validate_recipe(recipe):
    rig = recipe["rig"]
    if rig.get("mode") != "preserve" or set(rig) != {"mode", "rootNode"} or not isinstance(rig.get("rootNode"), str) or not rig["rootNode"]:
        raise ValueError("Mechanical assembly preserves existing rigs; rig construction and single-rig targets are unsupported")
    if recipe["parts"] or recipe["anchors"]:
        raise ValueError("Mechanical assembly preserves its approved hierarchy/pivots; new parts and anchors require a revised design")
    for clip in recipe["clips"]:
        if clip["rootMotion"] != "in-place":
            raise ValueError("Mechanical assembly motion preserves its assembly root")
        for channel in clip["channels"]:
            target = channel.get("target", {})
            if set(target) not in ({"node"}, {"rig", "bone"}):
                raise ValueError("Mechanical bone channels require explicit owning rig and bone")


def bindings(document):
    return {"nodes": document.get("nodes", []), "skins": document.get("skins", [])}


def admit(job, root, source, recipe):
    from core import confined, read_glb
    value = job.get("mechanicalHandoff")
    if not isinstance(value, dict) or value.get("schemaVersion") != "bubo-mechanical-assembly-handoff/v1":
        raise ValueError("Mechanical assembly requires an explicit immutable reviewed handoff")
    if not isinstance(job.get("mechanicalMetadata"), dict):
        raise ValueError("Mechanical relationship metadata is required")
    approved_numerics(root, value, job["mechanicalMetadata"])
    # The native caller supplies a current-state fence. Historical snapshots cannot
    # adopt a newer failed attempt or changed/rejected review.
    state = value.get("sourceState")
    if not isinstance(state, dict) or set(state) != {"design", "parts", "assembly", "export"}:
        raise ValueError("Current complete mechanical source-state fence required")
    for reference in state.values():
        path = confined(root, reference["path"])
        if not path.is_file() or path.stat().st_size > 8 * 1024 * 1024 or digest(path) != reference["sha256"]:
            raise ValueError("Latest mechanical source state invalidated the handoff")
    if source.suffix.lower() == ".glb" and job["operation"] in ("inspect", "prepare"):
        if digest(source) != value["model"]["sha256"] or bindings(read_glb(source)) != value["bindings"]:
            raise ValueError("Mechanical source does not match exact approved handoff bindings")
    validate_recipe(recipe)
    if source.suffix.lower() == ".glb":
        restore_import_hierarchy(value)
    return value


def restore_import_hierarchy(handoff):
    """Restore owned source frames after importer parenting and skinned-node transform baking."""
    import bpy
    from mathutils import Matrix
    portable = handoff["bindings"]["nodes"]
    parents = {}
    for parent in portable:
        for child in parent.get("children", []):
            parents[portable[child]["name"]] = parent["name"]
    scene = {obj.name: obj for obj in objects()}
    worlds = {name: obj.matrix_world.copy() for name, obj in scene.items()}
    for name, obj in scene.items():
        expected = parents.get(name)
        if expected is not None and expected not in scene:
            # Bone parenting has a separate owning armature and is not rewritten.
            continue
        obj.parent = scene.get(expected)
    for name, world in worlds.items():
        scene[name].matrix_world = world
    # The glTF importer bakes a skinned mesh node's nonzero transform into its
    # vertices and inverse binds. Restore that approved frame without changing
    # a single world-space surface point or borrowing another component's data.
    by_name = {node["name"]:node for node in portable}
    cached = {}
    def portable_world(name):
        if name not in cached:
            local = node_matrix(by_name[name])
            cached[name] = product(portable_world(parents[name]),local) if name in parents else local
        return cached[name]
    conversion = Matrix(((1,0,0,0),(0,0,-1,0),(0,1,0,0),(0,0,0,1)))
    for component in handoff["components"]:
        if component["kind"] != "flexible": continue
        obj = scene[component["node"]]
        values = portable_world(obj.name)
        desired = conversion @ Matrix(tuple(tuple(values[column*4+row] for column in range(4)) for row in range(4))) @ conversion.inverted()
        correction = desired.inverted() @ obj.matrix_world
        obj.data = obj.data.copy()
        obj.data.transform(correction)
        obj.matrix_world = desired
        obj.data.update()
    bpy.context.view_layer.update()


def validate_scene(handoff, recipe, establish=False):
    import bpy
    components = handoff["components"]
    nodes = {obj.name: obj for obj in objects() if obj.type not in ("CAMERA", "LIGHT")}
    mesh_names = {obj.name for obj in meshes()}
    if mesh_names != {component["node"] for component in components}:
        raise ValueError("Mechanical physical component count or identity changed")
    owned = {}
    for component in components:
        mesh = nodes[component["node"]]
        modifiers = [modifier for modifier in mesh.modifiers if modifier.type == "ARMATURE"]
        if component["kind"] == "rigid":
            if modifiers or mesh.vertex_groups:
                raise ValueError("Rigid component acquired an undeclared skin")
            continue
        rig = nodes.get(component.get("rigNode"))
        if rig is None or rig.type != "ARMATURE" or rig.name in owned or len(modifiers) != 1 or modifiers[0].object != rig:
            raise ValueError("Flexible component requires its exact separately owned armature")
        owned[rig.name] = mesh.name
        names = {bone.name for bone in rig.data.bones if bone.use_deform}
        groups = {group.index: group.name for group in mesh.vertex_groups}
        if not names or set(groups.values()) - {bone.name for bone in rig.data.bones}:
            raise ValueError("Flexible vertex groups reference a different rig")
        for vertex in mesh.data.vertices:
            if any(group.weight > 1e-8 and groups.get(group.group) not in names for group in vertex.groups):
                raise ValueError("Flexible vertex has a weight outside its owning deform rig")
            weights = [group.weight for group in vertex.groups if groups.get(group.group) in names and group.weight > 1e-8]
            if not weights or abs(sum(weights) - 1) > .001 or len(weights) > recipe["budgets"]["maxInfluences"]:
                raise ValueError("Invalid per-owner mechanical skin weights")
    if set(owned) != {rig.name for rig in armatures()}:
        raise ValueError("Mechanical scene has an undeclared rig")
    current = scene_identity()
    previous = bpy.context.scene.get("bubo_mechanical_rest_identity")
    if establish:
        if previous and json.loads(previous) != current:
            raise ValueError("Mechanical rest hierarchy/geometry changed")
        bpy.context.scene["bubo_mechanical_rest_identity"] = json.dumps(current, sort_keys=True, separators=(",", ":"))
    elif previous is not None and json.loads(previous) != current:
        raise ValueError("Mechanical rest hierarchy/rig/geometry changed")
    return current


def scene_identity():
    # Stored rest matrices avoid hashing a currently sampled animation pose.
    result = []
    for obj in sorted((obj for obj in objects() if obj.type not in ("CAMERA", "LIGHT")), key=lambda obj: obj.name):
        matrix = json.loads(obj["hephaera_rest_matrix"]) if "hephaera_rest_matrix" in obj else [list(row) for row in obj.matrix_basis]
        value = {"node": obj.name, "parent": obj.parent.name if obj.parent else None, "kind": obj.type, "rest": matrix}
        if obj.type == "ARMATURE":
            value["bones"] = [{"name": bone.name, "parent": bone.parent.name if bone.parent else None, "matrix": [list(row) for row in bone.matrix_local], "deform": bone.use_deform} for bone in obj.data.bones]
        elif obj.type == "MESH":
            value["geometry"] = identity({"vertices": [list(vertex.co) for vertex in obj.data.vertices], "faces": [list(face.vertices) for face in obj.data.polygons], "materials": [material.name if material else None for material in obj.data.materials], "assignments": [face.material_index for face in obj.data.polygons]})
            value["skin"] = [{"rig": modifier.object.name if modifier.object else None} for modifier in obj.modifiers if modifier.type == "ARMATURE"]
            value["weights"] = identity({"groups": [group.name for group in obj.vertex_groups], "weights": [[[group.group, group.weight] for group in vertex.groups] for vertex in obj.data.vertices]})
        result.append(value)
    return {"id": identity(result), "kind": "mechanical-assembly", "nodes": result, "boneCount": sum(len(rig.data.bones) for rig in armatures())}


def runtime_structure(document, components):
    nodes = document.get("nodes", [])
    names = [node.get("name") for node in nodes]
    if len(names) != len(set(names)) or None in names:
        raise ValueError("Portable mechanical binding names are ambiguous")
    by_name = dict(zip(names, nodes))
    parents = {}
    for name, node in by_name.items():
        for child in node.get("children", []):
            if child < 0 or child >= len(nodes) or names[child] in parents:
                raise ValueError("Invalid portable mechanical hierarchy")
            parents[names[child]] = name
    result = {}
    for name, node in by_name.items():
        result[name] = {"parent": parents.get(name), "matrix": node_matrix(node), "mesh": "mesh" in node}
        if "skin" in node:
            skin = document["skins"][node["skin"]]
            result[name]["joints"] = [names[index] for index in skin["joints"]]
    return result


def compare_runtime(source, candidate, components, numerics):
    left, right = runtime_structure(source, components), runtime_structure(candidate, components)
    if set(left) != set(right):
        raise ValueError("Portable export changed mechanical hierarchy inventory")
    for name in left:
        if left[name]["parent"] != right[name]["parent"] or left[name]["mesh"] != right[name]["mesh"] or left[name].get("joints") != right[name].get("joints") or not transform_matches(left[name]["matrix"],right[name]["matrix"],numerics):
            raise ValueError("Portable export changed mechanical rest transforms, parents or skin ownership beyond approved precision")


def capture_export_frames(handoff):
    """Capture actual owned local geometry before the exporter world-bakes skins."""
    import bpy
    frames = {}
    for component in handoff["components"]:
        if component["kind"] != "flexible": continue
        obj = bpy.context.scene.objects[component["node"]]
        if len(obj.data.vertices) > 16_777_216 or obj.data.attributes.get("_BUBO_MOTION_VERTEX") is not None:
            raise ValueError("Unsupported or conflicting exact motion vertex correspondence")
        attribute = obj.data.attributes.new(name="_BUBO_MOTION_VERTEX",type="FLOAT",domain="POINT")
        for index,item in enumerate(attribute.data): item.value = index
        frames[obj.name] = {"local":obj.matrix_local.copy(),"world":obj.matrix_world.copy(),"vertices":[tuple(vertex.co) for vertex in obj.data.vertices]}
    return frames


def normalize_export_frames(raw, output, frames, recipe):
    """Normalize measured exporter frames using exact actual vertex correspondence."""
    from core import read_glb
    document = read_glb(raw,recipe["budgets"]["maxRuntimeBytes"])
    encoded = raw.read_bytes()
    json_bytes = struct.unpack_from("<I",encoded,12)[0]
    binary_bytes,kind = struct.unpack_from("<II",encoded,20+json_bytes)
    if kind != 0x004E4942: raise ValueError("Actual mechanical export binary required")
    data = bytearray(encoded[28+json_bytes:28+json_bytes+binary_bytes])
    def read(index,width):
        accessor = document["accessors"][index]; view = document["bufferViews"][accessor["bufferView"]]
        if accessor["componentType"] != 5126 or accessor["count"] > 3*recipe["budgets"]["maxTriangles"]:
            raise ValueError("Unsupported actual mechanical correspondence accessor")
        offset = view.get("byteOffset",0)+accessor.get("byteOffset",0);stride=view.get("byteStride",width*4)
        if offset<0 or stride<width*4 or offset+(accessor["count"]-1)*stride+width*4>len(data): raise ValueError("Actual mechanical accessor escaped binary")
        return [struct.unpack_from("<"+"f"*width,data,offset+index*stride) for index in range(accessor["count"])]
    def append(rows,kind):
        width={"VEC3":3,"MAT4":16}[kind]
        data.extend(b"\0"*((-len(data))%4));offset=len(data)
        for row in rows:
            if len(row)!=width or any(not math.isfinite(value) for value in row): raise ValueError("Nonfinite measured mechanical export")
            data.extend(struct.pack("<"+"f"*width,*row))
        view=len(document["bufferViews"]);document["bufferViews"].append({"buffer":0,"byteOffset":offset,"byteLength":len(rows)*width*4})
        value={"bufferView":view,"componentType":5126,"count":len(rows),"type":kind}
        if kind=="VEC3": value.update(min=[min(row[i] for row in rows) for i in range(3)],max=[max(row[i] for row in rows) for i in range(3)])
        index=len(document["accessors"]);document["accessors"].append(value);return index
    def inverse(matrix):
        rows=[[matrix[column*4+row] for column in range(4)]+[float(row==column) for column in range(4)] for row in range(4)]
        for column in range(4):
            pivot=max(range(column,4),key=lambda row:abs(rows[row][column]))
            if abs(rows[pivot][column])<1e-14: raise ValueError("Noninvertible actual mechanical frame")
            rows[column],rows[pivot]=rows[pivot],rows[column];divisor=rows[column][column];rows[column]=[value/divisor for value in rows[column]]
            for row in range(4):
                if row!=column:
                    factor=rows[row][column];rows[row]=[a-factor*b for a,b in zip(rows[row],rows[column])]
        return [rows[row][column+4] for column in range(4) for row in range(4)]
    def portable(matrix):
        # Blender Z-up to glTF Y-up, using actual measured transforms.
        conversion=[1,0,0,0,0,0,-1,0,0,1,0,0,0,0,0,1]
        values=[float(matrix[row][column]) for column in range(4) for row in range(4)]
        return product(product(conversion,values),inverse(conversion))
    nodes=document["nodes"];names=[node["name"] for node in nodes];parents={child:i for i,node in enumerate(nodes) for child in node.get("children",[])}
    for name,frame in frames.items():
        node=nodes[names.index(name)];matrix=frame["local"];location,rotation,scale=matrix.decompose();rotation.normalize()
        node.pop("matrix",None);node["translation"]=[float(location[0]),float(location[2]),-float(location[1])];node["rotation"]=[float(rotation.x),float(rotation.z),-float(rotation.y),float(rotation.w)];node["scale"]=[float(scale[0]),float(scale[2]),float(scale[1])]
    worlds={}
    def world(index):
        if index not in worlds: worlds[index]=product(world(parents[index]),node_matrix(nodes[index])) if index in parents else node_matrix(nodes[index])
        return worlds[index]
    for name,frame in frames.items():
        node=nodes[names.index(name)];skin=document["skins"][node["skin"]];mesh_world=portable(frame["world"])
        skin["inverseBindMatrices"]=append([product(inverse(world(joint)),mesh_world) for joint in skin["joints"]],"MAT4")
        for primitive in document["meshes"][node["mesh"]]["primitives"]:
            attributes=primitive["attributes"];logical=read(attributes.pop("_BUBO_MOTION_VERTEX"),1);positions=[]
            for value, in logical:
                if value!=int(value) or not 0<=value<len(frame["vertices"]): raise ValueError("Lost actual motion vertex identity")
                x,y,z=frame["vertices"][int(value)];positions.append((x,z,-y))
            attributes["POSITION"]=append(positions,"VEC3")
            if "NORMAL" in attributes:
                normal_matrix=[mesh_world[row*4+column] for column in range(4) for row in range(4)];normals=[]
                for normal in read(attributes["NORMAL"],3):
                    value=tuple(sum(normal_matrix[column*4+row]*normal[column] for column in range(3)) for row in range(3));length=math.sqrt(sum(v*v for v in value))
                    if length<=0: raise ValueError("Invalid actual mechanical normal")
                    normals.append(tuple(v/length for v in value))
                attributes["NORMAL"]=append(normals,"VEC3")
    document["buffers"][0]["byteLength"]=len(data)
    body=json.dumps(document,separators=(",",":"),allow_nan=False).encode("utf-8");body+=b" "*((-len(body))%4);data.extend(b"\0"*((-len(data))%4))
    size=28+len(body)+len(data)
    if len(body)>8*1024*1024 or size>recipe["budgets"]["maxRuntimeBytes"]: raise ValueError("Complete normalized mechanical export exceeds its byte budget")
    with open(output,"xb") as stream: stream.write(struct.pack("<III",0x46546C67,2,size)+struct.pack("<II",len(body),0x4E4F534A)+body+struct.pack("<II",len(data),0x004E4942)+data)


def verify_portable_geometry(root, candidate, handoff, recipe):
    """Compare local triangle corners and per-owner skin bindings independently of vertex order/splits."""
    from core import confined, read_glb
    numerics = approved_numerics(root, handoff)
    original = confined(root, handoff["model"]["path"])
    if digest(original) != handoff["model"]["sha256"]:
        raise ValueError("Mechanical source changed before portable verification")

    def measured(path):
        document = read_glb(path, recipe["budgets"]["maxRuntimeBytes"])
        nodes = document["nodes"]
        names = [node.get("name") for node in nodes]
        parents = {}
        for index,node in enumerate(nodes):
            for child in node.get("children",[]):
                if child in parents: raise ValueError("Mechanical portable node has multiple parents")
                parents[child] = index
        world = {}
        visiting = set()
        def world_matrix(index):
            if index not in world:
                if index in visiting: raise ValueError("Cyclic mechanical portable hierarchy")
                visiting.add(index)
                local = node_matrix(nodes[index])
                world[index] = product(world_matrix(parents[index]),local) if index in parents else local
                visiting.remove(index)
            return world[index]
        with open(path, "rb") as stream:
            stream.seek(12)
            json_bytes, kind = struct.unpack("<II", stream.read(8))
            stream.seek(20 + json_bytes)
            binary_bytes, kind = struct.unpack("<II", stream.read(8))
            if kind != 0x004E4942:
                raise ValueError("Mechanical GLB must embed its actual binary data")
            binary_start = 28 + json_bytes

            def accessor(index):
                spec = document["accessors"][index]
                sizes = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT4": 16}
                formats = {5121: ("B", 1, 255), 5123: ("H", 2, 65535), 5125: ("I", 4, 4294967295), 5126: ("f", 4, 1)}
                if "sparse" in spec or spec["componentType"] not in formats or spec["type"] not in sizes:
                    raise ValueError("Unsupported mechanical accessor representation")
                width = sizes[spec["type"]]
                code, unit, divisor = formats[spec["componentType"]]
                view = document["bufferViews"][spec["bufferView"]]
                stride = view.get("byteStride", width * unit)
                count = spec["count"]
                start = view.get("byteOffset", 0) + spec.get("byteOffset", 0)
                if view.get("buffer", 0) != 0 or count < 1 or count > 3 * recipe["budgets"]["maxTriangles"] or stride < width * unit or start < 0 or start + (count-1)*stride + width*unit > binary_bytes:
                    raise ValueError("Mechanical accessor escapes its binary/geometry budget")
                values = []
                for item in range(count):
                    stream.seek(binary_start + start + item * stride)
                    value = struct.unpack("<" + code * width, stream.read(unit * width))
                    if spec.get("normalized"):
                        value = tuple(number / divisor for number in value)
                    if any(not math.isfinite(number) for number in value):
                        raise ValueError("Nonfinite mechanical accessor")
                    values.append(value)
                return values

            result = {}
            triangles = 0
            for component in handoff["components"]:
                node_index = names.index(component["node"])
                node = nodes[node_index]
                joints = None
                bind = []
                if "skin" in node:
                    skin = document["skins"][node["skin"]]
                    joints = [names[index] for index in skin["joints"]]
                    matrices = accessor(skin["inverseBindMatrices"])
                    if len(joints) != len(matrices):
                        raise ValueError("Owned inverse bind inventory changed")
                    bind = sorted(zip(joints, matrices))
                surfaces = []
                for primitive in document["meshes"][node["mesh"]]["primitives"]:
                    if primitive.get("mode", 4) != 4:
                        raise ValueError("Mechanical portable representation requires triangles")
                    attributes = primitive["attributes"]
                    material = document.get("materials", [])[primitive["material"]] if "material" in primitive else {}
                    pbr = material.get("pbrMetallicRoughness", {})
                    material_identity = (material.get("name"), tuple(pbr.get("baseColorFactor", [1, 1, 1, 1])), pbr.get("metallicFactor", 1), pbr.get("roughnessFactor", 1))
                    positions = accessor(attributes["POSITION"])
                    influences = [[] for _ in positions]
                    if joints is not None:
                        for suffix in ("0", "1"):
                            if "JOINTS_" + suffix not in attributes:
                                continue
                            indices = accessor(attributes["JOINTS_" + suffix])
                            weights = accessor(attributes["WEIGHTS_" + suffix])
                            if len(indices) != len(positions) or len(weights) != len(positions):
                                raise ValueError("Owned weight accessor count changed")
                            for vertex, (ids, values) in enumerate(zip(indices, weights)):
                                for index, weight in zip(ids, values):
                                    if weight > 1e-8:
                                        if index >= len(joints):
                                            raise ValueError("Owned weight references another joint inventory")
                                        influences[vertex].append((joints[index], weight))
                        if any(not values or abs(sum(weight for _, weight in values)-1) > .001 for values in influences):
                            raise ValueError("Portable flexible mesh lost normalized skin bindings")
                    indices = [value[0] for value in accessor(primitive["indices"])] if "indices" in primitive else list(range(len(positions)))
                    if len(indices) % 3 or any(index < 0 or index >= len(positions) for index in indices):
                        raise ValueError("Invalid mechanical portable triangles")
                    for first in range(0, len(indices), 3):
                        corners = [(tuple(positions[index]), transformed(world_matrix(node_index), positions[index]), tuple(sorted(influences[index]))) for index in indices[first:first+3]]
                        surfaces.append((material_identity, tuple(corners)))
                    triangles += len(indices)//3
                    if triangles > recipe["budgets"]["maxTriangles"]:
                        raise ValueError("Portable triangle comparison exceeds its geometry budget")
                result[component["node"]] = {"surfaces": surfaces, "bind": bind}
            return result

    before,after = measured(original),measured(candidate)
    work = 0
    def charge():
        nonlocal work
        work += 1
        if work > numerics["maxChecks"]:
            raise ValueError("Portable mechanical comparison exceeds its approved geometry-check budget")
    for name,left in before.items():
        right = after[name]
        if len(left["surfaces"]) != len(right["surfaces"]) or len(left["bind"]) != len(right["bind"]):
            raise ValueError("Portable mechanical surface or binding count changed")
        binds_left,binds_right = dict(left["bind"]),dict(right["bind"])
        if set(binds_left) != set(binds_right) or any(not transform_matches(binds_left[joint],binds_right[joint],numerics) for joint in binds_left):
            raise ValueError("Portable mechanical inverse bind transforms exceed approved precision")
        def bucket(corners):
            return tuple(math.floor(sum(corner[0][axis] for corner in corners)/(3*numerics["linear"])) for axis in range(3))
        bins = {}
        for index,(_,corners) in enumerate(right["surfaces"]): bins.setdefault(bucket(corners),[]).append(index)
        def compatible(first,second):
            charge()
            material_a,corners_a = first
            material_b,corners_b = second
            if material_a != material_b: return False
            # Correspondence tests all cyclic starts; it never reverses winding.
            for shift in range(3):
                valid = True
                for index,(local_a,world_a,weights_a) in enumerate(corners_a):
                    charge()
                    local_b,world_b,weights_b = corners_b[(index+shift)%3]
                    if distance(local_a,local_b) > numerics["linear"] or distance(world_a,world_b) > numerics["linear"] or weights_a != weights_b:
                        valid = False; break
                    for joint,_ in weights_a:
                        charge()
                        if distance(transformed(binds_left[joint],local_a),transformed(binds_right[joint],local_b)) > numerics["linear"]:
                            valid = False; break
                    if not valid: break
                if valid: return True
            return False
        adjacency = {}
        def options(index):
            if index not in adjacency:
                first = left["surfaces"][index]
                center = bucket(first[1])
                candidates = [candidate_index for offset in combinations((-1,0,1),repeat=3) for candidate_index in bins.get(tuple(a+b for a,b in zip(center,offset)),[])]
                adjacency[index] = [candidate_index for candidate_index in candidates if compatible(first,right["surfaces"][candidate_index])]
            return adjacency[index]
        source_match,candidate_match = {},{}
        for first_index in range(len(left["surfaces"])):
            queue = deque([first_index]); seen_sources = {first_index}; predecessors = {}; free = None
            while queue and free is None:
                source_index = queue.popleft()
                for candidate_index in options(source_index):
                    charge()
                    if candidate_index in predecessors: continue
                    predecessors[candidate_index] = source_index
                    if candidate_index not in candidate_match:
                        free = candidate_index; break
                    previous_source = candidate_match[candidate_index]
                    if previous_source not in seen_sources:
                        seen_sources.add(previous_source); queue.append(previous_source)
            if free is None:
                raise ValueError("Portable mechanical oriented geometry, exact materials/weights or bind-space positions exceed approved precision")
            while True:
                source_index = predecessors[free]
                previous_candidate = source_match.get(source_index)
                source_match[source_index] = free; candidate_match[free] = source_index
                if previous_candidate is None: break
                free = previous_candidate
