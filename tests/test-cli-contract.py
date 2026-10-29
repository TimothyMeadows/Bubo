"""Exercise public CLI failures before side effects, identities and resource tampering.

Uses synthetic local policy only. No native tools, model downloads or generation.
"""
from pathlib import Path
import argparse
import hashlib
import json
import shutil
import subprocess
import tempfile

ROOT=Path(__file__).resolve().parents[1]

def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--cli',type=Path,default=ROOT/'artifacts/bubo/bubo.dll')
    args=parser.parse_args()
    cli=args.cli.resolve()
    assert cli.is_file(),'Build the CLI explicitly first.'
    parent=ROOT/'.bubo/cli-contract'; parent.mkdir(parents=True,exist_ok=True)
    work=Path(tempfile.mkdtemp(prefix='run-',dir=parent))
    project=work/'project'; project.mkdir()
    checks=[]
    def check(value,message):
        assert value,message
        checks.append(message)
        print('PASS:',message,flush=True)
    def invoke(arguments,expected=None,selected=cli):
        command=['dotnet',str(selected)] if selected.suffix=='.dll' else [str(selected)]
        result=subprocess.run([*command,*arguments],cwd=ROOT,capture_output=True,text=True,encoding='utf-8',timeout=30)
        record={'arguments':arguments,'exitCode':result.returncode,'stdout':result.stdout,'stderr':result.stderr}
        (work/f'call-{len(list(work.glob("call-*.json"))):03d}.json').write_text(json.dumps(record,indent=2)+'\n',encoding='utf-8')
        value=json.loads(result.stdout)
        if expected:
            check(result.returncode!=0 and value['status']=='failed' and expected in value['error'],expected)
        else:
            check(result.returncode==0,'public capabilities succeeds')
        return value
    capabilities=invoke(['capabilities','--api-version','1'])
    check(capabilities['apiVersions']==[1] and capabilities['mediaBackends']==[],'API and extraction-only capability identities')
    check(all(len(value)==64 for value in capabilities['build']['assemblies'].values()),'all shipped assemblies have build identities')
    invoke(['capabilities','--api-version','999'],'Explicit --api-version 1')
    invoke(['capabilities','--api-version','1','--api-version','1'],'Duplicate option')
    invoke(['proof-to-model','finish','--api-version','1','--callback','arbitrary.py'],'Unknown or inapplicable option')
    def sha(path): return hashlib.sha256(path.read_bytes()).hexdigest()
    policy=project/'policy.json'; policy.write_text('{"kind":"owned-contract-fixture"}\n',encoding='utf-8')
    context={'schemaVersion':'bubo-host-context/v1','projectRoot':str(project),'task':'contract',
        'roots':{'state':'state','staging':'staging','tools':['tools'],'inputs':['inputs'],'outputs':['outputs']},
        'policy':{'pipeline':'PROOF_TO_MODEL','styleSha256':sha(policy),'engine':{'effectiveEngine':'THREEJS',
            'selectionSource':'default','definitionSha256':'a'*64,'registrySha256':'b'*64,'contractSha256':None},'libraries':[]},
        'references':[{'path':'policy.json','sha256':sha(policy),'kind':'policy'}],'tools':[],
        'budgets':{'timeoutSeconds':30,'maxLogBytes':1048576},'permissions':{'promotion':False,'provisioning':False}}
    context_path=project/'context.json'
    def write_context(): context_path.write_text(json.dumps(context,indent=2)+'\n',encoding='utf-8')
    write_context()
    common=['--api-version','1','--root',str(project),'--host-context',str(context_path)]
    original_policy=context['policy']
    context['roots']['state']='inspection-state'
    context['policy']={'purpose':'read-only-blender-inspection','receipt':{'path':'policy.json','sha256':sha(policy)}}
    write_context()
    invoke(['proof-to-model','status',*common,'--manifest','state/old.json'],'Inspection-only context cannot execute an asset workflow')
    invoke(['blender-inspection','inspect',*common,'--manifest','unregistered.json'],'matching registered host reference')
    context['policy']['pipeline']='BLENDER'; write_context()
    invoke(['blender-inspection','inspect',*common],'must not assert art or engine selections')
    del context['policy']['pipeline']; context['permissions']['provisioning']=True; write_context()
    invoke(['blender-inspection','inspect',*common],'cannot authorize provisioning or promotion')
    context['permissions']['provisioning']=False; context['policy']=original_policy; context['roots']['state']='state'; write_context()
    invoke(['proof-to-model','preprocess',*common,'--mode','single-view-diagnostic','--manifest','state/missing.json'],'Could not find')
    invoke(['model-to-action','init',*common,'--asset','owned','--version','v1','--manifest','state/custom.json'],'Action init requires its canonical manifest path')
    check(not (project/'state').exists() and not (project/'staging').exists(),'noncanonical action init rejects before any workflow write')
    invoke(['proof-to-model','setup',*common,'--execute'],'does not permit explicit provisioning')
    invoke(['proof-to-model','setup',*common,'--execute','false'],'--execute accepts only the explicit true flag')
    invoke(['proof-to-model','promote',*common,'--destination','outputs/model'],'does not permit promotion')
    del context['policy']['engine']['definitionSha256']; write_context()
    invoke(['model-to-animation','status',*common,'--manifest','state/old.json'],'requires explicit host engine definitionSha256')
    context['policy']['engine']['definitionSha256']='a'*64; write_context()
    state=project/'state'; state.mkdir()
    manifest=state/'old.json'; manifest.write_text(json.dumps({'task':'contract'})+'\n',encoding='utf-8')
    before=manifest.read_bytes()
    invoke(['proof-to-model','status',*common,'--manifest','state/old.json'],'initialize a new asset version')
    check(manifest.read_bytes()==before and not (state/'bubo-executions').exists(),'historical manifest rejection preserves evidence and creates no execution')
    fingerprint={key:context['policy'][key] for key in ('pipeline','styleSha256','engine','libraries')}
    old={'task':'contract','hostPolicySha256':hashlib.sha256(json.dumps(fingerprint,sort_keys=True,separators=(',',':')).encode()).hexdigest(),
         'runtimeBuildSha256':'0'*64}
    manifest.write_text(json.dumps(old)+'\n',encoding='utf-8'); before=manifest.read_bytes()
    invoke(['proof-to-model','status',*common,'--manifest','state/old.json'],'Bubo build identity changed')
    check(manifest.read_bytes()==before,'changed runtime build cannot reuse or rewrite prior approvals')
    isolated=work/'runtime'; shutil.copytree(cli.parent,isolated)
    launch=isolated/'bubo.runtimeconfig.json'
    launch.write_bytes(launch.read_bytes()+b'\n')
    changed=invoke(['capabilities','--api-version','1'],selected=isolated/cli.name)
    check(changed['build']!=capabilities['build'],'launch configuration changes the runtime build identity')
    old['runtimeBuildSha256']=hashlib.sha256(json.dumps(capabilities['build'],separators=(',',':')).encode()).hexdigest()
    manifest.write_text(json.dumps(old)+'\n',encoding='utf-8')
    invoke(['proof-to-model','status',*common,'--manifest','state/old.json'],'Bubo build identity changed',isolated/cli.name)
    resource=isolated/'resources/blender/inspect-blender-scene.py'
    resource.write_bytes(resource.read_bytes()+b'\n# altered fixture bytes\n')
    invoke(['capabilities','--api-version','1'],'audited resource is missing or changed',isolated/cli.name)
    (work/'summary.json').write_text(json.dumps({'checks':len(checks),'passed':True,'nativeExecution':False,'cliSha256':sha(cli)},indent=2)+'\n',encoding='utf-8')
    print('PASS:',len(checks),'CLI contract assertions;',work/'summary.json')

if __name__=='__main__': main()
