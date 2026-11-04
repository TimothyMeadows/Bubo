"""Original mixed-assembly regression fixture; no external assets or acceptance."""
import argparse
import json
from pathlib import Path
import sys
sys.dont_write_bytecode = True


def main():
    import bpy
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    output = Path(parser.parse_args(sys.argv[sys.argv.index("--") + 1:]).output).resolve()
    output.relative_to(Path(__file__).resolve().parents[3])
    output.mkdir(parents=True, exist_ok=True)
    bpy.ops.wm.read_factory_settings(use_empty=True)
    root = bpy.data.objects.new("AssemblyRoot", None)
    bpy.context.scene.collection.objects.link(root)
    components = []
    for index in range(3):
        bpy.ops.mesh.primitive_cube_add(size=.1, location=(index * .3, 0, .1))
        mesh = bpy.context.object
        mesh.name = "bubo.instance." + str(index).zfill(6)
        mesh.parent = root
        component = {"instanceId": "instance-" + str(index), "partId": "part-" + str(index), "node": mesh.name, "kind": "rigid", "rigNode": None}
        if index:
            armature = bpy.data.armatures.new("armature-" + str(index))
            rig = bpy.data.objects.new("bubo.rig." + str(index).zfill(6), armature)
            bpy.context.scene.collection.objects.link(rig)
            rig.parent = root
            bpy.ops.object.select_all(action="DESELECT")
            rig.select_set(True)
            bpy.context.view_layer.objects.active = rig
            bpy.ops.object.mode_set(mode="EDIT")
            bone = armature.edit_bones.new("bubo.bone." + str(index).zfill(6) + ".000000")
            bone_name = bone.name
            bone.head = (index * .3, 0, 0)
            bone.tail = (index * .3, 0, .3)
            bpy.ops.object.mode_set(mode="OBJECT")
            group = mesh.vertex_groups.new(name=bone_name)
            group.add(list(range(len(mesh.data.vertices))), 1, "REPLACE")
            modifier = mesh.modifiers.new("OwnedFlex", "ARMATURE")
            modifier.object = rig
            component.update(kind="flexible", rigNode=rig.name)
        components.append(component)
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.export_scene.gltf(filepath=str(output / "assembly.glb"), export_format="GLB", export_animations=False, export_def_bones=False)
    (output / "components.json").write_text(json.dumps(components), encoding="utf-8")


if __name__ == "__main__":
    main()
