#!/usr/bin/env python3
"""Opt-in actual Blender 4.5 engineering QA; no model/human acceptance.

python tests/test-schematic-parts-blender.py --blender ABSOLUTE_EXISTING_BLENDER
Outputs and failure receipts remain below the owned .bubo fixture directory.
"""
import argparse
import copy
import hashlib
import json
import math
from pathlib import Path
import struct
import subprocess
import sys
import time
import uuid

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
BUNDLE = ROOT / "resources/schematic-to-model/blender"
sys.path.insert(0, str(BUNDLE))
from parts_admission import BUNDLE as FILES, digest
from parts_geometry import measurements


def xyz(x=0, y=0, z=0):
    return {"x": x, "y": y, "z": z}


def frame(x=0, y=0, z=0, rx=0, ry=0, rz=0):
    return {"origin": xyz(x, y, z), "rotationDegrees": xyz(rx, ry, rz)}


def feature(kind, parameters=None, origin=None, profile=None, path=None, sections=None, operation="add", key="shape"):
    return {"id": key, "kind": kind, "operation": operation, "frame": origin or frame(), "parameters": parameters or {},
            "profile": [{"x": x, "y": y} for x, y in (profile or [])], "path": path or [], "sections": sections or []}


def fixtures():
    design = json.loads((ROOT / "resources/schematic-to-model/examples/device.json").read_text(encoding="utf-8"))
    prototype = design["parts"][0]
    design["id"] = "actual-parts-engineering"
    design["purpose"] = "Owned actual Blender engineering fixture; synthetic identities do not grant acceptance"
    design["parts"], design["instances"] = [], []
    for key in ("joints", "mechanisms", "routes", "connections", "sections"):
        design[key] = []
    design["materials"].append({"id": "polymer", "name": "Owned blue polymer fixture", "color": "#3266cc", "roughness": .73, "metallic": .04})

    def part(key, size, shapes, material="steel"):
        item = copy.deepcopy(prototype)
        item.update(id=key, name=key, materialId=material, size=xyz(*size), features=shapes,
                    interfaces=[{"id": "fixture-port", "name": "Owned semantic port", "frame": frame(.01, .02, .03, 13, 29, 37)}], terminals=[], flexible=None)
        design["parts"].append(item)
        design["instances"].append({"id": key + "-instance", "partId": key, "parentId": "assembly", "frame": frame(), "explodeOffset": xyz()})

    part("box-opening", (1, 1, 1), [feature("box", {"width": 1, "depth": 1, "height": 1}),
          feature("cylinder", {"radius": .15, "length": 1.2, "segments": 32}, frame(.5, .5, -.1), operation="subtract", key="opening")])
    part("hex-cylinder", (.4, .4, .2), [feature("cylinder", {"radius": .2, "length": .2, "segments": 6}, frame(.2, .2, 0))])
    hexagon = [(.2 + .2 * math.cos(i * math.pi / 3), .2 + .2 * math.sin(i * math.pi / 3)) for i in range(6)]
    part("hex-extrusion", (.4, .4, .2), [feature("extrude", {"depth": .2}, profile=hexagon)], "polymer")
    radial = [(.1, 0), (.2, 0), (.2, .3), (.1, .3)]
    part("revolved-ring", (.4, .4, .3), [feature("revolve", {"angleDegrees": 360, "segments": 32}, frame(.2, .2, 0), profile=radial)])
    part("partial-revolve", (.4, .4, .3), [feature("revolve", {"angleDegrees": 180, "segments": 16}, frame(.2, .2, 0), profile=[(0, 0), (.2, 0), (.2, .3), (0, .3)])])
    part("straight-sweep", (.36, .06, .06), [feature("sweep", {"radius": .02, "segments": 12}, path=[xyz(.03, .03, .03), xyz(.33, .03, .03)])], "polymer")
    # These authored samples intentionally keep every nonadjacent swept capsule
    # clear. Workers never lower a caller's declared path detail automatically.
    elbow = [xyz(.3 + .2 * math.cos(i * math.pi / 8), .3 + .2 * math.sin(i * math.pi / 8), .03) for i in range(5)]
    part("elbow-sweep", (.55, .55, .06), [feature("sweep", {"radius": .02, "segments": 12}, path=elbow)], "polymer")
    helix = [xyz(.25 + .15 * math.cos(i * math.pi / 16), .25 + .15 * math.sin(i * math.pi / 16), .04 + .04 * i * math.pi / 16) for i in range(65)]
    part("helix-sweep", (.45, .45, .6), [feature("sweep", {"radius": .015, "segments": 12}, path=helix)])
    belt = [xyz(.3 + .2 * math.cos(i * math.pi / 8), .3 + .2 * math.sin(i * math.pi / 8), .03) for i in range(16)]
    belt.append(copy.deepcopy(belt[0]))
    part("closed-belt-sweep", (.55, .55, .06), [feature("sweep", {"radius": .02, "segments": 12}, path=belt)], "polymer")
    sections = [{"frame": frame(.1, .1, 0), "profile": [{"x": 0, "y": 0}, {"x": .2, "y": 0}, {"x": .2, "y": .2}, {"x": 0, "y": .2}]},
                {"frame": frame(.1, .1, .3), "profile": [{"x": .05, "y": .05}, {"x": .15, "y": .05}, {"x": .15, "y": .15}, {"x": .05, "y": .15}]}]
    part("tapered-loft", (.4, .4, .3), [feature("loft", sections=sections)])
    diagonal_sections = copy.deepcopy(sections)
    diagonal_sections[1]["profile"] = [{"x": .05, "y": 0}, {"x": .15, "y": 0}, {"x": .15, "y": .2}, {"x": .05, "y": .2}]
    part("diagonal-taper-loft", (.4, .4, .3), [feature("loft", sections=diagonal_sections)])
    # An overlapping additive feature and rotated frame exercise exact unions
    # and the fixed-axis convention independently from one-feature primitives.
    part("union-frame", (.5, .5, .2), [feature("box", {"width": .3, "depth": .3, "height": .2}),
          feature("box", {"width": .2, "depth": .2, "height": .2}, frame(.2, .1, 0, rz=30), key="rotated-add")])
    # Maximum-length IDs and equal first63-character prefixes must retain every
    # exact identity despite Blender's internal datablock name constraints.
    long_part = "part-" + "p" * 75
    prefix = "feature-" + "f" * 65
    part(long_part, (.5, .5, .2), [feature("box", {"width": .3, "depth": .3, "height": .2}, key=prefix + "-one"),
         feature("box", {"width": .2, "depth": .2, "height": .2}, frame(.2, .1, 0, rz=30), key=prefix + "-two")])
    design["parts"][-1]["interfaces"][0]["id"] = "port-" + "i" * 75
    design["parts"][-1]["terminals"] = [{"id": "term-" + "t" * 75, "name": "Long terminal fixture", "kind": "power", "frame": frame(.01, .02, .03)}]
    design["instances"][-1]["id"] = "long-part-instance"
    duplicate = copy.deepcopy(design["instances"][1])
    duplicate.update(id="hex-cylinder-second", frame=frame(2, 3, 4, rz=90))
    design["instances"].append(duplicate)
    return design


