#!/usr/bin/env python3
"""Opt-in actual Blender engineering QA, with synthetic design identities.

python tests/test-schematic-assembly-blender.py --blender ABSOLUTE_EXISTING_BLENDER
Passing supplies no human model, motion, visual or runtime acceptance.
"""
import argparse
import copy
import hashlib
import json
import math
from pathlib import Path
import shutil
import struct
import subprocess
import sys
import uuid

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
BUNDLE = ROOT / "resources/schematic-to-model/blender"
sys.path.insert(0,str(BUNDLE))
from assembly_admission import BUNDLE as ASSEMBLY_FILES, digest
from parts_admission import BUNDLE as PART_FILES


def xyz(x=0,y=0,z=0):
    return dict(x=x,y=y,z=z)


def frame(x=0,y=0,z=0):
    return {"origin":xyz(x,y,z),"rotationDegrees":xyz()}


def rigid(x=0,y=0,z=0,angle=0):
    a=math.radians(angle)
    return {"origin":xyz(x,y,z),"x":xyz(math.cos(a),math.sin(a),0),"y":xyz(-math.sin(a),math.cos(a),0),"z":xyz(0,0,1)}


def ring_frames(points,closed):
    def unit(v):
        length=math.sqrt(sum(c*c for c in v))
        return tuple(c/length for c in v)
    def cross(a,b):
        return (a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0])
    points=[tuple(p[k] for k in "xyz") for p in points]
    points=points[:-1] if closed else points
    directions=[unit(tuple(points[(i+1)%len(points)][k]-p[k] for k in range(3))) for i,p in enumerate(points[:len(points) if closed else -1])]
    result=[]
    for index,point in enumerate(points):
        if not closed and index==0:
            tangent=directions[0]
        elif not closed and index==len(points)-1:
            tangent=directions[-1]
        else:
            tangent=unit(tuple(a+b for a,b in zip(directions[index-1],directions[index%len(directions)])))
        seed=min(((1,0,0),(0,1,0),(0,0,1)),key=lambda a:abs(sum(x*y for x,y in zip(a,tangent))))
        x=unit(cross(tangent,seed))
        z=cross(x,tangent)
        result.append({"origin":dict(zip("xyz",point)),"x":dict(zip("xyz",x)),"y":dict(zip("xyz",tangent)),"z":dict(zip("xyz",z))})
    return result


