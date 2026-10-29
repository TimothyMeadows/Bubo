"""Original isolated engineering regression scene; never a production authoring entrypoint."""
from pathlib import Path
import bpy,sys
output=Path(sys.argv[sys.argv.index('--')+1]).resolve()
for item in list(bpy.data.objects): bpy.data.objects.remove(item,do_unlink=True)
def triangle(name,collection):
    mesh=bpy.data.meshes.new(name+'-mesh'); mesh.from_pydata([(0,0,0),(1,0,0),(0,1,0)],[],[(0,1,2)])
    item=bpy.data.objects.new(name,mesh); collection.objects.link(item)
triangle('DirectMasterObject',bpy.context.scene.collection)
child=bpy.data.collections.new('NestedCollection'); bpy.context.scene.collection.children.link(child)
triangle('NestedObject',child)
collision=bpy.data.collections.new(f'{bpy.context.scene.name}: {bpy.context.scene.collection.name}')
bpy.context.scene.collection.children.link(collision)
second=bpy.data.scenes.new('SecondScene')
triangle('SecondMasterObject',second.collection)
output.parent.mkdir(parents=True,exist_ok=True)
bpy.ops.wm.save_as_mainfile(filepath=str(output))
