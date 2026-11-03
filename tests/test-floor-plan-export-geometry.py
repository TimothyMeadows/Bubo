"""Independent standard-library fixtures: no Blender, compiler, or exporter."""

import copy
import importlib.util
import json
import math
from pathlib import Path
import struct
import tempfile
import unittest
from unittest import mock


ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("floor_plan_export_geometry", ROOT / "resources/floor-plan-to-model/blender/export_geometry.py")
GEOMETRY = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(GEOMETRY)

LIMITS = {"maxVertices": 10_000, "maxTriangles": 10_000, "maxNodes": 100, "maxMeshes": 100,
          "maxMaterials": 100, "maxAccessors": 100, "maxPrimitives": 100,
          "maxGeometryChecks": 1_000_000, "maxOutputBytes": 16*1024*1024}
META = ("id", "semanticId", "storeyId", "kind", "representation", "collisionRole", "materialId", "parentAnchorId")


def scene_fixture():
    # A closed slab ring. Altering its inner four sides changes a stairwell
    # aperture while leaving the complete outer bounding box unchanged.
    outer = [(-2,-2), (2,-2), (2,2), (-2,2)]
    inner = [(-.5,-.5), (.5,-.5), (.5,.5), (-.5,.5)]
    vertices = [[x,y,z] for z in (0.,.25) for x,y in outer+inner]
    triangles = []
    def quad(a,b,c,d):
        triangles.extend((a,b,c,a,c,d))
    for i in range(4):
        j = (i+1)%4
        quad(i+8,j+8,j+12,i+12)  # top rim
        quad(j,i,i+4,j+4)        # bottom rim
        quad(i,j,j+8,i+8)        # outside
        quad(j+4,i+4,i+12,j+12)  # aperture
    material = {"id":"stone", "name":"Stone", "color":"#808080", "roughness":.4,
                "metallic":.1, "transmission":.3, "ior":1.33}
    mesh = {"id":"slab:0", "semanticId":"slab", "storeyId":"ground", "kind":"slab",
            "representation":"visual", "collisionRole":"none", "materialId":"stone",
            "parentAnchorId":"pivot:door", "vertices":[dict(zip("xyz",v)) for v in vertices], "triangles":triangles}
    return {"schemaVersion":"bubo-floor-plan-construction-scene/v1", "planId":"test", "linearTolerance":1e-5,
            "materials":[material], "meshes":[mesh],
            "anchors":[{"id":"pivot:door", "semanticId":"door", "storeyId":"ground", "kind":"door-pivot",
                        "position":{"x":.25,"y":.75,"z":0.}, "axis":{"x":0,"y":0,"z":1}, "rotationRadians":0}]}


def measured_mesh(scene):
    source = scene["meshes"][0]
    return {key:copy.deepcopy(source[key]) for key in META} | {
        "vertices":[[v[k] for k in "xyz"] for v in source["vertices"]], "triangles":source["triangles"].copy()}


def identity():
    return [1.,0.,0.,0., 0.,1.,0.,0., 0.,0.,1.,0., 0.,0.,0.,1.]


def extras(identifier, kind, storey="", representation="", semantic="", collision="", material="", anchor=""):
    return {"bubo_id":identifier, "bubo_kind":kind, "bubo_semanticId":semantic, "bubo_storeyId":storey,
            "bubo_representation":representation, "bubo_collisionRole":collision,
            "bubo_materialId":material, "bubo_parent_anchor_id":anchor}


