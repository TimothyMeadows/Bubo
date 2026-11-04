"""Fixed CPU review of complete portable assemblies and diagnostic clips.

Rendering changes only a transient imported scene. Nothing is repaired, saved
back, promoted, uploaded, or granted human/model/runtime acceptance.
"""
import importlib
import json
import math
from pathlib import Path
import sys
import hashlib

sys.dont_write_bytecode = True
# Verify the exact job and all fixed resources before any repository module is
# imported. Native orchestration repeats these checks at its execution boundary.
if __name__ == "__main__":
    _args = sys.argv[sys.argv.index("--")+1:] if "--" in sys.argv else []
    if len(_args) != 2:
        raise ValueError("Expected exact job path and SHA256")
    _job_path = Path(_args[0])
    if not _job_path.is_absolute() or not _job_path.is_file() or not 2 <= _job_path.stat().st_size <= 8*1024**2:
        raise ValueError("Missing/oversized fixed export job")
    for _entry in [_job_path, *_job_path.parents]:
        _info = _entry.lstat()
        if _entry.is_symlink() or getattr(_info, "st_file_attributes", 0) & 0x400:
            raise ValueError("Linked export bootstrap path")
    _raw = _job_path.read_bytes()
    if hashlib.sha256(_raw).hexdigest() != _args[1]:
        raise ValueError("Fixed export job SHA256 changed")
    _job = json.loads(_raw)
    _names = {"export_admission.py", "export_geometry.py", "export_author.py",
              "export_verify.py", "export_review.py", "assembly_admission.py",
              "assembly_author.py", "assembly_geometry.py", "assembly_motion.py",
              "assembly_verify.py", "parts_admission.py", "parts_geometry.py"}
    if not isinstance(_job, dict) or not isinstance(_job.get("bundle"), dict) or set(_job["bundle"]) != _names:
        raise ValueError("Exact twelve-resource export bundle required")
    for _name, _expected in _job["bundle"].items():
        _resource = Path(__file__).parent/_name
        for _entry in [_resource, *_resource.parents]:
            _info = _entry.lstat()
            if _entry.is_symlink() or getattr(_info, "st_file_attributes", 0) & 0x400:
                raise ValueError("Linked export resource")
        if not _resource.is_file() or _resource.stat().st_size > 2*1024**2 or hashlib.sha256(_resource.read_bytes()).hexdigest() != _expected:
            raise ValueError("Audited export resource SHA256 changed: "+_name)
sys.path.insert(0, str(Path(__file__).resolve().parent))
from export_admission import admit, process, read_json, safe_path, digest, require
from export_verify import import_actual
import export_geometry as geo


def payload(admitted, suffix):
    package = read_json(admitted.paths["package"])
    require(package.get("schemaVersion") == "bubo-schematic-export-package/v1", "current portable package required")
    rows = [r for r in package["files"] if r["path"] == suffix or r["path"].endswith("/"+suffix)]
    require(len(rows) == 1, "one exact review payload: "+suffix)
    row = rows[0]
    relative = row["path"]
    require(isinstance(relative, str) and not Path(relative).is_absolute() and "\\" not in relative and not any(p in ("", ".", "..") for p in relative.split("/")), "confined package payload")
    root = admitted.paths["package"].parent
    path = safe_path(str(root/relative))
    require(path.is_relative_to(root) and path.stat().st_size == row["bytes"] and digest(path, admitted.job["limits"]["maxSourceBytes"]) == row["sha256"], "review package content changed")
    return path


def bounds(bpy):
    from mathutils import Vector
    points = [obj.matrix_world@Vector(corner) for obj in bpy.data.objects if obj.type == "MESH" and not obj.hide_render for corner in obj.bound_box]
    require(points, "review has no physical geometry")
    low, high = [min(p[k] for p in points) for k in range(3)], [max(p[k] for p in points) for k in range(3)]
    center = Vector(tuple((a+b)/2 for a, b in zip(low, high)))
    radius = max(math.dist(low, high)/2, .01)
    return center, radius


