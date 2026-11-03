"""Pure fixed-worker admission/section fixtures; never starts Blender."""
import copy
from contextlib import contextmanager
import hashlib
import importlib.util
import json
import math
from pathlib import Path
import struct
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest import mock

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
BUNDLE = ROOT / "resources/floor-plan-to-model/blender"
TEST_ROOT = ROOT / ".bubo/floor-plan-export-admission-tests"
TEST_ROOT.mkdir(parents=True, exist_ok=True)


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


common = load("floor_plan_export_common_tests", BUNDLE / "admission.py")
admission = load("floor_plan_export_admission_tests", BUNDLE / "export_admission.py")
exporter = load("floor_plan_export_wrapper_tests", BUNDLE / "export.py")
views = load("floor_plan_export_views_tests", BUNDLE / "export_views.py")
geometry = load("floor_plan_export_material_geometry_tests", BUNDLE / "export_geometry.py")
geometry_fixtures = load("floor_plan_export_binary_fixture_tests", ROOT / "tests/test-floor-plan-export-geometry.py")


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write(path, value):
    raw = (json.dumps(value, separators=(",", ":"), allow_nan=False) + "\n").encode()
    path.write_bytes(raw)
    return raw


def ref(path):
    return {"path": str(path), "sha256": digest(path), "bytes": path.stat().st_size}


def point(x, y, z):
    return dict(x=x, y=y, z=z)


