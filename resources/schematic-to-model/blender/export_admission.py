"""Closed admission for the fixed schematic portable-model workers.

Source jobs, native certificates and installed glTF code are immutable inputs.
This is cooperative process admission, not an operating-system sandbox.
"""
import hashlib
import importlib
import importlib.machinery
import importlib.util
import json
import math
import os
from pathlib import Path
import stat
import sys

sys.dont_write_bytecode = True
NEW_BUNDLE = ("export_admission.py", "export_geometry.py", "export_author.py", "export_verify.py", "export_review.py")
OLD_BUNDLE = ("assembly_admission.py", "assembly_author.py", "assembly_geometry.py", "assembly_motion.py", "assembly_verify.py", "parts_admission.py", "parts_geometry.py")
BUNDLE = NEW_BUNDLE + OLD_BUNDLE
INPUTS = ("assemblyJob", "assemblyReport", "assemblyVerification", "motionJob", "motionReport", "motionVerification", "sourceAssembly", "sourceMotion", "glb", "producerReport", "package", "restVerification", "diagnosticVerification")
IDENTITY = ("build", "approvalSha256", "assemblyJobSha256", "assemblyReportSha256", "assemblyVerificationSha256", "assemblySourceSha256", "motionJobSha256", "motionReportSha256", "motionVerificationSha256", "motionSourceSha256")
LIMITS = {"maxSourceBytes": 16*1024**3, "maxJobBytes": 8*1024**2, "maxOutputBytes": 16*1024**3,
          "maxVertices": 750000, "maxTriangles": 250000, "maxNodes": 65536,
          "maxAccessors": 262144, "maxFiles": 16384, "maxMotionSamples": 32768,
          "maxGeometryChecks": 50_000_000, "maxViews": 4096, "imageSize": 4096,
          "renderSamples": 256, "maxSkinJoints": 16384, "maxChannels": 32768, "maxPrimitives": 16384}
MAX_REPORT = 8*1024**2


def require(condition, message):
    if not condition:
        raise ValueError("Schematic export admission: " + message)


def closed(value, keys, label):
    require(isinstance(value, dict) and set(value) == set(keys), "closed " + label + " fields")
    return value


def safe_path(value):
    require(isinstance(value, str) and not any(ord(c) < 32 for c in value), "path must be text")
    path = Path(value)
    normalized = value.replace("\\", "/")
    require(path.is_absolute() and not normalized.startswith("//") and not any(p.lower() in (".", "..", ".git") for p in normalized.split("/")), "absolute local canonical path")
    for part in path.parts[1:]:
        require(not part.endswith((".", " ")) and ":" not in part, "unambiguous path component")
    for node in [path, *path.parents]:
        if node.exists() or node.is_symlink():
            info = node.lstat()
            require(not stat.S_ISLNK(info.st_mode) and not getattr(info, "st_file_attributes", 0) & 0x400, "linked/reparse path forbidden")
    require(path.resolve() == path, "canonical path required")
    return path


def digest(path, maximum=16*1024**3):
    path = safe_path(str(path))
    require(path.is_file() and 0 <= path.stat().st_size <= maximum, "regular bounded input file")
    total, hasher = 0, hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(65536), b""):
            total += len(chunk)
            require(total <= maximum, "file grew beyond byte limit")
            hasher.update(chunk)
    require(total == path.stat().st_size, "input size changed")
    return hasher.hexdigest()


def hash_value(value):
    require(isinstance(value, str) and len(value) == 64 and all(c in "0123456789abcdef" for c in value), "lowercase SHA256 required")
    return value


def bounded_integer(value, maximum, minimum=1):
    require(type(value) is int and minimum <= value <= maximum, "bounded integer required")
    return value


def read_json(path, maximum=MAX_REPORT):
    path = safe_path(str(path))
    require(path.is_file() and 2 <= path.stat().st_size <= maximum, "bounded JSON input required")
    from export_geometry import decode_json
    return decode_json(path.read_bytes())