def read_binary(path):
    raw = path.read_bytes()
    magic, nv, nt = struct.unpack_from("<8sII", raw)
    assert magic == b"BUBOPRT1" and len(raw) == 16 + nv * 24 + nt * 12
    vertices = [struct.unpack_from("<ddd", raw, 16 + i * 24) for i in range(nv)]
    triangles = [struct.unpack_from("<III", raw, 16 + nv * 24 + i * 12) for i in range(nt)]
    return vertices, triangles


def close(actual, expected, relative=3e-5):
    assert abs(actual - expected) <= relative * max(abs(expected), 1e-8), (actual, expected)


def launch(blender, worker, job_path, job_sha, directory, expected_success=True):
    command = [str(blender), "--background", "--factory-startup", "--disable-autoexec", "--offline-mode", "--threads", "1",
               "--python-exit-code", "41", "--python", str(BUNDLE / worker), "--", str(job_path), job_sha]
    run = subprocess.run(command, capture_output=True, timeout=180)
    (directory / (worker + ".log")).write_bytes(run.stdout + b"\nSTDERR\n" + run.stderr)
    if expected_success:
        assert run.returncode == 0, (worker, run.returncode, (run.stdout + run.stderr)[-12000:].decode("utf-8", errors="replace"))
    else:
        assert run.returncode != 0, "Tampered fixture unexpectedly verified"