class ExportAdmissionTests(unittest.TestCase):
    def setUp(self):
        # Immutable failed/partial fixtures are retained; no recursive deletion.
        self.directory = Path(tempfile.mkdtemp(prefix="case-", dir=TEST_ROOT))
        self.workspace = self.directory / "workspace"
        self.attempt = self.workspace / "export/produce"
        self.attempt.mkdir(parents=True)
        self.path = self.attempt / "job.json"
        self.output = self.attempt / "output"
        original = self.workspace / "construction/one"
        original.mkdir(parents=True)
        original_output = original / "output"
        original_output.mkdir()
        self.blend = original_output / "building.blend"
        self.blend.write_bytes(b"BLENDER-exact-source")
        self.source_job = original / "job.json"
        self.source_report = original_output / "construction-report.json"
        self.plan_path = self.workspace / "building-plan.json"
        self.scene = {"schemaVersion": "bubo-floor-plan-construction-scene/v1", "planId": "qa", "linearTolerance": .001,
                      "materials": [{"id": "wall", "name": "Wall", "color": "#806040", "roughness": .7, "metallic": 0, "transmission": 0, "ior": 1.45}], "anchors": [],
                      "meshes": [{"id": "tetra-" + kind, "semanticId": "wall-one", "storeyId": "ground", "kind": "wall", "representation": kind,
                                  "collisionRole": "none" if kind == "visual" else "static", "materialId": "wall", "parentAnchorId": None,
                                  "vertices": [point(0, 0, 0), point(1, 0, 0), point(0, 1, 0), point(0, 0, 1)],
                                  "triangles": [0, 2, 1, 0, 1, 3, 0, 3, 2, 1, 2, 3], "triangleMaterialIds": None} for kind in ("visual", "collision")],
                      "metrics": {"meshCount": 2, "vertexCount": 8, "triangleCount": 8, "visualTriangleCount": 4, "collisionTriangleCount": 4,
                                  "geometryChecks": 1, "minimum": point(0, 0, 0), "maximum": point(1, 1, 1)},
                      "clearance": {"routes": [], "doorSweeps": [], "minimumRouteClearance": 0}}
        self.plan = {"schemaVersion": "bubo-building-plan/v2", "id": "qa", "coordinateSystem": "xy-metres-z-up",
                     "numerics": {"linearTolerance": .001, "angularToleranceDegrees": .01, "maxCoordinateMagnitude": 10000, "minimumFeatureSize": .001},
                     "budgets": {"maxGeneratedTriangles": 100, "maxGeometryChecks": 10000, "maxExportBytes": 8 * 1024**2},
                     "construction": {"maxConstructionVertices": 100},
                     "storeys": [{"id": "ground", "elevation": 0, "height": 3, "slabThickness": .2}], "stairs": []}
        write(self.plan_path, self.plan)
        self.source = {"schemaVersion": "bubo-floor-plan-blender-job/v1", "operation": "construct", "workspaceRoot": str(self.workspace), "outputDirectory": str(original_output),
                       "scene": self.scene, "identities": {"planSha256": digest(self.plan_path), "designFingerprint": "b"*64, "runtimeSha256": "c"*64},
                       "bundle": {"author.py": digest(BUNDLE / "author.py"), "admission.py": digest(BUNDLE / "admission.py")},
                       "limits": {"maxTriangles": 100, "maxVertices": 100, "maxObjects": 20, "maxJobBytes": 1024**2, "maxOutputBytes": 8 * 1024**2}}
        write(self.source_job, self.source)
        self.report = {"schemaVersion": "bubo-floor-plan-construction-report/v1", "jobSha256": digest(self.source_job), "bundle": self.source["bundle"], "identities": self.source["identities"], "blenderVersion": "4.5.3",
                       "blend": {"path": "building.blend", "sha256": digest(self.blend), "bytes": self.blend.stat().st_size},
                       "counts": {}, "minimum": point(0, 0, 0), "maximum": point(1, 1, 1), "maxWorldCoordinateError": 0, "meshes": [], "anchors": [], "materials": self.scene["materials"],
                       "independentReimport": False, "humanModelAccepted": False, "runtimeAccepted": False}
        write(self.source_report, self.report)
        tool = self.directory / "blender/blender.exe"
        module = tool.parent / "4.5/scripts/addons_core/io_scene_gltf2"
        module.mkdir(parents=True)
        tool.write_bytes(b"selected executable")
        (module / "__init__.py").write_bytes(b"# selected bundled glTF\n")
        self.toolchain = {"blenderPath": str(tool), "blenderSha256": digest(tool), "moduleRoot": str(module), "files": [{"path": "__init__.py", "sha256": digest(module / "__init__.py"), "bytes": (module / "__init__.py").stat().st_size}]}
        self.fingerprint()
        self.job = {"schemaVersion": "bubo-floor-plan-export-job/v1", "operation": "produce", "workspaceRoot": str(self.workspace), "outputDirectory": str(self.output),
                    "bundle": {name: digest(BUNDLE / name) for name in admission.BUNDLE}, "toolchain": self.toolchain,
                    "identities": {"planSha256": digest(self.plan_path), "designFingerprint": "b"*64, "approvalSha256": "d"*64, "runtimeSha256": "c"*64,
                                   "constructionJobSha256": digest(self.source_job), "constructionReportSha256": digest(self.source_report), "sourceSha256": digest(self.blend), "packageSha256": None},
                    "limits": {"maxSourceBytes": 8 * 1024**2, "maxJobBytes": 1024**2, "maxOutputBytes": 8 * 1024**2, "maxVertices": 100, "maxTriangles": 100,
                               "maxNodes": 20, "maxMeshes": 10, "maxMaterials": 10, "maxAccessors": 100, "maxPrimitives": 100, "maxGeometryChecks": 10000, "maxViews": 7, "imageSize": 768},
                    "inputs": {"sourceBlend": ref(self.blend), "constructionJob": ref(self.source_job), "constructionReport": ref(self.source_report), "plan": ref(self.plan_path), "package": None, "glb": None, "visualVerification": None, "collisionVerification": None}}

    def fingerprint(self):
        value = self.toolchain
        text = "\n".join([value["blenderPath"], value["blenderSha256"], value["moduleRoot"]] + [row["path"]+"\t"+row["sha256"]+"\t"+str(row["bytes"]) for row in value["files"]])+"\n"
        value["fingerprint"] = hashlib.sha256(text.encode()).hexdigest()

    def admit(self):
        raw = write(self.path, self.job)
        return admission.validate_job(common, self.job, self.path, len(raw))

    def refresh_source(self):
        write(self.source_job, self.source)
        self.report["jobSha256"] = digest(self.source_job)
        write(self.source_report, self.report)
        for key, path, identity in (("constructionJob", self.source_job, "constructionJobSha256"), ("constructionReport", self.source_report, "constructionReportSha256")):
            self.job["inputs"][key] = ref(path)
            self.job["identities"][identity] = digest(path)

    def seal(self):
        payload = self.workspace / "export/package"
        (payload / "provenance").mkdir(parents=True)
        (payload / "drawings").mkdir()
        approval = b'{"syntheticTestOnly":true}\n'
        self.job["identities"]["approvalSha256"] = hashlib.sha256(approval).hexdigest()
        for name, raw in {"building.blend": self.blend.read_bytes(), "building-plan.json": self.plan_path.read_bytes(),
                          "visual.glb": b"separately-verified-visual", "collision.glb": b"separately-verified-collision", "semantics.json": b"{}\n",
                          "provenance/construction-report.json": self.source_report.read_bytes(), "provenance/design-approval.json": approval,
                          "provenance/design-preview.json": b"{}\n"}.items():
            (payload / name).write_bytes(raw)
        names = ["floor-01.svg", "roof-plan.svg", "elevation-north.svg", "elevation-south.svg", "elevation-east.svg", "elevation-west.svg", "section-principal.svg", "construction-details.svg"]
        for name in names:
            (payload / "drawings" / name).write_bytes(b"<svg/>\n")
        ancestry = {key: value for key, value in self.job["identities"].items() if key != "packageSha256"}
        ancestry.update(catalogSha256="e"*64, constructionAttempt="construction/one", modelAuthor="synthetic-author",
                        bundle=self.job["bundle"], toolchain={"blenderSha256": self.toolchain["blenderSha256"], "blenderVersion": "4.5.3", "gltfFiles": self.toolchain["files"]})
        files = [{"path": path.relative_to(payload).as_posix(), "sha256": digest(path), "bytes": path.stat().st_size} for path in payload.rglob("*") if path.is_file()]
        self.package = {"schemaVersion": "bubo-floor-plan-export-package/v1", "task": "qa", "asset": "building", "version": "v1", "ancestry": ancestry,
                        "coordinates": {"source": "xy-metres-z-up", "target": "gltf-metres-y-up", "sourceToGltf": list(admission.SOURCE_TO_GLTF)}, "numerics": self.plan["numerics"],
                        "files": sorted(files, key=lambda row: row["path"])}
        self.package_path = payload / "package.json"
        write(self.package_path, self.package)
        self.job["operation"] = "verify"
        self.job["identities"]["packageSha256"] = digest(self.package_path)
        for key in ("sourceBlend", "constructionReport"):
            self.job["inputs"][key] = None
        self.job["inputs"]["package"] = ref(self.package_path)
        self.job["inputs"]["glb"] = ref(payload / "visual.glb")
        return payload

    def test_completed_constructor_is_read_only_and_old_absent_output_api_is_never_called(self):
        with mock.patch.object(common, "validate_job", side_effect=AssertionError("old fresh-output API called")):
            result = self.admit()
        self.assertEqual(result.scene, self.scene)
        self.assertFalse(self.output.exists())
        self.assertEqual(self.blend.read_bytes(), b"BLENDER-exact-source")

    def test_bootstrap_verifies_every_resource_before_any_blender_dependency(self):
        raw = write(self.path, self.job)
        module, result = exporter.bootstrap([str(self.path), hashlib.sha256(raw).hexdigest()])
        self.assertEqual(result.scene["planId"], "qa")
        self.assertNotIn("bpy", sys.modules)
        self.assertFalse(self.output.exists())

    def test_unknown_top_input_identity_and_limit_fields_fail_closed(self):
        for key in (None, "inputs", "identities", "limits", "toolchain"):
            job = copy.deepcopy(self.job)
            (job if key is None else job[key])["unknown"] = 1
            raw = write(self.path, job)
            with self.subTest(key=key), self.assertRaises(ValueError):
                admission.validate_job(common, job, self.path, len(raw))

    def test_existing_owned_output_cannot_be_reused(self):
        self.output.mkdir()
        with self.assertRaises(ValueError):
            self.admit()

    def test_input_exact_size_hash_and_identity_are_independently_required(self):
        for mutation in (lambda job: job["inputs"]["sourceBlend"].update(bytes=1),
                         lambda job: job["inputs"]["sourceBlend"].update(sha256="0"*64),
                         lambda job: job["identities"].update(sourceSha256="0"*64)):
            job = copy.deepcopy(self.job)
            mutation(job)
            raw = write(self.path, job)
            with self.subTest(job=job["inputs"]["sourceBlend"]), self.assertRaises(ValueError):
                admission.validate_job(common, job, self.path, len(raw))

    def test_null_unused_inputs_are_explicit_and_wrong_operation_inventory_fails(self):
        self.job["inputs"]["glb"] = ref(self.blend)
        with self.assertRaises(ValueError):
            self.admit()

    def test_source_plan_tolerances_and_approved_budgets_cannot_be_widened(self):
        for key in ("maxVertices", "maxTriangles", "maxGeometryChecks", "maxOutputBytes"):
            job = copy.deepcopy(self.job)
            job["limits"][key] += 1
            raw = write(self.path, job)
            with self.subTest(key=key), self.assertRaises(ValueError):
                admission.validate_job(common, job, self.path, len(raw))

    def test_scene_tolerance_change_fails_after_exact_reference_hash_refresh(self):
        self.scene["linearTolerance"] = .01
        self.refresh_source()
        with self.assertRaises(ValueError):
            self.admit()

    def test_angular_policy_matches_canonical_plan_domain(self):
        self.plan["numerics"]["angularToleranceDegrees"] = .0001
        write(self.plan_path, self.plan)
        self.source["identities"]["planSha256"] = digest(self.plan_path)
        self.report["identities"] = self.source["identities"]
        self.job["identities"]["planSha256"] = digest(self.plan_path)
        self.job["inputs"]["plan"] = ref(self.plan_path)
        self.refresh_source()
        with self.assertRaises(ValueError):
            self.admit()

    def test_historical_source_admission_checks_remaining_work_before_indexed_geometry(self):
        self.job["limits"]["maxGeometryChecks"] = 8
        with mock.patch.object(common, "inspect_triangle_solid", side_effect=AssertionError("unadmitted source geometry work")) as inspected:
            with self.assertRaises(ValueError):
                self.admit()
        self.assertFalse(inspected.called)

    def measured_anchor_fixture(self):
        source = {"id": "door-anchor-ground-entry", "semanticId": "ground-entry", "storeyId": "ground", "kind": "door-pivot",
                  "position": point(1.025, -.04000000000000001, 0), "axis": point(0, 0, 1), "rotationRadians": 0}
        actual = copy.deepcopy(source)
        actual["position"] = point(1.024999976158142, -.03999999910593033, 0)
        self.scene["anchors"] = [source]
        self.report["anchors"] = [actual]
        self.scene["linearTolerance"] = self.plan["numerics"]["linearTolerance"] = 1e-6
        write(self.plan_path, self.plan)
        self.source["identities"]["planSha256"] = digest(self.plan_path)
        self.job["identities"]["planSha256"] = digest(self.plan_path)
        self.job["inputs"]["plan"] = ref(self.plan_path)
        self.refresh_source()
        return source, actual

    def test_actual_blender_float32_anchor_receipt_is_admitted_without_replacing_measurements(self):
        source, actual = self.measured_anchor_fixture()
        original_actual = copy.deepcopy(actual)
        admitted = self.admit()
        self.assertEqual(admitted.scene["anchors"], [source])
        self.assertEqual(self.report["anchors"], [original_actual])
        self.assertNotEqual(actual["position"], source["position"])
        self.assertFalse(self.output.exists())

    def test_anchor_receipt_position_outside_approved_linear_policy_is_rejected(self):
        source, actual = self.measured_anchor_fixture()
        actual["position"]["x"] = source["position"]["x"] + 2e-6
        self.refresh_source()
        with self.assertRaises(ValueError):
            self.admit()

    def test_anchor_receipt_angular_measurements_use_degrees_policy_without_linear_epsilon(self):
        source, actual = self.measured_anchor_fixture()
        angle = math.radians(.005)
        actual["axis"] = point(math.sin(angle), 0, math.cos(angle))
        actual["rotationRadians"] = angle
        self.refresh_source()
        self.admit()
        for key in ("axis", "rotationRadians"):
            angle = math.radians(.02)
            actual[key] = point(math.sin(angle), 0, math.cos(angle)) if key == "axis" else angle
            self.refresh_source()
            with self.subTest(key=key), self.assertRaises(ValueError):
                self.admit()
            actual["axis"], actual["rotationRadians"] = point(0, 0, 1), 0

    def test_anchor_receipt_identity_fields_inventory_and_unit_axis_remain_strict(self):
        source, actual = self.measured_anchor_fixture()
        for mutate in (lambda row: row.update(id="unknown"), lambda row: row.update(semanticId="different"),
                       lambda row: row.update(storeyId="upper"), lambda row: row.update(kind="different"),
                       lambda row: row.update(unknown=True), lambda row: row.update(axis=point(0, 0, 0)),
                       lambda row: row.update(axis=point(0, 0, 2))):
            changed = copy.deepcopy(actual)
            mutate(changed)
            self.report["anchors"] = [changed]
            self.refresh_source()
            with self.subTest(anchor=changed), self.assertRaises(ValueError):
                self.admit()
        for anchors in ([], [actual, copy.deepcopy(actual)]):
            self.report["anchors"] = anchors
            self.refresh_source()
            with self.subTest(count=len(anchors)), self.assertRaises(ValueError):
                self.admit()

    def test_source_geometry_and_metric_mutation_fail(self):
        self.scene["meshes"][0]["triangles"][0:3] = [0, 1, 2]
        self.refresh_source()
        with self.assertRaises(ValueError):
            self.admit()

    def test_source_trusted_constructor_bundle_and_identities_cannot_change(self):
        self.source["bundle"]["admission.py"] = "0"*64
        self.refresh_source()
        with self.assertRaises(ValueError):
            self.admit()

    def test_toolchain_opaque_fingerprint_is_not_accepted(self):
        self.toolchain["fingerprint"] = "0"*64
        with self.assertRaises(ValueError):
            self.admit()

    def test_installed_module_extra_source_bytecode_and_changed_files_fail(self):
        module = Path(self.toolchain["moduleRoot"])
        (module / "unexpected.pyc").write_bytes(b"executable bytecode")
        with self.assertRaises(ValueError):
            self.admit()

    def test_existing_bytecode_is_required_in_exact_toolchain_inventory(self):
        module = Path(self.toolchain["moduleRoot"])
        bytecode = module / "__init__.pyc"
        bytecode.write_bytes(b"selected existing bytecode")
        self.toolchain["files"].append({"path": bytecode.name, "sha256": digest(bytecode), "bytes": bytecode.stat().st_size})
        self.toolchain["files"].sort(key=lambda row: row["path"])
        self.fingerprint()
        self.admit()
        bytecode.write_bytes(b"changed bytecode")
        with self.assertRaises(ValueError):
            self.admit()

    def test_selected_executable_and_module_origin_are_independently_verified(self):
        self.admit()
        fake = type("FakeBpy", (), {"app": type("App", (), {"binary_path": self.toolchain["blenderPath"]})()})()
        with mock.patch.object(importlib.util, "find_spec", return_value=type("Spec", (), {"origin": str(self.directory / "foreign.py")})()):
            with self.assertRaises(ValueError):
                admission.validate_toolchain(common, self.toolchain, fake)

    def test_job_and_bundle_hash_mutations_fail_before_bpy(self):
        raw = write(self.path, self.job)
        with self.assertRaises(ValueError):
            exporter.bootstrap([str(self.path), "0"*64])
        self.job["bundle"]["review.py"] = "0"*64
        raw = write(self.path, self.job)
        with self.assertRaises(ValueError):
            exporter.bootstrap([str(self.path), hashlib.sha256(raw).hexdigest()])

    def test_both_fixed_entry_points_reject_duplicate_nonfinite_and_deep_json(self):
        for raw in (b'{"bundle":{},"bundle":{}}', b'{"bundle":{},"value":NaN}', b'['*65+b'0'+b']'*65):
            self.path.write_bytes(raw)
            for entry in (exporter.bootstrap, views.bootstrap):
                with self.subTest(entry=entry.__module__, raw=raw[:32]), self.assertRaises((ValueError, RecursionError)):
                    entry([str(self.path), hashlib.sha256(raw).hexdigest()])

    def test_report_publication_preserves_partial_and_uses_cumulative_budget(self):
        admitted = self.admit()
        common.create_output(admitted)
        admitted.output.joinpath("visual.glb").write_bytes(b"x"*100)
        admitted.job["limits"]["maxOutputBytes"] = 110
        with self.assertRaises(ValueError):
            admission.write_report(admitted, "producer-report.json", {"measured": "x"*200})
        self.assertTrue(admitted.output.joinpath("producer-report.json.partial").exists())
        self.assertFalse(admitted.output.joinpath("producer-report.json").exists())

    def test_report_publication_never_overwrites_and_exact_inventory_is_checked(self):
        admitted = self.admit()
        common.create_output(admitted)
        admission.write_report(admitted, "verification.json", {"measured": {"passed": True}})
        with self.assertRaises(ValueError):
            admission.write_report(admitted, "verification.json", {"different": True})
        with self.assertRaises(ValueError):
            admission.output_files(admitted, {"visual.glb"})

    def test_sealed_package_exact_representation_and_every_approved_drawing_are_admitted(self):
        payload = self.seal()
        admitted = self.admit()
        self.assertEqual(admitted.representation, "visual")
        self.assertEqual(admitted.package["numerics"], self.plan["numerics"])
        self.assertFalse(self.output.exists())
        self.assertTrue((payload / "drawings/construction-details.svg").is_file())

    def test_hidden_extra_payload_file_and_missing_drawing_fail(self):
        payload = self.seal()
        (payload / "extra.bin").write_bytes(b"hidden unlisted mesh")
        with self.assertRaises(ValueError):
            self.admit()

    def test_package_wrong_axis_tool_bundle_and_missing_drawing_fail_after_hash_refresh(self):
        self.seal()
        for mutate in (lambda package: package["coordinates"]["sourceToGltf"].__setitem__(0, -1),
                       lambda package: package["ancestry"]["toolchain"].update(blenderSha256="0"*64),
                       lambda package: package["ancestry"].update(bundle={}),
                       lambda package: package["files"].__setitem__(slice(None), [row for row in package["files"] if row["path"] != "drawings/floor-01.svg"])):
            original = copy.deepcopy(self.package)
            mutate(self.package)
            write(self.package_path, self.package)
            self.job["identities"]["packageSha256"] = digest(self.package_path)
            self.job["inputs"]["package"] = ref(self.package_path)
            with self.subTest(mutation=mutate), self.assertRaises(ValueError):
                self.admit()
            self.package = original

    def test_nonfinite_overflow_is_rejected_even_inside_unused_json_metadata(self):
        with self.assertRaises(ValueError):
            admission.decode_json(common, b'{"unused":{"number":1e999}}')

    def test_canonical_path_alias_is_rejected_before_bootstrap_module_import(self):
        raw = write(self.path, self.job)
        aliased = str(self.path.parent) + "/../produce/job.json"
        for entry in (exporter.bootstrap, views.bootstrap):
            with self.subTest(entry=entry.__module__), self.assertRaises(ValueError):
                entry([aliased, hashlib.sha256(raw).hexdigest()])


