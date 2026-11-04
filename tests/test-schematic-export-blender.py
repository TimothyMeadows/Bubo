#!/usr/bin/env python3
"""Opt-in real fixed export/import/review engineering QA; no acceptance grant."""
import argparse
import copy
import hashlib
import importlib.util
import json
import math
from pathlib import Path
import struct
import subprocess
import sys
import uuid

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
BUNDLE = ROOT/"resources/schematic-to-model/blender"
sys.path.insert(0, str(BUNDLE))
from export_admission import BUNDLE as EXPORT_FILES, INPUTS, digest
from assembly_admission import BUNDLE as ASSEMBLY_FILES
from parts_admission import BUNDLE as PART_FILES
import export_geometry as geo


def write_job(path, job):
    path.write_bytes((json.dumps(job, sort_keys=True, separators=(",", ":"), allow_nan=False)+"\n").encode())
    assert path.stat().st_size <= 8*1024**2
    return digest(path)


def reference(path):
    return {"path": str(path), "sha256": digest(path), "bytes": path.stat().st_size}


def launch(blender, worker, path, directory, success=True, timeout=600):
    command = [str(blender), "--background", "--factory-startup", "--disable-autoexec", "--offline-mode", "--threads", "1", "--python-exit-code", "41", "--python", str(BUNDLE/worker), "--", str(path), digest(path)]
    run = subprocess.run(command, capture_output=True, timeout=timeout)
    log = directory/(path.stem+"."+worker+".log")
    log.write_bytes(run.stdout+b"\nSTDERR\n"+run.stderr)
    if success:
        assert run.returncode == 0, (worker, run.returncode, str(log), (run.stdout+run.stderr)[-14000:].decode(errors="replace"))
    else:
        assert run.returncode != 0, "Expected actual fixed-worker rejection"


def toolchain(blender):
    root = blender.parent/"4.5/scripts/addons_core/io_scene_gltf2"
    files = [{"path": p.relative_to(root).as_posix(), "sha256": digest(p), "bytes": p.stat().st_size} for p in root.rglob("*") if p.is_file()]
    files.sort(key=lambda row: row["path"].encode("utf-16-be"))
    sha = digest(blender)
    content = "\n".join([str(blender), sha, str(root)]+[r["path"]+"\t"+r["sha256"]+"\t"+str(r["bytes"]) for r in files])+"\n"
    return {"blenderPath": str(blender), "blenderSha256": sha, "moduleRoot": str(root), "files": files, "fingerprint": hashlib.sha256(content.encode()).hexdigest()}