def configure(admitted, bpy):
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.device = "CPU"
    scene.cycles.samples = admitted.job["limits"]["renderSamples"]
    scene.cycles.use_denoising = False
    scene.render.threads_mode, scene.render.threads = "FIXED", 1
    scene.render.resolution_x = scene.render.resolution_y = admitted.job["limits"]["imageSize"]
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = "PNG"
    scene.render.image_settings.color_mode = "RGBA"
    scene.render.image_settings.color_depth = "8"
    scene.render.film_transparent = False
    scene.render.use_stamp = False
    scene.view_settings.view_transform = "Standard"
    scene.view_settings.look = "Medium High Contrast" if "Medium High Contrast" in {i.identifier for i in scene.view_settings.bl_rna.properties["look"].enum_items} else "None"
    world = bpy.data.worlds.new("bubo.review.world")
    world.use_nodes = True
    world.node_tree.nodes["Background"].inputs["Color"].default_value = (.16, .18, .22, 1)
    world.node_tree.nodes["Background"].inputs["Strength"].default_value = .7
    scene.world = world
    center, radius = bounds(bpy)
    light = bpy.data.lights.new("bubo.review.key", "AREA")
    light.energy, light.shape, light.size = 1600*radius*radius, "DISK", 4*radius
    owner = bpy.data.objects.new("bubo.review.key", light)
    scene.collection.objects.link(owner)
    owner.location = center+type(center)((-2*radius, -3*radius, 4*radius))
    owner.rotation_euler = (center-owner.location).to_track_quat("-Z", "Y").to_euler()
    camera = bpy.data.cameras.new("bubo.review.camera")
    camera.type = "ORTHO"
    camera.clip_start, camera.clip_end = radius/10000, radius*100
    owner = bpy.data.objects.new("bubo.review.camera", camera)
    scene.collection.objects.link(owner)
    scene.camera = owner


