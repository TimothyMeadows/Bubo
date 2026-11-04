"""Actual mixed-rig GLB/profile regression, without production human acceptance."""
import copy
import hashlib
import json
import os
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
BLENDER = os.environ.get("BLENDER_EXE", "C:/Program Files/Blender Foundation/Blender 4.5/blender.exe")
SCRIPTS = ROOT / "resources/model-to-animation/blender"


def glb(path):
    data = path.read_bytes()
    length, kind = struct.unpack_from("<II", data, 12)
    assert kind == 0x4e4f534a
    return json.loads(data[20:20 + length])


class MechanicalMotion(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not Path(BLENDER).is_file():
            raise unittest.SkipTest("BLOCKED: existing Blender4.5 unavailable")
        cache = ROOT / ".bubo/mm"
        cache.mkdir(parents=True, exist_ok=True)
        cls.project = Path(tempfile.mkdtemp(prefix="m-", dir=cache))
        cls.calls = []
        cls.blender(ROOT / "tests/fixtures/model-animation/create_mechanical.py", ["--output", str(cls.project / "inputs")])
        executable=Path(BLENDER).resolve();sdk=executable.parent/"4.5/scripts/addons_core/io_scene_gltf2"
        cls.toolchain={"blenderPath":str(executable),"blenderSha256":hashlib.sha256(executable.read_bytes()).hexdigest(),"gltfFiles":[{"path":path.relative_to(sdk).as_posix(),"sha256":hashlib.sha256(path.read_bytes()).hexdigest(),"bytes":path.stat().st_size} for path in sorted(sdk.rglob("*")) if path.is_file()]}
        cls.handoff = {"schemaVersion": "bubo-mechanical-assembly-handoff/v1", "model": {"path": "inputs/assembly.glb", "sha256": hashlib.sha256((cls.project / "inputs/assembly.glb").read_bytes()).hexdigest()}, "components": json.loads((cls.project / "inputs/components.json").read_text())}
        document = glb(cls.project / "inputs/assembly.glb")
        cls.handoff["bindings"] = {"nodes": document["nodes"], "skins": document.get("skins", [])}
        cls.numerics = {"linearTolerance":1e-6,"angularToleranceDegrees":.01,"minimumFeatureSize":.0001,"maxCoordinateMagnitude":10000}
        cls.metadata = {"schemaVersion":"controlled-test-metadata/v1","relationships":["preserve exact metadata"],"productionAcceptance":"not-established","numerics":cls.numerics}
        for name,content in (("design",{"numerics":cls.numerics,"assemblyProfile":{"maxGeometryChecks":100000},"simulationOnly":True}),("metadata",cls.metadata)):
            path = cls.project / ("inputs/"+name+".json")
            path.write_text(json.dumps(content),encoding="utf-8")
            cls.handoff[name] = {"path":path.relative_to(cls.project).as_posix(),"sha256":hashlib.sha256(path.read_bytes()).hexdigest()}
        cls.handoff["sourceState"] = {}
        for name in ("design", "parts", "assembly", "export"):
            path = cls.project / ("inputs/state-" + name + ".json")
            path.write_text(json.dumps({"simulationOnly": True, "state": name}), encoding="utf-8")
            cls.handoff["sourceState"][name] = {"path": path.relative_to(cls.project).as_posix(), "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}
        cls.recipe = {"schemaVersion": 1, "profile": "mechanical-assembly", "fps": 12, "requiredClips": ["articulate"], "rig": {"mode": "preserve", "rootNode": "AssemblyRoot"}, "parts": [], "anchors": [], "clips": [{"id": "articulate", "role": "diagnostic-controlled-fixture", "loop": True, "rootMotion": "in-place", "startFrame": 0, "endFrame": 6, "markers": [], "channels": [{"target": {"node": "bubo.instance.000000"}, "property": "location", "keyframes": [[0, [0, 0, .1]], [3, [0, .1, .1]], [6, [0, 0, .1]]]}, {"target": {"rig": "bubo.rig.000001", "bone": "bubo.bone.000001.000000"}, "property": "location", "keyframes": [[0, [0, 0, 0]], [3, [.01, 0, 0]], [6, [0, 0, 0]]]}]}], "states": [], "transitions": [], "parameters": [], "effects": [], "budgets": {"maxTriangles": 10000, "maxBones": 16, "maxInfluences": 4, "maxMaterials": 10, "maxTextureBytes": 1048576, "maxRuntimeBytes": 8388608, "maxClips": 10, "maxClipSeconds": 2, "maxSamples": 1000, "maxPreviewFrames": 100, "maxAttempts": 10, "timeoutSeconds": 120, "maxLogBytes": 16777216}, "review": {"renderer": "BLENDER_WORKBENCH", "fps": 4, "resolution": [64, 64], "cameras": ["front"]}}
        cls.stages = {}
        source = "inputs/assembly.glb"
        for operation in ("inspect", "prepare", "rig", "author", "bake", "export", "review"):
            result = cls.stage(operation, source)
            cls.stages[operation] = result
            source = result["artifacts"].get("glb") if operation == "export" else result["artifacts"].get("blend", source)

    @classmethod
    def blender(cls, script, arguments, success=True):
        command = [BLENDER, "--background", "--factory-startup", "--disable-autoexec", "--python-exit-code", "1", "--python", str(script), "--", *arguments]
        run = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=180)
        log = cls.project / ("call-" + str(len(cls.calls)) + ".log")
        log.write_bytes(run.stdout)
        cls.calls.append({"command": command, "exitCode": run.returncode, "log": str(log)})
        (cls.project / "calls.json").write_text(json.dumps(cls.calls, indent=2), encoding="utf-8")
        if (run.returncode == 0) != success:
            raise AssertionError(run.stdout.decode("utf-8", errors="replace")[-7000:])

    @classmethod
    def stage(cls, operation, source, recipe=None, handoff=None, success=True, toolchain=None):
        output = ".art-staging/model-to-animation/fixture/v1/attempts/" + operation + "-" + str(len(cls.calls))
        job = {"schemaVersion": 1, "root": str(cls.project), "operation": operation, "input": source, "outputDir": output, "result": output + "/result.json", "asset": "fixture", "version": "v1", "profile": (recipe or cls.recipe)["profile"], "recipe": copy.deepcopy(recipe or cls.recipe), "mechanicalHandoff": handoff or cls.handoff, "mechanicalMetadata": copy.deepcopy(cls.metadata)}
        if job["profile"]=="mechanical-assembly":job["mechanicalToolchain"]=copy.deepcopy(toolchain or cls.toolchain)
        path = cls.project / ("job-" + str(len(cls.calls)) + ".json")
        path.write_text(json.dumps(job), encoding="utf-8")
        cls.blender(SCRIPTS / "motion.py", ["--job", str(path)], success)
        return json.loads((cls.project / job["result"]).read_text()) if success else None

    def test_mixed_owned_rigs_survive_actual_export_and_reimport(self):
        package = json.loads((self.project / self.stages["export"]["artifacts"]["package"]).read_text())
        document = glb(self.project / self.stages["export"]["artifacts"]["glb"])
        self.assertEqual(len(document["skins"]), 2)
        self.assertEqual(len([n for n in document["nodes"] if "mesh" in n]), 3)
        self.assertEqual(package["rig"]["kind"], "mechanical-assembly")
        self.assertEqual(package["mechanicalHandoff"], self.handoff)
        self.assertEqual(package["verification"]["human"], "pending")
        self.assertTrue(any(binding["node"] == "bubo.bone.000001.000000" for binding in package["clips"][0]["bindings"]))
        self.assertFalse(any(binding["node"] == "bubo.bone.000002.000000" for binding in package["clips"][0]["bindings"]))

    def test_existing_profiles_reject_mixed_rigs(self):
        source = self.stages["prepare"]["artifacts"]["blend"]
        for profile in ("rigid", "articulated", "skinned"):
            recipe = copy.deepcopy(self.recipe)
            recipe["profile"] = profile
            self.stage("rig", source, recipe=recipe, success=False)

    def test_10um_owned_motion_survives_and_dropped_motion_is_rejected(self):
        recipe = copy.deepcopy(self.recipe)
        recipe["clips"][0]["channels"] = [recipe["clips"][0]["channels"][1]]
        recipe["clips"][0]["channels"][0]["keyframes"][1][1] = [1e-5,0,0]
        source = self.stages["rig"]["artifacts"]["blend"]
        for operation in ("author","bake","export","review"):
            result = self.stage(operation,source,recipe=recipe)
            source = result["artifacts"].get("glb") if operation=="export" else result["artifacts"].get("blend",source)
        delivered = self.project/source
        document = glb(delivered)
        data = bytearray(delivered.read_bytes())
        binary_start = 28+struct.unpack_from("<I",data,12)[0]
        for animation in document["animations"]:
            for sampler in animation["samplers"]:
                accessor = document["accessors"][sampler["output"]]
                width = {"VEC3":3,"VEC4":4}[accessor["type"]]
                view = document["bufferViews"][accessor["bufferView"]]
                offset = binary_start+view.get("byteOffset",0)+accessor.get("byteOffset",0)
                stride = view.get("byteStride",width*4)
                value = bytes(data[offset:offset+width*4])
                for index in range(accessor["count"]): data[offset+index*stride:offset+index*stride+width*4] = value
        dropped = self.project/"dropped-10um-motion.glb"
        dropped.write_bytes(data)
        self.stage("review",dropped.relative_to(self.project).as_posix(),recipe=recipe,success=False)

    def test_first_clip_pose_is_independent_of_canonical_bind_rest(self):
        recipe = copy.deepcopy(self.recipe)
        recipe["clips"][0]["channels"] = [recipe["clips"][0]["channels"][1]]
        recipe["clips"][0]["channels"][0]["keyframes"] = [[0,[.02,0,0]],[3,[.03,0,0]],[6,[.02,0,0]]]
        source = self.stages["rig"]["artifacts"]["blend"]
        for operation in ("author","bake","export","review"):
            result = self.stage(operation,source,recipe=recipe)
            source = result["artifacts"].get("glb") if operation=="export" else result["artifacts"].get("blend",source)

    def test_mechanical_rejects_new_rig_and_ambiguous_bone(self):
        for mutate in (lambda recipe: recipe["rig"].update(mode="declared"), lambda recipe: recipe["clips"][0]["channels"][1].update(target={"bone": "bubo.bone.000001.000000"})):
            recipe = copy.deepcopy(self.recipe)
            mutate(recipe)
            self.stage("inspect", "inputs/assembly.glb", recipe=recipe, success=False)

    def test_changed_reviewed_sdk_identity_rejects_before_import(self):
        changed=copy.deepcopy(self.toolchain);changed["gltfFiles"][0]["sha256"]="0"*64
        self.stage("inspect","inputs/assembly.glb",toolchain=changed,success=False)

    def test_shadow_sdk_rejects_without_executing_package_source(self):
        sys.path.insert(0,str(SCRIPTS))
        import mechanical
        import importlib.machinery
        import types
        expected=self.project/"guard-expected/io_scene_gltf2";expected.mkdir(parents=True)
        (expected/"__init__.py").write_text("# declared test SDK; never executed\n",encoding="utf-8")
        shadow=self.project/"guard-shadow";(shadow/"io_scene_gltf2").mkdir(parents=True)
        marker=self.project/"unreviewed-sdk-executed.txt"
        (shadow/"io_scene_gltf2/__init__.py").write_text("from pathlib import Path\nPath("+repr(str(marker))+").write_text('executed')\n",encoding="utf-8")
        previous_root,previous_files=mechanical.SDK_ROOT,mechanical.SDK_FILES
        previous_module=sys.modules.pop("io_scene_gltf2",None)
        mechanical.SDK_ROOT=expected.resolve();mechanical.SDK_FILES={"__init__.py"};sys.path.insert(0,str(shadow))
        try:
            with self.assertRaises(ValueError):mechanical.preflight_sdk_import()
            self.assertNotIn("io_scene_gltf2",sys.modules);self.assertFalse(marker.exists())
            module=types.ModuleType("io_scene_gltf2");module.__file__=str(expected/"__init__.py");module.__path__=[str(shadow)]
            module.__spec__=importlib.machinery.ModuleSpec("io_scene_gltf2",loader=None,origin=module.__file__,is_package=True);module.__spec__.submodule_search_locations=[str(expected)]
            sys.modules["io_scene_gltf2"]=module
            with self.assertRaises(ValueError):mechanical.verify_sdk_origins()
            module.__path__=[str(expected)];module.__spec__.loader=object()
            with self.assertRaises(ValueError):mechanical.preflight_sdk_import()
            module.__spec__.loader=importlib.machinery.SourceFileLoader("io_scene_gltf2",str(shadow/"io_scene_gltf2/__init__.py"))
            with self.assertRaises(ValueError):mechanical.preflight_sdk_import()
            self.assertFalse(marker.exists())
        finally:
            sys.path.remove(str(shadow));sys.modules.pop("io_scene_gltf2",None)
            if previous_module is not None:sys.modules["io_scene_gltf2"]=previous_module
            mechanical.SDK_ROOT,mechanical.SDK_FILES=previous_root,previous_files

    def test_missing_component_and_cross_rig_ownership_rejected(self):
        for mutate in (lambda handoff: handoff["components"].pop(), lambda handoff: handoff["components"][1].update(rigNode="bubo.rig.000002")):
            handoff = copy.deepcopy(self.handoff)
            mutate(handoff)
            self.stage("inspect", "inputs/assembly.glb", handoff=handoff, success=False)

    def test_later_source_state_invalidates_the_old_handoff(self):
        path = self.project / self.handoff["sourceState"]["export"]["path"]
        original = path.read_bytes()
        try:
            path.write_bytes(original + b" ")
            self.stage("inspect", "inputs/assembly.glb", success=False)
        finally:
            path.write_bytes(original)

    def test_portable_vertex_weights_and_inverse_bind_changes_rejected(self):
        sys.path.insert(0, str(SCRIPTS))
        from mechanical import verify_portable_geometry
        source = self.project / "inputs/assembly.glb"
        document = glb(source)
        data = source.read_bytes()
        binary_start = 28 + struct.unpack_from("<I", data, 12)[0]
        mesh_node = next(node for node in document["nodes"] if "skin" in node)
        primitive = document["meshes"][mesh_node["mesh"]]["primitives"][0]
        accessor_ids = [primitive["attributes"]["POSITION"], primitive["attributes"]["WEIGHTS_0"], document["skins"][mesh_node["skin"]]["inverseBindMatrices"]]
        for index in accessor_ids:
            spec = document["accessors"][index]
            view = document["bufferViews"][spec["bufferView"]]
            offset = binary_start + view.get("byteOffset", 0) + spec.get("byteOffset", 0)
            changed = bytearray(data)
            self.assertEqual(spec["componentType"], 5126)
            struct.pack_into("<f", changed, offset, struct.unpack_from("<f", changed, offset)[0] + .03)
            candidate = self.project / ("changed-accessor-" + str(index) + ".glb")
            candidate.write_bytes(changed)
            with self.assertRaises(ValueError):
                verify_portable_geometry(self.project, candidate, self.handoff, self.recipe)
        # An inverse-bind translation has linear units, even when the matrix
        # contains large values. It follows the approved absolute tolerance.
        spec = document["accessors"][accessor_ids[-1]]
        view = document["bufferViews"][spec["bufferView"]]
        offset = binary_start + view.get("byteOffset",0) + spec.get("byteOffset",0) + 12*4
        for delta,rejected in ((1e-5,True),(2e-7,False)):
            changed = bytearray(data)
            struct.pack_into("<f",changed,offset,struct.unpack_from("<f",changed,offset)[0]+delta)
            candidate = self.project/("changed-bind-translation-"+str(delta)+".glb")
            candidate.write_bytes(changed)
            if rejected:
                with self.assertRaises(ValueError): verify_portable_geometry(self.project,candidate,self.handoff,self.recipe)
            else:
                verify_portable_geometry(self.project,candidate,self.handoff,self.recipe)

    def test_approved_precision_large_origin_small_hardware_and_winding(self):
        sys.path.insert(0, str(SCRIPTS))
        from mechanical import approved_numerics, compare_runtime, verify_portable_geometry
        numerics = approved_numerics(self.project,self.handoff,self.metadata)
        def write(path,origin,positions=None,indices=(0,1,2),scale=None,rotation=None):
            positions = positions or [(0,0,0),(.01,0,0),(0,.01,0)]
            binary = struct.pack("<9f3H",*(number for point in positions for number in point),*indices)+b"\0\0"
            document = {"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0]}],"nodes":[{"name":"hardware","mesh":0,"translation":[origin,0,0]}],"meshes":[{"primitives":[{"attributes":{"POSITION":0},"indices":1}]}],"buffers":[{"byteLength":len(binary)}],"bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":36},{"buffer":0,"byteOffset":36,"byteLength":6}],"accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3"},{"bufferView":1,"componentType":5123,"count":3,"type":"SCALAR"}]}
            if scale is not None: document["nodes"][0]["scale"] = scale
            if rotation is not None: document["nodes"][0]["rotation"] = rotation
            encoded = json.dumps(document,separators=(",",":")).encode("utf-8")
            encoded += b" "*((-len(encoded))%4)
            path.write_bytes(struct.pack("<III",0x46546c67,2,28+len(encoded)+len(binary))+struct.pack("<II",len(encoded),0x4e4f534a)+encoded+struct.pack("<II",len(binary),0x004e4942)+binary)
            return document
        for origin in (0,1000,.001):
            source = self.project/("precision-source-"+str(origin)+".glb")
            document = write(source,origin)
            handoff = copy.deepcopy(self.handoff)
            handoff["components"] = [{"instanceId":"hardware","partId":"hardware","node":"hardware","kind":"rigid","rigNode":None}]
            handoff["model"] = {"path":source.relative_to(self.project).as_posix(),"sha256":hashlib.sha256(source.read_bytes()).hexdigest()}
            handoff["bindings"] = {"nodes":document["nodes"],"skins":[]}
            candidate = self.project/("precision-candidate-"+str(origin)+".glb")
            write(candidate,origin,[(0,0,0),(.01001,0,0),(0,.01,0)])
            with self.assertRaises(ValueError):
                verify_portable_geometry(self.project,candidate,handoff,self.recipe)
            shifted = write(candidate,origin+1e-5)
            with self.assertRaises(ValueError):
                compare_runtime(handoff["bindings"],shifted,handoff["components"],numerics)
            with self.assertRaises(ValueError):
                verify_portable_geometry(self.project,candidate,handoff,self.recipe)
            write(candidate,origin,indices=(0,2,1))
            with self.assertRaises(ValueError):
                verify_portable_geometry(self.project,candidate,handoff,self.recipe)
            write(candidate,origin,indices=(1,2,0))
            verify_portable_geometry(self.project,candidate,handoff,self.recipe)
            # A within-tolerance edit may change lexical first-corner or surface ordering.
            write(source,origin,[(0,0,0),(0,1,0),(1,0,0)])
            handoff["model"]["sha256"] = hashlib.sha256(source.read_bytes()).hexdigest()
            write(candidate,origin,[(0,0,0),(-1e-7,1,0),(1,0,0)])
            verify_portable_geometry(self.project,candidate,handoff,self.recipe)
            write(source,origin,[(0,0,0),(100,0,0),(0,100,0)])
            handoff["model"]["sha256"] = hashlib.sha256(source.read_bytes()).hexdigest()
            write(candidate,origin,[(0,0,0),(100,0,0),(0,100,0)],scale=[1+8e-8,1,1])
            with self.assertRaises(ValueError): verify_portable_geometry(self.project,candidate,handoff,self.recipe)
        q = 2**-.5
        original = {"nodes":[{"name":"q","rotation":[0,0,q*(1+1e-8),q*(1+1e-8)]}],"skins":[]}
        candidate = {"nodes":[{"name":"q","rotation":[0,0,q,q]}],"skins":[]}
        compare_runtime(original,candidate,[],numerics)
        changed_metadata = copy.deepcopy(self.metadata)
        changed_metadata["numerics"]["linearTolerance"] = .001
        with self.assertRaises(ValueError):
            approved_numerics(self.project,self.handoff,changed_metadata)


if __name__ == "__main__":
    unittest.main()
