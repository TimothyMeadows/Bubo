"""Offline negative admission QA; no Blender or production acceptance claim."""
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import struct
import sys
import tempfile
from types import SimpleNamespace
import unittest

sys.dont_write_bytecode = True
ROOT=Path(__file__).resolve().parents[1]
BUNDLE=ROOT/"resources/schematic-to-model/blender"
sys.path.insert(0,str(BUNDLE))
from assembly_admission import BUNDLE as FILES, admit, digest, read_geometry, validate_assembly_design
from assembly_geometry import receipt, report_bytes, write_report
from parts_geometry import geometry_bytes, report_bytes as parts_report_bytes, stable_json


def load_fixture():
    spec=importlib.util.spec_from_file_location("assembly_blender_fixture",ROOT/"tests/test-schematic-assembly-blender.py")
    module=importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module.fixture()


class AssemblyAdmissionTests(unittest.TestCase):
    def setUp(self):
        root=ROOT/".bubo/schematic-assembly-admission-tests"
        root.mkdir(parents=True,exist_ok=True)
        self.directory=Path(tempfile.mkdtemp(prefix="case-",dir=root))
        self.parts=self.directory/"parts"
        self.output=self.directory/"assembly"
        self.parts.mkdir()
        self.output.mkdir()
        self.path=self.directory/"job.json"
        design,plan,rigs=load_fixture()
        vertices=[(0.,0.,0.),(1.,0.,0.),(0.,1.,0.),(0.,0.,1.)]
        triangles=[(0,2,1),(0,1,3),(0,3,2),(1,2,3)]
        prototypes=[]
        for p in design["parts"]:
            blend=self.parts/(p["id"]+".blend")
            blend.write_bytes(b"synthetic-blend-admission-only")
            geometry=self.parts/(p["id"]+".geometry.bin")
            geometry.write_bytes(geometry_bytes(vertices,triangles))
            prototypes.append({"id":p["id"],"definitionSha256":"a"*64,"blendFile":blend.name,"blendSha256":digest(blend),"geometryFile":geometry.name,"geometrySha256":digest(geometry)})
        parts={"schemaVersion":"bubo-schematic-parts/v1","jobSha256":"b"*64,"parts":prototypes}
        for name in ("parts-report.json","parts-verification.json"):
            (self.parts/name).write_text(json.dumps(parts),encoding="utf8")
        self.job={"schemaVersion":"bubo-schematic-assembly-job/v1","operation":"assemble","projectRoot":str(self.directory),"output":str(self.output),"design":design,"partsDirectory":str(self.parts),"partsReportSha256":digest(self.parts/"parts-report.json"),"partsVerificationSha256":digest(self.parts/"parts-verification.json"),"parts":parts,"identity":{k:"a"*64 for k in ("designSha256","designFingerprint","runtimeBuildSha256","toolSha256")},"bundle":{f:digest(BUNDLE/f) for f in FILES},"limits":{"maxVertices":100000,"maxTriangles":100000,"maxOutputBytes":64000000,"maxJobBytes":8388608,"maxFiles":256,"maxMotionSamples":64,"maxWorkChecks":1000000},"plan":plan,"rigs":rigs,"sourceAssembly":None,"sourceAssemblySha256":None}
        self.job["identity"].update(partsJobSha256="b"*64,partsReportSha256=self.job["partsReportSha256"],partsVerificationSha256=self.job["partsVerificationSha256"])

    def invoke(self,job=None):
        self.path.write_text(json.dumps(self.job if job is None else job),encoding="utf8")
        return admit(self.path,digest(self.path))

    def test_closed_valid_synthetic_admission(self):
        self.assertEqual(self.invoke()["operation"],"assemble")

    def test_assembly_file_budget_does_not_inherit_prototype_output_quota(self):
        design=json.loads((ROOT/"resources/schematic-to-model/examples-v2/device.json").read_bytes())
        limits=copy.deepcopy(self.job["limits"])
        limits.update(maxFiles=3+len(design["instances"]),maxWorkChecks=50000000)
        self.assertGreater(2*len(design["parts"])+2,limits["maxFiles"])
        self.assertEqual(validate_assembly_design(design,limits),{p["id"] for p in design["parts"]})
        job=copy.deepcopy(self.job)
        job["limits"]["maxFiles"]=3+len(job["design"]["instances"])
        self.assertEqual(self.invoke(job)["operation"],"assemble")
        job["limits"]["maxFiles"]-=1
        with self.assertRaises(ValueError): self.invoke(job)

    def test_closed_v2_profile_preserves_full_design(self):
        job=copy.deepcopy(self.job)
        job["design"].update(schemaVersion="bubo-assembly-design/v2",assemblyProfile={"schemaVersion":"bubo-assembly-profile/v1","samplesPerRange":3,"samplesPerCycle":9,"maxGeometryChecks":1000000,"minimumSeparation":0,"contacts":[],"flexibleRigs":[],"couplingFits":[]})
        self.assertEqual(self.invoke(job)["design"],job["design"])
        job["design"]["assemblyProfile"]["callback"]="run"
        with self.assertRaises(ValueError): self.invoke(job)

    def test_closed_job_and_rig_fields(self):
        for mutation in (lambda j:j.update(callback="run"),lambda j:j["rigs"][0].update(script="run")):
            job=copy.deepcopy(self.job)
            mutation(job)
            with self.assertRaises(ValueError): self.invoke(job)

    def test_unknown_or_nonrigid_pose_rejected(self):
        for mutation in (lambda j:j["plan"]["rest"]["instances"].update(unknown=j["plan"]["rest"]["instances"]["fixed-occurrence"]),lambda j:j["plan"]["rest"]["instances"]["fixed-occurrence"]["x"].update(x=2),lambda j:j["rigs"][0]["restRingFrames"][0]["z"].update(z=-1)):
            job=copy.deepcopy(self.job)
            mutation(job)
            with self.assertRaises(ValueError): self.invoke(job)

    def test_exact_ring_and_clip_inventory(self):
        for mutation in (lambda j:j["rigs"].pop(),lambda j:j["rigs"][0]["restRingFrames"].pop(),lambda j:j["rigs"][0]["clips"][0]["samples"].pop(),lambda j:j["rigs"][0]["clips"][0]["samples"][1].update(sampleIndex=0),lambda j:j["rigs"][0].update(radialSegments=9),lambda j:j["design"]["parts"][1]["flexible"].update(skinSegments=3)):
            job=copy.deepcopy(self.job)
            mutation(job)
            with self.assertRaises(ValueError): self.invoke(job)

    def test_motion_sampling_time_and_budget(self):
        for mutation in (lambda j:j["plan"]["clips"][0]["samples"][1].update(timeSeconds=0),lambda j:j["plan"]["sampling"].update(totalSamples=5),lambda j:j["limits"].update(maxMotionSamples=2),lambda j:j["limits"].update(maxWorkChecks=1),lambda j:j["limits"].update(maxJobBytes=32)):
            job=copy.deepcopy(self.job)
            mutation(job)
            with self.assertRaises(ValueError): self.invoke(job)

    def test_native_range_cycle_and_maximum_generated_identity(self):
        job=copy.deepcopy(self.job)
        joint_id="joint-"+"j"*74
        self.assertEqual(len(joint_id),80)
        job["design"]["joints"][0].update(id=joint_id,kind="rotary",maximum=360)
        for pose in [job["plan"]["rest"]]+[s["pose"] for s in job["plan"]["clips"][0]["samples"]]:
            pose["jointValues"]={joint_id:pose["jointValues"]["driver"]}
            pose["closures"][0]["jointId"]=joint_id
        original=job["plan"]["clips"][0]
        original.update(id="range-"+joint_id,driverJointId=joint_id)
        cycle=copy.deepcopy(original)
        cycle.update(id="cycle-"+joint_id,completeCycle=True)
        job["plan"]["clips"].append(cycle)
        job["plan"]["sampling"]["totalSamples"]=7
        job["plan"]["sampling"]["drivers"][0]["driverJointId"]=joint_id
        for rig in job["rigs"]:
            rig["clips"][0]["clipId"]=original["id"]
            addition=copy.deepcopy(rig["clips"][0])
            addition["clipId"]=cycle["id"]
            rig["clips"].append(addition)
        result=self.invoke(job)
        self.assertEqual(len(result["plan"]["clips"]),2)
        self.assertTrue(all(len(c["id"])==86 for c in result["plan"]["clips"]))
        job["plan"]["clips"][0]["id"]+="x"
        with self.assertRaises(ValueError): self.invoke(job)

    def test_exact_bundle_hash_and_job_hash(self):
        job=copy.deepcopy(self.job)
        job["bundle"]["assembly_author.py"]="f"*64
        with self.assertRaises(ValueError): self.invoke(job)
        self.path.write_text(json.dumps(self.job),encoding="utf8")
        with self.assertRaises(ValueError): admit(self.path,"f"*64)

    def test_duplicate_json_and_nonfinite(self):
        self.path.write_text('{"operation":"assemble","operation":"prove-motion"}',encoding="utf8")
        with self.assertRaises(ValueError): admit(self.path,digest(self.path))
        job=copy.deepcopy(self.job)
        job["plan"]["rest"]["instances"]["fixed-occurrence"]["origin"]["x"]=float("nan")
        with self.assertRaises(ValueError): self.invoke(job)

    def test_payload_ancestry_and_source_mutation(self):
        prototype=self.parts/"rigid.blend"
        prototype.write_bytes(prototype.read_bytes()+b"tamper")
        with self.assertRaises(ValueError): self.invoke()
        prototype.write_bytes(b"synthetic-blend-admission-only")
        source=self.directory/"assembly.blend"
        source.write_bytes(b"synthetic-source-admission-only")
        job=copy.deepcopy(self.job)
        job.update(operation="prove-motion",sourceAssembly=str(source),sourceAssemblySha256=digest(source))
        self.assertEqual(self.invoke(job)["operation"],"prove-motion")
        source.write_bytes(source.read_bytes()+b"tamper")
        with self.assertRaises(ValueError): self.invoke(job)

    def test_output_paths_and_existing_outputs(self):
        for output in (str(self.directory.parent/"outside"),str(self.parts),str(self.output/"../assembly")):
            job=copy.deepcopy(self.job)
            job["output"]=output
            with self.assertRaises(ValueError): self.invoke(job)
        (self.output/"old.bin").write_bytes(b"owned-history")
        with self.assertRaises(ValueError): self.invoke()

    def test_oversized_or_truncated_binary_rejected_before_allocation(self):
        file=self.directory/"bad.bin"
        for raw in (b"BUBOPRT1",struct.pack("<8sII",b"BUBOPRT1",2000000000,4),struct.pack("<8sII",b"BUBOPRT1",4,4)+b"truncated"):
            file.write_bytes(raw)
            with self.assertRaises(ValueError): read_geometry(file,self.job["limits"])

    def test_compact_receipts_preserve_all_full_ring_samples_and_exact_two_report_budget(self):
        # Synthetic serializer boundary: the original 32-ring/8-radial, 131-pose
        # inventory remains intact. This does not certify geometry or native QA.
        output=self.directory/"compact-receipts"
        output.mkdir()
        (output/"diagnostic.blend").write_bytes(b"synthetic-serialization-only")
        pose={key:{axis:(ordinal*3+index+1)/13 for index,axis in enumerate("xyz")} for ordinal,key in enumerate(("origin","x","y","z"))}
        bones=[{"name":"root","parentName":None,"restPose":pose,"pose":pose,"length":.01234567890123456}]
        bones.extend({"name":f"bubo.ring.{i:06d}","parentName":"root","restPose":pose,"pose":pose,"length":.01234567890123456} for i in range(32))
        rig={"armatureName":"bubo.rig.000000","armatureLocalPose":pose,"armatureWorldPose":pose,"bones":bones,
             "ringVertexIndices":[list(range(i*8,(i+1)*8)) for i in range(32)],"weightsSha256":"a"*64,
             "ringCenters":[dict(x=i/13,y=i/17,z=i/19) for i in range(32)],
             "segmentLengths":[.01234567890123456]*32,"bendAnglesDegrees":[11.234567890123456]*32}
        def instance(prefix):
            geometry=prefix+".000000.geometry.bin"
            (output/geometry).write_bytes(b"synthetic-binary-serialization-only")
            return {"instanceId":"belt-instance","partId":"belt-definition","objectName":"bubo.instance.000000","parentId":"assembly",
                    "localPose":pose,"worldPose":pose,"materialId":"material","materialColorLinear":dict(x=.1,y=.2,z=.3),
                    "materialRoughness":.5,"materialMetallic":.1,"ports":[],"prototypeBlendSha256":"b"*64,
                    "prototypeGeometrySha256":"c"*64,"geometryFile":geometry,"geometrySha256":"d"*64,
                    "vertexCount":256,"triangleCount":512,"rig":rig}
        instances=[instance("rest")]
        clips=[{"id":f"cycle-{clip}","actions":[{"ownerId":"belt-instance","ownerName":"bubo.rig.000000","actionName":f"bubo.action.{clip:06d}.000000","fcurveCount":231,"keyframeCount":15015}],
                "samples":[{"index":sample,"timeSeconds":sample/64,"instances":[instance(f"clip.{clip:06d}.sample.{sample:06d}")]} for sample in range(65)]} for clip in range(2)]
        job=copy.deepcopy(self.job)
        job.update(operation="prove-motion",output=str(output))
        job["limits"].update(maxFiles=512,maxOutputBytes=64*1024*1024)
        bpy=SimpleNamespace(app=SimpleNamespace(version_string="4.5.3 LTS"))
        value=receipt(job,"e"*64,bpy,instances,clips)
        compact=report_bytes(value)
        self.assertEqual(compact,(stable_json(value)+"\n").encode("utf8"))
        self.assertEqual(json.loads(compact),value)
        self.assertLess(len(compact),8388608)
        self.assertGreater(len(parts_report_bytes(value)),8388608)
        self.assertEqual(value["totalVertices"],131*256)
        self.assertEqual(sum(len(c["samples"]) for c in value["clips"]),130)
        job["limits"]["maxOutputBytes"]=value["totalBytes"]+2*len(compact)
        self.assertEqual(receipt(job,"e"*64,bpy,instances,clips),value)
        job["limits"]["maxOutputBytes"]-=1
        with self.assertRaisesRegex(ValueError,"Cumulative two-report byte budget"): receipt(job,"e"*64,bpy,instances,clips)
        job["limits"]["maxOutputBytes"]=64*1024*1024
        with self.assertRaisesRegex(ValueError,"Cumulative two-report byte budget"): receipt(job,"e"*64,bpy,instances,clips*4)
        author=output/"motion-report.json"
        reopened=output/"motion-verification.json"
        write_report(author,value)
        write_report(reopened,value)
        self.assertEqual(author.read_bytes(),compact)
        self.assertEqual(reopened.read_bytes(),compact)
        with self.assertRaises(FileExistsError): write_report(author,value)
        self.assertTrue(parts_report_bytes({"unchanged":"parts"}).startswith(b"{\n"))


if __name__=="__main__": unittest.main()