def build_source(blender, directory, maximum=False, precision=False, nonneutral=False):
    spec = importlib.util.spec_from_file_location("assembly_fixture", ROOT/"tests/test-schematic-assembly-blender.py")
    helper = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(helper)
    design, plan, rigs = helper.fixture()
    if nonneutral:
        # Vehicle-like 15-key range: canonical rest is zero but the first
        # actual clip key is -180 degrees. SDK autoactivation must not turn
        # this first range endpoint into reported static bind evidence.
        design["joints"][0].update(kind="rotary", minimum=-180, maximum=180)
        initial = copy.deepcopy(plan["rest"])
        samples = []
        for index in range(15):
            t = index/14
            pose = copy.deepcopy(initial)
            pose["jointValues"]["driver"] = -180+360*t
            pose["instances"]["moving-occurrence"] = helper.rigid(1, angle=-180+360*t)
            samples.append({"timeSeconds": t, "pose": pose})
        plan["clips"][0]["samples"] = samples
        plan["sampling"].update(samplesPerRange=15, totalSamples=16)
        for rig in rigs:
            start = rig["restRingFrames"]
            end = rig["clips"][0]["samples"][-1]["ringFrames"]
            rig["clips"][0]["samples"] = []
            for index in range(15):
                frames = copy.deepcopy(start)
                for value, a, b in zip(frames, start, end):
                    for axis in "xyz":
                        value["origin"][axis] = a["origin"][axis]+index/14*(b["origin"][axis]-a["origin"][axis])
                rig["clips"][0]["samples"].append({"sampleIndex": index, "ringFrames": frames})
    if precision:
        design["parts"][0]["size"] = helper.xyz(.001, .001, .001)
        design["parts"][0]["features"][0]["parameters"] = {"width": .001, "depth": .001, "height": .001}
        design["parts"][0]["interfaces"][0]["frame"] = helper.frame()
        for item, x in zip(design["instances"][:2], (9999, 9998)):
            item["frame"] = helper.frame(x)
        for pose in [plan["rest"]]+[s["pose"] for c in plan["clips"] for s in c["samples"]]:
            pose["instances"]["fixed-occurrence"]["origin"]["x"] = 9999
            pose["instances"]["moving-occurrence"]["origin"]["x"] = 9998
    if maximum:
        joint_id = "joint-"+"j"*74
        design["joints"][0].update(id=joint_id, kind="rotary", maximum=360)
        old_rest = copy.deepcopy(plan["rest"])
        def pose(t):
            value = copy.deepcopy(old_rest)
            value["jointValues"] = {joint_id: 360*t}
            value["closures"][0]["jointId"] = joint_id
            value["instances"]["moving-occurrence"] = helper.rigid(1, angle=360*t)
            return value
        clips = [{"id": prefix+joint_id, "driverJointId": joint_id, "mechanismId": None, "completeCycle": cycle,
                  "samples": [{"timeSeconds": i/64, "pose": pose(i/64)} for i in range(65)]} for prefix, cycle in (("range-", False), ("cycle-", True))]
        plan = {"schemaVersion": "bubo-schematic-diagnostic-plan/v1", "rest": pose(0), "clips": clips,
                "sampling": {"samplesPerRange": 65, "samplesPerCycle": 65, "totalSamples": 131, "drivers": [{"driverJointId": joint_id, "cycleApplicable": True, "completeCycleSampled": True, "cycleStatus": "complete-mechanism-cycle"}]}, "workChecks": 100}
        for rig in rigs:
            final = copy.deepcopy(rig["clips"][0]["samples"][-1]["ringFrames"])
            samples = []
            for index in range(65):
                t = math.sin(math.pi*index/64)
                frames = copy.deepcopy(rig["restRingFrames"])
                for value, start, end in zip(frames, rig["restRingFrames"], final):
                    for axis in "xyz":
                        value["origin"][axis] = start["origin"][axis]+t*(end["origin"][axis]-start["origin"][axis])
                samples.append({"sampleIndex": index, "ringFrames": frames})
            rig["clips"] = [{"clipId": clip["id"], "samples": copy.deepcopy(samples)} for clip in clips]
    design.update(schemaVersion="bubo-assembly-design/v2", assemblyProfile={"schemaVersion": "bubo-assembly-profile/v1", "samplesPerRange": 3, "samplesPerCycle": 9, "maxGeometryChecks": 50000000, "minimumSeparation": 0, "contacts": [], "flexibleRigs": [{"routeId": r["id"], "minimumSegmentRatio": .8, "minimumEndpointDistance": None, "maximumEndpointDistance": None, "driverJointId": "driver" if r["closed"] else None, "travelMetresPerDegree": .001 if r["closed"] else None} for r in design["routes"]], "couplingFits": []})
    if maximum:
        design["assemblyProfile"].update(samplesPerRange=65, samplesPerCycle=65)
        for policy in design["assemblyProfile"]["flexibleRigs"]:
            if policy["driverJointId"] is not None:
                policy["driverJointId"] = joint_id
    else:
        plan["sampling"]["samplesPerCycle"] = 9
        if nonneutral:
            design["assemblyProfile"]["samplesPerRange"] = 15
    identity = {key: "a"*64 for key in ("designSha256", "designFingerprint", "runtimeBuildSha256")}
    identity["toolSha256"] = digest(blender)
    limits = {"maxVertices": 100000, "maxTriangles": 100000, "maxOutputBytes": 128*1024**2, "maxJobBytes": 8*1024**2, "maxFiles": 4096}
    parts = directory/"parts"
    parts.mkdir()
    job = {"schemaVersion": "bubo-schematic-part-job/v1", "operation": "build-parts", "projectRoot": str(ROOT), "output": str(parts), "design": design, "partIdentities": {p["id"]: hashlib.sha256(json.dumps(p, sort_keys=True).encode()).hexdigest() for p in design["parts"]}, "identity": identity, "bundle": {f: digest(BUNDLE/f) for f in PART_FILES}, "limits": limits}
    path = directory/"parts-job.json"
    write_job(path, job)
    for worker in ("parts_author.py", "parts_verify.py"):
        launch(blender, worker, path, directory)
    identity.update(partsJobSha256=digest(path), partsReportSha256=digest(parts/"parts-report.json"), partsVerificationSha256=digest(parts/"parts-verification.json"))
    assembly = directory/"assembly"
    assembly.mkdir()
    job = {"schemaVersion": "bubo-schematic-assembly-job/v1", "operation": "assemble", "projectRoot": str(ROOT), "output": str(assembly), "design": design, "partsDirectory": str(parts), "partsReportSha256": digest(parts/"parts-report.json"), "partsVerificationSha256": digest(parts/"parts-verification.json"), "parts": json.loads((parts/"parts-report.json").read_bytes()), "identity": identity, "bundle": {f: digest(BUNDLE/f) for f in ASSEMBLY_FILES}, "limits": dict(limits, maxMotionSamples=1024, maxWorkChecks=50000000), "plan": plan, "rigs": rigs, "sourceAssembly": None, "sourceAssemblySha256": None}
    path = directory/"assembly-job.json"
    write_job(path, job)
    for worker in ("assembly_author.py", "assembly_verify.py"):
        launch(blender, worker, path, directory)
    motion = directory/"motion"
    motion.mkdir()
    job.update(operation="prove-motion", output=str(motion), sourceAssembly=str(assembly/"assembly.blend"), sourceAssemblySha256=digest(assembly/"assembly.blend"))
    path = directory/"motion-job.json"
    write_job(path, job)
    for worker in ("assembly_motion.py", "assembly_verify.py"):
        launch(blender, worker, path, directory)
    return directory