def fixture():
    design=json.loads((ROOT/"resources/schematic-to-model/examples/device.json").read_bytes())
    template=copy.deepcopy(design["parts"][0])
    for key in ("parts","instances","joints","mechanisms","routes","connections","sections"):
        design[key]=[]
    design.update(id="actual-assembly-engineering",purpose="Synthetic engineering QA, no production acceptance")
    design["subassemblies"]=[{"id":"assembly","name":"assembly","parentId":None,"frame":frame()}]
    feature={"id":"solid","kind":"box","operation":"add","frame":frame(),"parameters":{"width":.1,"depth":.1,"height":.1},"profile":[],"path":[],"sections":[]}
    part=copy.deepcopy(template)
    part.update(id="rigid",kind="rigid",size=xyz(.1,.1,.1),features=[feature],interfaces=[{"id":"pivot","name":"pivot","frame":frame(.01,.02,.03)}],terminals=[],flexible=None)
    design["parts"].append(part)
    for key,x in (("fixed-occurrence",0),("moving-occurrence",1)):
        design["instances"].append({"id":key,"partId":"rigid","parentId":"assembly","frame":frame(x),"explodeOffset":xyz()})
    design["joints"]=[{"id":"driver","kind":"hinge","parentInstanceId":"fixed-occurrence","childInstanceId":"moving-occurrence","parentInterfaceId":"pivot","childInterfaceId":"pivot","axis":xyz(0,0,1),"minimum":0,"maximum":90,"rest":0,"pitch":0,"clearance":0}]
    rigs=[]
    for ordinal,kind in enumerate(("wire","hose","spring","belt")):
        closed=kind=="belt"
        if kind=="spring":
            path=[xyz(.3+.2*math.cos(i*math.pi/8),.3+.2*math.sin(i*math.pi/8),.04+.035*i) for i in range(25)]
        elif closed:
            path=[xyz(.3+.2*math.cos(i*math.pi/8),.3+.2*math.sin(i*math.pi/8),.04) for i in range(16)]
            path.append(copy.deepcopy(path[0]))
        else:
            path=[xyz(.1,.1,.04),xyz(.3,.12,.04),xyz(.5,.1,.04)]
        feature={"id":"sweep","kind":"sweep","operation":"add","frame":frame(),"parameters":{"radius":.008,"segments":8},"profile":[],"path":path,"sections":[]}
        length=sum(math.dist(tuple(a.values()),tuple(b.values())) for a,b in zip(path,path[1:]))
        part=copy.deepcopy(template)
        part.update(id=kind,kind=kind,size=xyz(.6,.6,1),features=[feature],interfaces=[],terminals=[],flexible={"restLength":length,"minimumLength":length*.8,"maximumLength":length*1.3,"minimumBendRadius":.01,"maximumStretchRatio":1.3,"skinSegments":len(path)-1})
        design["parts"].append(part)
        instance=kind+"-instance"
        design["instances"].append({"id":instance,"partId":kind,"parentId":"assembly","frame":frame(ordinal*2,2),"explodeOffset":xyz()})
        design["routes"].append({"id":kind+"-route","instanceId":instance,"points":copy.deepcopy(path),"closed":closed,"from":None,"to":None})
        rest=ring_frames(path,closed)
        samples=[]
        for sample_index,t in enumerate((0,.5,1)):
            frames=copy.deepcopy(rest)
            for index,f in enumerate(frames):
                f["origin"]["z"]+=.04*t*math.sin(math.pi*index/(len(frames)-1))
                if closed:
                    f["origin"]["x"]+=.02*t*(f["origin"]["x"]-.3)
            samples.append({"sampleIndex":sample_index,"ringFrames":frames})
        rigs.append({"instanceId":instance,"closedPath":closed,"radialSegments":8,"restRingFrames":rest,"boneLength":.005,"clips":[{"clipId":"travel-driver","samples":samples}]})
    instances={i["id"]:rigid(**i["frame"]["origin"]) for i in design["instances"]}
    def sample(t):
        poses=copy.deepcopy(instances)
        poses["moving-occurrence"]=rigid(1,angle=90*t)
        return {"jointValues":{"driver":90*t},"instances":poses,"closures":[{"jointId":"driver","linearError":0,"angularErrorDegrees":0}]}
    plan={"schemaVersion":"bubo-schematic-diagnostic-plan/v1","rest":sample(0),"clips":[{"id":"travel-driver","driverJointId":"driver","mechanismId":None,"completeCycle":False,"samples":[{"timeSeconds":t,"pose":sample(t)} for t in (0,.5,1)]}],"sampling":{"samplesPerRange":3,"samplesPerCycle":3,"totalSamples":4,"drivers":[{"driverJointId":"driver","cycleApplicable":False,"completeCycleSampled":False,"cycleStatus":"not-applicable"}]},"workChecks":100}
    return design,plan,rigs


def launch(blender,worker,job_path,job_sha,directory,success=True,error=None):
    command=[str(blender),"--background","--factory-startup","--disable-autoexec","--offline-mode","--threads","1","--python-exit-code","41","--python",str(BUNDLE/worker),"--",str(job_path),job_sha]
    run=subprocess.run(command,capture_output=True,timeout=180)
    (directory/(worker+".log")).write_bytes(run.stdout+b"\nSTDERR\n"+run.stderr)
    if success:
        assert run.returncode==0,(worker,run.returncode,(run.stdout+run.stderr)[-14000:].decode("utf8",errors="replace"))
    else:
        assert run.returncode!=0,"Expected fixed-worker rejection"
        if error is not None:
            assert error.encode() in run.stdout+run.stderr,(worker,error,(run.stdout+run.stderr)[-10000:].decode("utf8",errors="replace"))


def write_job(path,job):
    path.write_text(json.dumps(job,indent=2)+"\n",encoding="utf8")
    return digest(path)


def read_binary(path):
    raw=path.read_bytes()
    magic,nv,nt=struct.unpack_from("<8sII",raw)
    assert magic==b"BUBOPRT1" and len(raw)==16+nv*24+nt*12
    return [struct.unpack_from("<ddd",raw,16+i*24) for i in range(nv)]


