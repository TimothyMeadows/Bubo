#!/usr/bin/env python3
"""Meaningful data-only malformed GLB/correspondence/resource guard checks."""
import copy
import importlib.util
import importlib.abc
import importlib.machinery
import json
from pathlib import Path
import struct
import sys
import tempfile
import unittest
from unittest import mock
from types import ModuleType, SimpleNamespace

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT/"resources/schematic-to-model/blender"))
import export_geometry as geo
from export_admission import LIMITS, safe_path, import_sdk


class ExportAdmission(unittest.TestCase):
    def setUp(self):
        base = ROOT/".bubo/schematic-export-admission-tests"
        base.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=base)
        assert Path(self.temp.name).resolve().is_relative_to(base.resolve())
        self.path = Path(self.temp.name)/"fixture.glb"
        self.limits = dict(LIMITS)
        self.vertices = [(0., 0., 0.), (1., 0., 0.), (0., 1., 0.)]
        binary = b"".join(struct.pack("<fff", *p) for p in self.vertices)
        binary += struct.pack("<fff", 0, 1, 2)
        binary += struct.pack("<III", 0, 1, 2)
        binary += struct.pack("<fffffffff", 0, 0, 1, 0, 0, 1, 0, 0, 1)
        self.binary = binary
        self.doc = {"asset": {"version": "2.0"}, "scene": 0, "scenes": [{"nodes": [0]}], "nodes": [{"name": "part", "mesh": 0}],
                    "buffers": [{"byteLength": len(binary)}], "bufferViews": [{"buffer": 0, "byteOffset": 0, "byteLength": 36}, {"buffer": 0, "byteOffset": 36, "byteLength": 12}, {"buffer": 0, "byteOffset": 48, "byteLength": 12}, {"buffer": 0, "byteOffset": 60, "byteLength": 36}],
                    "accessors": [{"bufferView": 0, "componentType": 5126, "type": "VEC3", "count": 3}, {"bufferView": 1, "componentType": 5126, "type": "SCALAR", "count": 3}, {"bufferView": 2, "componentType": 5125, "type": "SCALAR", "count": 3}, {"bufferView": 3, "componentType": 5126, "type": "VEC3", "count": 3}],
                    "meshes": [{"primitives": [{"attributes": {"POSITION": 0, "NORMAL": 3, geo.ATTRIBUTE: 1}, "indices": 2}]}]}

    def tearDown(self):
        self.temp.cleanup()

    def load(self, doc=None, binary=None):
        geo.write_glb(self.path, self.doc if doc is None else doc, self.binary if binary is None else binary)
        return geo.read_glb(self.path, self.limits)

    def test_real_split_index_evidence_retains_oriented_multiplicity(self):
        doc, binary = self.load()
        actual = geo.geometry(doc, binary, 0, self.limits)
        expected = [geo.source_point(p) for p in self.vertices]
        geo.compare_geometry(actual, expected, [(0, 1, 2)], 1e-7)
        altered = copy.deepcopy(actual)
        altered["triangles"].append((0, 1, 2))
        with self.assertRaisesRegex(ValueError, "multiplicity"):
            geo.compare_geometry(altered, expected, [(0, 1, 2)], 1e-7)
        altered = copy.deepcopy(actual)
        altered["triangles"] = [(2, 1, 0)]
        with self.assertRaisesRegex(ValueError, "multiplicity"):
            geo.compare_geometry(altered, expected, [(0, 1, 2)], 1e-7)

    def test_ids_never_repair_actual_geometry(self):
        doc, binary = self.load()
        actual = geo.geometry(doc, binary, 0, self.limits)
        actual["vertices"][1] = (100, 0, 0)
        with self.assertRaisesRegex(ValueError, "actual local geometry"):
            geo.compare_geometry(actual, [geo.source_point(p) for p in self.vertices], [(0, 1, 2)], 1e-5)

    def test_unused_raw_vertices_rejected(self):
        binary = self.binary[:48]+struct.pack("<III", 0, 1, 1)+self.binary[60:]
        doc, binary = self.load(binary=binary)
        with self.assertRaisesRegex(ValueError, "unused raw"):
            geo.geometry(doc, binary, 0, self.limits)

    def test_missing_source_correspondence_rejected(self):
        doc = copy.deepcopy(self.doc)
        del doc["meshes"][0]["primitives"][0]["attributes"][geo.ATTRIBUTE]
        doc, binary = self.load(doc)
        with self.assertRaisesRegex(ValueError, "logical correspondence"):
            geo.geometry(doc, binary, 0, self.limits)

    def test_sparse_external_and_compressed_payloads_rejected(self):
        for mutate in (lambda d: d["buffers"][0].update(uri="file:///outside.bin"), lambda d: d.update(extensionsUsed=["KHR_draco_mesh_compression"]), lambda d: d.update(images=[{"uri": "https://example.invalid"}])):
            with self.subTest(mutate=mutate):
                doc = copy.deepcopy(self.doc)
                mutate(doc)
                with self.assertRaises(ValueError):
                    self.load(doc)
        doc, binary = self.load()
        doc["accessors"][0]["sparse"] = {"count": 1}
        with self.assertRaisesRegex(ValueError, "dense"):
            geo.geometry(doc, binary, 0, self.limits)

    def test_accessor_bounds_and_budget_rejected(self):
        doc, binary = self.load()
        doc["accessors"][0]["count"] = 4
        with self.assertRaisesRegex(ValueError, "storage bounds"):
            geo.geometry(doc, binary, 0, self.limits)
        doc["accessors"][0]["count"] = self.limits["maxVertices"]+1
        with self.assertRaisesRegex(ValueError, "accessor count"):
            geo.geometry(doc, binary, 0, self.limits)

    def test_nonunit_normals_and_broken_owned_weights_rejected(self):
        doc, binary = self.load()
        data = bytearray(binary)
        struct.pack_into("<fff", data, 60, 0, 0, 0)
        with self.assertRaisesRegex(ValueError, "surface normals"):
            geo.geometry(doc, data, 0, self.limits)
        doc["meshes"][0]["primitives"][0]["attributes"]["JOINTS_0"] = 3
        with self.assertRaisesRegex(ValueError, "paired skin"):
            geo.geometry(doc, binary, 0, self.limits)

    def test_duplicate_names_or_unreachable_hierarchy_rejected(self):
        doc, binary = self.load()
        doc["nodes"].append({"name": "part"})
        with self.assertRaisesRegex(ValueError, "unique"):
            geo.hierarchy(doc, self.limits)
        doc["nodes"][1]["name"] = "orphan"
        with self.assertRaisesRegex(ValueError, "all node roots"):
            geo.hierarchy(doc, self.limits)

    def test_bad_json_and_glb_framing_rejected(self):
        for raw in (b'{"x":1,"x":2}', b'{"v":1e999}', b"["*65+b"]"*65):
            with self.assertRaises(ValueError):
                geo.decode_json(raw)
        self.load()
        data = bytearray(self.path.read_bytes())
        struct.pack_into("<I", data, 8, len(data)+4)
        self.path.write_bytes(data)
        with self.assertRaisesRegex(ValueError, "header"):
            geo.read_glb(self.path, self.limits)

    def test_large_frame_small_hardware_affine_precision(self):
        matrix = geo.trs((123456.25, -345678.5, 456789.75))
        point = (.00003125, .0000625, .000125)
        measured = geo.transform(geo.inverse(matrix), geo.transform(matrix, point))
        self.assertLess(max(abs(a-b) for a, b in zip(measured, point)), 1e-10)
        relative = geo.multiply(geo.inverse(matrix), matrix)
        self.assertEqual(relative, geo.identity())

    def test_private_traversal_or_alias_paths_rejected(self):
        for value in ("../outside", "C:/safe/../outside", "C:/safe/.git/config", "C:/safe/trailing./file", "//server/share/file"):
            with self.subTest(value=value), self.assertRaises(ValueError):
                safe_path(value)

    def test_shadow_sdk_loader_rejected_before_exec_module(self):
        pinned = Path(self.temp.name)/"pinned"
        foreign = Path(self.temp.name)/"foreign"
        pinned.mkdir()
        foreign.mkdir()
        (pinned/"__init__.py").write_text("raise AssertionError('Must not execute')\n")
        (foreign/"__init__.py").write_text("raise AssertionError('Must not execute')\n")
        admitted = SimpleNamespace(job={"toolchain": {"moduleRoot": str(pinned), "files": [{"path": "__init__.py"}]}})
        class ShadowLoader(importlib.abc.Loader):
            def create_module(self, spec):
                return None
            exec_module = mock.Mock(side_effect=AssertionError("Shadow SDK code executed"))
        for origin, search in ((foreign/"__init__.py", foreign), (pinned/"__init__.py", foreign), (pinned/"__init__.py", pinned)):
            with self.subTest(origin=origin, search=search), mock.patch.dict(sys.modules):
                for name in list(sys.modules):
                    if name == "io_scene_gltf2" or name.startswith("io_scene_gltf2."):
                        del sys.modules[name]
                loader = ShadowLoader()
                spec = importlib.machinery.ModuleSpec("io_scene_gltf2", loader, origin=str(origin), is_package=True)
                spec.submodule_search_locations = [str(search)]
                class ShadowFinder(importlib.abc.MetaPathFinder):
                    def find_spec(self, fullname, path=None, target=None):
                        return spec if fullname == "io_scene_gltf2" else None
                finder = ShadowFinder()
                with mock.patch.object(sys, "meta_path", [finder, *sys.meta_path]):
                    with self.assertRaisesRegex(ValueError, "glTF SDK"):
                        import_sdk(admitted)
                loader.exec_module.assert_not_called()

    def test_already_loaded_foreign_sdk_rejected_before_resolution(self):
        pinned = Path(self.temp.name)/"pinned"
        foreign = Path(self.temp.name)/"foreign"
        pinned.mkdir()
        foreign.mkdir()
        for directory in (pinned, foreign):
            (directory/"__init__.py").write_text("# fixture\n")
        admitted = SimpleNamespace(job={"toolchain": {"moduleRoot": str(pinned), "files": [{"path": "__init__.py"}]}})
        module = ModuleType("io_scene_gltf2")
        module.__file__ = str(foreign/"__init__.py")
        module.__spec__ = importlib.util.spec_from_file_location("io_scene_gltf2", module.__file__)
        with mock.patch.dict(sys.modules, {"io_scene_gltf2": module}), mock.patch.object(importlib.util, "find_spec") as resolution:
            with self.assertRaisesRegex(ValueError, "loaded glTF module origin"):
                import_sdk(admitted)
            resolution.assert_not_called()


if __name__ == "__main__":
    unittest.main()