def toolchain_current(toolchain, bpy=None):
    closed(toolchain, ("blenderPath", "blenderSha256", "moduleRoot", "files", "fingerprint"), "toolchain")
    executable, root = safe_path(toolchain["blenderPath"]), safe_path(toolchain["moduleRoot"])
    require(root == executable.parent/"4.5"/"scripts"/"addons_core"/"io_scene_gltf2", "bundled selected glTF module required")
    require(digest(executable, 1024**3) == hash_value(toolchain["blenderSha256"]), "selected executable changed")
    if bpy:
        require(safe_path(bpy.app.binary_path) == executable, "running executable differs")
    require(isinstance(toolchain["files"], list) and 1 <= len(toolchain["files"]) <= 4096, "bounded module inventory")
    expected, total = {}, 0
    for row in toolchain["files"]:
        closed(row, ("path", "sha256", "bytes"), "toolchain file")
        name = row["path"]
        require(isinstance(name, str) and name and "\\" not in name and not any(p in ("", ".", "..") for p in name.split("/")) and "\t" not in name and "\n" not in name and not Path(name).is_absolute(), "confined module relative path")
        require(name not in expected, "unique module file")
        bounded_integer(row["bytes"], 16*1024**2, 0)
        total += row["bytes"]
        require(total <= 128*1024**2 and digest(root/name, row["bytes"]) == hash_value(row["sha256"]) and (root/name).stat().st_size == row["bytes"], "installed glTF file changed")
        expected[name] = row
    observed, dirs = set(), 0
    for directory, children, files in os.walk(root, followlinks=False):
        dirs += 1
        require(dirs <= 1024, "module directory ceiling")
        for child in [*children, *files]:
            safe_path(str(Path(directory)/child))
        for name in files:
            observed.add((Path(directory)/name).relative_to(root).as_posix())
            require(len(observed) <= 4096, "module file ceiling")
    require(observed == set(expected) and "__init__.py" in observed, "complete installed glTF inventory changed")
    ordered = sorted(expected, key=lambda value: value.encode("utf-16-be"))
    require([r["path"] for r in toolchain["files"]] == ordered, "canonical module inventory ordering")
    content = "\n".join([str(executable), toolchain["blenderSha256"], str(root)] + [name+"\t"+expected[name]["sha256"]+"\t"+str(expected[name]["bytes"]) for name in ordered])+"\n"
    require(hashlib.sha256(content.encode("utf-8")).hexdigest() == hash_value(toolchain["fingerprint"]), "module fingerprint changed")


def require_sdk_origins(job):
    root = safe_path(job["toolchain"]["moduleRoot"])
    inventory = {r["path"] for r in job["toolchain"]["files"]}
    for name, module in list(sys.modules.items()):
        if name == "io_scene_gltf2" or name.startswith("io_scene_gltf2."):
            require(type(module) is type(sys), "loaded glTF entry must be an actual module")
            values = module.__dict__
            spec = values.get("__spec__")
            require(type(spec) is importlib.machinery.ModuleSpec and spec.name == name and values.get("__name__") == name, "loaded glTF module spec identity changed")
            path = values.get("__file__")
            if path is not None:
                actual = safe_path(path)
                require(actual.is_relative_to(root) and actual.relative_to(root).as_posix() in inventory, "loaded glTF module origin changed")
                require(isinstance(spec.origin, str) and safe_path(spec.origin) == actual, "module spec origin changed")
                require(type(spec.loader) is importlib.machinery.SourceFileLoader and spec.loader.name == name and safe_path(spec.loader.path) == actual, "loaded glTF module loader changed")
                if spec.submodule_search_locations is not None:
                    expected = root.joinpath(*name.split(".")[1:])
                    require([safe_path(value) for value in spec.submodule_search_locations] == [expected] and [safe_path(value) for value in values.get("__path__", ())] == [expected], "loaded glTF package search path changed")
            else:
                expected = root.joinpath(*name.split(".")[1:])
                require(name != "io_scene_gltf2" and spec.origin is None and type(spec.loader) is importlib.machinery.NamespaceLoader and [safe_path(value) for value in values.get("__path__", ())] == [expected] and [safe_path(value) for value in spec.submodule_search_locations or ()] == [expected] and expected.is_dir() and not (expected/"__init__.py").exists(), "fixed namespace origin required")


