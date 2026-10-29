"""Real read-only inspection regression with duplicate-named scene master collections."""
from pathlib import Path
import hashlib,json,os,shutil,subprocess,tempfile
import host_fixture
ROOT=Path(__file__).resolve().parents[1]
BLENDER=os.environ.get('BLENDER_EXE') or shutil.which('blender') or 'C:/Program Files/Blender Foundation/Blender 4.5/blender.exe'
CLI=Path(os.environ.get('BUBO_TEST_CLI',ROOT/'src/Bubo.Cli/bin/Debug/net10.0/bubo.dll'))
def sha(path): return hashlib.sha256(path.read_bytes()).hexdigest()
def main():
    assert Path(BLENDER).is_file() and CLI.is_file(),'Configure existing Blender4.5 and explicitly build the CLI.'
    parent=ROOT/'.bubo/inspection-regression'; parent.mkdir(parents=True,exist_ok=True)
    project=Path(tempfile.mkdtemp(prefix='i-',dir=parent))
    scene=project/'inputs/collections.blend'
    fixture=ROOT/'tests/fixtures/blender-inspection/create_scene.py'
    authored=subprocess.run([BLENDER,'--background','--factory-startup','--disable-autoexec','--offline-mode','--python-exit-code','1','--python',str(fixture),'--',str(scene)],capture_output=True,timeout=60)
    (project/'author.log').write_bytes(authored.stdout+authored.stderr)
    assert authored.returncode==0,authored.stderr.decode(errors='replace')
    before=sha(scene)
    job=project/'job.json'; job.write_text(json.dumps({'schemaVersion':1,'scene':'inputs/collections.blend','profile':'static-asset','output':'.art-staging/report.json'})+'\n',encoding='utf-8')
    context=host_fixture.context(project,'inspection',['--source','inputs/collections.blend'])
    inspection_context=json.loads(context.read_text(encoding='utf-8'))
    inspection_context['policy']={'purpose':'read-only-blender-inspection','receipt':{'path':host_fixture.POLICY,'sha256':sha(project/host_fixture.POLICY)}}
    inspection_context['permissions']={'provisioning':False,'promotion':False}
    context.write_text(json.dumps(inspection_context,indent=2)+'\n',encoding='utf-8')
    assert not (project/'STYLE.md').exists() and not (project/'ENGINE.md').exists()
    result=subprocess.run(['dotnet',str(CLI),'blender-inspection','inspect','--api-version','1','--root',str(project),'--host-context',str(context),'--manifest','job.json','--blender',BLENDER],capture_output=True,timeout=120)
    (project/'result.json').write_bytes(result.stdout); (project/'inspection.log').write_bytes(result.stderr)
    assert result.returncode==0,result.stderr.decode(errors='replace')
    report=json.loads((project/'.art-staging/report.json').read_text())
    collections={item['id']:item for item in report['collections']}
    objects={item['name']:item for item in report['objects']}
    direct=objects['DirectMasterObject']['collectionIds'][0]; second=objects['SecondMasterObject']['collectionIds'][0]
    assert direct!=second and collections[direct]['parentId'] is None and collections[second]['parentId'] is None
    nested=objects['NestedObject']['collectionIds'][0]
    assert collections[nested]['parentId']==direct
    assert len(collections)==len(report['collections']) and all(key in collections for item in objects.values() for key in item['collectionIds'])
    assert len({item['name'] for item in collections.values()})==len(collections),'v1 consumer requires unique collection names'
    assert any(item['name']=='Scene: Scene Collection' and item['id'].startswith('collection-') for item in collections.values()),'existing ordinary collection names/IDs remain compatible'
    assert all(item['name']!='Render Result' for item in report['images']) and any(item['code']=='transient-image-buffer' for item in report['findings']),'transient render buffers are explained rather than reported as missing files'
    assert sha(scene)==before==report['source']['sha256']
    summary={'status':'pass','assertions':8,'realBlender':True,'sourceUnchanged':True,'duplicateNamedMastersHaveDistinctIds':True,'uniqueCollectionNames':True,'ordinaryNamesPreserved':True,'transientBuffersExplained':True,'nestedParentPreserved':True,'sourceSha256':before,'fixtureScriptSha256':sha(fixture),'reportSha256':sha(project/'.art-staging/report.json'),'productionAcceptance':False}
    (project/'summary.json').write_text(json.dumps(summary,indent=2)+'\n',encoding='utf-8')
    print('PASS: 8 actual inspection assertions;',project/'summary.json')
if __name__=='__main__':main()