def native_shaped_rotary(blender,directory,assembly_job):
    """One approved-width joint produces native-shaped range and cycle IDs.

    The parent lane separately exercises the actual native compiler/CLI. This
    worker engineering fixture transports its exact two-clip DTO shape.
    """
    job=copy.deepcopy(assembly_job)
    output=directory/"rotary-assembly"
    output.mkdir()
    design=job["design"]
    design["instances"]=design["instances"][:2]
    design["routes"]=[]
    design["assemblyProfile"]["flexibleRigs"]=[]
    joint_id="joint-"+"j"*74
    assert len(joint_id)==80
    design["joints"][0].update(id=joint_id,kind="rotary",maximum=360)
    def pose(t):
        return {"jointValues":{joint_id:360*t},"instances":{"fixed-occurrence":rigid(),"moving-occurrence":rigid(1,angle=360*t)},"closures":[{"jointId":joint_id,"linearError":0,"angularErrorDegrees":0}]}
    clips=[{"id":prefix+joint_id,"driverJointId":joint_id,"mechanismId":None,"completeCycle":cycle,"samples":[{"timeSeconds":i/8,"pose":pose(i/8)} for i in range(9)]} for prefix,cycle in (("range-",False),("cycle-",True))]
    plan={"schemaVersion":"bubo-schematic-diagnostic-plan/v1","rest":pose(0),"clips":clips,"sampling":{"samplesPerRange":9,"samplesPerCycle":9,"totalSamples":19,"drivers":[{"driverJointId":joint_id,"cycleApplicable":True,"completeCycleSampled":True,"cycleStatus":"complete-mechanism-cycle"}]},"workChecks":100}
    job.update(operation="assemble",output=str(output),rigs=[],plan=plan,sourceAssembly=None,sourceAssemblySha256=None)
    path=directory/"rotary-assembly-job.json"
    sha=write_job(path,job)
    for worker in ("assembly_author.py","assembly_verify.py"):
        launch(blender,worker,path,sha,directory)
    motion=directory/"rotary-motion"
    motion.mkdir()
    job.update(operation="prove-motion",output=str(motion),sourceAssembly=str(output/"assembly.blend"),sourceAssemblySha256=digest(output/"assembly.blend"))
    path=directory/"rotary-motion-job.json"
    sha=write_job(path,job)
    for worker in ("assembly_motion.py","assembly_verify.py"):
        launch(blender,worker,path,sha,directory)
    measured=json.loads((motion/"motion-report.json").read_bytes())
    assert [c["id"] for c in measured["clips"]]==["range-"+joint_id,"cycle-"+joint_id]
    assert all(len(c["id"])==86 and len(c["samples"])==9 for c in measured["clips"])
    assert all(len(i["objectName"])<63 for i in measured["instances"])