class Admitted:
    def __init__(self, job, job_sha, path, paths, assembly, motion, reports):
        self.job, self.job_sha, self.path, self.paths = job, job_sha, path, paths
        self.assembly, self.motion, self.reports = assembly, motion, reports
        self.output, self.work = Path(job["output"]), 0
        self.written, self.output_bytes = {}, 0

    def charge(self, count):
        self.work += count
        require(self.work <= self.job["limits"]["maxGeometryChecks"], "complete worker geometry work ceiling")

    def unchanged(self, bpy=None):
        for name, path in self.paths.items():
            reference = self.job["inputs"][name]
            require(path.stat().st_size == reference["bytes"] and digest(path, self.job["limits"]["maxSourceBytes"]) == reference["sha256"], "immutable source changed: " + name)
        toolchain_current(self.job["toolchain"], bpy)
        for name, expected in self.job["bundle"].items():
            require(digest(Path(__file__).parent/name, 2*1024**2) == expected, "audited bundle changed")

    def output_files(self):
        result, total = [], 0
        for path in self.output.iterdir():
            safe_path(str(path))
            require(path.is_file(), "worker output must be flat regular files")
            total += path.stat().st_size
            require(total <= self.job["limits"]["maxOutputBytes"] and len(result) < self.job["limits"]["maxFiles"], "output file/byte ceiling")
            result.append({"path": path.name, "sha256": digest(path), "bytes": path.stat().st_size})
        self.written, self.output_bytes = {r["path"]: r for r in result}, total
        return sorted(result, key=lambda r: r["path"])

    def write(self, name, data):
        require(isinstance(name, str) and name == Path(name).name and name not in (".", ".."), "flat owned output name")
        require(not (self.output/name).exists(), "immutable output exists")
        require(len(self.written)+1 <= self.job["limits"]["maxFiles"] and self.output_bytes+len(data) <= self.job["limits"]["maxOutputBytes"], "incremental output ceiling")
        with (self.output/name).open("xb") as handle:
            handle.write(data)
        row = {"path": name, "sha256": hashlib.sha256(data).hexdigest(), "bytes": len(data)}
        self.written[name] = row
        self.output_bytes += len(data)
        return row

    def report(self, bpy, raw=None, imported=None):
        result = {"schemaVersion": "bubo-schematic-export/v1", "operation": self.job["operation"], "representation": self.job["representation"], "jobSha256": self.job_sha,
                  "blenderVersion": bpy.app.version_string, **{key: self.job[key] for key in ("identity", "toolchain", "bundle", "limits", "inputs")},
                  "outputs": self.output_files(), "raw": raw, "imported": imported, "humanModelAccepted": False, "runtimeAccepted": False}
        data = (json.dumps(result, ensure_ascii=False, sort_keys=True, separators=(",", ":"), allow_nan=False)+"\n").encode("utf-8")
        require(len(data) <= MAX_REPORT, "compact eight MiB report ceiling")
        self.write("report.json", data)
        return result