def source_inputs(source, directory):
    assembly = json.loads((source/"assembly-job.json").read_bytes())
    motion = json.loads((source/"motion-job.json").read_bytes())
    adir, mdir = Path(assembly["output"]), Path(motion["output"])
    paths = {"assemblyJob": source/"assembly-job.json", "motionJob": source/"motion-job.json", "assemblyReport": adir/"assembly-report.json", "motionReport": mdir/"motion-report.json", "sourceAssembly": adir/"assembly.blend", "sourceMotion": mdir/"diagnostic.blend"}
    # Worker-only fixtures bind synthetic certificate bytes. Actual native CLI
    # tests separately require real Task3 native certificates and approvals.
    for name in ("assemblyVerification", "motionVerification"):
        path = directory/(name+".synthetic-worker-certificate.json")
        path.write_text('{"syntheticWorkerFixture":true,"humanAccepted":false}\n')
        paths[name] = path
    inputs = {name: reference(paths[name]) if name in paths else None for name in INPUTS}
    identity = {"build": assembly["identity"], "approvalSha256": "b"*64}
    for prefix in ("assembly", "motion"):
        for suffix, name in (("Job", prefix+"Job"), ("Report", prefix+"Report"), ("Verification", prefix+"Verification"), ("Source", "source"+prefix.capitalize())):
            identity[prefix+suffix+"Sha256"] = inputs[name]["sha256"]
    return inputs, identity


def read_geometry(path, exported):
    raw = path.read_bytes()
    magic, nv, nt = struct.unpack_from("<8sII", raw)
    assert magic == (b"BUBOEXP1" if exported else b"BUBOPRT1")
    vertices = [struct.unpack_from("<ddd", raw, 16+i*24) for i in range(nv)]
    offset = 16+nv*24
    logical = list(struct.unpack_from("<"+"I"*nv, raw, offset)) if exported else list(range(nv))
    offset += nv*4 if exported else 0
    triangles = [struct.unpack_from("<III", raw, offset+i*12) for i in range(nt)]
    assert len(raw) == offset+nt*12
    return {"vertices": vertices, "logical": logical, "triangles": triangles}


def verify_actual_geometry(report, target, source_report_path):
    source = json.loads(source_report_path.read_bytes())
    for lane in ("raw", "imported"):
        pairs = [(report[lane]["instances"], source["instances"])]
        for actual, expected in zip(report[lane]["clips"], source["clips"]):
            assert actual["id"] == expected["id"]
            pairs.extend((a["instances"], e["instances"]) for a, e in zip(actual["samples"], expected["samples"]))
        for actual_instances, expected_instances in pairs:
            expected = {i["instanceId"]: i for i in expected_instances}
            for item in actual_instances:
                baseline = expected[item["instanceId"]]
                actual = read_geometry(target/item["geometryFile"], True)
                original = read_geometry(source_report_path.parent/baseline["geometryFile"], False)
                geo.compare_geometry(actual, original["vertices"], original["triangles"], 1e-6)
                assert item["geometry"]["sourceNodeIndex"] >= 0
                assert item["geometry"]["node"] and item["geometry"]["semanticNodeKind"] in ("mesh", "empty")
                for measured in (item["worldPose"], item["geometry"]["worldPose"]):
                    assert math.dist(tuple(measured["origin"].values()), tuple(baseline["worldPose"]["origin"].values())) <= 1e-6
                    assert max(abs(measured[axis][component]-baseline["worldPose"][axis][component]) for axis in "xyz" for component in "xyz") <= math.radians(.01)
                if item["skin"] is not None:
                    raw = (target/item["skin"]["weightsFile"]).read_bytes()
                    magic, count = struct.unpack_from("<8sI", raw)
                    assert magic == b"BUBOSKX1" and count == len(actual["logical"])
                    offset = 12
                    expected_rings = {logical: ring+1 for ring, indices in enumerate(baseline["rig"]["ringVertexIndices"]) for logical in indices}
                    for logical in actual["logical"]:
                        influences, = struct.unpack_from("<I", raw, offset)
                        offset += 4
                        assert influences == 1
                        joint, weight = struct.unpack_from("<Id", raw, offset)
                        offset += 12
                        assert weight == 1 and item["skin"]["joints"][joint].endswith("."+str(expected_rings[logical]).zfill(6))
                    assert offset == len(raw)