class CompleteViewTests(unittest.TestCase):
    def test_complete_source_derived_cardinal_roof_floor_principal_and_each_connector(self):
        plan = {"storeys": [{"id": "upper", "elevation": 3, "height": 2.8, "slabThickness": .2}, {"id": "ground", "elevation": 0, "height": 2.8, "slabThickness": .2}],
                "stairs": [{"id": "east-flight", "start": {"x": 2, "y": 3}, "end": {"x": 5, "y": 3}}, {"id": "angled-flight", "start": {"x": 0, "y": 0}, "end": {"x": 2, "y": 2}}]}
        scene = {"metrics": {"minimum": point(0, 0, 0), "maximum": point(10, 8, 6)}}
        rows = views.view_specs(plan, scene)
        self.assertEqual(len(rows), 10)
        self.assertEqual([row["kind"] for row in rows[:5]], ["exterior-north", "exterior-east", "exterior-south", "exterior-west", "roof-plan"])
        self.assertEqual([row["sourceId"] for row in rows if row["kind"] == "floor-plan"], ["ground", "upper"])
        for row in rows:
            if row["kind"].endswith("section"):
                self.assertEqual(row["direction"], row["clipPlanes"][0]["normal"])

    def test_real_triangle_clipping_preserves_intersections_and_far_half(self):
        triangle = [(-1., 0., 0.), (1., 0., 0.), (0., 1., 0.)]
        plane = {"point": [0, 0, 0], "normal": [1, 0, 0]}
        actual = views.clip_polygon(triangle, plane)
        self.assertEqual(set(actual), {(-1., 0., 0.), (0., 0., 0.), (0., 1., 0.)})
        self.assertTrue(all(point[0] <= 0 for point in actual))
        self.assertEqual(views.clip_polygon([(1, 0, 0), (2, 0, 0), (1, 1, 0)], plane), [])

    def test_two_floor_planes_clip_crossing_triangle_instead_of_hiding_whole_mesh(self):
        polygon = [(0., 0., -1.), (2., 0., 1.), (0., 2., 3.)]
        for plane in ({"point": [0, 0, 0], "normal": [0, 0, -1]}, {"point": [0, 0, 2], "normal": [0, 0, 1]}):
            polygon = views.clip_polygon(polygon, plane)
        self.assertGreaterEqual(len(polygon), 3)
        self.assertTrue(all(0 <= point[2] <= 2 for point in polygon))
        self.assertTrue(any(point[2] == 0 for point in polygon))
        self.assertTrue(any(point[2] == 2 for point in polygon))


class CumulativeGeometryWorkTests(unittest.TestCase):
    def fake_bpy(self):
        class Mesh:
            def __init__(self):
                self.materials, self.polygons = [], []

            def from_pydata(self, vertices, edges, faces):
                self.vertices, self.faces = vertices, faces
                self.polygons = [SimpleNamespace(material_index=None) for face in faces]

            def update(self):
                pass
        self.mesh_created = []
        def new(name):
            self.mesh_created.append(name)
            return Mesh()
        return SimpleNamespace(data=SimpleNamespace(meshes=SimpleNamespace(new=new), objects=SimpleNamespace(new=lambda name, data: SimpleNamespace(data=data))),
                               context=SimpleNamespace(scene=SimpleNamespace(collection=SimpleNamespace(objects=SimpleNamespace(link=lambda item: None)))))

    def captured(self, count=1):
        return [{"id": "mesh", "storeyId": "ground", "triangles": [(((0., 0., 0.), (1., 0., 0.), (0., 1., 0.)), 0)] * count, "materials": ["material"]}]

    def admitted(self, cap, used=0):
        return SimpleNamespace(job={"limits": {"maxVertices": 100, "maxTriangles": 100, "maxGeometryChecks": cap}}, geometry_work=[used])

    def test_review_probe_no_plane_exterior_and_roof_charge_view_and_triangle(self):
        for kind in ("exterior-north", "roof-plan"):
            admitted, bpy = self.admitted(10), self.fake_bpy()
            generated, visible, diagnostics = views._clipped_view(bpy, admitted, self.captured(), {"kind": kind, "clipPlanes": []}, admitted.geometry_work)
            self.assertEqual(admitted.geometry_work[0], 2)
            self.assertEqual(diagnostics["triangles"], 1)
            self.assertEqual(visible, ["mesh"])

    def test_review_probe_exhausted_entry_rejects_before_any_no_plane_geometry(self):
        for used in (1, 2):
            admitted, bpy = self.admitted(1, used), self.fake_bpy()
            with self.assertRaises(ValueError):
                views._clipped_view(bpy, admitted, self.captured(), {"kind": "exterior-north", "clipPlanes": []}, admitted.geometry_work)
            self.assertEqual(admitted.geometry_work[0], used)
            self.assertEqual(self.mesh_created, [])

    def test_no_plane_remaining_work_is_checked_before_each_triangle_and_later_view(self):
        admitted, bpy = self.admitted(2), self.fake_bpy()
        with self.assertRaises(ValueError):
            views._clipped_view(bpy, admitted, self.captured(2), {"kind": "roof-plan", "clipPlanes": []}, admitted.geometry_work)
        self.assertEqual(admitted.geometry_work[0], 2)
        self.assertEqual(self.mesh_created, [])
        with self.assertRaises(ValueError):
            views._clipped_view(bpy, admitted, self.captured(), {"kind": "exterior-east", "clipPlanes": []}, admitted.geometry_work)
        self.assertEqual(self.mesh_created, [])

    def test_clip_plane_work_rejects_before_crossing_shared_cap(self):
        admitted, bpy = self.admitted(4), self.fake_bpy()
        with self.assertRaises(ValueError):
            views._clipped_view(bpy, admitted, self.captured(), {"kind": "principal-section", "clipPlanes": [{"point": [0, 0, 0], "normal": [0, 0, 1]}]}, admitted.geometry_work)
        self.assertEqual(admitted.geometry_work[0], 2)
        self.assertEqual(self.mesh_created, [])

    def test_raw_phases_receive_only_remaining_source_approved_allowance(self):
        admitted = self.admitted(5)
        admitted.scene = {"linearTolerance": .001}
        admitted.plan = {"numerics": {"angularToleranceDegrees": .01}}
        admitted.geometry = SimpleNamespace(inspect_glb=None)
        seen = []
        def inspect(path, scene, representation, limits, linear, angular):
            seen.append((limits["maxGeometryChecks"], angular))
            if limits["maxGeometryChecks"] < 3:
                raise ValueError("bounded helper refuses third unit before work")
            return {"geometryChecks": 3}
        admitted.geometry.inspect_glb = inspect
        exporter.inspect_raw(admission, admitted, Path("unused"), "visual")
        with self.assertRaises(ValueError):
            exporter.inspect_raw(admission, admitted, Path("unused"), "collision")
        self.assertEqual(seen, [(5, .01), (2, .01)])
        self.assertEqual(admitted.geometry_work[0], 3)
        self.assertEqual(admitted.job["limits"]["maxGeometryChecks"], 5)

    def test_exhausted_raw_and_import_phases_never_enter_native_or_helper_work(self):
        admitted = self.admitted(5, 5)
        admitted.scene = {"linearTolerance": .001}
        admitted.plan = {"numerics": {"angularToleranceDegrees": .01}}
        inspect = mock.Mock(side_effect=AssertionError("helper work after exhaustion"))
        admitted.geometry = SimpleNamespace(inspect_glb=inspect)
        with self.assertRaises(ValueError):
            exporter.inspect_raw(admission, admitted, Path("unused"), "visual")
        native = mock.Mock(side_effect=AssertionError("native work after exhaustion"))
        bpy = SimpleNamespace(ops=SimpleNamespace(wm=SimpleNamespace(read_factory_settings=native)))
        with self.assertRaises(ValueError):
            exporter.import_visual_or_collision(bpy, admitted, admission, "visual", Path("unused"), {"observedVertexCount": 1, "observedTriangleCount": 1})
        self.assertFalse(inspect.called)
        self.assertFalse(native.called)

    def test_insufficient_import_reservation_stops_before_native_import_and_never_raises_cap(self):
        admitted = self.admitted(5, 3)
        with self.assertRaises(ValueError):
            exporter.import_visual_or_collision(SimpleNamespace(), admitted, admission, "visual", Path("unused"), {"observedVertexCount": 2, "observedTriangleCount": 1})
        self.assertEqual(admitted.geometry_work[0], 3)
        self.assertEqual(admitted.job["limits"]["maxGeometryChecks"], 5)

    def test_source_and_imported_entry_reject_before_data_work_after_exhaustion(self):
        admitted = self.admitted(5, 5)
        admitted.common = SimpleNamespace()
        admitted.scene = {"linearTolerance": .001}
        with self.assertRaises(ValueError):
            exporter._source_inventory(SimpleNamespace(), admitted, admission)
        # Imported inspection admits before traversing any actual object or
        # running a comparator; it cannot spend a fresh full phase allowance.
        admitted.review = SimpleNamespace(require_source_scope=lambda data: None)
        admitted.job["limits"].update(maxNodes=10, maxMaterials=10)
        fake_data = SimpleNamespace(scenes=[1], libraries=[], texts=[], actions=[], images=[], objects=[], materials=[])
        admitted.geometry = SimpleNamespace()
        admitted.plan = {"numerics": {"angularToleranceDegrees": .01}}
        with mock.patch.object(exporter, "mesh_rows", side_effect=lambda bpy, value, rep, module: module.remaining_limits(value, "actual imported fixture")):
            with self.assertRaises(ValueError):
                exporter.inspect_import(SimpleNamespace(data=fake_data), admitted, admission, "visual")