def saved_source_semantic_tamper(blender,directory,assembly_job,accepted_output):
    """Refresh job/source/report identities so semantic guards cause failure."""
    helper=directory/"synthetic-source-mutator.py"
    helper.write_text('''import bpy,json,sys
from pathlib import Path
args=sys.argv[sys.argv.index("--")+1:]
source,case=args
bpy.ops.wm.open_mainfile(filepath=source,load_ui=False,use_scripts=False)
physical=bpy.data.objects["bubo.instance.000002"]
arm=bpy.data.objects["bubo.rig.000002"]
if case=="physical-scale": bpy.data.objects["bubo.instance.000000"].scale.x=1+2**-20
elif case=="port-scale": bpy.data.objects["bubo.interface.000000.000000"].scale.x=1+2**-20
elif case=="armature-scale": arm.scale.x=1+2**-20
elif case=="bone-scale": arm.pose.bones["bubo.ring.000000"].scale.x=1+2**-20
elif case=="ring-deform-disabled": arm.data.bones["bubo.ring.000000"].use_deform=False
elif case=="root-deform-enabled": arm.data.bones["root"].use_deform=True
elif case=="armature-rest-mode": arm.data.pose_position="REST"
elif case=="modifier-viewport-disabled": physical.modifiers[0].show_viewport=False
elif case=="modifier-render-disabled": physical.modifiers[0].show_render=False
elif case=="armature-hidden": arm.hide_set(True)
elif case=="armature-render-disabled": arm.hide_render=True
else: raise ValueError("Unknown engineering mutation")
bpy.context.preferences.filepaths.save_version=0
bpy.ops.wm.save_as_mainfile(filepath=source,check_existing=False,compress=False)
''',encoding="utf8")
    cases={"physical-scale":"Exact identity scale/delta transforms required","port-scale":"Exact identity scale/delta transforms required","armature-scale":"Exact identity scale/delta transforms required","bone-scale":"Exact identity pose-bone scale required","ring-deform-disabled":"Root/ring bone active deformation flags changed","root-deform-enabled":"Owned root must not weight/deform vertices","armature-rest-mode":"Owned armature must remain active in pose mode","modifier-viewport-disabled":"Active actual owned armature modifier required","modifier-render-disabled":"Active actual owned armature modifier required","armature-hidden":"Owned armature must remain active in pose mode","armature-render-disabled":"Owned armature must remain active in pose mode"}
    for case,error in cases.items():
        output=directory/("semantic-"+case)
        output.mkdir()
        for payload in accepted_output.iterdir():
            if payload.name!="assembly-verification.json":
                shutil.copyfile(payload,output/payload.name)
        command=[str(blender),"--background","--factory-startup","--disable-autoexec","--offline-mode","--threads","1","--python-exit-code","41","--python",str(helper),"--",str(output/"assembly.blend"),case]
        run=subprocess.run(command,capture_output=True,timeout=180)
        (output/"mutation.log").write_bytes(run.stdout+b"\nSTDERR\n"+run.stderr)
        assert run.returncode==0,(case,(run.stdout+run.stderr)[-6000:])
        # Mutation logs are evidence outside the sealed worker payload.
        (output/"mutation.log").rename(directory/(case+"-mutation.log"))
        job=copy.deepcopy(assembly_job)
        job["output"]=str(output)
        path=directory/(case+"-job.json")
        sha=write_job(path,job)
        report_path=output/"assembly-report.json"
        report=json.loads(report_path.read_bytes())
        report.update(jobSha256=sha,sourceSha256=digest(output/"assembly.blend"),totalBytes=sum(p.stat().st_size for p in output.iterdir() if p.suffix!=".json"))
        report_path.write_text(json.dumps(report,indent=2)+"\n",encoding="utf8")
        launch(blender,"assembly_verify.py",path,sha,directory,success=False,error=error)
        # Receiving a source with its new exact SHA must also reject it before
        # any pose reset or diagnostic action can conceal the source mutation.
        proof=directory/("proof-reject-"+case)
        proof.mkdir()
        job.update(operation="prove-motion",output=str(proof),sourceAssembly=str(output/"assembly.blend"),sourceAssemblySha256=digest(output/"assembly.blend"))
        proof_sha=write_job(path,job)
        launch(blender,"assembly_motion.py",path,proof_sha,directory,success=False,error=error)
    return list(cases)