def rejection_checks(blender, directory, producer, accepted_verify):
    cases = []
    for case in ("bundle", "source-hash", "output-bytes", "output-files", "zero-normal", "missing-owned-weights", "extra-physical-node"):
        target = directory/("reject-"+case)
        target.mkdir()
        job = copy.deepcopy(producer if case in ("output-bytes", "output-files") else accepted_verify)
        job["output"] = str(target)
        if case == "bundle":
            job["bundle"]["export_geometry.py"] = "0"*64
        elif case == "source-hash":
            job["inputs"]["sourceAssembly"]["sha256"] = "0"*64
        elif case == "output-bytes":
            job["limits"]["maxOutputBytes"] = 64
        elif case == "output-files":
            job["limits"]["maxFiles"] = 1
        else:
            doc, raw = geo.read_glb(Path(job["inputs"]["glb"]["path"]), job["limits"])
            binary = bytearray(raw)
            if case == "zero-normal":
                accessor = doc["accessors"][doc["meshes"][0]["primitives"][0]["attributes"]["NORMAL"]]
                view = doc["bufferViews"][accessor["bufferView"]]
                struct.pack_into("<fff", binary, view.get("byteOffset", 0)+accessor.get("byteOffset", 0), 0, 0, 0)
            elif case == "missing-owned-weights":
                node = next(n for n in doc["nodes"] if "skin" in n)
                del doc["meshes"][node["mesh"]]["primitives"][0]["attributes"]["WEIGHTS_0"]
            else:
                original = next((i, n) for i, n in enumerate(doc["nodes"]) if "mesh" in n and "skin" not in n)
                clone = copy.deepcopy(original[1])
                clone["name"] = "unauthorized-physical-duplicate"
                clone.pop("children", None)
                index = len(doc["nodes"])
                doc["nodes"].append(clone)
                parent = next(n for n in doc["nodes"] if original[0] in n.get("children", []))
                parent["children"].append(index)
            tampered = directory/(case+".glb")
            geo.write_glb(tampered, doc, binary)
            job["inputs"]["glb"] = reference(tampered)
        path = directory/("reject-"+case+"-job.json")
        write_job(path, job)
        launch(blender, "export_author.py" if job["operation"] == "produce" else "export_verify.py", path, directory, success=False)
        assert not any(target.iterdir()), "Rejection must precede model/native output: "+case
        cases.append(case)
    return cases


