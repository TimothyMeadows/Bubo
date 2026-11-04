"""Offline data/path/hash admission fixtures; these never construct Blender geometry.

The JSON identities and approval-independent design are synthetic engineering
fixtures. Passing these checks supplies no Blender, visual or human acceptance.
"""

from pathlib import Path
import copy
import hashlib
import importlib.util
import json
import math
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest import mock

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
BUNDLE = ROOT / "resources/schematic-to-model/blender"
TEST_ROOT = ROOT / ".bubo/schematic-parts-admission-tests"
FIXED_BUNDLE = ("parts_admission.py", "parts_author.py", "parts_geometry.py", "parts_verify.py")


def load_admission():
    specification = importlib.util.spec_from_file_location("bubo_schematic_parts_admission_tests", BUNDLE / "parts_admission.py")
    module = importlib.util.module_from_spec(specification)
    sys.modules[specification.name] = module
    specification.loader.exec_module(module)
    return module


def point(x=0, y=0, z=0):
    return {"x": x, "y": y, "z": z}


def frame(x=0, y=0, z=0):
    return {"origin": point(x, y, z), "rotationDegrees": point()}


def profile():
    return [{"x": 0, "y": 0}, {"x": 0.5, "y": 0}, {"x": 0.5, "y": 0.5}, {"x": 0, "y": 0.5}]


def feature(kind="box"):
    result = {"id": "solid", "kind": kind, "operation": "add", "frame": frame(),
              "parameters": {}, "profile": [], "path": [], "sections": []}
    if kind == "box":
        result["parameters"] = {"width": 1, "depth": 1, "height": 1}
    elif kind == "cylinder":
        result["parameters"] = {"radius": 0.25, "length": 1, "segments": 16}
        result["frame"] = frame(0.5, 0.5)
    elif kind == "extrude":
        result["parameters"] = {"depth": 1}
        result["profile"] = profile()
    elif kind == "revolve":
        result["parameters"] = {"angleDegrees": 360, "segments": 16}
        result["frame"] = frame(0.5, 0.5)
        result["profile"] = [{"x": 0.1, "y": 0}, {"x": 0.3, "y": 0},
                             {"x": 0.3, "y": 0.5}, {"x": 0.1, "y": 0.5}]
    elif kind == "sweep":
        result["parameters"] = {"radius": 0.1, "segments": 16}
        result["path"] = [point(0.5, 0.5, 0.2), point(0.5, 0.5, 0.8)]
    elif kind == "loft":
        result["sections"] = [{"frame": frame(0.2, 0.2, 0.1), "profile": profile()},
                              {"frame": frame(0.2, 0.2, 0.8), "profile": profile()}]
    return result


class PartsAdmissionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.admission = load_admission()
        TEST_ROOT.mkdir(parents=True, exist_ok=True)

    def setUp(self):
        # Keep failed attempts and deliberate links under an owned test root.
        self.directory = Path(tempfile.mkdtemp(prefix="case-", dir=TEST_ROOT))
        self.project = self.directory / "project"
        self.attempt = self.project / ".bubo/attempts/one"
        self.attempt.mkdir(parents=True)
        self.job_path = self.attempt / "job.json"
        self.output = self.attempt / "parts"
        self.output.mkdir()
        design = json.loads((ROOT / "resources/schematic-to-model/examples/device.json").read_bytes())
        part = copy.deepcopy(design["parts"][0])
        part.update(id="prototype", size=point(1, 1, 1), features=[feature()], interfaces=[], terminals=[], flexible=None)
        design["parts"] = [part]
        design["instances"] = [{"id": "occurrence", "partId": "prototype", "parentId": "assembly",
                                "frame": frame(), "explodeOffset": point()}]
        for key in ("joints", "mechanisms", "routes", "connections", "sections"):
            design[key] = []
        self.job = {
            "schemaVersion": "bubo-schematic-part-job/v1", "operation": "build-parts",
            "projectRoot": str(self.project), "output": str(self.output), "design": design,
            "partIdentities": {"prototype": "a" * 64},
            "identity": {"designSha256": "b" * 64, "designFingerprint": "c" * 64,
                         "runtimeBuildSha256": "d" * 64, "toolSha256": "e" * 64},
            "bundle": {name: hashlib.sha256((BUNDLE / name).read_bytes()).hexdigest() for name in FIXED_BUNDLE},
            "limits": {"maxVertices": 100000, "maxTriangles": 100000,
                       "maxOutputBytes": 64 * 1024 * 1024, "maxJobBytes": 8 * 1024 * 1024, "maxFiles": 4},
        }

    def write(self, job=None):
        raw = (json.dumps(job if job is not None else self.job, separators=(",", ":"), allow_nan=True) + "\n").encode()
        self.job_path.write_bytes(raw)
        return hashlib.sha256(raw).hexdigest()

    def admit(self, job=None, **kwargs):
        expected = self.write(job)
        return self.admission.admit(self.job_path, expected, bundle_dir=BUNDLE, **kwargs)

    def rejected(self, job=None):
        with self.assertRaises((ValueError, OSError)):
            self.admit(job)
        self.assertEqual(list(self.output.iterdir()), [], "admission must reject before writing payloads")

    def test_valid_job_is_read_only_and_never_imports_blender(self):
        admitted = self.admit()
        self.assertIsNotNone(admitted)
        self.assertEqual(list(self.output.iterdir()), [])
        self.assertNotIn("bpy", sys.modules)

    def test_all_six_bounded_shape_families_are_admitted_as_data(self):
        for kind in ("box", "cylinder", "extrude", "revolve", "sweep", "loft"):
            with self.subTest(kind=kind):
                job = copy.deepcopy(self.job)
                job["design"]["parts"][0]["features"] = [feature(kind)]
                self.admit(job)
        self.assertEqual(list(self.output.iterdir()), [])

    def test_exact_input_and_each_fixed_resource_hash_are_required(self):
        expected = self.write()
        for wrong in ("0" * 64, expected.upper(), "short"):
            with self.subTest(hash=wrong), self.assertRaises(ValueError):
                self.admission.admit(self.job_path, wrong, bundle_dir=BUNDLE)
        for name in FIXED_BUNDLE:
            job = copy.deepcopy(self.job)
            job["bundle"][name] = "0" * 64
            with self.subTest(resource=name):
                self.rejected(job)

    def test_all_declared_connection_kinds_cross_the_worker_data_boundary(self):
        schema = json.loads((ROOT / "resources/schematic-to-model/assembly-design.schema.json").read_bytes())
        kinds = schema["$defs"]["connection"]["properties"]["kind"]["enum"]
        self.assertIn("mechanical", kinds, "hose and spring connections are declared native inputs")
        # This checks the closed transport boundary. Native Domain validation still
        # owns endpoint/route semantics and actual geometric compatibility.
        design = json.loads((ROOT / "resources/schematic-to-model/examples/device.json").read_bytes())
        self.assertTrue(design["connections"])
        for kind in kinds:
            with self.subTest(kind=kind):
                design["connections"][0]["kind"] = kind
                self.admission.validate_non_geometry(design)
        design["connections"][0]["kind"] = "mechanical-callback"
        with self.assertRaises(ValueError):
            self.admission.validate_non_geometry(design)

    def test_part_producer_retains_its_own_prototype_output_file_quota(self):
        job = copy.deepcopy(self.job)
        prototype = copy.deepcopy(job["design"]["parts"][0])
        prototype["id"] = "second-prototype"
        job["design"]["parts"].append(prototype)
        occurrence = copy.deepcopy(job["design"]["instances"][0])
        occurrence.update(id="second-occurrence", partId=prototype["id"])
        job["design"]["instances"].append(occurrence)
        job["partIdentities"][prototype["id"]] = "f" * 64
        self.rejected(job)
        job["limits"]["maxFiles"] = 6
        self.admit(job)

    def test_unknown_missing_and_callback_resources_are_rejected(self):
        for removed in FIXED_BUNDLE:
            job = copy.deepcopy(self.job)
            del job["bundle"][removed]
            with self.subTest(removed=removed):
                self.rejected(job)
        job = copy.deepcopy(self.job)
        job["bundle"]["callback.py"] = "a" * 64
        self.rejected(job)

    def test_closed_consumed_json_objects_reject_unknown_properties(self):
        paths = [(), ("identity",), ("limits",), ("design",), ("design", "numerics"),
                 ("design", "budgets"), ("design", "materials", 0), ("design", "parts", 0),
                 ("design", "parts", 0, "size"), ("design", "parts", 0, "features", 0),
                 ("design", "parts", 0, "features", 0, "frame"),
                 ("design", "parts", 0, "features", 0, "frame", "origin"),
                 ("design", "instances", 0), ("design", "subassemblies", 0)]
        for path in paths:
            job = copy.deepcopy(self.job)
            target = job
            for key in path:
                target = target[key]
            target["callback"] = "fixture must never execute"
            with self.subTest(path=path):
                self.rejected(job)

    def test_duplicate_json_properties_and_nonfinite_tokens_are_rejected(self):
        for raw in (b'{"schemaVersion":"x","schemaVersion":"y"}',
                    b'{"x":{"nested":1,"nested":2}}', b'{"x":NaN}', b'{"x":Infinity}',
                    b'[' * 65 + b'0' + b']' * 65):
            self.job_path.write_bytes(raw)
            with self.subTest(raw=raw[:32]), self.assertRaises(ValueError):
                self.admission.admit(self.job_path, hashlib.sha256(raw).hexdigest(), bundle_dir=BUNDLE)
        self.assertEqual(list(self.output.iterdir()), [])

    def test_schema_operation_and_coordinate_contract_are_exact(self):
        for path, value in ((("schemaVersion",), "bubo-schematic-part-job/v2"),
                            (("operation",), "assemble"), (("design", "coordinateSystem"), "y-up")):
            job = copy.deepcopy(self.job)
            target = job
            for key in path[:-1]:
                target = target[key]
            target[path[-1]] = value
            with self.subTest(path=path):
                self.rejected(job)

    def test_absolute_confined_paths_reject_escape_dot_segments_and_git(self):
        paths = [str(self.directory / "outside"), str(self.attempt / ".." / "parts"),
                 str(self.project / ".git/parts"), "relative/parts"]
        for output in paths:
            job = copy.deepcopy(self.job)
            job["output"] = output
            with self.subTest(output=output):
                self.rejected(job)
        job = copy.deepcopy(self.job)
        job["projectRoot"] = "relative-project"
        self.rejected(job)

    def test_job_must_be_owned_and_absolute(self):
        expected = self.write()
        outside = self.directory / "outside-job.json"
        outside.write_bytes(self.job_path.read_bytes())
        for candidate in (outside, Path("relative-job.json")):
            with self.subTest(path=str(candidate)), self.assertRaises((ValueError, OSError)):
                self.admission.admit(candidate, expected, bundle_dir=BUNDLE)

    def test_existing_output_and_foreign_file_are_never_reused(self):
        sentinel = self.output / "foreign.txt"
        sentinel.write_bytes(b"preserve exact foreign bytes")
        with self.assertRaises(ValueError):
            self.admit()
        self.assertEqual(sentinel.read_bytes(), b"preserve exact foreign bytes")

    def test_native_symbolic_link_is_rejected_when_creation_is_available(self):
        expected = self.write()
        link = self.project / "linked-attempt"
        try:
            link.symlink_to(self.attempt, target_is_directory=True)
        except (OSError, NotImplementedError):
            self.skipTest("Native symlink creation unavailable; no real-link verification claim.")
        with self.assertRaises(ValueError):
            self.admission.admit(link / "job.json", expected, bundle_dir=BUNDLE)
        self.assertEqual(list(self.output.iterdir()), [])

    def test_simulated_windows_reparse_attribute_rejects_before_resolution(self):
        self.write()
        original = Path.lstat

        def inspect(path, *args, **kwargs):
            result = original(path, *args, **kwargs)
            if path == self.attempt:
                return SimpleNamespace(st_mode=result.st_mode, st_file_attributes=0x400)
            return result

        # The attribute is simulated; the separately labeled test above attempts
        # an actual native link and reports unavailable hosts honestly.
        with mock.patch.object(Path, "lstat", inspect), self.assertRaisesRegex(ValueError, "reparse"):
            self.admission.no_links(self.job_path)
        self.assertEqual(list(self.output.iterdir()), [])

    def test_budget_types_caps_and_preallocation_estimates_are_finite(self):
        changes = [("maxVertices", True), ("maxVertices", 1), ("maxVertices", 10 ** 12),
                   ("maxTriangles", 1), ("maxTriangles", 10 ** 12), ("maxOutputBytes", 0),
                   ("maxOutputBytes", 10 ** 18), ("maxJobBytes", 16), ("maxJobBytes", 10 ** 12),
                   ("maxFiles", 1), ("maxFiles", 10 ** 12)]
        for key, value in changes:
            job = copy.deepcopy(self.job)
            job["limits"][key] = value
            with self.subTest(key=key, value=value):
                self.rejected(job)

    def test_source_allocation_and_profile_work_are_cumulatively_bounded(self):
        job = copy.deepcopy(self.job)
        extra = feature()
        extra["id"] = "second-solid"
        job["design"]["parts"][0]["features"].append(extra)
        job["limits"]["maxVertices"] = 8
        self.rejected(job)
        job = copy.deepcopy(self.job)
        job["design"]["parts"][0]["features"] = [feature("extrude")]
        job["design"]["budgets"]["maxValidationChecks"] = 1
        self.rejected(job)

    def test_fixed_argv_accepts_only_job_path_and_sha(self):
        expected = self.write()
        self.assertEqual(self.admission.arguments(["blender", "--", str(self.job_path), expected]),
                         [str(self.job_path), expected])
        for argv in ([], ["--"], ["--", str(self.job_path)],
                     ["--", str(self.job_path), expected, "callback.py"]):
            with self.subTest(argv=argv), self.assertRaises(ValueError):
                self.admission.arguments(argv)

    def test_simulated_runtime_guard_checks_exact_tool_and_lts_version(self):
        # Guard fixture only: these bytes are not an executable or real bpy.
        tool = self.project / "simulated-blender.exe"
        tool.write_bytes(b"synthetic identity fixture; never execute")
        job = copy.deepcopy(self.job)
        job["identity"]["toolSha256"] = hashlib.sha256(tool.read_bytes()).hexdigest()
        runtime = SimpleNamespace(
            app=SimpleNamespace(version=(4, 5, 10), binary_path=str(tool), autoexec_fail=False, online_access=False),
            context=SimpleNamespace(preferences=SimpleNamespace(filepaths=SimpleNamespace(use_scripts_auto_execute=False))))
        self.admission.check_runtime(job, runtime)
        runtime.app.version = (4, 4, 3)
        with self.assertRaises(ValueError):
            self.admission.check_runtime(job, runtime)
        runtime.app.version = (4, 5, 10)
        runtime.app.autoexec_fail = True
        with self.assertRaises(ValueError):
            self.admission.check_runtime(job, runtime)
        runtime.app.autoexec_fail = False
        runtime.context.preferences.filepaths.use_scripts_auto_execute = True
        with self.assertRaises(ValueError):
            self.admission.check_runtime(job, runtime)
        runtime.context.preferences.filepaths.use_scripts_auto_execute = False
        runtime.app.online_access = True
        with self.assertRaises(ValueError):
            self.admission.check_runtime(job, runtime)
        runtime.app.online_access = False
        tool.write_bytes(b"changed synthetic tool fixture")
        with self.assertRaises(ValueError):
            self.admission.check_runtime(job, runtime)
        self.assertNotIn("bpy", sys.modules)

    def test_definition_identities_are_complete_canonical_and_unique(self):
        mappings = [{}, {"prototype": "A" * 64}, {"prototype": "a" * 63},
                    {"prototype": "a" * 64, "unapproved": "b" * 64}]
        for mapping in mappings:
            job = copy.deepcopy(self.job)
            job["partIdentities"] = mapping
            with self.subTest(mapping=mapping):
                self.rejected(job)
        for key in self.job["identity"]:
            job = copy.deepcopy(self.job)
            job["identity"][key] = "A" * 64
            with self.subTest(identity=key):
                self.rejected(job)

    def test_unsupported_feature_operators_and_parameter_names_reject(self):
        for key, value in (("kind", "mesh"), ("kind", "Box"), ("operation", "intersect"),
                           ("parameters", {"width": 1, "depth": 1, "height": 1, "script": 0})):
            job = copy.deepcopy(self.job)
            job["design"]["parts"][0]["features"][0][key] = value
            with self.subTest(key=key, value=value):
                self.rejected(job)

    def test_feature_parameters_reject_boolean_nonfinite_negative_and_huge_segments(self):
        for value in (True, float("nan"), float("inf"), -1, 0):
            job = copy.deepcopy(self.job)
            job["design"]["parts"][0]["features"][0]["parameters"]["width"] = value
            with self.subTest(width=value):
                self.rejected(job)
        for value in (True, 2, 16.5, 10 ** 12):
            job = copy.deepcopy(self.job)
            job["design"]["parts"][0]["features"] = [feature("cylinder")]
            job["design"]["parts"][0]["features"][0]["parameters"]["segments"] = value
            with self.subTest(segments=value):
                self.rejected(job)

    def test_profile_self_intersection_and_repeated_closure_reject(self):
        malformed = [[], [{"x": 0, "y": 0}, {"x": 0.5, "y": 0.5}, {"x": 0, "y": 0.5}, {"x": 0.5, "y": 0}],
                     profile() + [profile()[0]], [{"x": 0, "y": 0}, {"x": 0.5, "y": 0}, {"x": 1, "y": 0}]]
        for points in malformed:
            job = copy.deepcopy(self.job)
            solid = feature("extrude")
            solid["profile"] = points
            job["design"]["parts"][0]["features"] = [solid]
            with self.subTest(profile=points):
                self.rejected(job)

    def test_invalid_sweep_paths_reject_instead_of_creating_a_proxy(self):
        paths = [[point(0.5, 0.5, 0.2)], [point(0.5, 0.5, 0.2), point(0.5, 0.5, 0.2)],
                 [point(0.5, 0.5, 0.2), point(0.5, 0.5, 0.8), point(0.5, 0.5, 0.4)]]
        for path in paths:
            job = copy.deepcopy(self.job)
            solid = feature("sweep")
            solid["path"] = path
            job["design"]["parts"][0]["features"] = [solid]
            with self.subTest(path=path):
                self.rejected(job)

    def test_curved_elbow_helix_and_closed_belt_are_admitted_as_data(self):
        elbow = [point(0.5 + 0.25 * math.cos(math.pi * i / 8),
                       0.5 + 0.25 * math.sin(math.pi * i / 8), 0.5) for i in range(5)]
        helix = [point(0.5 + 0.22 * math.cos(4 * math.pi * i / 64),
                       0.5 + 0.22 * math.sin(4 * math.pi * i / 64), 0.15 + 0.7 * i / 64) for i in range(65)]
        belt = [point(0.5 + 0.3 * math.cos(2 * math.pi * i / 16),
                      0.5 + 0.3 * math.sin(2 * math.pi * i / 16), 0.5) for i in range(16)]
        belt.append(copy.deepcopy(belt[0]))
        for name, path, radius in (("elbow", elbow, 0.03), ("helix", helix, 0.02), ("closed-belt", belt, 0.03)):
            job = copy.deepcopy(self.job)
            solid = feature("sweep")
            solid["path"] = path
            solid["parameters"]["radius"] = radius
            job["design"]["parts"][0]["features"] = [solid]
            with self.subTest(shape=name):
                self.admit(job)
        self.assertEqual(list(self.output.iterdir()), [])

    def test_too_tight_bend_and_nonlocal_tube_crossing_are_rejected_explicitly(self):
        tight = [point(0.5 + 0.03 * math.cos(math.pi * i / 16),
                       0.5 + 0.03 * math.sin(math.pi * i / 16), 0.5) for i in range(9)]
        # Offset samples keep the figure-eight crossing between vertices. Every
        # centreline vertex is distinct, so nonlocal tube clearance owns rejection.
        crossing = [point(0.5 + 0.3 * math.sin(2 * math.pi * (i + 0.25) / 64),
                          0.5 + 0.2 * math.sin(4 * math.pi * (i + 0.25) / 64), 0.5) for i in range(64)]
        crossing.append(copy.deepcopy(crossing[0]))
        for name, path, radius, error in (("tight", tight, 0.08, "bend radius"),
                                          ("crossing", crossing, 0.005, "Nonlocal sweep")):
            job = copy.deepcopy(self.job)
            solid = feature("sweep")
            solid["path"] = path
            solid["parameters"]["radius"] = radius
            job["design"]["parts"][0]["features"] = [solid]
            with self.subTest(shape=name), self.assertRaisesRegex(ValueError, error):
                self.admit(job)
        self.assertEqual(list(self.output.iterdir()), [])

    def test_nonparallel_nonmonotonic_and_mismatched_loft_sections_reject(self):
        cases = []
        tilted = feature("loft")
        tilted["sections"][1]["frame"]["rotationDegrees"]["x"] = 10
        cases.append(tilted)
        coincident = feature("loft")
        coincident["sections"][1]["frame"]["origin"]["z"] = 0.1
        cases.append(coincident)
        mismatched = feature("loft")
        mismatched["sections"][1]["profile"].pop()
        cases.append(mismatched)
        reversing = feature("loft")
        reversing["sections"].append({"frame": frame(0.2, 0.2, 0.4), "profile": profile()})
        cases.append(reversing)
        for index, solid in enumerate(cases):
            job = copy.deepcopy(self.job)
            job["design"]["parts"][0]["features"] = [solid]
            with self.subTest(case=index):
                self.rejected(job)

    def test_duplicate_definitions_instances_and_features_reject(self):
        for key in ("parts", "instances"):
            job = copy.deepcopy(self.job)
            job["design"][key].append(copy.deepcopy(job["design"][key][0]))
            with self.subTest(collection=key):
                self.rejected(job)
        job = copy.deepcopy(self.job)
        job["design"]["parts"][0]["features"].append(copy.deepcopy(job["design"]["parts"][0]["features"][0]))
        self.rejected(job)

    def test_path_shaped_part_identifiers_and_unknown_material_reject(self):
        for identifier in ("..", "../escape", "part/escape"):
            job = copy.deepcopy(self.job)
            job["design"]["parts"][0]["id"] = identifier
            job["design"]["instances"][0]["partId"] = identifier
            job["partIdentities"] = {identifier: "a" * 64}
            with self.subTest(identifier=identifier):
                self.rejected(job)
        job = copy.deepcopy(self.job)
        job["design"]["parts"][0]["materialId"] = "unknown"
        self.rejected(job)

    def test_material_envelope_and_frame_numbers_remain_finite_and_typed(self):
        changes = [(("design", "materials", 0, "color"), "red"),
                   (("design", "materials", 0, "roughness"), 1.1),
                   (("design", "materials", 0, "metallic"), float("nan")),
                   (("design", "parts", 0, "size", "x"), 0),
                   (("design", "parts", 0, "size", "z"), float("inf")),
                   (("design", "parts", 0, "features", 0, "frame", "origin", "x"), True)]
        for path, value in changes:
            job = copy.deepcopy(self.job)
            target = job
            for key in path[:-1]:
                target = target[key]
            target[path[-1]] = value
            with self.subTest(path=path, value=value):
                self.rejected(job)

    def test_repeated_instances_remain_an_exact_map_and_weight_the_budget(self):
        job = copy.deepcopy(self.job)
        second = copy.deepcopy(job["design"]["instances"][0])
        second["id"] = "occurrence-two"
        second["frame"]["origin"]["x"] = 2
        job["design"]["instances"].append(second)
        self.admit(job)
        # One box has at least twelve triangles; its repeated physical map must
        # not pass a fifteen-triangle total allowance by counting one prototype.
        job["limits"]["maxTriangles"] = 15
        self.rejected(job)

    def test_nonlocal_short_u_and_adjacent_ring_foldover_reject(self):
        cases = [([point(1, 1, 1), point(2, 1, 1), point(2, 2, 1), point(1, 2, 1)], .55, "Nonlocal sweep"),
                 ([point(0, 0, 0), point(1, 0, 0), point(1, .01, 0)], .1, "Adjacent sweep rings")]
        for path, radius, message in cases:
            job = copy.deepcopy(self.job)
            solid = feature("sweep")
            solid["path"], solid["parameters"]["radius"] = path, radius
            job["design"]["parts"][0]["features"] = [solid]
            with self.subTest(path=path), self.assertRaisesRegex(ValueError, message):
                self.admit(job)
        self.assertEqual(list(self.output.iterdir()), [])

    def test_native_per_field_budget_caps_match_large_legal_export(self):
        job = copy.deepcopy(self.job)
        job["design"]["budgets"]["maxExportBytes"] = 16 * 1024 ** 3
        job["limits"]["maxOutputBytes"] = 2 * 1024 ** 3
        self.admit(job)
        # A legal tiny output ceiling is admitted as data; actual geometry must
        # then fail its output allowance rather than changing that allowance.
        job["limits"]["maxOutputBytes"] = 1
        self.admit(job)
        for key, maximum in (("maxPartDefinitions", 4096), ("maxInstances", 32768), ("maxFeatures", 131072),
                             ("maxRoutePoints", 262144), ("maxDrawings", 8192), ("maxValidationChecks", 10000000),
                             ("maxPreviewBytes", 67108864), ("maxExportBytes", 17179869184),
                             ("maxGeneratedTriangles", 50000000), ("maxMotionSamples", 1000000)):
            invalid = copy.deepcopy(job)
            invalid["design"]["budgets"][key] = maximum + 1
            with self.subTest(budget=key):
                self.rejected(invalid)

    def test_native_positive_numerics_have_no_invented_absolute_floor(self):
        job = copy.deepcopy(self.job)
        job["design"]["numerics"]["linearTolerance"] = 1e-15
        job["design"]["numerics"]["minimumFeatureSize"] = 1e-8
        job["design"]["numerics"]["angularToleranceDegrees"] = 1e-15
        self.admit(job)

    def test_exact_triangle_predicates_handle_coplanar_and_shared_vertex_overlap(self):
        sys.path.insert(0, str(BUNDLE))
        try:
            import parts_geometry as geometry
            def proof(vertices, triangles, reject):
                worker = geometry.GeometryWork({"design": {"numerics": self.job["design"]["numerics"], "budgets": {"maxValidationChecks": 100000}, "parts": []}})
                if reject:
                    with self.assertRaisesRegex(ValueError, "self-intersection"):
                        geometry.reject_self_intersections(vertices, triangles, worker)
                else:
                    geometry.reject_self_intersections(vertices, triangles, worker)
            # Disjoint-index coplanar overlap and noncoplanar intersection
            # extending away from a legally shared vertex are both rejected.
            proof([(0., 0., 0.), (2., 0., 0.), (0., 2., 0.), (.5, .5, 0.), (1.5, .5, 0.), (.5, 1.5, 0.)], [(0, 1, 2), (3, 4, 5)], True)
            proof([(0., 0., 0.), (2., 0., 0.), (0., 2., 0.), (1., .5, 1.), (1., .5, -1.)], [(0, 1, 2), (0, 3, 4)], True)
            proof([(0., 0., 0.), (2., 0., 0.), (0., 2., 0.), (.5, .5, 0.)], [(0, 1, 2), (1, 0, 3)], True)
            # A normal shared edge or an isolated shared vertex is permitted.
            proof([(0., 0., 0.), (2., 0., 0.), (0., 2., 0.), (0., -1., 0.)], [(0, 1, 2), (1, 0, 3)], False)
            proof([(0., 0., 0.), (2., 0., 0.), (0., 2., 0.), (-1., 0., 0.), (0., -1., 0.)], [(0, 1, 2), (0, 3, 4)], False)
        finally:
            sys.path.pop(0)

    def test_vertex_links_and_separate_material_islands_reject(self):
        sys.path.insert(0, str(BUNDLE))
        try:
            import parts_geometry as geometry
            for offset, reason in (((1, 1, 1), "vertex link"), ((2, 0, 0), "One connected physical solid")):
                verts, triangles, lookup = [], [], {}
                for origin in ((0, 0, 0), offset):
                    solid = feature("box")
                    solid["frame"]["origin"] = point(*origin)
                    points, faces = geometry.feature_mesh(solid)
                    local = []
                    for vertex in points:
                        if vertex not in lookup:
                            lookup[vertex] = len(verts)
                            verts.append(vertex)
                        local.append(lookup[vertex])
                    for face in faces:
                        triangles.extend((local[face[0]], local[face[i]], local[face[i + 1]]) for i in range(1, len(face) - 1))
                with self.subTest(offset=offset), self.assertRaisesRegex(ValueError, reason):
                    geometry.measurements(verts, triangles)
        finally:
            sys.path.pop(0)


if __name__ == "__main__":
    suite = unittest.defaultTestLoader.loadTestsFromTestCase(PartsAdmissionTests)
    result = unittest.TextTestRunner(verbosity=2).run(suite)
    print(json.dumps({"status": "passed" if result.wasSuccessful() else "failed", "tests": result.testsRun,
                      "skipped": len(result.skipped), "blenderExecuted": False,
                      "scope": "Offline admission/path/hash tests with synthetic design identities; no Blender geometry, visual, human or runtime acceptance."}))
    sys.exit(0 if result.wasSuccessful() else 1)
