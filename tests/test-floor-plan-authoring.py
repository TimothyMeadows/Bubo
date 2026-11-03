"""Real data/path/byte guards for fixed authoring; this suite never runs Blender."""

from pathlib import Path
import copy
import hashlib
import importlib.util
import json
import os
import struct
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest import mock

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
BUNDLE = ROOT / "resources/floor-plan-to-model/blender"
TEST_ROOT = ROOT / ".bubo/floor-plan-authoring-tests"
TEST_ROOT.mkdir(parents=True, exist_ok=True)


def load(name, path):
    specification = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(specification)
    sys.modules[name] = module
    specification.loader.exec_module(module)
    return module


admission = load("bubo_floor_plan_admission_tests", BUNDLE / "admission.py")
author = load("bubo_floor_plan_author_tests", BUNDLE / "author.py")
review = load("bubo_floor_plan_review_tests", BUNDLE / "review.py")


def point(x, y, z):
    return {"x": x, "y": y, "z": z}


TETRA = [point(0, 0, 0), point(1, 0, 0), point(0, 1, 0), point(0, 0, 1)]
FACES = [0, 2, 1, 0, 1, 3, 0, 3, 2, 1, 2, 3]


class AuthoringAdmissionTests(unittest.TestCase):
    def setUp(self):
        # Retain fixture attempts, especially failed/partial writes. No cleanup
        # command can accidentally traverse a deliberately rejected test link.
        self.directory = Path(tempfile.mkdtemp(prefix="case-", dir=TEST_ROOT))
        self.assertTrue(self.directory.resolve().is_relative_to(TEST_ROOT.resolve()))
        self.workspace = self.directory / "workspace"
        self.attempt = self.workspace / "attempts/one"
        self.attempt.mkdir(parents=True)
        self.job_path = self.attempt / "job.json"
        self.output = self.attempt / "output"
        self.job = {
            "schemaVersion": "bubo-floor-plan-blender-job/v1", "operation": "construct",
            "workspaceRoot": str(self.workspace), "outputDirectory": str(self.output),
            "identities": {"planSha256": "a" * 64, "designFingerprint": "b" * 64, "runtimeSha256": "c" * 64},
            "bundle": {name: hashlib.sha256((BUNDLE / name).read_bytes()).hexdigest() for name in ("author.py", "admission.py")},
            "limits": {"maxTriangles": 100, "maxVertices": 100, "maxObjects": 10, "maxOutputBytes": 8 * 1024 * 1024, "maxJobBytes": 1024 * 1024},
            "scene": {
                "schemaVersion": "bubo-floor-plan-construction-scene/v1", "planId": "qa", "linearTolerance": 0.001,
                "materials": [{"id": "wall", "name": "Declared wall", "color": "#806040", "roughness": 0.7, "metallic": 0, "transmission": 0, "ior": 1.45}],
                "anchors": [],
                "meshes": [{"id": "solid:visual", "semanticId": "source-wall", "storeyId": "ground", "kind": "wall",
                            "representation": "visual", "collisionRole": "none", "materialId": "wall", "parentAnchorId": None,
                            "vertices": copy.deepcopy(TETRA), "triangles": list(FACES), "triangleMaterialIds": None}],
                "metrics": {"meshCount": 1, "vertexCount": 4, "triangleCount": 4, "visualTriangleCount": 4,
                            "collisionTriangleCount": 0, "geometryChecks": 1, "minimum": point(0, 0, 0), "maximum": point(1, 1, 1)},
                "clearance": {"routes": [], "doorSweeps": [], "minimumRouteClearance": 0},
            },
        }

    def write(self, job=None):
        raw = (json.dumps(job if job is not None else self.job, separators=(",", ":"), allow_nan=False) + "\n").encode()
        self.job_path.write_bytes(raw)
        return raw

    def admit(self, job=None):
        job = job if job is not None else self.job
        raw = self.write(job)
        return admission.validate_job(job, self.job_path, len(raw))

    def metrics(self, job=None):
        scene = (job or self.job)["scene"]
        meshes = scene["meshes"]
        vertices = [v for mesh in meshes for v in mesh["vertices"]]
        scene["metrics"] = {
            "meshCount": len(meshes), "vertexCount": len(vertices),
            "triangleCount": sum(len(mesh["triangles"]) // 3 for mesh in meshes),
            "visualTriangleCount": sum(len(mesh["triangles"]) // 3 for mesh in meshes if mesh["representation"] == "visual"),
            "collisionTriangleCount": sum(len(mesh["triangles"]) // 3 for mesh in meshes if mesh["representation"] == "collision"),
            "geometryChecks": 1,
            "minimum": {axis: min(v[axis] for v in vertices) for axis in ("x", "y", "z")},
            "maximum": {axis: max(v[axis] for v in vertices) for axis in ("x", "y", "z")},
        }

    def anchor(self, identifier="hinge", storey="ground"):
        return {"id": identifier, "semanticId": "door", "storeyId": storey, "kind": "door-pivot",
                "position": point(0, 0, 0), "axis": point(0, 0, 1), "rotationRadians": 0}

    def test_valid_native_solid_does_not_create_outputs(self):
        result = self.admit()
        self.assertEqual(result.output, self.output.resolve())
        self.assertEqual(result.mesh_stats["solid:visual"]["componentCount"], 1)
        self.assertAlmostEqual(result.mesh_stats["solid:visual"]["minimumComponentVolume"], 1 / 6)
        self.assertFalse(self.output.exists())

    def test_verified_bootstrap_imports_only_fixed_bundle_without_blender(self):
        raw = self.write()
        module, result, expected, directory = author.bootstrap([str(self.job_path), hashlib.sha256(raw).hexdigest()])
        self.assertEqual(result.job["scene"]["planId"], "qa")
        self.assertEqual(directory, BUNDLE.resolve())
        self.assertEqual(expected, hashlib.sha256(raw).hexdigest())
        self.assertEqual(module.MAX_REPORT_BYTES, 8 * 1024 * 1024)
        self.assertNotIn("bpy", sys.modules)
        self.assertFalse(self.output.exists())

    def test_bootstrap_rejects_job_and_bundle_hash_changes_before_bpy(self):
        raw = self.write()
        with self.assertRaisesRegex(ValueError, "byte identity"):
            author.bootstrap([str(self.job_path), "0" * 64])
        self.job["bundle"]["admission.py"] = "0" * 64
        raw = self.write()
        with self.assertRaisesRegex(ValueError, "bundle changed"):
            author.bootstrap([str(self.job_path), hashlib.sha256(raw).hexdigest()])
        self.assertNotIn("bpy", sys.modules)

    def test_bootstrap_does_not_accept_extra_arguments_or_unknown_bundle(self):
        self.write()
        for args in ([], [str(self.job_path)], [str(self.job_path), "A" * 64], [str(self.job_path), "0" * 64, "callback"]):
            with self.subTest(args=args), self.assertRaises(ValueError):
                author.bootstrap(args)
        self.job["bundle"]["user-script.py"] = "0" * 64
        raw = self.write()
        with self.assertRaisesRegex(ValueError, "exact authoring bundle"):
            author.bootstrap([str(self.job_path), hashlib.sha256(raw).hexdigest()])

    def test_duplicate_nonfinite_and_deep_json_reject(self):
        for raw in (b'{"x":1,"x":2}', b'{"x":{"y":1,"y":2}}', b'{"x":NaN}', b'{"x":Infinity}', b'[' * 65 + b'0' + b']' * 65):
            with self.subTest(raw=raw[:30]), self.assertRaises(ValueError):
                admission.decode_json(raw)
        raw = b'{"text":"braces { and escaped \\\"quotes do not create depth"}'
        self.assertIsInstance(admission.decode_json(raw), dict)

    def test_unknown_fields_are_closed_at_every_consumed_level(self):
        paths = [(), ("limits",), ("identities",), ("bundle",), ("scene",), ("scene", "materials", 0),
                 ("scene", "meshes", 0), ("scene", "meshes", 0, "vertices", 0), ("scene", "metrics"), ("scene", "clearance")]
        for path in paths:
            with self.subTest(path=path):
                job = copy.deepcopy(self.job)
                target = job
                for key in path:
                    target = target[key]
                target["callback"] = "never execute"
                with self.assertRaisesRegex(ValueError, "unknown fields"):
                    self.admit(job)
        self.assertFalse(self.output.exists())

    def test_missing_schema_wrong_operation_and_hash_case_reject(self):
        for key, value in (("schemaVersion", "bubo-floor-plan-blender-job/v2"), ("operation", "export")):
            job = copy.deepcopy(self.job)
            job[key] = value
            with self.subTest(key=key), self.assertRaises(ValueError):
                self.admit(job)
        self.job["identities"]["planSha256"] = "A" * 64
        with self.assertRaisesRegex(ValueError, "lowercase SHA"):
            self.admit()

    def test_finite_integer_and_aggregate_budget_guards(self):
        for key, value in (("maxObjects", True), ("maxVertices", 3), ("maxTriangles", 3),
                           ("maxJobBytes", 16), ("maxOutputBytes", 0), ("maxObjects", admission.MAX_OBJECTS + 1)):
            job = copy.deepcopy(self.job)
            job["limits"][key] = value
            with self.subTest(key=key, value=value), self.assertRaises(ValueError):
                self.admit(job)
        self.job["scene"]["anchors"] = [self.anchor()]
        self.job["limits"]["maxObjects"] = 1
        with self.assertRaisesRegex(ValueError, "mesh and anchor count"):
            self.admit()

    def test_no_hidden_nonfinite_boolean_or_missing_point_coordinate(self):
        for value in (True, float("nan"), float("inf"), admission.MAX_COORDINATE + 1):
            job = copy.deepcopy(self.job)
            job["scene"]["meshes"][0]["vertices"][0]["x"] = value
            with self.subTest(value=value), self.assertRaises(ValueError):
                admission.validate_job(job, self.write_and_path(), 4096)
        del self.job["scene"]["meshes"][0]["vertices"][0]["z"]
        with self.assertRaisesRegex(ValueError, "unknown fields"):
            self.admit()

    def write_and_path(self):
        self.write()
        return self.job_path

    def test_bad_triangle_indices_and_open_or_reversed_faces_reject(self):
        mutations = [([-1] + FACES[1:]), ([True] + FACES[1:]), ([4] + FACES[1:]),
                     FACES[:-1], FACES[:-3], [0, 1, 2] + FACES[3:], FACES + FACES[:3],
                     [value for offset in range(0, len(FACES), 3) for value in reversed(FACES[offset:offset + 3])]]
        for indices in mutations:
            with self.subTest(indices=indices):
                job = copy.deepcopy(self.job)
                job["scene"]["meshes"][0]["triangles"] = indices
                with self.assertRaises(ValueError):
                    self.admit(job)

    def test_unreferenced_and_collapsed_vertices_reject(self):
        self.job["scene"]["meshes"][0]["vertices"].append(point(9, 9, 9))
        with self.assertRaisesRegex(ValueError, "unreferenced"):
            self.admit()
        self.job["scene"]["meshes"][0]["vertices"] = [point(v["x"], v["y"], 0) for v in TETRA]
        with self.assertRaises(ValueError):
            self.admit()

    def test_touching_but_disconnected_index_shells_are_retained(self):
        mesh = self.job["scene"]["meshes"][0]
        mesh["vertices"] += [point(v["x"] + 1, v["y"], v["z"]) for v in TETRA]
        mesh["triangles"] += [value + 4 for value in FACES]
        self.metrics()
        result = self.admit()
        self.assertEqual(result.mesh_stats[mesh["id"]]["componentCount"], 2)

    def test_two_closed_shells_sharing_one_index_vertex_are_not_manifold(self):
        vertices = [(v["x"], v["y"], v["z"]) for v in TETRA] + [(-1, 0, 0), (0, -1, 0), (0, 0, -1)]
        lookup = {0: 0, 1: 4, 2: 5, 3: 6}
        second = [lookup[value] for offset in range(0, len(FACES), 3) for value in reversed(FACES[offset:offset + 3])]
        with self.assertRaisesRegex(ValueError, "vertex fan"):
            admission.inspect_triangle_solid(vertices, FACES + second)

    def test_material_assignments_are_exact_not_silently_replaced(self):
        for override in (["wall"], ["unknown"] * 4, [["wall"]] * 4):
            job = copy.deepcopy(self.job)
            job["scene"]["meshes"][0]["triangleMaterialIds"] = override
            with self.subTest(override=override), self.assertRaises(ValueError):
                self.admit(job)
        self.job["scene"]["meshes"][0]["triangleMaterialIds"] = ["wall"] * 4
        self.assertEqual(self.admit().mesh_stats["solid:visual"]["materialAssignmentsSha256"], admission.material_assignments_sha256(["wall"] * 4))

    def test_material_values_and_identity_duplicates_reject(self):
        for key, value in (("color", "url(texture)"), ("roughness", -0.1), ("metallic", True), ("transmission", 2), ("ior", 0.5)):
            job = copy.deepcopy(self.job)
            job["scene"]["materials"][0][key] = value
            with self.subTest(key=key), self.assertRaises(ValueError):
                self.admit(job)
        self.job["scene"]["materials"].append(copy.deepcopy(self.job["scene"]["materials"][0]))
        with self.assertRaisesRegex(ValueError, "duplicate material"):
            self.admit()

    def test_unused_material_palette_is_independent_of_mesh_object_budget(self):
        self.job["limits"]["maxObjects"] = 1
        extra = copy.deepcopy(self.job["scene"]["materials"][0])
        extra["id"], extra["name"] = "unused-palette-entry", "Unused approved palette entry"
        self.job["scene"]["materials"].append(extra)
        self.admit()
        palette = []
        for index in range(admission.MAX_MATERIALS + 1):
            material = copy.deepcopy(extra)
            material["id"] = "palette-" + str(index)
            palette.append(material)
        palette[0] = copy.deepcopy(self.job["scene"]["materials"][0])
        self.job["scene"]["materials"] = palette[:-1]
        self.admit()
        self.job["scene"]["materials"] = palette
        with self.assertRaisesRegex(ValueError, "materials must be a bounded array"):
            self.admit()

    def test_stale_counts_and_bounds_cannot_certify_the_job(self):
        for key in ("meshCount", "vertexCount", "triangleCount", "visualTriangleCount", "collisionTriangleCount"):
            job = copy.deepcopy(self.job)
            job["scene"]["metrics"][key] += 1
            with self.subTest(key=key), self.assertRaisesRegex(ValueError, "stale native metric"):
                self.admit(job)
        self.job["scene"]["metrics"]["maximum"]["z"] = 2
        with self.assertRaisesRegex(ValueError, "stale native scene bounds"):
            self.admit()

    def test_parent_anchor_identity_storey_axis_and_zero_rotation(self):
        self.job["scene"]["meshes"][0]["parentAnchorId"] = "hinge"
        with self.assertRaisesRegex(ValueError, "declared anchor"):
            self.admit()
        self.job["scene"]["anchors"] = [self.anchor(storey="other")]
        with self.assertRaisesRegex(ValueError, "storey"):
            self.admit()
        self.job["scene"]["anchors"][0] = self.anchor()
        self.admit()
        for key, value in (("axis", point(1, 0, 0)), ("rotationRadians", 1e-10)):
            job = copy.deepcopy(self.job)
            job["scene"]["anchors"][0][key] = value
            with self.subTest(key=key), self.assertRaisesRegex(ValueError, "zero rotation"):
                self.admit(job)

    def test_duplicate_mesh_anchor_and_missing_collision_role_reject(self):
        job = copy.deepcopy(self.job)
        job["scene"]["meshes"].append(copy.deepcopy(job["scene"]["meshes"][0]))
        with self.assertRaisesRegex(ValueError, "duplicate mesh"):
            self.admit(job)
        self.job["scene"]["anchors"] = [self.anchor(identifier="solid:visual")]
        with self.assertRaisesRegex(ValueError, "duplicate mesh/anchor"):
            self.admit()
        self.job["scene"]["anchors"] = []
        self.job["scene"]["meshes"][0]["representation"] = "collision"
        with self.assertRaisesRegex(ValueError, "static or dynamic"):
            self.admit()

    def test_route_and_door_data_are_closed_finite_and_bound_to_anchors(self):
        self.job["scene"]["anchors"] = [self.anchor()]
        self.job["scene"]["clearance"] = {
            "routes": [{"roomId": "room", "fromId": "entry", "toId": "exit", "points": [point(0, 0, 0), point(1, 0, 0)], "radius": 0.3, "height": 1.8, "minimumClearance": 0.1}],
            "doorSweeps": [{"openingId": "door", "anchorId": "hinge", "angleDegrees": 90, "minimumClearance": 0,
                            "boundsMinimum": point(0, 0, 0), "boundsMaximum": point(1, 1, 2)}], "minimumRouteClearance": 0.1,
        }
        self.admit()
        self.job["scene"]["clearance"]["doorSweeps"][0]["angleDegrees"] = -90
        self.admit()
        job = copy.deepcopy(self.job)
        job["scene"]["clearance"]["doorSweeps"][0]["anchorId"] = "missing"
        with self.assertRaisesRegex(ValueError, "unknown anchor"):
            self.admit(job)
        job = copy.deepcopy(self.job)
        job["scene"]["clearance"]["routes"][0]["callback"] = "not code"
        with self.assertRaisesRegex(ValueError, "unknown fields"):
            self.admit(job)

    def test_output_escape_alias_existing_directory_and_nested_git_reject(self):
        candidates = [str(self.directory / "outside"), str(self.attempt / ".." / "output"),
                      str(self.attempt / "output."), str(self.workspace / ".git/output"), "relative/output"]
        for value in candidates:
            job = copy.deepcopy(self.job)
            job["outputDirectory"] = value
            with self.subTest(value=value), self.assertRaises(ValueError):
                self.admit(job)
        self.output.mkdir()
        sentinel = self.output / "foreign.txt"
        sentinel.write_bytes(b"preserve")
        with self.assertRaisesRegex(ValueError, "already exists"):
            self.admit()
        self.assertEqual(sentinel.read_bytes(), b"preserve")

    def test_job_must_be_nested_and_inside_the_declared_workspace(self):
        raw = self.write()
        for path in (self.workspace / "job.json", self.directory / "job.json"):
            path.write_bytes(raw)
            with self.subTest(path=path), self.assertRaisesRegex(ValueError, "owned nested"):
                admission.validate_job(self.job, path, len(raw))

    def test_simulated_windows_reparse_attribute_is_rejected_before_resolution(self):
        self.write()
        real_lstat = Path.lstat
        protected = self.attempt

        def inspect(path, *args, **kwargs):
            result = real_lstat(path, *args, **kwargs)
            if path == protected:
                return type("ReparseFixture", (), {"st_mode": result.st_mode, "st_file_attributes": 0x400})()
            return result

        with mock.patch.object(Path, "lstat", inspect):
            with self.assertRaisesRegex(ValueError, "reparse"):
                admission.safe_absolute(str(self.job_path), "simulated reparse fixture")

    def test_real_symbolic_link_is_rejected_when_platform_allows_creation(self):
        self.write()
        link = self.workspace / "linked-attempt"
        try:
            link.symlink_to(self.attempt, target_is_directory=True)
        except (OSError, NotImplementedError):
            self.skipTest("Native symlink creation unavailable; no real-link claim. Simulated reparse case is separate.")
        with self.assertRaisesRegex(ValueError, "link or reparse"):
            admission.safe_absolute(str(link / "job.json"), "real owned test link")

    def test_changed_job_and_bundle_cannot_survive_freshness_check(self):
        raw = self.write()
        result = admission.validate_job(self.job, self.job_path, len(raw))
        self.job_path.write_bytes(raw + b" ")
        with self.assertRaisesRegex(ValueError, "job bytes changed"):
            admission.verify_current_sources(result, hashlib.sha256(raw).hexdigest(), BUNDLE)
        self.job_path.write_bytes(raw)
        result.job["bundle"]["admission.py"] = "0" * 64
        with self.assertRaisesRegex(ValueError, "bundle changed"):
            admission.verify_current_sources(result, hashlib.sha256(raw).hexdigest(), BUNDLE)

    def test_exclusive_creation_never_reuses_an_output(self):
        result = self.admit()
        self.assertEqual(admission.create_output(result), self.output.resolve())
        sentinel = self.output / "sentinel"
        sentinel.write_bytes(b"owned attempt")
        with self.assertRaisesRegex(ValueError, "appeared"):
            admission.create_output(result)
        self.assertEqual(sentinel.read_bytes(), b"owned attempt")

    def test_late_output_type_change_is_rejected_without_overwriting_foreign_bytes(self):
        raw = self.write()
        result = admission.validate_job(self.job, self.job_path, len(raw))
        self.output.write_bytes(b"foreign file appeared")
        with self.assertRaisesRegex(ValueError, "identity/type changed"):
            admission.verify_current_sources(result, hashlib.sha256(raw).hexdigest(), BUNDLE)
        self.assertEqual(self.output.read_bytes(), b"foreign file appeared")

    def test_streamed_report_caps_utf8_and_keeps_failure_partial(self):
        self.output.mkdir()
        target = self.output / "construction-report.json"
        with self.assertRaisesRegex(ValueError, "bounded UTF-8"):
            admission.write_report(target, {"text": "\u20ac" * 100}, 100, 1)
        self.assertFalse(target.exists())
        partial = self.output / "construction-report.json.partial"
        self.assertTrue(partial.is_file())
        self.assertLessEqual(partial.stat().st_size, 99)

    def test_report_exact_bytes_and_no_overwrite(self):
        self.output.mkdir()
        target = self.output / "construction-report.json"
        count = admission.write_report(target, {"message": "UTF-8 \u20ac"}, 1000, 10)
        raw = target.read_bytes()
        self.assertEqual(count, len(raw))
        self.assertTrue(raw.endswith(b"\n"))
        self.assertEqual(json.loads(raw), {"message": "UTF-8 \u20ac"})
        with self.assertRaisesRegex(ValueError, "existing report"):
            admission.write_report(target, {"message": "overwrite"}, 1000, 10)
        self.assertEqual(target.read_bytes(), raw)

    def test_report_foreign_partial_is_preserved_and_nan_never_published(self):
        self.output.mkdir()
        target = self.output / "construction-report.json"
        partial = self.output / "construction-report.json.partial"
        partial.write_bytes(b"foreign partial")
        with self.assertRaises(FileExistsError):
            admission.write_report(target, {"x": 1}, 1000, 1)
        self.assertEqual(partial.read_bytes(), b"foreign partial")
        self.assertFalse(target.exists())
        another = self.attempt / "another-output"
        another.mkdir()
        with self.assertRaises(ValueError):
            admission.write_report(another / "construction-report.json", {"x": float("nan")}, 1000, 1)
        self.assertFalse((another / "construction-report.json").exists())

    def test_geometry_and_assignment_hashes_have_portable_explicit_encoding(self):
        positive = admission.geometry_sha256([(0.0, 1.0, 2.0)], [0])
        negative = admission.geometry_sha256([(-0.0, 1.0, 2.0)], [0])
        expected = hashlib.sha256(struct.pack("<dddI", 0.0, 1.0, 2.0, 0)).hexdigest()
        self.assertEqual(positive, negative)
        self.assertEqual(positive, expected)
        self.assertEqual(admission.material_assignments_sha256(["wall"]), hashlib.sha256(struct.pack("<I", 4) + b"wall").hexdigest())

    def review_job(self):
        blend = self.workspace / "authored/building.blend"
        blend.parent.mkdir()
        # Guard-only fixture, explicitly not a real Blender source or render.
        blend.write_bytes(b"not a real blend; admission guard fixture only")
        return {
            "schemaVersion": "bubo-floor-plan-blender-review-job/v1", "workspaceRoot": str(self.workspace),
            "source": str(blend), "sourceSha256": hashlib.sha256(blend.read_bytes()).hexdigest(), "outputDirectory": str(self.output),
            "bundle": {name: hashlib.sha256((BUNDLE / name).read_bytes()).hexdigest() for name in ("review.py", "admission.py")},
            "limits": {"maxObjects": 100, "maxVertices": 1000, "maxTriangles": 1000, "maxOutputBytes": 8 * 1024 * 1024,
                       "maxSourceBytes": 1024 * 1024, "maxJobBytes": 65536, "maxViews": 5, "imageSize": 768},
            "floors": [{"id": "ground", "elevation": 0, "height": 2.8}],
        }

    def test_review_bootstrap_is_source_bound_and_runs_no_renderer(self):
        job = self.review_job()
        raw = self.write(job)
        module, admitted, blend, floors, expected, _ = review.bootstrap([str(self.job_path), hashlib.sha256(raw).hexdigest()])
        self.assertEqual(blend, Path(job["source"]))
        self.assertEqual(floors[0]["id"], "ground")
        self.assertEqual(expected, hashlib.sha256(raw).hexdigest())
        self.assertFalse(admitted.output.exists())
        self.assertNotIn("bpy", sys.modules)
        self.assertEqual(module.MAX_MATERIALS, 4096)

    def test_review_requires_every_floor_view_exact_size_and_source_hash(self):
        base = self.review_job()
        for path, value in ((("limits", "maxViews"), 4), (("limits", "imageSize"), 512),
                            (("limits", "maxSourceBytes"), 4), (("sourceSha256",), "0" * 64)):
            job = copy.deepcopy(base)
            target = job
            for key in path[:-1]:
                target = target[key]
            target[path[-1]] = value
            raw = self.write(job)
            with self.subTest(path=path), self.assertRaises(ValueError):
                review.validate_job(admission, job, self.job_path, len(raw))
        duplicate = copy.deepcopy(base)
        duplicate["floors"].append(copy.deepcopy(duplicate["floors"][0]))
        raw = self.write(duplicate)
        with self.assertRaisesRegex(ValueError, "duplicate review floor"):
            review.validate_job(admission, duplicate, self.job_path, len(raw))

    def test_review_refuses_unbound_resources_and_changed_source_without_writes(self):
        job = self.review_job()
        raw = self.write(job)
        admitted, blend, _ = review.validate_job(admission, job, self.job_path, len(raw))
        expected = hashlib.sha256(raw).hexdigest()
        blend.write_bytes(b"changed local guard fixture")
        with self.assertRaisesRegex(ValueError, "review source changed"):
            review.verify_sources(admission, admitted, blend, expected, BUNDLE)
        self.assertFalse(self.output.exists())
        job["bundle"]["review.py"] = "0" * 64
        raw = self.write(job)
        with self.assertRaisesRegex(ValueError, "review resource changed"):
            review.bootstrap([str(self.job_path), hashlib.sha256(raw).hexdigest()])

    @staticmethod
    def source_scene_stub():
        # Pure guard fixture: this is deliberately not bpy or a Blender scene.
        return SimpleNamespace(use_nodes=False, node_tree=None, sequence_editor=None, animation_data=None,
                               render=SimpleNamespace(use_compositing=True, use_sequencer=True, use_multiview=True,
                                                      use_border=True, use_crop_to_border=True, use_freestyle=True))

    def test_review_pure_scene_guard_rejects_inherited_compositor_and_sequences(self):
        scene = self.source_scene_stub()
        review.require_source_scene_policy(scene)
        variants = [
            ("use_nodes", True, "compositor use"),
            # A disabled graph can still retain a File Output's unrelated path.
            ("node_tree", SimpleNamespace(nodes=[SimpleNamespace(bl_idname="CompositorNodeOutputFile", base_path="outside/owned-output")], links=[]), "nonempty compositor"),
            ("node_tree", SimpleNamespace(nodes=[], links=[object()]), "nonempty compositor"),
            ("compositing_node_group", object(), "compositor node group"),
            ("sequence_editor", SimpleNamespace(strips=[]), "sequence editor/strips"),
            ("sequence_editor", SimpleNamespace(strips=[SimpleNamespace(filepath="outside/input.mp4")]), "sequence editor/strips"),
            ("animation_data", object(), "scene animation"),
        ]
        for name, value, message in variants:
            candidate = self.source_scene_stub()
            setattr(candidate, name, value)
            with self.subTest(feature=name, message=message), self.assertRaisesRegex(ValueError, message):
                review.require_source_scene_policy(candidate)
            # The guard must reject the source before changing render settings.
            self.assertTrue(candidate.render.use_compositing)
            self.assertTrue(candidate.render.use_sequencer)
            self.assertTrue(candidate.render.use_multiview)
        self.assertNotIn("bpy", sys.modules)

    def test_review_pure_render_policy_bounds_inherited_multiview(self):
        scene = self.source_scene_stub()
        with self.assertRaisesRegex(ValueError, "policy changed"):
            review.fixed_render_policy(scene)
        policy = review.fixed_render_policy(scene, apply=True)
        self.assertEqual((scene.render.use_compositing, scene.render.use_sequencer, scene.render.use_multiview,
                          scene.render.use_border, scene.render.use_crop_to_border, scene.render.use_freestyle), (False,) * 6)
        self.assertEqual(policy["useMultiview"], False)
        self.assertEqual(policy["sourceFeaturePolicy"]["nonemptyCompositorGraph"], "rejected")
        for name in ("use_compositing", "use_sequencer", "use_multiview", "use_border", "use_crop_to_border", "use_freestyle"):
            setattr(scene.render, name, True)
            with self.subTest(flag=name), self.assertRaisesRegex(ValueError, "policy changed"):
                review.fixed_render_policy(scene)
            setattr(scene.render, name, False)
        self.assertEqual(review.fixed_render_policy(scene), policy)
        self.assertNotIn("bpy", sys.modules)

    def test_review_pure_data_guard_rejects_shape_keys_and_animated_data(self):
        def source_data():
            return SimpleNamespace(scenes=[self.source_scene_stub()],
                                   meshes=[SimpleNamespace(shape_keys=None, animation_data=None)],
                                   materials=[SimpleNamespace(animation_data=None, node_tree=None)],
                                   worlds=[SimpleNamespace(animation_data=None, node_tree=None)])

        review.require_source_scope(source_data())
        data = source_data()
        data.meshes[0].shape_keys = SimpleNamespace(key_blocks=["Basis", "Changed geometry"])
        with self.assertRaisesRegex(ValueError, "shape keys"):
            review.require_source_scope(data)
        for collection in ("scenes", "meshes", "materials", "worlds"):
            data = source_data()
            getattr(data, collection)[0].animation_data = object()
            with self.subTest(data=collection), self.assertRaisesRegex(ValueError, "animation"):
                review.require_source_scope(data)
        for collection in ("scenes", "materials", "worlds"):
            data = source_data()
            getattr(data, collection)[0].node_tree = SimpleNamespace(nodes=[], links=[], animation_data=object())
            with self.subTest(nodeTree=collection), self.assertRaisesRegex(ValueError, "shader/compositor animation"):
                review.require_source_scope(data)
        self.assertNotIn("bpy", sys.modules)


if __name__ == "__main__":
    suite = unittest.defaultTestLoader.loadTestsFromTestCase(AuthoringAdmissionTests)
    result = unittest.TextTestRunner(verbosity=2).run(suite)
    print(json.dumps({"status": "passed" if result.wasSuccessful() else "failed", "tests": result.testsRun,
                      "skipped": len(result.skipped), "blenderExecuted": False,
                      "scope": "Real pure admission/path/hash/report guards; simulated reparse marked separately; no Blender construction/visual/human/runtime acceptance."}))
    sys.exit(0 if result.wasSuccessful() else 1)