def inject_saved_port_delta_scale(blender, target, directory):
    # Explicit fault injection operates only on this fixture's copied payload.
    # The production worker still admits only its exact fixed four-file bundle.
    script = directory / "inject-owned-port-delta.py"
    script.write_bytes(b'''import bpy, sys
from pathlib import Path
path = Path(sys.argv[sys.argv.index("--") + 1])
assert path.is_absolute() and path.is_file() and path.parent.name == "parts"
bpy.ops.wm.open_mainfile(filepath=str(path), load_ui=False, use_scripts=False)
port = bpy.data.objects["interface.000000"]
assert port.type == "EMPTY" and tuple(port.delta_scale) == (1.0, 1.0, 1.0)
port.delta_scale = (2.0, 2.0, 2.0)
bpy.context.view_layer.update()
assert tuple(port.delta_scale) == (2.0, 2.0, 2.0)
bpy.context.preferences.filepaths.save_version = 0
bpy.ops.wm.save_as_mainfile(filepath=str(path), check_existing=False, compress=False)
''')
    command = [str(blender), "--background", "--factory-startup", "--disable-autoexec", "--offline-mode", "--threads", "1",
               "--python-exit-code", "41", "--python", str(script), "--", str(target)]
    run = subprocess.run(command, capture_output=True, timeout=60)
    (directory / "port-delta-injection.log").write_bytes(run.stdout + b"\nSTDERR\n" + run.stderr)
    assert run.returncode == 0, (run.stdout + run.stderr).decode("utf-8", errors="replace")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--blender", type=Path, required=True)
    args = parser.parse_args()
    blender = args.blender.resolve(strict=True)
    design = fixtures()
    owned = ROOT / ".bubo" / ("schematic-parts-native-" + time.strftime("%Y%m%d-%H%M%S") + "-" + uuid.uuid4().hex[:8])
    output = owned / "parts"
    output.mkdir(parents=True)
    hashes = {name: digest(BUNDLE / name) for name in FILES}
    job = {"schemaVersion": "bubo-schematic-part-job/v1", "operation": "build-parts", "projectRoot": str(ROOT), "output": str(output),
           "design": design, "partIdentities": {p["id"]: hashlib.sha256(json.dumps(p, sort_keys=True).encode()).hexdigest() for p in design["parts"]},
           "identity": {"designSha256": hashlib.sha256(json.dumps(design, sort_keys=True).encode()).hexdigest(), "designFingerprint": "a" * 64,
                        "runtimeBuildSha256": "b" * 64, "toolSha256": digest(blender)}, "bundle": hashes,
           "limits": {"maxVertices": 200000, "maxTriangles": 500000, "maxOutputBytes": 100000000, "maxJobBytes": 8388608, "maxFiles": 100}}
    job_path = owned / "job.json"
    job_path.write_bytes((json.dumps(job, indent=2) + "\n").encode())
    sha = digest(job_path)
    launch(blender, "parts_author.py", job_path, sha, owned)
    launch(blender, "parts_verify.py", job_path, sha, owned)
    report = json.loads((output / "parts-report.json").read_bytes())
    verified = json.loads((output / "parts-verification.json").read_bytes())
    assert report == verified and len(report["parts"]) == len(design["parts"])
    assert report["instances"] == design["instances"] and len(report["instances"]) == len(design["parts"]) + 1
    records = {item["id"]: item for item in report["parts"]}
    for record in records.values():
        verts, tris = read_binary(output / record["geometryFile"])
        measured = measurements(verts, tris)
        for field in measured:
            if type(measured[field]) is float:
                close(measured[field], record[field], 1e-12)
            else:
                assert measured[field] == record[field], (record["id"], field)
        assert record["blendSha256"] == digest(output / record["blendFile"])
        assert record["geometrySha256"] == digest(output / record["geometryFile"])
        assert record["manifold"] and record["components"] == 1
    polygon_area = .5 * 32 * .15 ** 2 * math.sin(2 * math.pi / 32)
    close(records["box-opening"]["volume"], 1 - polygon_area)
    close(records["hex-cylinder"]["volume"], .5 * 6 * .2 ** 2 * math.sin(math.pi / 3) * .2)
    close(records["hex-extrusion"]["volume"], records["hex-cylinder"]["volume"])
    close(records["revolved-ring"]["volume"], .5 * 32 * (.2 ** 2 - .1 ** 2) * math.sin(2 * math.pi / 32) * .3)
    close(records["partial-revolve"]["volume"], .5 * 16 * .2 ** 2 * math.sin(math.pi / 16) * .3)
    close(records["straight-sweep"]["volume"], .5 * 12 * .02 ** 2 * math.sin(math.pi / 6) * .3)
    close(records["tapered-loft"]["volume"], .3 / 3 * (.04 + .01 + math.sqrt(.04 * .01)))
    close(records["diagonal-taper-loft"]["volume"], .3 * (.04 + .02) / 2)
    for key in ("hex-extrusion", "straight-sweep", "elbow-sweep", "closed-belt-sweep"):
        close(records[key]["materialRoughness"], .73)
        close(records[key]["materialMetallic"], .04)
    expected_weight = sum(records[instance["partId"]]["triangleCount"] for instance in design["instances"])
    assert report["weightedTriangles"] == expected_weight
    assert report["totalBytes"] == sum((output / r[field]).stat().st_size for r in records.values() for field in ("blendFile", "geometryFile"))
    # Restore only fixture-owned bytes after injecting one actual payload fault.
    verification_path = output / "parts-verification.json"
    verification = verification_path.read_bytes()
    verification_path.unlink()
    target = output / records["box-opening"]["geometryFile"]
    original = target.read_bytes()
    try:
        damaged = bytearray(original)
        damaged[16] ^= 1
        target.write_bytes(damaged)
        launch(blender, "parts_verify.py", job_path, sha, owned, expected_success=False)
        assert not verification_path.exists()
    finally:
        target.write_bytes(original)
        verification_path.write_bytes(verification)
    # Rebind hashes deliberately so this test reaches source semantic checks:
    # receipt/source-hash mismatch must not substitute for delta-frame rejection.
    delta_root = owned / "tampered-port-delta"
    delta_output = delta_root / "parts"
    delta_output.mkdir(parents=True)
    for record in records.values():
        for field in ("blendFile", "geometryFile"):
            (delta_output / record[field]).write_bytes((output / record[field]).read_bytes())
    delta_job = copy.deepcopy(job)
    delta_job["output"] = str(delta_output)
    delta_path = delta_root / "job.json"
    delta_path.write_bytes((json.dumps(delta_job, indent=2) + "\n").encode())
    delta_sha = digest(delta_path)
    changed_source = delta_output / records["box-opening"]["blendFile"]
    inject_saved_port_delta_scale(blender, changed_source, delta_root)
    delta_report = copy.deepcopy(report)
    delta_report["jobSha256"] = delta_sha
    for record in delta_report["parts"]:
        if record["id"] == "box-opening":
            record["blendSha256"] = digest(changed_source)
    delta_report["totalBytes"] = sum((delta_output / r[field]).stat().st_size for r in delta_report["parts"] for field in ("blendFile", "geometryFile"))
    (delta_output / "parts-report.json").write_bytes((json.dumps(delta_report, indent=2) + "\n").encode())
    launch(blender, "parts_verify.py", delta_path, delta_sha, delta_root, expected_success=False)
    delta_log = (delta_root / "parts_verify.py.log").read_text(encoding="utf-8", errors="replace")
    assert "Port delta transforms must remain identity" in delta_log
    assert not (delta_output / "parts-verification.json").exists()
    examples = []
    for name in ("vehicle", "device", "vessel"):
        example = json.loads((ROOT / ("resources/schematic-to-model/examples/" + name + ".json")).read_bytes())
        example_root = owned / name
        example_output = example_root / "parts"
        example_output.mkdir(parents=True)
        example_job = copy.deepcopy(job)
        example_job.update(output=str(example_output), design=example,
                           partIdentities={p["id"]: hashlib.sha256(json.dumps(p, sort_keys=True).encode()).hexdigest() for p in example["parts"]})
        example_job["identity"]["designSha256"] = hashlib.sha256(json.dumps(example, sort_keys=True).encode()).hexdigest()
        example_path = example_root / "job.json"
        example_path.write_bytes((json.dumps(example_job, indent=2) + "\n").encode())
        example_sha = digest(example_path)
        launch(blender, "parts_author.py", example_path, example_sha, example_root)
        launch(blender, "parts_verify.py", example_path, example_sha, example_root)
        actual = json.loads((example_output / "parts-report.json").read_bytes())
        assert actual == json.loads((example_output / "parts-verification.json").read_bytes())
        assert actual["instances"] == example["instances"] and len(actual["parts"]) == len(example["parts"])
        examples.append({"name": name, "definitions": len(actual["parts"]), "instances": len(actual["instances"]), "weightedTriangles": actual["weightedTriangles"]})
    rejected = []
    negative_shapes = [
        ("disjoint-additions", [feature("box", {"width": .3, "depth": .3, "height": .3}),
          feature("box", {"width": .3, "depth": .3, "height": .3}, frame(.6, 0, 0), key="separate-island")], "One connected physical solid"),
        ("splitting-subtraction", [feature("box", {"width": 1, "depth": 1, "height": 1}),
          feature("box", {"width": .2, "depth": 1.2, "height": 1.2}, frame(.4, -.1, -.1), operation="subtract", key="split")], "One connected physical solid"),
        ("sealed-cavity", [feature("box", {"width": 1, "depth": 1, "height": 1}),
          feature("box", {"width": .4, "depth": .4, "height": .4}, frame(.3, .3, .3), operation="subtract", key="enclosed-cavity")], "positive outward volume"),
        ("overlapping-u-sweep", [feature("sweep", {"radius": .55, "segments": 12},
          path=[xyz(1, 1, 1), xyz(2, 1, 1), xyz(2, 2, 1), xyz(1, 2, 1)])], "Nonlocal sweep"),
        ("adjacent-ring-foldover", [feature("sweep", {"radius": .1, "segments": 12},
          path=[xyz(.2, .2, .2), xyz(1.2, .2, .2), xyz(1.2, .21, .2)])], "Adjacent sweep rings"),
        ("unrepresentable-feature-precision", [feature("box", {"width": .123456789, "depth": .25, "height": .25})],
          "authored feature coordinates within approved linear tolerance", {"linearTolerance": 1e-10}),
        ("unrepresentable-port-origin", [feature("box", {"width": .25, "depth": .25, "height": .25})],
          "port origin within approved linear tolerance", {"linearTolerance": 1e-10, "port": frame(.123456789, .125, .125)}),
        ("unrepresentable-port-rotation", [feature("box", {"width": .25, "depth": .25, "height": .25})],
          "port rotation within approved angular tolerance", {"linearTolerance": 1e-10, "angularToleranceDegrees": 1e-10, "port": frame(.125, .125, .125, 13, 29, 37)})]
    for entry in negative_shapes:
        name, shapes, message = entry[:3]
        options = entry[3] if len(entry) == 4 else {}
        negative = copy.deepcopy(design)
        negative["parts"] = [copy.deepcopy(design["parts"][0])]
        negative["parts"][0].update(size=xyz(3, 3, 3), features=shapes)
        negative["instances"] = [copy.deepcopy(design["instances"][0])]
        for key in ("linearTolerance", "angularToleranceDegrees"):
            if key in options:
                negative["numerics"][key] = options[key]
        if "port" in options:
            negative["parts"][0]["interfaces"][0]["frame"] = options["port"]
        directory = owned / ("rejected-" + name)
        negative_output = directory / "parts"
        negative_output.mkdir(parents=True)
        negative_job = copy.deepcopy(job)
        negative_job.update(output=str(negative_output), design=negative,
                            partIdentities={p["id"]: hashlib.sha256(json.dumps(p, sort_keys=True).encode()).hexdigest() for p in negative["parts"]})
        negative_job["identity"]["designSha256"] = hashlib.sha256(json.dumps(negative, sort_keys=True).encode()).hexdigest()
        negative_path = directory / "job.json"
        negative_path.write_bytes((json.dumps(negative_job, indent=2) + "\n").encode())
        launch(blender, "parts_author.py", negative_path, digest(negative_path), directory, expected_success=False)
        assert message in (directory / "parts_author.py.log").read_text(encoding="utf-8", errors="replace"), name
        assert not any(negative_output.iterdir()), "Rejected shape wrote a payload"
        rejected.append(name)
    summary = {"status": "passed", "blender": str(blender), "blenderVersion": report["blenderVersion"], "fixture": str(owned),
               "definitions": len(records), "instances": len(report["instances"]), "weightedTriangles": expected_weight,
               "checks": ["all-six-feature-families", "exact-boolean-opening-and-union", "axis-and-partial-revolve", "hex-fastener", "curved-elbow", "helix", "closed-round-belt", "positive-diagonal-loft-taper", "maximum-length-identities-with-common-prefixes", "actual-materials", "clean-reopen-and-independent-boolean-replay", "binary-tamper-rejected", "saved-source-port-delta-scale-rejected-after-hash-refresh"],
               "examples": examples, "rejectedBeforePayload": rejected,
               "scope": "Actual engineering geometry/reopen evidence with synthetic native identities; no visual, human model or runtime acceptance"}
    (owned / "summary.json").write_bytes((json.dumps(summary, indent=2) + "\n").encode())
    print(json.dumps(summary))


if __name__ == "__main__":
    main()