def render(admitted, bpy, name, kind, direction, clip_id=None, sample_index=None, time=None):
    from mathutils import Vector
    require(len([p for p in admitted.output.iterdir() if p.suffix == ".png"]) < admitted.job["limits"]["maxViews"], "complete review view ceiling")
    center, radius = bounds(bpy)
    scene = bpy.context.scene
    camera = scene.camera
    camera.location = center+Vector(direction).normalized()*radius*4
    camera.rotation_euler = (center-camera.location).to_track_quat("-Z", "Y").to_euler()
    camera.data.ortho_scale = 2.4*radius
    camera.data.clip_start, camera.data.clip_end = radius/10000, radius*100
    path = admitted.output/name
    require(not path.exists(), "immutable review image exists")
    raw_bytes = 4*scene.render.resolution_x*scene.render.resolution_y+scene.render.resolution_y
    # RGBA8 scanlines + DEFLATE stored-block expansion, IDAT framing and a
    # fixed 64 KiB ancillary/header allowance, before invoking the writer.
    reserve = raw_bytes+5*((raw_bytes+16382)//16383)+12*((raw_bytes+32767)//32768)+65536
    current = admitted.output_files()
    require(len(current)+3 <= admitted.job["limits"]["maxFiles"] and sum(r["bytes"] for r in current)+reserve <= admitted.job["limits"]["maxOutputBytes"], "insufficient output reservation before fixed PNG write")
    scene.render.filepath = str(path)
    bpy.ops.render.render(write_still=True)
    admitted.output_files()
    require(path.is_file() and path.stat().st_size >= 32, "complete actual review image missing")
    data = path.read_bytes()
    require(len(data) <= reserve, "PNG exceeds conservative preflight reservation")
    require(data[:8] == b"\x89PNG\r\n\x1a\n", "actual PNG review output")
    return {"path": name, "sha256": digest(path), "bytes": len(data), "kind": kind,
            "clipId": clip_id, "sampleIndex": sample_index, "timeSeconds": time,
            "instanceIds": sorted({o["bubo.instanceId"] for o in bpy.data.objects if "bubo.instanceId" in o})}


def section(bpy, axis, offset):
    """Return reversible in-memory section mesh copies, in shared coordinates."""
    import bmesh
    from mathutils import Vector
    saved = []
    normal = Vector(tuple(float(k == "xyz".index(axis)) for k in range(3)))
    point = normal*offset
    for obj in bpy.data.objects:
        if obj.type != "MESH":
            continue
        old = obj.data
        mesh = old.copy()
        obj.data = mesh
        saved.append((obj, old, mesh))
        bm = bmesh.new()
        try:
            bm.from_mesh(mesh)
            inverse = obj.matrix_world.inverted()
            local_point = inverse@point
            local_normal = obj.matrix_world.to_3x3().transposed()@normal
            bmesh.ops.bisect_plane(bm, geom=[*bm.verts, *bm.edges, *bm.faces], dist=1e-7,
                                  plane_co=local_point, plane_no=local_normal, clear_outer=True, clear_inner=False)
            bm.to_mesh(mesh)
            mesh.update()
        finally:
            bm.free()
    return saved


def restore_section(bpy, saved):
    for obj, original, temporary in saved:
        obj.data = original
        bpy.data.meshes.remove(temporary)


def review(admitted):
    import bpy
    from mathutils import Vector
    process(admitted, bpy)
    rest, diagnostic = payload(admitted, "assembly.glb"), payload(admitted, "diagnostic.glb")
    import_actual(admitted, bpy, rest)
    configure(admitted, bpy)
    views, coverage = [], []
    for index, direction in enumerate(((1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1), (1, -1, .8), (-1, 1, .8))):
        views.append(render(admitted, bpy, "exterior."+str(index).zfill(6)+".png", "exterior", direction))
    declared_sections = admitted.assembly["design"]["sections"]
    if not declared_sections:
        # A diagnostic viewing cut is not an approved design schematic.
        center, _ = bounds(bpy)
        declared_sections = [{"axis": "z", "offset": float(center.z)}]
    for index, declared in enumerate(declared_sections):
        saved = section(bpy, declared["axis"], declared["offset"])
        try:
            direction = tuple(float(k == "xyz".index(declared["axis"])) for k in range(3))
            prefix = "section." if admitted.assembly["design"]["sections"] else "section.inspection-only."
            views.append(render(admitted, bpy, prefix+str(index).zfill(6)+".png", "section", direction))
        finally:
            restore_section(bpy, saved)
    transforms = []
    for item in admitted.assembly["design"]["instances"]:
        obj = next(o for o in bpy.data.objects if o.get("bubo.instanceId") == item["id"])
        offset = Vector(tuple(item["explodeOffset"][k] for k in "xyz"))
        for owner in [obj]+[o for o in bpy.data.objects if o.type == "ARMATURE" and o.get("bubo.owner") == item["id"]]:
            original = owner.matrix_world.copy()
            desired = original.copy()
            desired.translation += offset
            transforms.append((owner, original, desired))
    transforms.sort(key=lambda row: row[0].type != "ARMATURE")
    for owner, _, desired in transforms:
        owner.matrix_world = desired
        bpy.context.view_layer.update()
    bpy.context.view_layer.update()
    views.append(render(admitted, bpy, "exploded.000000.png", "exploded", (1, -1, .8)))
    for obj, matrix, _ in transforms:
        obj.matrix_world = matrix
        bpy.context.view_layer.update()
    importer = import_actual(admitted, bpy, diagnostic)
    configure(admitted, bpy)
    restorer = importlib.import_module("io_scene_gltf2.blender.imp.animation").BlenderAnimation
    for clip_index, clip in enumerate(admitted.motion["plan"]["clips"]):
        portable = "bubo.clip."+str(clip_index).zfill(6)
        actual = [a for a in importer.data.animations if a.name == portable]
        require(len(actual) == 1, "actual complete diagnostic review clip")
        restorer.restore_animation(importer, actual[0].track_name)
        frames = []
        for index, sample in enumerate(clip["samples"]):
            frame = 1000*sample["timeSeconds"]
            bpy.context.scene.frame_set(math.floor(frame), subframe=frame-math.floor(frame))
            bpy.context.view_layer.update()
            name = "diagnostic."+str(clip_index).zfill(6)+"."+str(index).zfill(6)+".png"
            views.append(render(admitted, bpy, name, "diagnostic", (1, -1, .8), clip["id"], index, sample["timeSeconds"]))
            frames.append(name)
        coverage.append({"clipId": clip["id"], "portableAnimationName": portable, "sampleCount": len(clip["samples"]), "frameFiles": frames})
    index = {"schemaVersion": "bubo-schematic-export-review/v1", "views": views, "clipCoverage": coverage,
             "complete": True, "humanModelAccepted": False, "runtimeAccepted": False}
    data = (json.dumps(index, sort_keys=True, separators=(",", ":"), allow_nan=False)+"\n").encode()
    require(len(data) <= 8*1024**2, "compact review index byte ceiling")
    admitted.write("review.json", data)
    admitted.unchanged(bpy)
    return admitted.report(bpy)


def main():
    args = sys.argv[sys.argv.index("--")+1:] if "--" in sys.argv else []
    return review(admit(args, "review"))


if __name__ == "__main__":
    main()