class ClearancePreservationTests(unittest.TestCase):
    def fixture(self):
        anchor = {"id": "hinge", "position": point(0, 0, 0)}
        scene = {"linearTolerance": .001, "anchors": [anchor], "meshes": [{"representation": "collision", "parentAnchorId": "hinge", "kind": "door-leaf", "vertices": [point(0, 0, 0), point(1, 0, 0), point(0, 0, 2)]}],
                 "clearance": {"routes": [{"roomId": "room", "fromId": "hub", "toId": "portal", "minimumClearance": .02}],
                               "doorSweeps": [{"anchorId": "hinge", "openingId": "portal", "angleDegrees": 90, "minimumClearance": .02}]}}
        matrix = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]
        nodes = [{"id": "hinge", "position": [0, 0, 0], "worldMatrix": matrix}]
        raw = {"maxWorldCoordinateError": .0001, "nodes": copy.deepcopy(nodes)}
        imported = {"maxWorldCoordinateError": .0002, "nodes": {"nodes": copy.deepcopy(nodes)}}
        return scene, {"storeys": [], "stairs": []}, raw, imported

    def test_route_and_full_sweep_bounds_use_measured_surface_errors(self):
        result = exporter.clearance_preservation(*self.fixture(), "collision")
        self.assertAlmostEqual(result["routes"][0]["conservativeClearanceLowerBound"], .0198)
        self.assertAlmostEqual(result["doorSweeps"][0]["conservativeClearanceLowerBound"], .0196)
        self.assertFalse(result["newNavigationMeasurement"])
        self.assertTrue(result["doorSweeps"][0]["completeSweepAndFinalOpenCovered"])

    def test_hinge_translation_and_frame_error_are_bounded_with_leaf_lever_arm(self):
        values = self.fixture()
        values[3]["nodes"]["nodes"][0]["position"][0] = .0001
        values[3]["nodes"]["nodes"][0]["worldMatrix"][1] = .0001
        result = exporter.clearance_preservation(*values, "collision")
        sweep = result["doorSweeps"][0]
        self.assertEqual(sweep["hingePositionErrorBound"], .0001)
        self.assertGreater(sweep["movingLeafErrorBound"], .0008)
        self.assertGreater(sweep["frameConditionAmplificationBound"], 1)

    def test_unprovable_margin_and_missing_measured_hinge_are_rejected(self):
        values = self.fixture()
        values[0]["clearance"]["doorSweeps"][0]["minimumClearance"] = -.001
        with self.assertRaises(ValueError):
            exporter.clearance_preservation(*values, "collision")
        values = self.fixture()
        values[3]["nodes"]["nodes"] = []
        with self.assertRaises(ValueError):
            exporter.clearance_preservation(*values, "collision")


class SourceMaterialCoverageTests(unittest.TestCase):
    def fixture(self):
        materials = [{"id": identifier, "name": identifier, "color": "#806040", "roughness": .7, "metallic": 0, "transmission": 0, "ior": 1.45}
                     for identifier in ("masonry", "plaster", "rail-metal")]
        scene = {"materials": materials, "meshes": [{"representation": kind, "materialId": "masonry", "triangleMaterialIds": ["masonry", "plaster"]} for kind in ("visual", "collision")]}
        def linear(value):
            return value / 12.92 if value <= .04045 else ((value + .055) / 1.055)**2.4
        rows = [{"id": row["id"], "color": [linear(int(row["color"][i:i+2], 16)/255) for i in (1, 3, 5)] + [1.],
                 **{key: row[key] for key in ("roughness", "metallic", "transmission", "ior")}} for row in materials]
        return scene, rows

    def test_reopened_blend_may_drop_only_unassigned_declared_material(self):
        scene, rows = self.fixture()
        original = copy.deepcopy(scene)
        actual = rows[:2]  # rail-metal has zero triangle users in the pilot.
        self.assertEqual(len(exporter.compare_source_materials(scene, actual, geometry)), 2)
        self.assertEqual(scene, original)

    def test_retained_unused_declared_material_is_validated_in_full(self):
        scene, rows = self.fixture()
        self.assertEqual(len(exporter.compare_source_materials(scene, rows, geometry)), 3)
        rows[-1]["ior"] += .1
        with self.assertRaises(ValueError):
            exporter.compare_source_materials(scene, rows, geometry)

    def test_per_triangle_override_material_is_required_despite_absence_from_default_slots(self):
        scene, rows = self.fixture()
        with self.assertRaises(ValueError):
            exporter.compare_source_materials(scene, [rows[0]], geometry)

    def test_assigned_material_removal_shader_mutation_unknown_and_duplicate_ids_fail(self):
        scene, rows = self.fixture()
        for actual in ([rows[1]], [rows[0], rows[0], rows[1]], [*rows[:2], {**rows[2], "id": "unknown"}],
                       [{**rows[0], "transmission": .1}, rows[1]]):
            with self.subTest(actual=actual), self.assertRaises(ValueError):
                exporter.compare_source_materials(scene, actual, geometry)

    def test_null_material_overrides_keep_default_assigned_material_required(self):
        scene, rows = self.fixture()
        scene["meshes"] = [{"representation": "visual", "materialId": "masonry", "triangleMaterialIds": None}]
        exporter.compare_source_materials(scene, [rows[0]], geometry)
        with self.assertRaises(ValueError):
            exporter.compare_source_materials(scene, [], geometry)