def glb_fixture(scene):
    """Emit a standalone specification fixture with deliberately split vertices."""
    source = scene["meshes"][0]
    doc = {"asset":{"version":"2.0", "generator":"independent test fixture"}, "scene":0,
           "scenes":[{"nodes":[0]}], "nodes":[
               {"extras":extras("building:test","building"), "children":[1]},
               {"extras":extras("storey:ground","storey","ground"), "children":[2,3]},
               {"extras":extras("representation:visual:ground","representation","ground","visual")},
               {"extras":extras("pivot:door","door-pivot","ground",semantic="door"),
                "translation":[.25,0.,-.75],"children":[4]},
               {"extras":extras("slab:0","slab","ground","visual","slab","none","stone","pivot:door"),"mesh":0}],
           "meshes":[{"primitives":[]}], "materials":[], "accessors":[], "bufferViews":[], "buffers":[],
           "extensionsUsed":["KHR_materials_transmission","KHR_materials_ior"]}
    material = scene["materials"][0]
    color = []
    for i in (1,3,5):
        v = int(material["color"][i:i+2],16)/255
        color.append(v/12.92 if v<=.04045 else ((v+.055)/1.055)**2.4)
    doc["materials"] = [{"extras":{"bubo_id":material["id"],"bubo_name":material["name"],"bubo_color":material["color"]},
                         "pbrMetallicRoughness":{"baseColorFactor":color+[1],"roughnessFactor":material["roughness"],"metallicFactor":material["metallic"]},
                         "extensions":{"KHR_materials_transmission":{"transmissionFactor":material["transmission"]},
                                       "KHR_materials_ior":{"ior":material["ior"]}}}]
    # One independent primitive containing a fresh vertex for every corner.
    anchor = (.25,.75,0.)
    positions = []
    for index in source["triangles"]:
        v = source["vertices"][index]
        local = [v[k]-anchor[i] for i,k in enumerate("xyz")]
        positions.append((local[0],local[2],-local[1]))
    binary = bytearray()
    def accessor(rows, component, width, target):
        while len(binary)%4:
            binary.append(0)
        start = len(binary)
        code = "f" if component==5126 else "I"
        binary.extend(b"".join(struct.pack("<"+code*width,*row) for row in rows))
        index = len(doc["accessors"])
        doc["bufferViews"].append({"buffer":0,"byteOffset":start,"byteLength":len(binary)-start,"target":target})
        raw_rows = [struct.unpack("<"+code*width,struct.pack("<"+code*width,*row)) for row in rows]
        doc["accessors"].append({"bufferView":index,"componentType":component,"count":len(rows),
                                 "type":"VEC3" if width==3 else "SCALAR",
                                 "min":[min(v[i] for v in raw_rows) for i in range(width)],
                                 "max":[max(v[i] for v in raw_rows) for i in range(width)]})
        return index
    position = accessor(positions,5126,3,34962)
    indices = accessor([(i,) for i in range(len(positions))],5125,1,34963)
    doc["meshes"][0]["primitives"] = [{"attributes":{"POSITION":position},"indices":indices,"material":0}]
    doc["buffers"] = [{"byteLength":len(binary)}]
    return doc,binary


def encode_glb(doc,binary):
    text = json.dumps(doc,allow_nan=False,separators=(",",":")).encode()
    text += b" "*((-len(text))%4)
    binary = bytes(binary)+b"\0"*((-len(binary))%4)
    return struct.pack("<III",0x46546c67,2,28+len(text)+len(binary))+struct.pack("<II",len(text),0x4e4f534a)+text+struct.pack("<II",len(binary),0x004e4942)+binary


def interleaved_normal_fixture(scene):
    doc,binary=glb_fixture(scene)
    count=doc["accessors"][0]["count"]
    positions=[struct.unpack_from("<fff",binary,i*12) for i in range(count)]
    result=bytearray()
    for first in range(0,count,3):
        a,b,c=positions[first:first+3]
        u=[b[i]-a[i] for i in range(3)]
        v=[c[i]-a[i] for i in range(3)]
        normal=[u[1]*v[2]-u[2]*v[1],u[2]*v[0]-u[0]*v[2],u[0]*v[1]-u[1]*v[0]]
        length=math.hypot(*normal)
        normal=[value/length for value in normal]
        for position in positions[first:first+3]:
            result.extend(struct.pack("<ffffff",*position,*normal))
    indices=bytes(binary[count*12:])
    doc["bufferViews"][0].update(byteLength=len(result),byteStride=24)
    doc["bufferViews"][1]["byteOffset"]=len(result)
    result.extend(indices)
    doc["buffers"][0]["byteLength"]=len(result)
    doc["accessors"].append({"bufferView":0,"byteOffset":12,"componentType":5126,"count":count,"type":"VEC3"})
    doc["meshes"][0]["primitives"][0]["attributes"]["NORMAL"]=2
    return doc,result