def run(blender, source=None, review=False, maximum=False, precision=False, tamper=False, nonneutral=False):
    directory = ROOT/".bubo/schematic-export-blender-tests"/uuid.uuid4().hex
    directory.mkdir(parents=True)
    source = build_source(blender, directory, maximum, precision, nonneutral) if source is None else source
    inputs, identity = source_inputs(source, directory)
    output = directory/"producer"
    output.mkdir()
    job = {"schemaVersion": "bubo-schematic-export-job/v1", "operation": "produce", "representation": None, "projectRoot": str(ROOT), "output": str(output), "bundle": {f: digest(BUNDLE/f) for f in EXPORT_FILES}, "toolchain": toolchain(blender), "identity": identity,
           "limits": {"maxSourceBytes": 256*1024**2, "maxJobBytes": 8*1024**2, "maxOutputBytes": 256*1024**2, "maxVertices": 100000, "maxTriangles": 200000, "maxNodes": 40000, "maxAccessors": 200000, "maxFiles": 4096, "maxMotionSamples": 1024, "maxGeometryChecks": 50000000, "maxViews": 4096, "imageSize": 256, "renderSamples": 1, "maxSkinJoints": 16384, "maxChannels": 32768, "maxPrimitives": 16384}, "inputs": inputs}
    path = directory/"produce-job.json"
    write_job(path, job)
    launch(blender, "export_author.py", path, directory)
    package = directory/"package.json"
    package.write_text('{"schemaVersion":"synthetic-worker-package/v1"}\n')
    for representation, filename in (("rest", "assembly.glb"), ("diagnostic", "diagnostic.glb")):
        target = directory/representation
        target.mkdir()
        verify = copy.deepcopy(job)
        verify.update(operation="verify", representation=representation, output=str(target))
        verify["inputs"].update(glb=reference(output/filename), producerReport=reference(output/"report.json"), package=reference(package))
        path = directory/(representation+"-job.json")
        write_job(path, verify)
        launch(blender, "export_verify.py", path, directory)
        report = json.loads((target/"report.json").read_bytes())
        assert report["raw"] and report["imported"]
        verify_actual_geometry(report, target, Path(inputs["assemblyReport" if representation == "rest" else "motionReport"]["path"]))
        for lane in ("raw", "imported"):
            assert len(report[lane]["instances"]) == 6
            assert sum(i["skin"] is not None for i in report[lane]["instances"]) == 4
            if maximum and representation == "diagnostic":
                assert len(report[lane]["clips"]) == 2
                assert all(len(c["id"]) == 86 and len(c["samples"]) == 65 for c in report[lane]["clips"])
    rejected = rejection_checks(blender, directory, job, verify) if tamper else []
    if review:
        payload = directory/"payload"
        payload.mkdir()
        files = []
        for name in ("assembly.glb", "diagnostic.glb"):
            (payload/name).write_bytes((output/name).read_bytes())
            row = reference(payload/name)
            row["path"] = name
            files.append(row)
        package = payload/"package.json"
        package.write_text(json.dumps({"schemaVersion": "bubo-schematic-export-package/v1", "files": files}, separators=(",", ":"))+"\n")
        target = directory/"review"
        target.mkdir()
        review_job = copy.deepcopy(job)
        review_job.update(operation="review", output=str(target))
        review_job["inputs"].update(producerReport=reference(output/"report.json"), package=reference(package), restVerification=reference(directory/"rest/report.json"), diagnosticVerification=reference(directory/"diagnostic/report.json"))
        path = directory/"review-job.json"
        write_job(path, review_job)
        launch(blender, "export_review.py", path, directory, timeout=1800)
        index = json.loads((target/"review.json").read_bytes())
        assert {v["kind"] for v in index["views"]} == {"exterior", "section", "exploded", "diagnostic"}
        assert all(len(v["instanceIds"]) == 6 for v in index["views"])
    print(json.dumps({"status": "passed", "evidence": str(directory), "source": str(source), "actualPortableExport": True, "independentImport": True, "actualRejectedCases": rejected, "humanAcceptance": False}, indent=2))
    return directory


def replay_actual_job(blender, source_job):
    """Read-only prior payload, refreshed current worker job, separate output.

    This diagnostic replay supplies no new package identity or acceptance.
    Public CLI creation with the current resource catalog remains separate.
    """
    job = json.loads(source_job.read_bytes())
    assert job["operation"] == "verify"
    root = Path(job["projectRoot"])
    directory = root/".bubo/schematic-export-import-regressions"/uuid.uuid4().hex
    directory.mkdir(parents=True)
    target = directory/"output"
    target.mkdir()
    job["output"] = str(target)
    job["bundle"] = {name: digest(BUNDLE/name) for name in EXPORT_FILES}
    path = directory/"job.json"
    write_job(path, job)
    launch(blender, "export_verify.py", path, directory)
    report = json.loads((target/"report.json").read_bytes())
    source = Path(job["inputs"]["assemblyReport" if job["representation"] == "rest" else "motionReport"]["path"])
    verify_actual_geometry(report, target, source)
    print(json.dumps({"status": "passed", "evidence": str(directory), "sourceJob": str(source_job), "actualStaticBindAndAllSamplesMatchSource": True, "newPackageAcceptance": False}, indent=2))
    return directory


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--blender", type=Path, required=True)
    parser.add_argument("--source", type=Path)
    parser.add_argument("--review", action="store_true")
    parser.add_argument("--maximum", action="store_true")
    parser.add_argument("--precision", action="store_true")
    parser.add_argument("--tamper", action="store_true")
    parser.add_argument("--nonneutral-start", action="store_true")
    parser.add_argument("--verify-job", type=Path)
    args = parser.parse_args()
    assert not (args.maximum and args.precision), "Separate fixtures keep their declared source poses explicit"
    if args.verify_job:
        replay_actual_job(args.blender.resolve(), args.verify_job.resolve())
    else:
        run(args.blender.resolve(), args.source, args.review, args.maximum, args.precision, args.tamper, args.nonneutral_start)