class ExplicitMaterialFormatTests(unittest.TestCase):
    def setUp(self):
        self.directory = Path(tempfile.mkdtemp(prefix="ior-", dir=TEST_ROOT))
        self.output = self.directory / "output"
        self.output.mkdir()
        self.path = self.output / "visual.glb"
        self.scene = geometry_fixtures.scene_fixture()
        self.scene["materials"][0].update(transmission=0, ior=1.45)
        self.document, self.binary = geometry_fixtures.glb_fixture(self.scene)
        self.document["materials"][0].pop("extensions")
        self.document.pop("extensionsUsed")
        limits = {**geometry_fixtures.LIMITS, "maxJobBytes": 1024**2, "maxViews": 10}
        self.admitted = SimpleNamespace(job={"limits": limits}, geometry_work=[0], common=common, geometry=geometry,
                                        scene=self.scene, output=self.output)

    def write(self, document=None):
        self.path.write_bytes(geometry_fixtures.encode_glb(self.document if document is None else document, self.binary))

    def read(self):
        raw = self.path.read_bytes()
        size, kind = struct.unpack_from("<II", raw, 12)
        document = json.loads(raw[20:20+size])
        binary_size, binary_kind = struct.unpack_from("<II", raw, 20+size)
        self.assertEqual((kind, binary_kind), (0x4e4f534a, 0x004e4942))
        self.assertEqual(len(raw), 28+size+binary_size)
        return document, raw[28+size:]

    def emit(self):
        with mock.patch("builtins.print"):
            return exporter.preserve_material_iors(admission, self.admitted, self.path, "visual")

    def test_missing_opaque_ior_is_explicit_source_emission_with_identical_binary_offsets_and_other_semantics(self):
        self.write()
        before_doc, before_bin = self.read()
        diagnostic = self.emit()
        after_doc, after_bin = self.read()
        self.assertEqual(before_bin, after_bin)
        self.assertEqual(diagnostic["binSha256"], hashlib.sha256(before_bin).hexdigest())
        self.assertEqual(after_doc["accessors"], before_doc["accessors"])
        self.assertEqual(after_doc["bufferViews"], before_doc["bufferViews"])
        self.assertEqual(after_doc["materials"][0]["extensions"]["KHR_materials_ior"], {"ior": 1.45})
        after_doc["materials"][0].pop("extensions")
        after_doc.pop("extensionsUsed")
        self.assertEqual(after_doc, before_doc)
        observed = geometry.inspect_glb(self.path, self.scene, "visual", self.admitted.job["limits"], self.scene["linearTolerance"], .001)
        self.assertEqual(observed["materials"][0]["ior"], 1.45)
        self.assertEqual({path.name for path in self.output.iterdir()}, {"visual.glb"})

    def test_existing_expected_float32_ior_is_normalized_to_exact_approved_scalar(self):
        self.document["materials"][0]["extensions"] = {"KHR_materials_ior": {"ior": 1.4500000476837158}}
        self.document["extensionsUsed"] = ["KHR_materials_ior"]
        self.write()
        self.emit()
        self.assertEqual(self.read()[0]["materials"][0]["extensions"]["KHR_materials_ior"]["ior"], 1.45)

    def test_opaque_source_ior_one_and_format_default_one_point_five_are_both_explicit(self):
        for value in (1, 1.5):
            self.scene["materials"][0]["ior"] = value
            self.write()
            self.emit()
            with self.subTest(value=value):
                self.assertEqual(self.read()[0]["materials"][0]["extensions"]["KHR_materials_ior"], {"ior": value})
                geometry.inspect_glb(self.path, self.scene, "visual", self.admitted.job["limits"], self.scene["linearTolerance"], .001)

    def test_default_ior_is_still_emitted_and_transmission_is_preserved(self):
        self.scene["materials"][0].update(ior=1.5, transmission=.95)
        self.document["materials"][0]["extensions"] = {"KHR_materials_transmission": {"transmissionFactor": .95}}
        self.document["extensionsUsed"] = ["KHR_materials_transmission"]
        self.write()
        self.emit()
        document, binary = self.read()
        self.assertEqual(document["materials"][0]["extensions"], {"KHR_materials_transmission": {"transmissionFactor": .95}, "KHR_materials_ior": {"ior": 1.5}})
        geometry.inspect_glb(self.path, self.scene, "visual", self.admitted.job["limits"], self.scene["linearTolerance"], .001)

    def test_unexpected_existing_ior_or_other_shader_scalar_is_rejected_and_raw_is_retained(self):
        original = copy.deepcopy(self.document)
        for mutate in (lambda document: document["materials"][0].update(extensions={"KHR_materials_ior": {"ior": 1.6}}),
                       lambda document: document["materials"][0]["pbrMetallicRoughness"].update(roughnessFactor=.99)):
            self.document = copy.deepcopy(original)
            mutate(self.document)
            if "extensions" in self.document["materials"][0]:
                self.document["extensionsUsed"] = ["KHR_materials_ior"]
            self.write()
            before = self.path.read_bytes()
            with self.subTest(document=self.document), self.assertRaises(ValueError):
                self.emit()
            self.assertEqual(self.path.read_bytes(), before)
            self.assertFalse(self.path.with_name("visual.glb.partial").exists())

    def test_unknown_duplicate_material_extension_and_external_resource_are_rejected(self):
        original = copy.deepcopy(self.document)
        mutations = (lambda document: document["materials"][0]["extras"].update(bubo_id="unknown"),
                     lambda document: document["materials"].append(copy.deepcopy(document["materials"][0])),
                     lambda document: document.update(extensionsUsed=["KHR_materials_ior", "KHR_materials_ior"]),
                     lambda document: document.update(extensionsUsed=["KHR_unknown"]),
                     lambda document: document["buffers"][0].update(uri="outside.bin"),
                     lambda document: document["nodes"][0].update(extensions={"KHR_unknown": {}}))
        for mutate in mutations:
            self.document = copy.deepcopy(original)
            mutate(self.document)
            self.write()
            before = self.path.read_bytes()
            with self.subTest(document=self.document), self.assertRaises(ValueError):
                self.emit()
            self.assertEqual(self.path.read_bytes(), before)

    def test_duplicate_json_total_length_and_bin_padding_fail_before_publication(self):
        self.write()
        original = self.path.read_bytes()
        for mutate in (lambda raw: raw.__setitem__(slice(8, 12), struct.pack("<I", len(raw)+4)),
                       lambda raw: raw.__setitem__(slice(16, 20), struct.pack("<I", 0x004e4942))):
            raw = bytearray(original)
            mutate(raw)
            self.path.write_bytes(raw)
            with self.assertRaises(ValueError):
                self.emit()
            self.assertEqual(self.path.read_bytes(), raw)
        text = json.dumps(self.document, separators=(",", ":")).replace('"scene":0', '"scene":0,"scene":0').encode()
        text += b" " * (-len(text) % 4)
        self.path.write_bytes(struct.pack("<4sII", b"glTF", 2, 28+len(text)+len(self.binary)) + struct.pack("<II", len(text), 0x4e4f534a) + text + struct.pack("<II", len(self.binary), 0x004e4942) + self.binary)
        with self.assertRaises(ValueError):
            self.emit()
        self.document["buffers"][0]["byteLength"] = len(self.binary) - 1
        binary = bytearray(self.binary)
        binary[-1] = 1
        self.path.write_bytes(geometry_fixtures.encode_glb(self.document, binary))
        before = self.path.read_bytes()
        with self.assertRaises(ValueError):
            self.emit()
        self.assertEqual(self.path.read_bytes(), before)
        self.assertTrue(self.path.with_name("visual.glb.partial").exists())

    def test_json_and_cumulative_intermediate_output_caps_are_not_raised(self):
        self.write()
        self.admitted.job["limits"]["maxJobBytes"] = 128
        before = self.path.read_bytes()
        with self.assertRaises(ValueError):
            self.emit()
        self.admitted.job["limits"]["maxJobBytes"] = 1024**2
        self.admitted.job["limits"]["maxOutputBytes"] = len(before) + 64
        with self.assertRaises(ValueError):
            self.emit()
        self.assertEqual(self.path.read_bytes(), before)
        self.assertFalse(self.path.with_name("visual.glb.partial").exists())

    def test_shared_work_and_existing_partial_prevent_replacement_without_changing_raw(self):
        self.write()
        before = self.path.read_bytes()
        self.admitted.job["limits"]["maxGeometryChecks"] = 5
        with self.assertRaises(ValueError):
            self.emit()
        self.assertLessEqual(self.admitted.geometry_work[0], 5)
        self.assertEqual(self.path.read_bytes(), before)
        self.admitted.job["limits"]["maxGeometryChecks"] = 100000
        partial = self.path.with_name("visual.glb.partial")
        partial.write_bytes(b"retained earlier partial")
        with self.assertRaises(FileExistsError):
            self.emit()
        self.assertEqual(self.path.read_bytes(), before)
        self.assertEqual(partial.read_bytes(), b"retained earlier partial")


class TransientSourceSceneTests(unittest.TestCase):
    def fixture(self):
        identities = {"constructionJobSha256": "a"*64, "planSha256": "b"*64, "designFingerprint": "c"*64, "runtimeSha256": "d"*64}
        admitted = SimpleNamespace(job={"limits": {"maxGeometryChecks": 100}, "identities": identities}, geometry_work=[0],
                                    scene={"schemaVersion": "bubo-floor-plan-construction-scene/v1", "planId": "building-example"})
        scene = {"bubo_schema_version": admitted.scene["schemaVersion"], "bubo_plan_id": "building-example", "bubo_job_sha256": identities["constructionJobSha256"],
                 **{"bubo_" + key: identities[key] for key in ("planSha256", "designFingerprint", "runtimeSha256")},
                 "bubo_independent_reimport": False, "bubo_human_model_accepted": False, "bubo_runtime_accepted": False}
        return scene, admitted

    def test_only_source_scene_props_are_cleared_in_memory_and_source_bytes_and_semantic_extras_stay_intact(self):
        scene, admitted = self.fixture()
        objects = [{"bubo_id": "wall", "bubo_semanticId": "ground-wall"}]
        materials = [{"bubo_id": "masonry", "bubo_color": "#806040"}]
        original_objects, original_materials = copy.deepcopy(objects), copy.deepcopy(materials)
        directory = Path(tempfile.mkdtemp(prefix="scene-", dir=TEST_ROOT))
        source = directory / "building.blend"
        immutable_bytes = b"BLENDER-source-identity-and-geometry-remain-unchanged"
        source.write_bytes(immutable_bytes)
        exporter.clear_transient_source_scene_metadata(scene, admitted, admission)
        self.assertEqual(scene, {})
        self.assertEqual((objects, materials), (original_objects, original_materials))
        self.assertEqual(source.read_bytes(), immutable_bytes)
        self.assertEqual(admitted.geometry_work[0], 9)

    def test_unknown_missing_tampered_and_non_boolean_acceptance_fields_fail_before_any_deletion(self):
        mutations = (lambda scene: scene.update(unknown=True), lambda scene: scene.pop("bubo_plan_id"),
                     lambda scene: scene.update(bubo_plan_id="different"), lambda scene: scene.update(bubo_job_sha256="0"*64),
                     lambda scene: scene.update(bubo_schema_version="other/v1"), lambda scene: scene.update(bubo_independent_reimport=True),
                     lambda scene: scene.update(bubo_human_model_accepted=True), lambda scene: scene.update(bubo_runtime_accepted=True),
                     lambda scene: scene.update(bubo_runtime_accepted=0))
        for mutate in mutations:
            scene, admitted = self.fixture()
            mutate(scene)
            before = copy.deepcopy(scene)
            with self.subTest(scene=scene), self.assertRaises(ValueError):
                exporter.clear_transient_source_scene_metadata(scene, admitted, admission)
            self.assertEqual(scene, before)

    def test_exhausted_allowance_preserves_all_source_scene_properties(self):
        scene, admitted = self.fixture()
        before = copy.deepcopy(scene)
        admitted.geometry_work[0] = admitted.job["limits"]["maxGeometryChecks"]
        with self.assertRaises(ValueError):
            exporter.clear_transient_source_scene_metadata(scene, admitted, admission)
        self.assertEqual(scene, before)