def unsigned_byte_boundary_fixture(merge_count=2):
    # Preserve the reviewer's closed-shell reproducer: 27 face subdivisions
    # yield 258 seam corners. Merging two/three equal corners gives exactly
    # 256/255 referenced portable vertices and boundary index 255/254.
    scene=scene_fixture()
    mesh=scene["meshes"][0]
    original=mesh["triangles"]
    mesh["triangles"]=[]
    for number,first in enumerate(range(0,len(original),3)):
        a,b,c=original[first:first+3]
        if number<27:
            center={k:sum(mesh["vertices"][i][k] for i in (a,b,c))/3 for k in "xyz"}
            d=len(mesh["vertices"])
            mesh["vertices"].append(center)
            mesh["triangles"].extend((a,b,d,b,c,d,c,a,d))
        else:
            mesh["triangles"].extend((a,b,c))
    doc,raw=glb_fixture(scene)
    positions,indices,seen,merges=[],[],{},0
    for first in range(0,len(mesh["triangles"])*12,12):
        point=struct.unpack_from("<fff",raw,first)
        if point in seen and merges<merge_count:
            indices.append(seen[point])
            merges+=1
        else:
            indices.append(len(positions))
            seen[point]=len(positions)
            positions.append(point)
    binary=bytearray(b"".join(struct.pack("<fff",*p) for p in positions))
    offset=len(binary)
    binary.extend(bytes(indices))
    doc["bufferViews"][0]["byteLength"]=offset
    doc["bufferViews"][1].update(byteOffset=offset,byteLength=len(indices))
    doc["accessors"][0].update(count=len(positions),min=[min(p[i] for p in positions) for i in range(3)],
                                max=[max(p[i] for p in positions) for i in range(3)])
    doc["accessors"][1].update(componentType=5121,count=len(indices),min=[0],max=[max(indices)])
    doc["buffers"][0]["byteLength"]=len(binary)
    return scene,doc,binary


