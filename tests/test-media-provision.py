"""Owned adversarial installer fixtures. No network, software installation or model execution."""
from pathlib import Path
import hashlib,importlib.util,io,json,os,subprocess,sys,tarfile,tempfile
from unittest.mock import patch

ROOT=Path(__file__).resolve().parents[1]
sys.dont_write_bytecode=True
spec=importlib.util.spec_from_file_location('bubo_fixed_provision',ROOT/'resources/media/provision.py');provision=importlib.util.module_from_spec(spec);spec.loader.exec_module(provision)
parent=ROOT/'.bubo/provision-contract';parent.mkdir(parents=True,exist_ok=True)
root=Path(tempfile.mkdtemp(prefix='owned-',dir=parent));install=root/'install';cache=root/'cache';install.mkdir();cache.mkdir()
provision.PROJECT=root;provision.OWNED=[install,cache];checks=[];unavailable=[]
def check(value,name):
    assert value,name;checks.append(name);print('PASS:',name,flush=True)
def denied(action,name):
    try:action()
    except ValueError:check(True,name);return
    raise AssertionError('Unexpected admission: '+name)

with patch.dict(os.environ,{'HF_TOKEN':'OWNED_TEST_SENTINEL','GITHUB_TOKEN':'OWNED_TEST_SENTINEL','PIP_TARGET':str(root/'outside'),'PIP_PREFIX':str(root/'outside'),'PIP_ROOT':str(root/'outside'),'AWS_SECRET_ACCESS_KEY':'OWNED_TEST_SENTINEL'}):
    environment=provision.child_environment()
    check(not any(name in environment for name in ['HF_TOKEN','GITHUB_TOKEN','PIP_TARGET','PIP_PREFIX','PIP_ROOT','AWS_SECRET_ACCESS_KEY']),'child environment omits credentials and inherited pip destinations')
check(environment['GIT_CONFIG_NOSYSTEM']=='1' and environment['GIT_CONFIG_GLOBAL'] in ('NUL','/dev/null'),'child Git ignores system/global configuration')

request=provision.urllib.request.Request('https://huggingface.co/owned',headers={'Authorization':'Bearer OWNED_TEST_SENTINEL'})
redirect=provision.Redirect()
def target(url):return redirect.redirect_request(request,None,302,'Found',{},url)
denied(lambda:target('http://huggingface.co/owned'),'HTTPS downgrade rejected before credential forwarding')
check(target('https://huggingface.co/other').get_header('Authorization')=='Bearer OWNED_TEST_SENTINEL','exact approved HTTPS origin may retain download authorization')
check(target('https://example.com/other').get_header('Authorization') is None,'cross-origin redirect strips download authorization')
check(target('https://huggingface.co:444/other').get_header('Authorization') is None,'different HTTPS port strips download authorization')

denied(lambda:provision.owned(root/'outside/file'),'installer rejects writes outside both owned roots')
denied(lambda:provision.safe(install,'../cache/cross-root'),'archive cannot cross from install to download root')
tool=install/'owned-tool';tool.write_bytes(b'fixture-executable-identity-only')
provision.TOOLS={os.path.normcase(str(tool)):provision.digest(tool)}
check(provision.fresh_tool(tool)==str(tool),'admitted tool hash is rechecked before use')
tool.write_bytes(b'modified');denied(lambda:provision.fresh_tool(tool),'changed tool rejected before execution')

archive=cache/'owned.tar.gz'
with tarfile.open(archive,'w:gz') as output:
    empty=tarfile.TarInfo('python/empty');empty.size=0;output.addfile(empty,io.BytesIO())
    payload=b'owned archive fixture';entry=tarfile.TarInfo('python/Lib/owned.txt');entry.size=len(payload);output.addfile(entry,io.BytesIO(payload))
extracted=install/'python';provision.extract(archive,extracted)
check((extracted/'empty').read_bytes()==b'' and (extracted/'Lib/owned.txt').read_bytes()==payload,'fixed extractor preserves empty and regular archive files')
attack=cache/'escape.tar.gz'
with tarfile.open(attack,'w:gz') as output:
    entry=tarfile.TarInfo('python/../../escaped');entry.size=1;output.addfile(entry,io.BytesIO(b'x'))
denied(lambda:provision.extract(attack,install/'rejected'),'archive traversal rejected')
check(not (root/'escaped').exists(),'rejected archive created no escaped file')

link=install/'linked'
try:link.symlink_to(cache,target_is_directory=True)
except OSError:
    if os.name=='nt':
        powershell=Path(os.environ['SystemRoot'])/'System32/WindowsPowerShell/v1.0/powershell.exe'
        literal=lambda value:"'"+str(value).replace("'","''")+"'"
        script='New-Item -ItemType Junction -Path '+literal(link)+' -Target '+literal(cache)+' -ErrorAction Stop | Out-Null'
        result=subprocess.run([str(powershell),'-NoProfile','-NonInteractive','-Command',script],capture_output=True,text=True)
        if result.returncode==0:denied(lambda:provision.owned(link/'file'),'native directory junction rejected')
        else:unavailable.append('native link/junction creation unavailable; reparse fixture not run')
    else:unavailable.append('native link creation unavailable; reparse fixture not run')
else:denied(lambda:provision.owned(link/'file'),'native linked directory rejected')

if os.name=='nt':
    system=Path(os.environ['SystemRoot'])/'System32';shell=system/'cmd.exe';where=system/'where.exe'
    command='"'+str(shell)+'" /d /s /c ""'+str(where)+'" cmd.exe >nul && echo BUBO_OWNED_QUOTE_OK"'
    output=subprocess.check_output(command,executable=str(shell),text=True,env=environment,stdin=subprocess.DEVNULL)
    check(output.strip()=='BUBO_OWNED_QUOTE_OK','raw Windows command line preserves quoted executable paths')
    batch=install/'compiler environment.bat';batch.write_bytes(b'@echo off\r\nset "BUBO_ENV_FIXTURE=owned"\r\n')
    command='"'+str(shell)+'" /d /s /c ""'+str(batch)+'" >nul && set BUBO_ENV_FIXTURE"'
    output=subprocess.check_output(command,executable=str(shell),text=True,env=environment,stdin=subprocess.DEVNULL)
    check(output.strip()=='BUBO_ENV_FIXTURE=owned','quoted batch environment capture retains its selected compiler environment')

report={'status':'pass','checks':len(checks),'unavailable':unavailable,'installerSha256':hashlib.sha256((ROOT/'resources/media/provision.py').read_bytes()).hexdigest(),'network':False,'softwareInstalled':False,'modelsLoaded':False,'sampling':False}
(root/'summary.json').write_text(json.dumps(report,indent=2)+'\n');print(json.dumps(report),flush=True)
