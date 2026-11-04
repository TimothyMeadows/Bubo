"""Actual public receiving admission from a current schematic export fixture; no motion acceptance."""
from pathlib import Path
import argparse
import copy
import hashlib
import json
import struct
import subprocess
import tempfile

ROOT=Path(__file__).resolve().parents[1]


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--summary',type=Path,required=True)
    parser.add_argument('--cli',type=Path,required=True)
    parser.add_argument('--blender',type=Path,required=True)
    args=parser.parse_args()
    summary=args.summary.resolve(strict=True);summary.relative_to(ROOT)
    source=json.loads(summary.read_bytes());assert source['status']=='passed'
    project=Path(source['project']).resolve(strict=True);project.relative_to(ROOT)
    cli=args.cli.resolve(strict=True);blender=args.blender.resolve(strict=True)
    parent=ROOT/'.bubo/sh';parent.mkdir(parents=True,exist_ok=True)
    run=Path(tempfile.mkdtemp(prefix='r-',dir=parent))
    sha=lambda path:hashlib.sha256(path.read_bytes()).hexdigest()
    context=copy.deepcopy(json.loads((project/'context.json').read_bytes()))
    # Motion is downstream; preserve the selected visual pipeline in host policy.
    source_staging=context['roots']['staging']
    context['roots']['staging']='.art-staging'
    if source_staging!='.art-staging' and source_staging not in context['roots']['inputs']:
        context['roots']['inputs'].append(source_staging)
    context_path=project/'motion-context.json';checks=[];cases={};calls=[]
    def save(status):
        (run/'SUMMARY.json').write_text(json.dumps({'status':status,'checks':checks,'cases':cases,'project':str(project),'sourceSummary':str(summary),
            'scope':'Current public CLI receiving the actual sealed schematic package: exact handoff references, immutable ancestry, owned mixed/rigid inspection. Receiving uses the existing canonical .art-staging motion output root; source schematic staging is a read-only input. Custom motion staging-root compatibility is not covered. Synthetic fixture permissions grant no production animation/human/runtime acceptance.'},indent=2)+'\n',encoding='utf-8')
    def check(value,label):
        assert value,label
        checks.append(label);print('PASS:',label,flush=True)
    def ref(path,kind='input'):
        relative=path.relative_to(project).as_posix()
        context['references']=[r for r in context['references'] if r['path']!=relative]
        context['references'].append({'path':relative,'sha256':sha(path),'kind':kind})
        context_path.write_text(json.dumps(context,indent=2)+'\n',encoding='utf-8')
    def invoke(operation,manifest=None,extra=(),expected=None):
        command=['dotnet',str(cli)] if cli.suffix=='.dll' else [str(cli)]
        command+=['model-to-animation',operation,'--api-version','2','--root',str(project),'--host-context',str(context_path)]
        if manifest:command+=['--manifest',manifest]
        command+=list(extra)
        result=subprocess.run(command,cwd=ROOT,capture_output=True,text=True,encoding='utf-8',timeout=930)
        calls.append({'command':command,'exitCode':result.returncode,'stdout':result.stdout,'stderr':result.stderr})
        (run/f'call-{len(calls):03d}.json').write_text(json.dumps(calls[-1],indent=2)+'\n',encoding='utf-8')
        value=json.loads(result.stdout)
        check(result.returncode!=0 and expected in value.get('error','') if expected else result.returncode==0,
              'expected receiving rejection: '+expected if expected else 'actual public receiving: '+operation)
        return value
    def gltf(path):
        with path.open('rb') as stream:
            _,_,_,length,kind=struct.unpack('<5I',stream.read(20));assert kind==0x4e4f534a
            return json.loads(stream.read(length))
    try:
        for name in ('device','vessel','vehicle'):
            received=source['cases'][name]['record'];handoff_path=project/received['mechanicalHandoff']
            handoff=json.loads(handoff_path.read_bytes());model=project/handoff['model']['path'];doc=gltf(model)
            ref(model)
            scene=doc['scenes'][doc.get('scene',0)];root_node=doc['nodes'][scene['nodes'][0]]['name'];component=handoff['components'][0]['node']
            recipe={'schemaVersion':1,'profile':'mechanical-assembly','fps':12,'requiredClips':['fixture-motion'],'rig':{'mode':'preserve','rootNode':root_node},
                'parts':[],'anchors':[],'clips':[{'id':'fixture-motion','role':'receiving-fixture-no-production-motion','loop':True,'rootMotion':'in-place',
                'startFrame':0,'endFrame':6,'markers':[],'channels':[{'target':{'node':component},'property':'location','keyframes':[[0,[0,0,0]],[3,[0,0,.001]],[6,[0,0,0]]]}]}],
                'states':[],'transitions':[],'parameters':[],'effects':[],'budgets':{'maxTriangles':250000,'maxBones':64,'maxInfluences':4,'maxMaterials':32,
                    'maxTextureBytes':1048576,'maxRuntimeBytes':536870912,'maxClips':10,'maxClipSeconds':10,'maxSamples':1000,'maxPreviewFrames':100,'maxAttempts':10,
                    'timeoutSeconds':900,'maxLogBytes':8388608},'review':{'renderer':'BLENDER_WORKBENCH','fps':4,'resolution':[64,64],'cameras':['front']}}
            recipe_path=project/'inputs'/f'{name}-receiving-recipe.json';recipe_path.write_text(json.dumps(recipe,indent=2)+'\n',encoding='utf-8');ref(recipe_path)
            rights=project/'inputs'/f'{name}-receiving-rights.json';rights.write_text('{"authorized":true,"scope":"owned synthetic backend QA only; no production animation or promotion"}\n',encoding='utf-8');ref(rights,'rights')
            asset='received-'+name;manifest=f'state/model-to-animation/{asset}/v1/manifest.json'
            extra=['--asset',asset,'--version','v1','--actor',asset,'--model',handoff['model']['path'],'--recipe',recipe_path.relative_to(project).as_posix(),
                '--rights',rights.relative_to(project).as_posix(),'--blender',str(blender),'--handoff',handoff_path.relative_to(project).as_posix()]
            invoke('init',extra=extra,expected='matching registered host reference')
            check(not (project/manifest).exists(),name+': unregistered handoff fails before initialization')
            ref(handoff_path);initialized=invoke('init',extra=extra)
            check(initialized['manifest']==manifest,name+': mechanical profile uses its canonical receiving state')
            state=json.loads((project/manifest).read_bytes())
            check(state['profile']=='mechanical-assembly' and not state['approvals'] and not state['stages'],name+': source review grants no motion-stage or runtime approval')
            check(sha(project/state['mechanicalHandoff'])==sha(handoff_path) and state['mechanicalMetadata'] in state['originals'] and state['mechanicalDesign'] in state['originals'],name+': exact immutable handoff, design and mechanical metadata copied')
            for operation in ('inspect','prepare','rig'):
                invoke(operation,manifest)
            current=json.loads((project/manifest).read_bytes())
            check(all(current['stages'][key]['status']=='complete' for key in ('inspect','prepare','rig')) and not current['approvals'],name+': actual native inspection/preparation preserve rigid and owned flexible rigs without inherited approvals')
            cases[name]={'manifest':manifest,'sourceHandoff':str(handoff_path),'profile':current['profile'],'stages':current['stages']};save('in-progress')
        check(not (project/'assets').exists(),'receiving never promotes assets')
        save('passed');print(json.dumps({'status':'passed','checks':len(checks),'evidence':str(run/'SUMMARY.json')}),flush=True)
    except BaseException:
        save('failed');print('Preserved receiving evidence at '+str(run),flush=True);raise


if __name__=='__main__':main()
