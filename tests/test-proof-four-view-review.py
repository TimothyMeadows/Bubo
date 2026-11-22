"""CPU-only audited review-input validation; no bpy, model, render or approval.
Fixture files are arbitrary bytes, not production images. Native PNG/geometry
and process tests live in the C# executable fixture suite.
"""
from pathlib import Path
import copy
import json
import hashlib
import importlib.util
import tempfile
import os
import sys
import unittest

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("proof_review_common", ROOT / "resources/proof-to-model/blender/common.py")
common = importlib.util.module_from_spec(spec)
spec.loader.exec_module(common)

class FourReview(unittest.TestCase):
    def setUp(self):
        scratch = Path(os.environ.get("BUBO_TEST_TEMP_ROOT", str(ROOT / ".bubo/test-temp"))); scratch.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix="four-review-", dir=scratch)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        def image(name):
            file = self.root / (name + ".png")
            file.write_bytes(("SYNTHETIC REVIEW HASH FIXTURE ONLY: " + name).encode())
            return file.name, hashlib.sha256(file.read_bytes()).hexdigest()
        original, original_hash = image("original")
        source_hash, classification_hash = "a"*64, "b"*64
        self.job = {"schemaVersion":3,"mode":"multi-view-four","conditioningProfile":"cardinal-four-v1","viewCount":4,
            "inputFrame":"hephaera-proof-z-up",
            "sourceSet":{"schemaVersion":1,"images":[{"id":"original","path":original,"sha256":original_hash}]},
            "classification":{"sourceSetHash":source_hash,"sources":[{"sourceId":"original","sha256":original_hash}],
                "sourceViews":[{"sourceId":"original","view":"oblique","usable":False,"reason":"Synthetic oblique anchor"}]},
            "proofSet":{"schemaVersion":2,"conditioningProfile":"cardinal-four-v1","viewCount":4,
                "sourceSetHash":source_hash,"classificationHash":classification_hash,
                "camera":{"convention":"blender-nerf-z-up","calibration":"nominal-prescribed","meshScale":1,
                    "cameraAngleX":.35,"distance":3.1192049980163574},"views":[]}}
        for name in ("front","right","back","left"):
            path, sha = image(name)
            self.job["proofSet"]["views"].append({"view":name,"image":path,"sha256":sha,
                "approvedImage":path,"approvedSha256":sha,"origin":"generated","sourceIds":["original"],
                "cameraToWorld":common.review_camera_matrix(name,3.1192049980163574)})
    def test_manifest_schema_requires_original_preparation_for_both_mv_versions(self):
        # Interpret only this declared schema condition and its required/nonempty
        # contract; this is not advertised as a full JSON-schema implementation.
        schema=json.loads((ROOT/"resources/proof-to-model/manifest.schema.json").read_text())
        branch=schema["allOf"][0]
        selector=branch["if"]["properties"]["schemaVersion"]
        def selected(version):
            if "const" in selector:return version == selector["const"]
            return version in selector["enum"]
        self.assertFalse(selected(1), "diagnostic schema must not inherit MV-only preparation requirements")
        for version in (2,3):
            self.assertTrue(selected(version), "both MV versions must select the original/preparation schema branch")
            required=set(branch["then"]["required"]) if selected(version) else set()
            for field in ("sourceSet","classification","proofSet"):
                with self.subTest(version=version,missing=field):
                    candidate={name:"existing" for name in required if name != field}
                    self.assertFalse(required.issubset(candidate), "omitted preparation member must fail required contract")
            source=branch["then"]["properties"]["sourceSet"]
            self.assertEqual(source["type"],"string")
            self.assertGreaterEqual(source["minLength"],1)
            self.assertLess(len(""),source["minLength"], "empty source-set path must fail both MV schemas")

    def test_four_positive_and_nominal_ancestry(self):
        proof, sources, classification, checked = common.review_multiview_inputs(self.job,self.root)
        self.assertEqual([v["view"] for v in proof["views"]],["front","right","back","left"])
        self.assertEqual(proof["camera"]["calibration"],"nominal-prescribed")
        self.assertEqual(len(checked),5)
    def test_profile_schema_count_mode_mismatch(self):
        mutations = [lambda j:j.update(schemaVersion=2),lambda j:j.update(mode="multi-view"),
            lambda j:j.update(conditioningProfile="six"),lambda j:j.update(viewCount=6),
            lambda j:j["proofSet"].update(schemaVersion=1),lambda j:j["proofSet"].update(conditioningProfile="six"),
            lambda j:j["proofSet"].update(viewCount=6)]
        for mutate in mutations:
            job=copy.deepcopy(self.job); mutate(job)
            with self.assertRaises(ValueError):common.review_multiview_inputs(job,self.root)
    def test_missing_extra_swapped_or_duplicate_view(self):
        for change in (lambda v:v.pop(),lambda v:v.append(copy.deepcopy(v[0])),
                       lambda v:v.reverse(),lambda v:v.__setitem__(1,copy.deepcopy(v[0]))):
            job=copy.deepcopy(self.job);change(job["proofSet"]["views"])
            with self.assertRaises(ValueError):common.review_multiview_inputs(job,self.root)
    def test_unchanged_original_is_not_claimed_usable(self):
        job=copy.deepcopy(self.job);job["proofSet"]["views"][0]["origin"]="source"
        with self.assertRaises(ValueError):common.review_multiview_inputs(job,self.root)
    def test_camera_or_image_drift(self):
        job=copy.deepcopy(self.job);job["proofSet"]["views"][0]["cameraToWorld"][0][0]=2
        with self.assertRaises(ValueError):common.review_multiview_inputs(job,self.root)
        (self.root/"front.png").write_bytes(b"changed")
        with self.assertRaises(ValueError):common.review_multiview_inputs(self.job,self.root)

if __name__ == "__main__":unittest.main()