def run(blender):
    directory=ROOT/".bubo/schematic-assembly-blender-tests"/uuid.uuid4().hex
    directory.mkdir(parents=True)
    design,plan,rigs=fixture()
    design.update(schemaVersion="bubo-assembly-design/v2",assemblyProfile={"schemaVersion":"bubo-assembly-profile/v1","samplesPerRange":3,"samplesPerCycle":9,"maxGeometryChecks":1000000,"minimumSeparation":0,"contacts":[],"flexibleRigs":[{"routeId":r["id"],"minimumSegmentRatio":.8,"minimumEndpointDistance":None,"maximumEndpointDistance":None,"driverJointId":"driver" if r["closed"] else None,"travelMetresPerDegree":.001 if r["closed"] else None} for r in design["routes"]],"couplingFits":[]})
    plan["sampling"]["samplesPerCycle"]=9
    identity={key:"a"*64 for key in ("designSha256","designFingerprint","runtimeBuildSha256")}
    identity["toolSha256"]=digest(blender)
    limits={"maxVertices":100000,"maxTriangles":100000,"maxOutputBytes":128*1024*1024,"maxJobBytes":8*1024*1024,"maxFiles":512}
    parts=directory/"parts"
    parts.mkdir()
    job={"schemaVersion":"bubo-schematic-part-job/v1","operation":"build-parts","projectRoot":str(ROOT),"output":str(parts),"design":design,"partIdentities":{p["id"]:hashlib.sha256(json.dumps(p,sort_keys=True).encode()).hexdigest() for p in design["parts"]},"identity":identity,"bundle":{f:digest(BUNDLE/f) for f in PART_FILES},"limits":limits}
    path=directory/"parts-job.json"
    sha=write_job(path,job)
    for worker in ("parts_author.py","parts_verify.py"):
        launch(blender,worker,path,sha,directory)
    output=directory/"assembly"
    output.mkdir()
    assembly_identity=dict(identity,partsJobSha256=sha,partsReportSha256=digest(parts/"parts-report.json"),partsVerificationSha256=digest(parts/"parts-verification.json"))
    job={"schemaVersion":"bubo-schematic-assembly-job/v1","operation":"assemble","projectRoot":str(ROOT),"output":str(output),"design":design,"partsDirectory":str(parts),"partsReportSha256":digest(parts/"parts-report.json"),"partsVerificationSha256":digest(parts/"parts-verification.json"),"parts":json.loads((parts/"parts-report.json").read_bytes()),"identity":assembly_identity,"bundle":{f:digest(BUNDLE/f) for f in ASSEMBLY_FILES},"limits":dict(limits,maxMotionSamples=32,maxWorkChecks=1000000),"plan":plan,"rigs":rigs,"sourceAssembly":None,"sourceAssemblySha256":None}
    path=directory/"assembly-job.json"
    sha=write_job(path,job)
    assembly_job=copy.deepcopy(job)
    for worker in ("assembly_author.py","assembly_verify.py"):
        launch(blender,worker,path,sha,directory)
    assembly=json.loads((output/"assembly-report.json").read_bytes())
    assert assembly==json.loads((output/"assembly-verification.json").read_bytes())
    for instance in assembly["instances"]:
        if instance["rig"]:
            assert len(instance["rig"]["bones"])==len(instance["rig"]["ringVertexIndices"])+1
            assert sum(len(r) for r in instance["rig"]["ringVertexIndices"])==instance["vertexCount"]
    motion=directory/"motion"
    motion.mkdir()
    job.update(operation="prove-motion",output=str(motion),sourceAssembly=str(output/"assembly.blend"),sourceAssemblySha256=digest(output/"assembly.blend"))
    path=directory/"motion-job.json"
    sha=write_job(path,job)
    for worker in ("assembly_motion.py","assembly_verify.py"):
        launch(blender,worker,path,sha,directory)
    measured=json.loads((motion/"motion-report.json").read_bytes())
    assert measured==json.loads((motion/"motion-verification.json").read_bytes())
    samples=measured["clips"][0]["samples"]
    first={i["instanceId"]:i for i in samples[0]["instances"]}
    last={i["instanceId"]:i for i in samples[-1]["instances"]}
    assert first["fixed-occurrence"]["worldPose"]==last["fixed-occurrence"]["worldPose"]
    assert first["moving-occurrence"]["worldPose"]!=last["moving-occurrence"]["worldPose"]
    assert first["fixed-occurrence"]["geometrySha256"]==last["fixed-occurrence"]["geometrySha256"]
    for kind in ("wire","hose","spring","belt"):
        key=kind+"-instance"
        assert first[key]["geometrySha256"]!=last[key]["geometrySha256"],kind
        assert first[key]["rig"]["weightsSha256"]==last[key]["rig"]["weightsSha256"]
        assert first[key]["rig"]["ringCenters"]!=last[key]["rig"]["ringCenters"]
        assert read_binary(motion/first[key]["geometryFile"])!=read_binary(motion/last[key]["geometryFile"])
    native_shaped_rotary(blender,directory,assembly_job)
    semantic_cases=saved_source_semantic_tamper(blender,directory,assembly_job,output)
    # Independent reopen catches actual binary tamper rather than trusting hashes
    # transported in the producer receipt.
    payload=motion/last["wire-instance"]["geometryFile"]
    (motion/"motion-verification.json").rename(directory/"accepted-motion-verification.json")
    raw=payload.read_bytes()
    payload.write_bytes(raw[:-1]+bytes([raw[-1]^1]))
    launch(blender,"assembly_verify.py",path,sha,directory,success=False)
    payload.write_bytes(raw)
    # Source mutation is rejected at admission before native source loading.
    tampered=directory/"tampered.blend"
    tampered.write_bytes((output/"assembly.blend").read_bytes()+b"changed")
    reject=directory/"source-rejected"
    reject.mkdir()
    job.update(output=str(reject),sourceAssembly=str(tampered))
    path=directory/"source-rejected-job.json"
    sha=write_job(path,job)
    launch(blender,"assembly_motion.py",path,sha,directory,success=False)
    print(json.dumps({"status":"passed","evidence":str(directory),"flexibleClasses":["wire","hose","spring","belt"],"independentReopen":True,"actualSkinDeformation":True,"sourceTamperRejected":True,"nativeShapedRangeAndCycle":True,"jointIdLength":80,"clipIdLength":86,"savedSourceSemanticTamperCases":semantic_cases,"humanAcceptance":False},indent=2))


if __name__=="__main__":
    parser=argparse.ArgumentParser()
    parser.add_argument("--blender",type=Path,required=True)
    args=parser.parse_args()
    assert args.blender.is_absolute() and args.blender.is_file(),"Existing absolute Blender executable required"
    run(args.blender)