class TransientFlatNormalTests(unittest.TestCase):
    POINTS = [(9.003564834594727, 2.880000114440918, -8.100000381469727),
              (9.004029273986816, 3., -8.102322578430176),
              (9.003564834594727, 3., -8.100000381469727)]
    WRONG_NORMAL = (.9803839921951294, 0., .1970967799425125)

    def admitted(self, cap=1000):
        return SimpleNamespace(job={"limits": {"maxGeometryChecks": cap, "maxVertices": 100, "maxTriangles": 100}}, geometry_work=[0],
                               common=common, plan={"numerics": {"angularToleranceDegrees": .01}}, scene={"meshes": [{"id": "mesh"}]})

    def test_exact_failed_courtyard_triangle_gets_double_flat_normals_inside_approved_angle(self):
        admitted = self.admitted()
        before = copy.deepcopy(self.POINTS)
        result = exporter.flat_corner_normals(self.POINTS, [(0, 1, 2)], [(0, 1, 2)], 3, admission, admitted)
        self.assertEqual(result[0], result[1])
        self.assertEqual(result[1], result[2])
        expected = (.9805806756909201, 0., .19611613513818404)
        self.assertLess(exporter._normal_angle_degrees(result[0], expected), 1e-10)
        self.assertGreater(exporter._normal_angle_degrees(result[0], self.WRONG_NORMAL), .057)
        # The pinned exporter rounds each component to four decimals before
        # normalization. Do not patch that code or widen its approved policy.
        rounded = [struct.unpack("<f", struct.pack("<f", round(component, 4)))[0] for component in result[0]]
        self.assertLess(exporter._normal_angle_degrees(rounded, result[0]), .01)
        self.assertEqual(self.POINTS, before)

    def test_shared_vertices_have_independent_flat_corner_normals_with_exact_loop_ownership(self):
        vertices = [(0, 0, 0), (1, 0, 0), (0, 1, 0), (0, 0, 1)]
        result = exporter.flat_corner_normals(vertices, [(0, 1, 2), (0, 3, 1)], [(2, 0, 1), (3, 5, 4)], 6, admission, self.admitted())
        self.assertEqual(result[:3], [(0., 0., 1.)]*3)
        self.assertEqual(result[3:], [(0., 1., 0.)]*3)

    def test_collapsed_nonfinite_quad_and_duplicate_corner_inputs_fail_without_repair(self):
        cases = ( ([(0, 0, 0)]*3, [(0, 1, 2)], [(0, 1, 2)], 3),
                  ([(0, 0, 0), (1, 0, 0), (0, float("inf"), 0)], [(0, 1, 2)], [(0, 1, 2)], 3),
                  (self.POINTS, [(0, 1, 2, 0)], [(0, 1, 2)], 3),
                  (self.POINTS, [(0, 1, 2)], [(0, 0, 2)], 3),
                  (self.POINTS, [(0, 1, 2)], [(0, 1, 3)], 3))
        for vertices, faces, loops, count in cases:
            with self.subTest(faces=faces, loops=loops), self.assertRaises(ValueError):
                exporter.flat_corner_normals(vertices, faces, loops, count, admission, self.admitted())

    def test_shared_work_is_checked_before_triangle_and_corner_computation(self):
        admitted = self.admitted(3)
        with self.assertRaises(ValueError):
            exporter.flat_corner_normals(self.POINTS, [(0, 1, 2)], [(0, 1, 2)], 3, admission, admitted)
        self.assertEqual(admitted.geometry_work[0], 3)
        self.assertEqual(admitted.job["limits"]["maxGeometryChecks"], 3)

    def mesh_fixture(self, points=None):
        class AttributeValues(list):
            def __init__(self, owner):
                super().__init__([None]*len(owner.loops))
                self.owner = owner

            def foreach_set(self, key, values):
                if key != "vector" or len(values) != len(self)*3:
                    raise AssertionError("unexpected attribute field/count")
                self.owner.corner_normals = [SimpleNamespace(vector=tuple(values[i:i+3])) for i in range(0, len(values), 3)]

        class Attributes:
            def __init__(self, owner):
                self.owner, self.rows = owner, {}

            def __len__(self):
                return len(self.rows)

            def __iter__(self):
                return iter(self.rows.values())

            def get(self, name):
                return self.rows.get(name)

            def new(self, name, type, domain):
                attribute = SimpleNamespace(name=name, data_type=type, domain=domain, data=AttributeValues(self.owner))
                self.rows[name] = attribute
                self.owner.has_custom_normals = True
                return attribute

        class Mesh:
            def __init__(self, vertices):
                self.vertices = [SimpleNamespace(co=point) for point in vertices]
                self.polygons = [SimpleNamespace(vertices=(0, 1, 2), loop_indices=(0, 1, 2), material_index=0, use_smooth=False)]
                self.loops = [None]*3
                self.edges = [SimpleNamespace(vertices=vertices, use_edge_sharp=False) for vertices in ((0, 1), (1, 2), (2, 0))]
                self.materials = ["unchanged-material"]
                self.corner_normals = []
                self.has_custom_normals = False
                self.attributes = Attributes(self)

            def copy(self):
                return copy.deepcopy(self)

            def normals_split_custom_set(self, normals):
                raise AssertionError("short2 setter must never run in direct-float authoring")

            def update(self):
                pass
        class Object(dict):
            type = "MESH"
        original = Mesh(self.POINTS if points is None else points)
        item = Object(bubo_id="mesh", bubo_semanticId="cornice", bubo_materialId="masonry")
        item.data = original
        item.matrix_world = "unchanged-transform"
        return SimpleNamespace(data=SimpleNamespace(objects=[item])), item, original

    def test_adapter_changes_only_normal_data_on_copy_and_keeps_source_geometry_and_semantics(self):
        bpy, item, original = self.mesh_fixture()
        before = copy.deepcopy(dict(item))
        with mock.patch("builtins.print"):
            exporter.prepare_transient_flat_normals(bpy, self.admitted(), admission)
        self.assertIsNot(item.data, original)
        self.assertFalse(original.has_custom_normals)
        self.assertFalse(original.polygons[0].use_smooth)
        self.assertTrue(item.data.has_custom_normals)
        self.assertTrue(item.data.polygons[0].use_smooth)
        self.assertTrue(all(edge.use_edge_sharp for edge in item.data.edges))
        self.assertTrue(all(not edge.use_edge_sharp for edge in original.edges))
        self.assertEqual([edge.vertices for edge in item.data.edges], [edge.vertices for edge in original.edges])
        self.assertEqual([vertex.co for vertex in original.vertices], [vertex.co for vertex in item.data.vertices])
        self.assertEqual(item.data.materials, original.materials)
        self.assertEqual(dict(item), before)
        self.assertEqual(item.matrix_world, "unchanged-transform")

    def test_actual_custom_normal_storage_above_approved_angle_is_rejected(self):
        bpy, item, original = self.mesh_fixture()
        working = original.copy()
        create = working.attributes.new
        def new(**kwargs):
            attribute = create(**kwargs)
            attribute.data.foreach_set = lambda key, values: setattr(working, "corner_normals", [SimpleNamespace(vector=self.WRONG_NORMAL)]*3)
            return attribute
        working.attributes.new = new
        original.copy = lambda: working
        with mock.patch("builtins.print") as diagnostic, self.assertRaises(ValueError) as rejected:
            exporter.prepare_transient_flat_normals(bpy, self.admitted(), admission)
        context = json.loads(diagnostic.call_args.args[0])
        self.assertEqual(context["meshId"], "mesh")
        self.assertEqual((context["faceIndex"], context["loopIndex"]), (0, 0))
        self.assertEqual(context["observed"], list(self.WRONG_NORMAL))
        self.assertEqual(context["expected"], list(exporter._unit_triangle_normal(self.POINTS)))
        self.assertEqual(context["localFacePositions"], [list(point) for point in self.POINTS])
        self.assertGreater(context["angularErrorDegrees"], .057)
        self.assertEqual(context["angularToleranceDegrees"], .01)
        self.assertIn('"meshId":"mesh"', str(rejected.exception))

    def test_smoothing_cache_update_precedes_float_normal_attribute_and_is_not_repeated_after(self):
        bpy, item, original = self.mesh_fixture()
        working = original.copy()
        events = []
        create = working.attributes.new
        def update():
            events.append("update")
            self.assertTrue(all(polygon.use_smooth for polygon in working.polygons))
            self.assertTrue(all(edge.use_edge_sharp for edge in working.edges))
            if working.has_custom_normals:
                working.corner_normals = [SimpleNamespace(vector=self.WRONG_NORMAL)]*3
        def new(**kwargs):
            self.assertEqual(events, ["update"])
            events.append("create-float-vector")
            attribute = create(**kwargs)
            store = attribute.data.foreach_set
            def write(key, values):
                events.append("write-corner-vectors")
                store(key, values)
            attribute.data.foreach_set = write
            return attribute
        working.update = update
        working.attributes.new = new
        original.copy = lambda: working
        with mock.patch("builtins.print"):
            exporter.prepare_transient_flat_normals(bpy, self.admitted(), admission)
        self.assertEqual(events, ["update", "create-float-vector", "write-corner-vectors"])

    def test_all_edges_are_isolated_before_direct_float_corner_write(self):
        bpy, item, original = self.mesh_fixture()
        working = original.copy()
        create = working.attributes.new
        events = []
        def update():
            self.assertTrue(all(edge.use_edge_sharp for edge in working.edges))
            events.append("isolated-fans-updated")
        def new(**kwargs):
            self.assertEqual(events, ["isolated-fans-updated"])
            self.assertTrue(all(edge.use_edge_sharp for edge in working.edges))
            attribute = create(**kwargs)
            events.append("float-corner-attribute-created")
            return attribute
        working.update = update
        working.attributes.new = new
        original.copy = lambda: working
        with mock.patch("builtins.print"):
            exporter.prepare_transient_flat_normals(bpy, self.admitted(), admission)
        self.assertEqual(events, ["isolated-fans-updated", "float-corner-attribute-created"])
        self.assertTrue(all(not edge.use_edge_sharp for edge in original.edges))

    def test_edge_isolation_work_is_admitted_before_changing_any_edge(self):
        bpy, item, original = self.mesh_fixture()
        working = original.copy()
        original.copy = lambda: working
        # Mesh reservation (10), local vertices (3), triangle/corners (4):
        # no allowance remains for the first transient edge operation.
        admitted = self.admitted(17)
        with self.assertRaises(ValueError):
            exporter.prepare_transient_flat_normals(bpy, admitted, admission)
        self.assertTrue(all(not edge.use_edge_sharp for edge in working.edges))
        self.assertTrue(all(not edge.use_edge_sharp for edge in original.edges))
        self.assertEqual(admitted.geometry_work[0], 17)

    def test_missing_custom_corner_api_fails_before_mesh_copy_or_assignment(self):
        bpy, item, original = self.mesh_fixture()
        original.attributes = None
        with self.assertRaises(ValueError):
            exporter.prepare_transient_flat_normals(bpy, self.admitted(), admission)
        self.assertIs(item.data, original)
        self.assertFalse(original.polygons[0].use_smooth)

    def test_corner_storage_cannot_turn_one_flat_face_into_varying_smooth_normals(self):
        bpy, item, original = self.mesh_fixture()
        working = original.copy()
        ideal = exporter._unit_triangle_normal(self.POINTS)
        theta = math.radians(.009)
        left = (ideal[0]*math.cos(theta), math.sin(theta), ideal[2]*math.cos(theta))
        right = (ideal[0]*math.cos(theta), -math.sin(theta), ideal[2]*math.cos(theta))
        create = working.attributes.new
        def new(**kwargs):
            attribute = create(**kwargs)
            attribute.data.foreach_set = lambda key, values: setattr(working, "corner_normals", [SimpleNamespace(vector=normal) for normal in (left, ideal, right)])
            return attribute
        working.attributes.new = new
        original.copy = lambda: working
        with mock.patch("builtins.print"), self.assertRaisesRegex(ValueError, "flat face appearance"):
            exporter.prepare_transient_flat_normals(bpy, self.admitted(), admission)

    def test_direct_float_corner_attribute_preserves_exact_short2_precision_counterexample(self):
        points = [(12.106574058532715, -.12999999523162842, 0.),
                  (12.107574462890625, -.125, 0.),
                  (12.107574462890625, -.125, .20000000298023224)]
        expected = exporter._unit_triangle_normal(points)
        short2_observed = (.9805294275283813, -.1963723748922348, 0.)
        self.assertGreater(exporter._normal_angle_degrees(expected, short2_observed), .0105)
        bpy, item, original = self.mesh_fixture(points)
        with mock.patch("builtins.print"):
            exporter.prepare_transient_flat_normals(bpy, self.admitted(), admission)
        attribute = item.data.attributes.get("custom_normal")
        self.assertEqual((attribute.name, attribute.data_type, attribute.domain, len(attribute.data)), ("custom_normal", "FLOAT_VECTOR", "CORNER", 3))
        for corner in item.data.corner_normals:
            self.assertLess(exporter._normal_angle_degrees(expected, corner.vector), .0001)
        self.assertIsNone(original.attributes.get("custom_normal"))

    def test_existing_or_alias_custom_normal_attributes_reject_before_source_copy(self):
        for name in ("custom_normal", "custom_normal.001", "CUSTOM_NORMAL"):
            bpy, item, original = self.mesh_fixture()
            original.attributes.new(name=name, type="FLOAT_VECTOR", domain="CORNER")
            original.has_custom_normals = False  # Exercise names, not a cached SDK boolean.
            with self.subTest(name=name), self.assertRaises(ValueError):
                exporter.prepare_transient_flat_normals(bpy, self.admitted(), admission)
            self.assertIs(item.data, original)

    def test_float_normal_attribute_metadata_and_vector_count_are_closed(self):
        for mutate in (lambda attribute: setattr(attribute, "name", "custom_normal.001"),
                       lambda attribute: setattr(attribute, "data_type", "FLOAT2"),
                       lambda attribute: setattr(attribute, "domain", "POINT"),
                       lambda attribute: attribute.data.pop(),
                       lambda attribute: setattr(attribute.data, "foreach_set", None)):
            bpy, item, original = self.mesh_fixture()
            working = original.copy()
            create = working.attributes.new
            def new(**kwargs):
                attribute = create(**kwargs)
                mutate(attribute)
                return attribute
            working.attributes.new = new
            original.copy = lambda: working
            with self.subTest(mutation=mutate), self.assertRaises(ValueError):
                exporter.prepare_transient_flat_normals(bpy, self.admitted(), admission)
            self.assertEqual(working.corner_normals, [])
            self.assertIsNone(original.attributes.get("custom_normal"))

    def test_float_vector_write_is_admitted_before_attribute_creation_or_foreach_set(self):
        bpy, item, original = self.mesh_fixture()
        working = original.copy()
        original.copy = lambda: working
        admitted = self.admitted(20)
        with self.assertRaises(ValueError):
            exporter.prepare_transient_flat_normals(bpy, admitted, admission)
        self.assertEqual(admitted.geometry_work[0], 20)
        self.assertIsNone(working.attributes.get("custom_normal"))
        self.assertIsNone(original.attributes.get("custom_normal"))

    def test_import_driver_uses_one_fixed_sdk_hook_flat_geometry_and_no_vertex_welding(self):
        events = []
        class Importer:
            def __init__(self, filename, settings):
                self.import_settings = settings
                self.import_user_extensions = settings["import_user_extensions"]
                self.log = SimpleNamespace(flush=lambda: events.append("flush"))
            def read(self):
                events.append("read")
            def checks(self):
                events.append("checks")
        sdk = SimpleNamespace(create=lambda importer: events.append("create"))
        extension = SimpleNamespace(bind=lambda importer, raw: events.append("bind"), require_complete=lambda: events.append("complete"))
        bpy = SimpleNamespace(app=SimpleNamespace(debug_value=0), ops=SimpleNamespace(wm=SimpleNamespace(read_factory_settings=mock.Mock())),
                              context=SimpleNamespace(scene=SimpleNamespace(sequence_editor_clear=mock.Mock(), render=SimpleNamespace(), unit_settings=SimpleNamespace(scale_length=1)), view_layer=SimpleNamespace(update=mock.Mock())))
        admitted = self.admitted()
        admitted.job["limits"]["maxSourceBytes"] = 1024
        directory = Path(tempfile.mkdtemp(prefix="sdk-driver-", dir=TEST_ROOT))
        path = directory / "owned.glb"
        path.write_bytes(b"strict-raw-admitted-fixture")
        with mock.patch.object(admission, "verify_sources"), mock.patch.object(exporter, "inspect_import", return_value={"passed": True}), \
                mock.patch.object(exporter, "_pinned_import_sdk", return_value=(Importer, sdk, None)), \
                mock.patch.object(exporter, "_require_loaded_sdk_origins"), mock.patch.object(exporter, "FixedDeliveredNormalImport", return_value=extension):
            exporter.import_visual_or_collision(bpy, admitted, admission, "visual", path, {"observedVertexCount": 3, "observedTriangleCount": 1})
        settings = exporter._fixed_import_settings(extension)
        self.assertEqual(events, ["read", "checks", "bind", "create", "complete", "flush"])
        self.assertEqual(settings["import_shading"], "FLAT")
        self.assertEqual(settings["import_user_extensions"], [extension])
        self.assertFalse(settings["merge_vertices"])
        self.assertFalse(settings["import_pack_images"])
        self.assertFalse(settings["import_scene_extras"])

    def test_sdk_logger_flushes_after_failed_create_and_preserves_original_failure(self):
        flushed = mock.Mock(side_effect=RuntimeError("logger-failure"))
        class Importer:
            def __init__(self, filename, settings):
                self.import_settings, self.import_user_extensions = settings, settings["import_user_extensions"]
                self.log = SimpleNamespace(flush=flushed)
            def read(self):
                pass
            def checks(self):
                pass
        sdk = SimpleNamespace(create=mock.Mock(side_effect=ValueError("original-sdk-create")))
        extension = SimpleNamespace(bind=mock.Mock(), require_complete=mock.Mock())
        bpy = SimpleNamespace(app=SimpleNamespace(debug_value=0), ops=SimpleNamespace(wm=SimpleNamespace(read_factory_settings=mock.Mock())),
                              context=SimpleNamespace(scene=SimpleNamespace(sequence_editor_clear=mock.Mock(), render=SimpleNamespace(), unit_settings=SimpleNamespace(scale_length=1)), view_layer=SimpleNamespace(update=mock.Mock())))
        admitted = self.admitted()
        admitted.job["limits"]["maxSourceBytes"] = 1024
        directory = Path(tempfile.mkdtemp(prefix="sdk-flush-", dir=TEST_ROOT))
        path = directory / "owned.glb"
        path.write_bytes(b"strict-raw-admitted-fixture")
        with mock.patch.object(admission, "verify_sources"), mock.patch.object(exporter, "_pinned_import_sdk", return_value=(Importer, sdk, None)), \
                mock.patch.object(exporter, "FixedDeliveredNormalImport", return_value=extension), mock.patch("builtins.print"), \
                self.assertRaisesRegex(ValueError, "original-sdk-create"):
            exporter.import_visual_or_collision(bpy, admitted, admission, "visual", path, {"observedVertexCount": 3, "observedTriangleCount": 1})
        flushed.assert_called_once()
        self.assertFalse(extension.require_complete.called)