def admit(arguments, operation):
    require(len(arguments) == 2, "job path and exact SHA required")
    path = safe_path(arguments[0])
    require(digest(path, 8*1024**2) == hash_value(arguments[1]), "job SHA changed")
    job = read_json(path)
    closed(job, ("schemaVersion", "operation", "representation", "projectRoot", "output", "bundle", "toolchain", "identity", "limits", "inputs"), "export job")
    require(job["schemaVersion"] == "bubo-schematic-export-job/v1" and job["operation"] == operation, "explicit supported operation required")
    require(job["representation"] in (("rest", "diagnostic") if operation == "verify" else (None,)), "operation representation")
    limits = closed(job["limits"], LIMITS, "limits")
    for key, maximum in LIMITS.items():
        bounded_integer(limits[key], maximum)
    require(limits["maxJobBytes"] == 8*1024**2 and limits["imageSize"] >= 64, "bounded fixed CPU review profile")
    require(path.stat().st_size <= limits["maxJobBytes"], "declared job byte limit")
    root, output = safe_path(job["projectRoot"]), safe_path(job["output"])
    require(root.is_dir() and path.is_relative_to(root) and path != root and output.is_relative_to(root) and output != root, "repository-local worker paths")
    require(output.is_dir() and not any(output.iterdir()) and not path.is_relative_to(output), "fresh separate owned output required")
    closed(job["bundle"], BUNDLE, "audited bundle")
    for name, sha in job["bundle"].items():
        require(digest(Path(__file__).parent/name, 2*1024**2) == hash_value(sha), "audited bundle SHA changed: " + name)
    closed(job["identity"], IDENTITY, "export identity")
    closed(job["identity"]["build"], ("designSha256", "designFingerprint", "runtimeBuildSha256", "toolSha256", "partsJobSha256", "partsReportSha256", "partsVerificationSha256"), "build identity")
    for value in job["identity"]["build"].values():
        hash_value(value)
    for key in IDENTITY[1:]:
        hash_value(job["identity"][key])
    closed(job["inputs"], INPUTS, "input references")
    required = set(INPUTS[:8]) | ({"glb", "producerReport", "package"} if operation == "verify" else {"producerReport", "package", "restVerification", "diagnosticVerification"} if operation == "review" else set())
    paths = {}
    for name, row in job["inputs"].items():
        require((row is not None) == (name in required), "explicit nullable input inventory: " + name)
        if row is None:
            continue
        closed(row, ("path", "sha256", "bytes"), "input file")
        bounded_integer(row["bytes"], limits["maxSourceBytes"])
        value = safe_path(row["path"])
        require(value.is_relative_to(root) and not value.is_relative_to(output) and value.is_file() and value.stat().st_size == row["bytes"] and digest(value, limits["maxSourceBytes"]) == hash_value(row["sha256"]), "exact confined source reference: " + name)
        paths[name] = value
    require(len(paths) == len(set(paths.values())), "distinct input files required")
    from assembly_admission import admit as admit_assembly
    assembly = admit_assembly(paths["assemblyJob"], job["inputs"]["assemblyJob"]["sha256"], Path(__file__).parent, verify=True)
    motion = admit_assembly(paths["motionJob"], job["inputs"]["motionJob"]["sha256"], Path(__file__).parent, verify=True)
    require(assembly["operation"] == "assemble" and motion["operation"] == "prove-motion" and assembly["identity"] == motion["identity"] == job["identity"]["build"], "current verified source identity")
    require(assembly["design"] == motion["design"] and assembly["plan"] == motion["plan"] and assembly["rigs"] == motion["rigs"], "canonical and diagnostic source programs differ")
    reports = {name: read_json(paths[name]) for name in ("assemblyReport", "motionReport")}
    for prefix, source_name, source, report in (("assembly", "sourceAssembly", assembly, reports["assemblyReport"]), ("motion", "sourceMotion", motion, reports["motionReport"])):
        for suffix, input_name in (("Job", prefix+"Job"), ("Report", prefix+"Report"), ("Verification", prefix+"Verification"), ("Source", source_name)):
            require(job["identity"][prefix+suffix+"Sha256"] == job["inputs"][input_name]["sha256"], "exact top-level source identity")
        require(report["identity"] == source["identity"] and report["jobSha256"] == job["inputs"][prefix+"Job"]["sha256"] and report["sourceSha256"] == job["inputs"][source_name]["sha256"] and paths[source_name] == Path(source["output"])/report["sourceFile"], "saved source report ancestry")
    admitted = Admitted(job, arguments[1], path, paths, assembly, motion, reports)
    admitted.unchanged()
    return admitted


def process(admitted, bpy):
    require(tuple(bpy.app.version[:2]) == (4, 5) and bpy.app.background and not bpy.data.filepath, "fresh unopened background Blender4.5 LTS required")
    require(all(flag in sys.argv for flag in ("--factory-startup", "--disable-autoexec", "--offline-mode")), "offline factory disabled-autoexec process flags required")
    require(sys.dont_write_bytecode, "no installed bytecode mutation")
    admitted.unchanged(bpy)


def import_sdk(admitted):
    root = safe_path(admitted.job["toolchain"]["moduleRoot"])
    # Reject poisoned already-loaded roots/children before asking the import
    # system to resolve anything. A top-level find_spec executes no SDK source.
    require_sdk_origins(admitted.job)
    spec = importlib.util.find_spec("io_scene_gltf2")
    expected = root/"__init__.py"
    require(type(spec) is importlib.machinery.ModuleSpec and spec.name == "io_scene_gltf2" and isinstance(spec.origin, str) and safe_path(spec.origin) == expected,
            "glTF SDK resolution differs from selected builtin origin")
    require([safe_path(value) for value in spec.submodule_search_locations or ()] == [root], "glTF SDK package search path differs from selected builtin root")
    require(type(spec.loader) is importlib.machinery.SourceFileLoader and spec.loader.name == "io_scene_gltf2" and safe_path(spec.loader.path) == expected,
            "glTF SDK loader differs from selected builtin source loader")
    module = importlib.import_module("io_scene_gltf2")
    require(safe_path(module.__file__) == root/"__init__.py", "selected builtin glTF importer required")
    require_sdk_origins(admitted.job)
    return module