class GeometryTests(unittest.TestCase):
    def setUp(self):
        self.scene=scene_fixture()
        self.limits=LIMITS.copy()

    def compare(self,row,scene=None,limits=None,angular=.001):
        target=scene or self.scene
        return GEOMETRY.compare_meshes(target,"visual",[row],limits or self.limits,target["linearTolerance"],angular)

    def inspect(self,doc=None,binary=None,raw=None,limits=None,angular=.001):
        if raw is None:
            if doc is None:
                doc,binary=glb_fixture(self.scene)
            raw=encode_glb(doc,binary)
        with tempfile.TemporaryDirectory(prefix="floor-plan-export-geometry-",dir=ROOT / "tests") as directory:
            path=Path(directory)/"fixture.glb"
            path.write_bytes(raw)
            return GEOMETRY.inspect_glb(path,self.scene,"visual",limits or self.limits,self.scene["linearTolerance"],angular)

    def test_independent_glb_seam_splits_and_source_axis_inverse(self):
        report=self.inspect()
        self.assertEqual(32,report["observedTriangleCount"])
        self.assertEqual(96,report["observedVertexCount"])
        self.assertEqual(5,len(report["nodes"]))
        self.assertLess(report["maxWorldCoordinateError"],1e-7)

    def test_native_explicit_null_material_overrides_use_default_material(self):
        # The native serializer emits the optional field explicitly as null.
        self.scene["meshes"][0]["triangleMaterialIds"] = None
        row = measured_mesh(self.scene)
        row["triangleMaterialIds"] = None
        self.assertEqual({"stone":32}, self.compare(row)["meshes"][0]["materialTriangleCounts"])
        self.assertEqual(["stone"], [m["id"] for m in self.inspect()["materials"]])

    def test_fixture_temporary_directory_is_explicitly_project_local(self):
        with mock.patch.object(tempfile,"TemporaryDirectory",wraps=tempfile.TemporaryDirectory) as temporary:
            self.inspect()
        self.assertEqual(ROOT / "tests",temporary.call_args.kwargs["dir"])

    def test_source_face_angle_rejects_spatially_admitted_row_with_unchanged_bounds(self):
        for vertex in self.scene["meshes"][0]["vertices"]:
            for key in "xyz":
                vertex[key]*=.01
        observed=copy.deepcopy(self.scene)
        observed["meshes"][0]["vertices"][4]["z"]+=7e-6
        row=measured_mesh(observed)
        before=measured_mesh(self.scene)["vertices"]
        for axis in range(3):
            self.assertEqual(min(v[axis] for v in before),min(v[axis] for v in row["vertices"]))
            self.assertEqual(max(v[axis] for v in before),max(v[axis] for v in row["vertices"]))
        with self.assertRaisesRegex(ValueError,"source-face angular tolerance"):
            self.compare(row,angular=.001)
        report=self.compare(row,angular=.1)
        self.assertLess(report["maxWorldCoordinateError"],self.scene["linearTolerance"])
        self.assertGreater(report["maxSourceFaceAngularErrorDegrees"],.07)
        self.assertAlmostEqual(report["maxSourceFaceAngularErrorDegrees"],report["meshes"][0]["maxSourceFaceAngularErrorDegrees"])

    def test_raw_source_face_angle_rejects_recomputed_flat_observed_normals(self):
        for vertex in self.scene["meshes"][0]["vertices"]:
            for key in "xyz":
                vertex[key]*=.01
        observed=copy.deepcopy(self.scene)
        observed["meshes"][0]["vertices"][4]["z"]+=7e-6
        doc,binary=interleaved_normal_fixture(observed)
        with self.assertRaisesRegex(ValueError,"source-face angular tolerance"):
            self.inspect(doc,binary,angular=.001)
        report=self.inspect(doc,binary,angular=.1)
        self.assertGreater(report["maxSourceFaceAngularErrorDegrees"],.07)

    def test_mixed_vertex_and_index_buffer_view_without_target_rejected(self):
        doc,binary=glb_fixture(self.scene)
        index_offset=doc["bufferViews"][1]["byteOffset"]
        doc["bufferViews"]=[{"buffer":0,"byteOffset":0,"byteLength":len(binary)}]
        doc["accessors"][1].update(bufferView=0,byteOffset=index_offset)
        with self.assertRaisesRegex(ValueError,"bufferView mixes"):
            self.inspect(doc,binary)

    def test_unsigned_byte_restart_boundary_rejects_255_and_allows_254(self):
        self.scene,doc,binary=unsigned_byte_boundary_fixture()
        self.assertEqual(256,doc["accessors"][0]["count"])
        self.assertEqual([255],doc["accessors"][1]["max"])
        with self.assertRaisesRegex(ValueError,"reserved unsigned restart-sentinel"):
            self.inspect(doc,binary)
        self.scene,doc,binary=unsigned_byte_boundary_fixture(merge_count=3)
        self.assertEqual([254],doc["accessors"][1]["max"])
        self.assertEqual(255,self.inspect(doc,binary)["observedVertexCount"])

    def test_unsigned_short_and_int_restart_sentinel_rejected_before_range(self):
        for component,code,sentinel in ((5123,"H",65535),(5125,"I",4294967295)):
            doc,binary=glb_fixture(self.scene)
            offset=doc["bufferViews"][1]["byteOffset"]
            values=[sentinel]+list(range(1,doc["accessors"][1]["count"]))
            encoded=b"".join(struct.pack("<"+code,index) for index in values)
            binary=binary[:offset]+encoded
            doc["bufferViews"][1]["byteLength"]=len(encoded)
            doc["accessors"][1].update(componentType=component,max=[sentinel],min=[1])
            doc["buffers"][0]["byteLength"]=len(binary)
            with self.assertRaisesRegex(ValueError,"reserved unsigned restart-sentinel"):
                self.inspect(doc,binary)

    def test_interleaved_positions_and_flat_normals_are_admitted(self):
        doc,binary=interleaved_normal_fixture(self.scene)
        self.assertEqual(32,self.inspect(doc,binary)["observedTriangleCount"])
        # A unit but inverted shading normal must not pass geometry matching.
        for axis in range(3):
            component=struct.unpack_from("<f",binary,12+axis*4)[0]
            struct.pack_into("<f",binary,12+axis*4,-component)
        with self.assertRaisesRegex(ValueError,"normal differs"):
            self.inspect(doc,binary)

    def test_nonindexed_triangle_primitive_is_admitted(self):
        doc,binary=glb_fixture(self.scene)
        del doc["meshes"][0]["primitives"][0]["indices"]
        doc["accessors"].pop()
        doc["bufferViews"].pop()
        binary=binary[:doc["bufferViews"][0]["byteLength"]]
        doc["buffers"][0]["byteLength"]=len(binary)
        self.assertEqual(32,self.inspect(doc,binary)["observedTriangleCount"])

    def test_material_primitives_share_position_accessor_without_duplicate_vertices(self):
        self.scene["materials"].append({"id":"red","name":"Red","color":"#FF0000","roughness":.2,
                                        "metallic":0,"transmission":0,"ior":1.5})
        self.scene["meshes"][0]["triangleMaterialIds"]=["stone"]*16+["red"]*16
        doc,binary=glb_fixture(self.scene)
        doc["materials"].append({"extras":{"bubo_id":"red","bubo_name":"Red","bubo_color":"#FF0000"},
                                 "pbrMetallicRoughness":{"baseColorFactor":[1,0,0,1],"roughnessFactor":.2,"metallicFactor":0}})
        doc["accessors"][1].update(count=48,max=[47])
        doc["accessors"].append({"bufferView":1,"byteOffset":48*4,"componentType":5125,"count":48,
                                 "type":"SCALAR","min":[48],"max":[95]})
        doc["meshes"][0]["primitives"].append({"attributes":{"POSITION":0},"indices":2,"material":1})
        result=self.inspect(doc,binary)
        self.assertEqual(96,result["observedVertexCount"])
        self.assertEqual({"stone":16,"red":16},result["meshes"][0]["materialTriangleCounts"])

    def test_cyclic_corner_and_triangle_order_allowed(self):
        row=measured_mesh(self.scene)
        triangles=[row["triangles"][i:i+3] for i in range(0,len(row["triangles"]),3)]
        row["triangles"]=[i for t in reversed(triangles) for i in t[1:]+t[:1]]
        self.assertEqual(32,self.compare(row)["observedTriangleCount"])

    def test_tolerance_uses_real_distances_across_grid_boundary(self):
        row=measured_mesh(self.scene)
        row["vertices"]=[[x+4e-6,y-3e-6,z] for x,y,z in row["vertices"]]
        report=self.compare(row)
        self.assertAlmostEqual(5e-6,report["maxWorldCoordinateError"],places=12)
        row["vertices"][0][0]+=1e-4
        with self.assertRaisesRegex(ValueError,"correspondence"):
            self.compare(row)

    def test_stairwell_and_window_aperture_mutations_keep_bounds(self):
        for axis in (0,1,2):
            row=measured_mesh(self.scene)
            before=([min(v[i] for v in row["vertices"]) for i in range(3)], [max(v[i] for v in row["vertices"]) for i in range(3)])
            # Inner aperture coordinates only; Z mutation is a notched reveal.
            row["vertices"][4][axis]+=.05
            after=([min(v[i] for v in row["vertices"]) for i in range(3)], [max(v[i] for v in row["vertices"]) for i in range(3)])
            self.assertEqual(before,after)
            with self.assertRaisesRegex(ValueError,"correspondence"):
                self.compare(row)

    def test_winding_and_material_and_triangle_count_rejected(self):
        for mutation in ("winding","material","multiplicity"):
            row=measured_mesh(self.scene)
            if mutation=="winding":
                row["triangles"][0],row["triangles"][1]=row["triangles"][1],row["triangles"][0]
            elif mutation=="material":
                row["triangleMaterialIds"]=["other"]*32
            else:
                row["triangles"]=row["triangles"][:-3]
            with self.assertRaises(ValueError):
                self.compare(row)

    def test_identical_triangle_multiplicity_is_counted(self):
        scene=copy.deepcopy(self.scene)
        scene["meshes"][0]["triangles"]*=2
        row=measured_mesh(scene)
        self.assertEqual(64,self.compare(row,scene)["observedTriangleCount"])
        row["triangles"][-3:]=row["triangles"][:3]
        with self.assertRaisesRegex(ValueError,"correspondence"):
            self.compare(row,scene)

    def test_ambiguity_rejected_instead_of_greedy_match(self):
        scene=copy.deepcopy(self.scene)
        mesh=scene["meshes"][0]
        original=mesh["vertices"][:3]
        mesh["vertices"]=[dict(v) for v in original]+[{k:v[k]+(2e-6 if k=="z" else 0) for k in "xyz"} for v in original]
        mesh["triangles"]=[0,1,2,3,4,5]
        row=measured_mesh(scene)
        with self.assertRaisesRegex(ValueError,"ambiguous"):
            self.compare(row,scene)

    def test_work_and_neighbor_caps_fail_closed(self):
        limits=self.limits|{"maxGeometryChecks":1}
        with self.assertRaisesRegex(ValueError,"work cap"):
            self.compare(measured_mesh(self.scene),limits=limits)
        scene=copy.deepcopy(self.scene)
        mesh=scene["meshes"][0]
        mesh["vertices"]=[]
        mesh["triangles"]=[]
        # Many distinct centroids in one cell, all nondegenerate.
        for i in range(257):
            first=len(mesh["vertices"])
            z=i*1e-9
            mesh["vertices"].extend([{"x":0,"y":0,"z":z},{"x":1,"y":0,"z":z},{"x":0,"y":1,"z":z}])
            mesh["triangles"].extend([first,first+1,first+2])
        with self.assertRaisesRegex(ValueError,"neighbor candidate cap"):
            self.compare(measured_mesh(scene),scene)

    def test_wrong_tolerance_and_nonfinite_vertices_fail(self):
        with self.assertRaisesRegex(ValueError,"differs"):
            GEOMETRY.compare_meshes(self.scene,"visual",[measured_mesh(self.scene)],self.limits,1e-3)
        row=measured_mesh(self.scene)
        row["vertices"][0][0]=float("nan")
        with self.assertRaisesRegex(ValueError,"finite"):
            self.compare(row)

    def test_semantic_ids_parent_roles_and_floor_fail(self):
        for key,value in (("id","forged"),("semanticId","other"),("storeyId","upstairs"),("parentAnchorId",None),
                          ("collisionRole","dynamic"),("representation","collision")):
            row=measured_mesh(self.scene)
            row[key]=value
            with self.assertRaises(ValueError):
                self.compare(row)
        for mutate in (lambda d:d["nodes"][4]["extras"].update(bubo_id="forged"),
                       lambda d:d["nodes"][4]["extras"].update(bubo_storeyId="upstairs"),
                       lambda d:d["nodes"][4]["extras"].update(bubo_collisionRole="dynamic"),
                       lambda d:d["nodes"][3].update(children=[]),
                       lambda d:d["nodes"][1].update(children=[2,3,4])):
            doc,binary=glb_fixture(self.scene)
            mutate(doc)
            with self.assertRaises(ValueError):
                self.inspect(doc,binary)

    def test_pivot_axis_scale_reflection_and_yup_transform_fail(self):
        for mutation in ({"translation":[.25,0.,.75]}, {"scale":[1,1,-1]}, {"scale":[1.01,1,1]},
                         {"rotation":[0,math.sin(.2),0,math.cos(.2)]}, {"rotation":[0,0,0,2]}):
            doc,binary=glb_fixture(self.scene)
            doc["nodes"][3].update(mutation)
            with self.assertRaises(ValueError):
                self.inspect(doc,binary)

    def test_all_material_scalar_and_color_mutations_fail(self):
        for key in ("baseColorFactor","roughnessFactor","metallicFactor","ior","transmissionFactor"):
            doc,binary=glb_fixture(self.scene)
            material=doc["materials"][0]
            if key=="baseColorFactor":
                material["pbrMetallicRoughness"][key][0]+=.01
            elif key in ("ior","transmissionFactor"):
                extension="KHR_materials_ior" if key=="ior" else "KHR_materials_transmission"
                material["extensions"][extension][key]+=.01
            else:
                material["pbrMetallicRoughness"][key]+=.01
            with self.assertRaisesRegex(ValueError,"material"):
                self.inspect(doc,binary)
        for extension in ("KHR_materials_ior","KHR_materials_transmission"):
            doc,binary=glb_fixture(self.scene)
            del doc["materials"][0]["extensions"][extension]
            doc["extensionsUsed"].remove(extension)
            with self.assertRaisesRegex(ValueError,"material"):
                self.inspect(doc,binary)

    def test_malformed_container_lengths_and_chunks_fail(self):
        doc,binary=glb_fixture(self.scene)
        raw=encode_glb(doc,binary)
        cases=[raw[:-1],raw+b"\0\0\0\0",raw[:8]+struct.pack("<I",len(raw)-4)+raw[12:],
               raw[:12]+struct.pack("<I",3)+raw[16:],raw[:16]+b"BAD!"+raw[20:]]
        for value in cases:
            with self.assertRaises(ValueError):
                self.inspect(raw=value)

    def test_accessor_range_stride_count_type_and_binary_nan_fail(self):
        mutations=[lambda d:d["bufferViews"][0].update(byteStride=8),
                   lambda d:d["bufferViews"][0].update(byteStride=13),
                   lambda d:d["accessors"][0].update(byteOffset=2),
                   lambda d:d["accessors"][0].update(count=10000),
                   lambda d:d["accessors"][0].update(componentType=5123),
                   lambda d:d["accessors"][1].update(type="VEC3"),
                   lambda d:d["accessors"][0].update(normalized=True),
                   lambda d:d["accessors"][0].update(sparse={}),
                   lambda d:d["accessors"][0].update(min=[0,0,0]),
                   lambda d:d["bufferViews"][0].update(byteLength=12),
                   lambda d:d["buffers"][0].update(byteLength=len(glb_fixture(self.scene)[1])+4)]
        for mutation in mutations:
            doc,binary=glb_fixture(self.scene)
            mutation(doc)
            with self.assertRaises(ValueError):
                self.inspect(doc,binary)
        doc,binary=glb_fixture(self.scene)
        binary[:4]=struct.pack("<f",float("nan"))
        with self.assertRaisesRegex(ValueError,"finite"):
            self.inspect(doc,binary)

    def test_external_uris_extensions_skin_animation_morph_and_hidden_nodes_fail(self):
        mutations=[lambda d:d["buffers"][0].update(uri="evil.bin"),
                   lambda d:d.update(extensionsUsed=["KHR_draco_mesh_compression"]),
                   lambda d:d.update(animations=[]),lambda d:d.update(skins=[]),
                   lambda d:d["nodes"][4].update(skin=0),
                   lambda d:d["meshes"][0]["primitives"][0].update(targets=[]),
                   lambda d:d["meshes"][0]["primitives"][0].update(extensions={"KHR_draco_mesh_compression":{}}),
                   lambda d:d["nodes"].append(copy.deepcopy(d["nodes"][4])),
                   lambda d:d["meshes"].append(copy.deepcopy(d["meshes"][0])),
                   lambda d:d["nodes"][4].update(children=[0])]
        for mutation in mutations:
            doc,binary=glb_fixture(self.scene)
            mutation(doc)
            with self.assertRaises(ValueError):
                self.inspect(doc,binary)

    def test_duplicate_parents_and_unreachable_graph_cycles_fail(self):
        for mutation in (lambda d:d["nodes"][1]["children"].append(3),
                         lambda d:d["nodes"][4].update(children=[0]),
                         lambda d:d["scenes"][0].update(nodes=[0,0]),
                         lambda d:d["nodes"][1].update(children=[2])):
            doc,binary=glb_fixture(self.scene)
            mutation(doc)
            with self.assertRaises(ValueError):
                self.inspect(doc,binary)

    def test_mathematical_matrix_and_trs_exclusivity_and_extras_fail(self):
        for mutation in (lambda d:d["nodes"][3].update(matrix=identity()),
                         lambda d:d["nodes"][1].update(matrix=identity()[:-1]+[2]),
                         lambda d:d["nodes"][4]["extras"].update(bubo_unknown="surprise"),
                         lambda d:d["materials"][0]["extras"].update(bubo_name="forged"),
                         lambda d:d["materials"][0].update(normalTexture={"index":0})):
            doc,binary=glb_fixture(self.scene)
            mutation(doc)
            with self.assertRaises(ValueError):
                self.inspect(doc,binary)

    def test_angular_tolerance_is_separate_from_spatial_tolerance(self):
        # Compare measured metadata directly to isolate transform admission
        # from the additional world-triangle test.
        nodes=self.inspect()["nodes"]
        pivot=next(n for n in nodes if n["id"]=="pivot:door")
        angle=math.radians(.05)
        c,s=math.cos(angle),math.sin(angle)
        pivot["worldMatrix"][0:3]=[c,-s,0]
        pivot["worldMatrix"][4:7]=[s,c,0]
        GEOMETRY.compare_nodes(self.scene,"visual",nodes,1e-5,.1)
        with self.assertRaisesRegex(ValueError,"orientation"):
            GEOMETRY.compare_nodes(self.scene,"visual",nodes,1e-5,.01)

    def test_raw_aperture_mutation_preserves_bounds_but_fails(self):
        doc,binary=glb_fixture(self.scene)
        # The first top quad has an interior corner at vertex two. Preserve
        # its enclosing accessor min/max while moving only the aperture.
        offset=2*12
        x=struct.unpack_from("<f",binary,offset)[0]
        struct.pack_into("<f",binary,offset,x+.125)
        with self.assertRaisesRegex(ValueError,"correspondence"):
            self.inspect(doc,binary)

    def test_duplicate_and_nonfinite_json_fail(self):
        doc,binary=glb_fixture(self.scene)
        raw=encode_glb(doc,binary)
        for old,new in ((b'"scene":0',b'"scene":0,"scene":0'),(b'"version":"2.0"',b'"version":1e999')):
            text=json.dumps(doc,separators=(",",":")).encode().replace(old,new)
            text+=b" "*((-len(text))%4)
            value=struct.pack("<III",0x46546c67,2,28+len(text)+len(binary))+struct.pack("<II",len(text),0x4e4f534a)+text+struct.pack("<II",len(binary),0x004e4942)+binary
            with self.assertRaises(ValueError):
                self.inspect(raw=value)

    def test_cumulative_decode_and_primitive_caps_fail_before_correspondence(self):
        doc,binary=glb_fixture(self.scene)
        limits=self.limits|{"maxVertices":40}
        with self.assertRaisesRegex(ValueError,"count|cap"):
            self.inspect(doc,binary,limits=limits)
        doc["meshes"][0]["primitives"]*=2
        with self.assertRaisesRegex(ValueError,"primitives|primitive cap"):
            self.inspect(doc,binary,limits=self.limits|{"maxPrimitives":1})


if __name__=="__main__":
    unittest.main()