class DeliveredNormalImportTests(unittest.TestCase):
    def fixture(self, indexed=True, second_primitive=True):
        class Rows(list):
            def copy(self):
                return Rows([list(row) for row in self])
        def f32(value):
            return struct.unpack("<f", struct.pack("<f", value))[0]
        positions = Rows([[0., 0., 0.], [1., 0., 0.], [0., 1., 0.]])
        normals = Rows([[0., f32(value), 1.] for value in (.00002, .00003, .00004)])
        arrays = {0: positions, 1: normals, 2: Rows([[2], [0], [1]]), 3: Rows([[1], [2], [0]])}
        class Binary:
            @staticmethod
            def decode_accessor(gltf, index, cache=False):
                if cache is not False:
                    raise AssertionError("exact decoder must not mutate SDK cached arrays")
                return arrays[index]
        primitives = [SimpleNamespace(mode=4, attributes={"POSITION": 0, "NORMAL": 1}, indices=2 if indexed else None, material=0)]
        if second_primitive:
            primitives.append(SimpleNamespace(mode=4, attributes={"POSITION": 0, "NORMAL": 1}, indices=3, material=1))
        pymesh = SimpleNamespace(primitives=primitives)
        data = SimpleNamespace(meshes=[pymesh], nodes=[SimpleNamespace(mesh=0, extras={"bubo_id": "delivered-mesh"})],
                               accessors=[SimpleNamespace(count=3) for index in range(4)],
                               materials=[SimpleNamespace(extras={"bubo_id": name}) for name in ("masonry", "plaster")])
        def convert(rows):
            for row in rows:
                row[1], row[2] = -row[2], row[1]
        gltf = SimpleNamespace(data=data, locs_batch_gltf_to_blender=convert, normals_batch_gltf_to_blender=convert)
        admitted = SimpleNamespace(common=common, geometry_work=[0], scene={},
            job={"limits": {"maxVertices": 100, "maxTriangles": 100, "maxMeshes": 10, "maxPrimitives": 10, "maxGeometryChecks": 10000}},
            plan={"numerics": {"angularToleranceDegrees": .01}})
        hook = exporter.FixedDeliveredNormalImport(admission, admitted, Binary, "a"*64)
        gltf.import_settings = exporter._fixed_import_settings(hook)
        gltf.import_user_extensions = [hook]
        _, item, mesh = TransientFlatNormalTests().mesh_fixture()
        ids = ([2, 0, 1] if indexed else [0, 1, 2]) + ([4, 5, 3] if second_primitive else [])
        expected_positions = [(0., 0., 0.), (1., 0., 0.), (0., 0., 1.)] * (2 if second_primitive else 1)
        mesh.vertices = [SimpleNamespace(co=position) for position in expected_positions]
        mesh.loops = [SimpleNamespace(vertex_index=index) for index in ids]
        mesh.polygons = [SimpleNamespace(vertices=tuple(ids[index:index+3]), loop_indices=(index, index+1, index+2), material_index=index//3, use_smooth=False)
                         for index in range(0, len(ids), 3)]
        mesh.materials = [{"bubo_id": name} for name in ("masonry", "plaster")[:len(mesh.polygons)]]
        mesh.corner_normals = [SimpleNamespace(vector=(0., -1., 0.)) for index in ids]
        raw = {"rawCounts": {"meshes": 1}}
        hook.bind(gltf, raw)
        return hook, gltf, pymesh, mesh, arrays, admitted

    def test_exact_delivered_vectors_expand_by_indices_across_material_primitives_without_ideal_recomputation(self):
        hook, gltf, pymesh, mesh, arrays, admitted = self.fixture()
        originals = copy.deepcopy(arrays)
        with mock.patch.object(exporter, "_unit_triangle_normal", side_effect=AssertionError("ideal/source normal must not be used")):
            hook.gather_import_mesh_after_hook(pymesh, mesh, gltf)
        expected = [(0., -1., float(arrays[1][index][1])) for index in (2, 0, 1, 1, 2, 0)]
        self.assertEqual([tuple(corner.vector) for corner in mesh.corner_normals], expected)
        self.assertTrue(all(vector != (0., -1., 0.) for vector in expected))
        self.assertEqual(arrays, originals)
        with mock.patch("builtins.print"):
            diagnostic = hook.require_complete()
        self.assertEqual((diagnostic["meshes"], diagnostic["primitives"], diagnostic["vertices"], diagnostic["corners"]), (1, 2, 6, 6))
        self.assertEqual(diagnostic["rawGlbSha256"], "a"*64)
        self.assertEqual(diagnostic["importShading"], "FLAT")
        self.assertFalse(diagnostic["sourceDerivedNormalValues"])
        self.assertGreater(diagnostic["maxPreexistingGeneratedToDeliveredAngleDegrees"], 0)

    def test_implicit_triangle_indices_use_delivered_normal_order(self):
        hook, gltf, pymesh, mesh, arrays, admitted = self.fixture(indexed=False, second_primitive=False)
        hook.gather_import_mesh_after_hook(pymesh, mesh, gltf)
        self.assertEqual([corner.vector for corner in mesh.corner_normals], [(0., -1., float(row[1])) for row in arrays[1]])

    def test_position_index_face_order_and_material_mutations_fail_before_any_normal_write(self):
        mutations = (lambda mesh: setattr(mesh.vertices[0], "co", (.1, 0., 0.)),
                     lambda mesh: setattr(mesh.loops[0], "vertex_index", 0),
                     lambda mesh: setattr(mesh.polygons[0], "loop_indices", (1, 2, 0)),
                     lambda mesh: mesh.materials[0].update(bubo_id="wrong"))
        for mutate in mutations:
            hook, gltf, pymesh, mesh, arrays, admitted = self.fixture()
            mutate(mesh)
            before = [corner.vector for corner in mesh.corner_normals]
            with self.subTest(mutation=mutate), self.assertRaises(ValueError):
                hook.gather_import_mesh_after_hook(pymesh, mesh, gltf)
            self.assertFalse(mesh.has_custom_normals)
            self.assertEqual([corner.vector for corner in mesh.corner_normals], before)
            with self.assertRaises(ValueError):
                hook.require_complete()

    def test_swallowed_sdk_hook_exception_missing_duplicate_and_foreign_calls_are_not_accepted(self):
        hook, gltf, pymesh, mesh, arrays, admitted = self.fixture()
        with self.assertRaises(ValueError):
            hook.require_complete()
        hook.gather_import_mesh_after_hook(pymesh, mesh, gltf)
        with self.assertRaises(ValueError):
            hook.gather_import_mesh_after_hook(pymesh, mesh, gltf)
        with self.assertRaises(ValueError):
            hook.require_complete()
        hook, gltf, pymesh, mesh, arrays, admitted = self.fixture()
        try:
            hook.gather_import_mesh_after_hook(SimpleNamespace(), mesh, gltf)
        except ValueError:
            pass  # Exactly the pinned SDK dispatcher's swallowed-exception behavior.
        with self.assertRaises(ValueError):
            hook.require_complete()

    def test_existing_custom_attribute_and_altered_float_storage_fail_closed(self):
        hook, gltf, pymesh, mesh, arrays, admitted = self.fixture()
        mesh.attributes.new(name="custom_normal", type="FLOAT_VECTOR", domain="CORNER")
        with self.assertRaises(ValueError):
            hook.gather_import_mesh_after_hook(pymesh, mesh, gltf)
        hook, gltf, pymesh, mesh, arrays, admitted = self.fixture()
        create = mesh.attributes.new
        def new(**kwargs):
            attribute = create(**kwargs)
            attribute.data.foreach_set = lambda key, values: None
            return attribute
        mesh.attributes.new = new
        with self.assertRaisesRegex(ValueError, "delivered accessor components"):
            hook.gather_import_mesh_after_hook(pymesh, mesh, gltf)

    def test_corner_expansion_and_decode_work_are_cumulative_and_never_raise_caps(self):
        hook, gltf, pymesh, mesh, arrays, admitted = self.fixture()
        admitted.job["limits"]["maxGeometryChecks"] = admitted.geometry_work[0] + 2
        with self.assertRaises(ValueError):
            hook.gather_import_mesh_after_hook(pymesh, mesh, gltf)
        self.assertFalse(mesh.has_custom_normals)
        self.assertLessEqual(admitted.geometry_work[0], admitted.job["limits"]["maxGeometryChecks"])
        hook, gltf, pymesh, mesh, arrays, admitted = self.fixture()
        admitted.job["limits"]["maxTriangles"] = 1
        with self.assertRaises(ValueError):
            hook.gather_import_mesh_after_hook(pymesh, mesh, gltf)
        self.assertFalse(mesh.has_custom_normals)

    def test_import_settings_and_extension_list_cannot_select_other_hooks_or_normal_modes(self):
        for mutate in (lambda gltf: setattr(gltf, "import_user_extensions", [object()]),
                       lambda gltf: gltf.import_settings.update(import_shading="NORMALS"),
                       lambda gltf: gltf.import_settings.update(merge_vertices=True)):
            hook, gltf, pymesh, mesh, arrays, admitted = self.fixture()
            mutate(gltf)
            with self.subTest(mutation=mutate), self.assertRaises(ValueError):
                hook.bind(gltf, {"rawCounts": {"meshes": 1}})

    def test_unbound_hook_cannot_report_success(self):
        hook, gltf, pymesh, mesh, arrays, admitted = self.fixture()
        unbound = exporter.FixedDeliveredNormalImport(admission, admitted, hook.binary_data, "a"*64)
        with self.assertRaises(ValueError):
            unbound.require_complete()

    def test_sorting_compaction_reservation_rejects_before_sorted_work_or_normal_write(self):
        hook, gltf, pymesh, mesh, arrays, admitted = self.fixture(second_primitive=False)
        # bind2 + three decoded arrays9 + corners3 leaves exactly14 used;
        # the three-element sort/remap needs nine additional charged units.
        admitted.job["limits"]["maxGeometryChecks"] = 14
        with mock.patch("builtins.sorted", side_effect=AssertionError("sort must not begin without its reservation")):
            with self.assertRaises(ValueError):
                hook.gather_import_mesh_after_hook(pymesh, mesh, gltf)
        self.assertEqual(admitted.geometry_work[0], 14)
        self.assertFalse(mesh.has_custom_normals)


class PinnedSdkNamespaceTests(unittest.TestCase):
    @contextmanager
    def fixture(self):
        directory = Path(tempfile.mkdtemp(prefix="sdk-namespace-", dir=TEST_ROOT))
        root = directory / "io_scene_gltf2"
        namespace = root / "io/com"
        namespace.mkdir(parents=True)
        (root / "__init__.py").write_bytes(b"# Trusted project-local origin fixture.\n")
        (namespace / "payload.py").write_bytes(b"# Pinned descendant; never imported.\n")
        foreign = directory / "foreign"
        foreign.mkdir()
        (foreign / "payload.py").write_bytes(b"# Foreign origin counterexample.\n")
        files = [{"path": "__init__.py"}, {"path": "io/com/payload.py"}]
        admitted = SimpleNamespace(common=common, job={"toolchain": {"moduleRoot": str(root), "files": files}})
        with mock.patch.dict(sys.modules), mock.patch.object(sys, "path", [str(directory), *sys.path]):
            for name in list(sys.modules):
                if name == "io_scene_gltf2" or name.startswith("io_scene_gltf2."):
                    del sys.modules[name]
            importlib.invalidate_caches()
            module = importlib.import_module("io_scene_gltf2.io.com")
            yield admitted, module, root, foreign
        importlib.invalidate_caches()

    def test_real_stdlib_namespace_packages_require_single_pinned_selected_directory(self):
        with self.fixture() as (admitted, module, root, foreign):
            self.assertIsNone(module.__file__)
            self.assertIs(type(module.__spec__.loader), importlib.machinery.NamespaceLoader)
            self.assertEqual(list(module.__path__), [str(root / "io/com")])
            exporter._require_loaded_sdk_origins(admitted)

    def test_empty_file_nonnamespace_loader_inconsistent_spec_and_mixed_paths_are_rejected(self):
        mutations = (lambda module, root, foreign: setattr(module, "__file__", ""),
                     lambda module, root, foreign: setattr(module.__spec__, "loader", object()),
                     lambda module, root, foreign: setattr(module.__spec__, "name", "io_scene_gltf2.other"),
                     lambda module, root, foreign: setattr(module.__spec__, "origin", str(foreign / "payload.py")),
                     lambda module, root, foreign: setattr(module, "__path__", [str(root / "io/com"), str(foreign)]),
                     lambda module, root, foreign: setattr(module, "__path__", [str(root / "io/com") + "/../com"]))
        for mutate in mutations:
            with self.fixture() as (admitted, module, root, foreign):
                mutate(module, root, foreign)
                with self.subTest(mutation=mutate), self.assertRaisesRegex(ValueError, "io_scene_gltf2.io.com"):
                    exporter._require_loaded_sdk_origins(admitted)

    def test_outside_or_wrong_in_root_namespace_directory_is_rejected_even_with_one_path(self):
        for target in ("foreign", "wrong-in-root"):
            with self.fixture() as (admitted, module, root, foreign):
                selected = foreign if target == "foreign" else root / "io"
                module.__path__ = [str(selected)]
                module.__spec__.submodule_search_locations = [str(selected)]
                with self.subTest(target=target), self.assertRaisesRegex(ValueError, "io_scene_gltf2.io.com"):
                    exporter._require_loaded_sdk_origins(admitted)

    def test_namespace_without_pinned_descendants_is_rejected(self):
        with self.fixture() as (admitted, module, root, foreign):
            (root / "io/unrelated.py").write_bytes(b"# A different pinned branch.\n")
            admitted.job["toolchain"]["files"] = [{"path": "__init__.py"}, {"path": "io/unrelated.py"}]
            with self.assertRaisesRegex(ValueError, "io_scene_gltf2.io.com"):
                exporter._require_loaded_sdk_origins(admitted)

    def test_executable_modules_still_require_matching_pinned_file_and_spec_origin(self):
        for mutate in (lambda module, foreign: setattr(module, "__file__", str(foreign / "payload.py")),
                       lambda module, foreign: setattr(module.__spec__, "origin", str(foreign / "payload.py"))):
            with self.fixture() as (admitted, module, root, foreign):
                executable = sys.modules["io_scene_gltf2"]
                mutate(executable, foreign)
                with self.subTest(mutation=mutate), self.assertRaisesRegex(ValueError, "io_scene_gltf2 rejected"):
                    exporter._require_loaded_sdk_origins(admitted)


if __name__ == "__main__":
    unittest.main()
