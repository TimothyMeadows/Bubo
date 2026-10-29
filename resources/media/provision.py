"""Fixed, explicitly authorized media installer. Never called by generation workers."""
from pathlib import Path
import hashlib,json,os,platform,shutil,subprocess,sys,tarfile,urllib.request,urllib.parse
TOOLS={}
CHILD_ENV=None
PROJECT=None
OWNED=[]

def no_links(path):
    path=Path(os.path.abspath(path));parts=[path,*path.parents]
    for component in reversed(parts):
        try:attributes=component.lstat()
        except FileNotFoundError:continue
        require(not component.is_symlink() and not getattr(attributes,'st_file_attributes',0)&0x400,'Linked setup path rejected')
    return path

def owned(path):
    path=no_links(path)
    require(path==PROJECT or PROJECT in path.parents,'Setup path escaped original project')
    require(any(path==root or root in path.parents for root in OWNED),'Setup path escaped its declared install/download roots');return path

def copy(source,destination):
    source=no_links(source);destination=owned(destination);destination.parent.mkdir(parents=True,exist_ok=True);owned(destination);shutil.copy2(source,destination)

def fresh_tool(path):
    path=str(no_links(path));key=os.path.normcase(path);require(key in TOOLS and digest(path)==TOOLS[key],'Setup executable changed or was not admitted');return path

def fresh_resources(job):
    for identity in job['resourceIdentities']:require(digest(identity['path'])==identity['sha256'],'Audited setup resource changed')

def child_environment():
    allowed={'SYSTEMROOT','WINDIR','SYSTEMDRIVE','PATH','PATHEXT','TEMP','TMP','PROCESSOR_ARCHITECTURE','NUMBER_OF_PROCESSORS'}
    result={key:value for key,value in os.environ.items() if key.upper() in allowed}
    result.update(PYTHONDONTWRITEBYTECODE='1',PIP_CONFIG_FILE='NUL' if os.name=='nt' else '/dev/null',PIP_DISABLE_PIP_VERSION_CHECK='1',GIT_CONFIG_NOSYSTEM='1',GIT_CONFIG_GLOBAL='NUL' if os.name=='nt' else '/dev/null')
    return result

def require(value,message):
    if not value:raise ValueError(message)

def digest(path):
    with Path(path).open('rb') as stream:return hashlib.file_digest(stream,'sha256').hexdigest()

def read(path,maximum=1048576):
    path=Path(path);require(path.is_file() and 1<path.stat().st_size<=maximum,'Missing/unbounded setup document')
    return json.loads(path.read_text(encoding='utf-8'))

def safe(root,relative):
    require(not Path(relative).is_absolute(),'Archive absolute path forbidden')
    root=owned(root);path=owned(root/relative);require(path!=root and root in path.parents,'Archive escapes setup root');return path

class Redirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self,request,*args,**kwargs):
        result=super().redirect_request(request,*args,**kwargs)
        if result is not None:
            origin=urllib.parse.urlsplit(result.full_url);require(origin.scheme=='https','Insecure download redirect rejected')
            if origin.hostname!='huggingface.co' or origin.port not in (None,443) or origin.username is not None:result.remove_header('Authorization')
        return result

def download(url,path,sha,size):
    path=owned(path)
    require(urllib.parse.urlsplit(url).scheme=='https','Only pinned HTTPS downloads are supported')
    if path.exists():require(path.stat().st_size==size and digest(path)==sha,'Existing download identity differs');return
    path.parent.mkdir(parents=True,exist_ok=True);owned(path);partial=owned(path.with_name(path.name+'.partial'))
    require(not partial.exists(),'Interrupted download preserved; reconcile its .partial file before explicit retry')
    headers={'User-Agent':'Bubo-explicit-media-setup/1'}
    token=os.environ.get('HF_TOKEN')
    if token and urllib.parse.urlsplit(url).hostname=='huggingface.co':headers['Authorization']='Bearer '+token
    request=urllib.request.Request(url,headers=headers)
    try:
        with urllib.request.build_opener(Redirect()).open(request,timeout=60) as source,partial.open('xb') as target:
            total=0;hasher=hashlib.sha256()
            while chunk:=source.read(4*1024*1024):
                total+=len(chunk);require(total<=size,'Download exceeds pinned size');target.write(chunk);hasher.update(chunk)
        require(total==size and hasher.hexdigest()==sha,'Downloaded bytes differ from pinned identity');owned(partial);owned(path);partial.rename(path)
    except Exception:
        raise RuntimeError('Pinned download failed; verify access/license at '+url.split('/resolve/')[0]+' and reconcile preserved partial data') from None

def extract(archive,destination):
    archive=owned(archive);destination=owned(destination);destination.mkdir(parents=True,exist_ok=False);pending=[];count=0;total=0
    with tarfile.open(archive,mode='r|gz') as source:
        for entry in source:
            count+=1;require(count<=10000,'Python archive entry limit exceeded')
            name=entry.name.rstrip('/');require(name=='python' or name.startswith('python/'),'Unexpected Python archive root')
            if name=='python':continue
            target=safe(destination,name[7:])
            if entry.isdir():target.mkdir(parents=True,exist_ok=True);continue
            if entry.issym() or entry.islnk():
                relative=entry.linkname.removeprefix('python/') if entry.islnk() else os.path.relpath((target.parent/entry.linkname).resolve(),destination)
                pending.append((target,safe(destination,relative)));continue
            require(entry.isfile(),'Unsupported Python archive entry');total+=entry.size;require(total<=1073741824,'Python archive exceeds1GiB')
            target.parent.mkdir(parents=True,exist_ok=True)
            owned(target)
            with source.extractfile(entry) as stream,target.open('xb') as output:shutil.copyfileobj(stream,output)
            target.chmod(entry.mode&0o777);os.utime(target,(entry.mtime,entry.mtime))
    for _ in range(32):
        remaining=[]
        for target,linked in pending:
            if linked.is_file():copy(linked,target)
            else:remaining.append((target,linked))
        pending=remaining
        if not pending:break
    require(not pending,'Unresolved Python archive links')

def run(args,env=None):
    args=[str(x) for x in args];args[0]=fresh_tool(args[0]);subprocess.run(args,check=True,env=env or CHILD_ENV,stdin=subprocess.DEVNULL)

def capture(args,env=None):
    args=[str(x) for x in args];args[0]=fresh_tool(args[0]);return subprocess.check_output(args,text=True,env=env or CHILD_ENV,stdin=subprocess.DEVNULL)

def create_environment(python,environment,base,state,clean):
    # venv/ensurepip subprocesses do not propagate -B. Bootstrap from the
    # archive-bound wheel in this same explicitly isolated/no-bytecode process.
    run([python,'-I','-B','-m','venv','--without-pip','--copies',environment],clean)
    executable=environment/('Scripts/python.exe' if os.name=='nt' else 'bin/python')
    TOOLS[os.path.normcase(str(no_links(executable)))]=digest(executable)
    bundled=base/('Lib/ensurepip/_bundled' if os.name=='nt' else 'lib/python3.12/ensurepip/_bundled')
    wheels=list(bundled.glob('pip-*-py3-none-any.whl'));require(len(wheels)==1,'Pinned bootstrap pip wheel missing/ambiguous');wheel=no_links(wheels[0])
    version=wheel.name[len('pip-'):-len('-py3-none-any.whl')]
    require(all(char.isdigit() or char=='.' for char in version),'Invalid bootstrap pip version')
    requirements=owned(state/'pip-bootstrap.txt');requirements.parent.mkdir(parents=True,exist_ok=True)
    with requirements.open('x',encoding='utf-8') as output:output.write('pip=='+version+' --hash=sha256:'+digest(wheel)+'\n')
    code="import runpy,sys; wheel=sys.argv.pop(1); sys.path.insert(0,wheel); runpy.run_module('pip',run_name='__main__',alter_sys=True)"
    run([executable,'-I','-B','-c',code,wheel,'--isolated','install','--no-index','--find-links',bundled,'--only-binary=:all:','--require-hashes','--no-deps','--no-compile','--prefix',environment,'-r',requirements],clean)
    return executable

def main():
    global TOOLS,CHILD_ENV,PROJECT,OWNED
    require(len(sys.argv)==4,'Usage: provision.py JOB.json RESULT.json JOB_SHA256')
    require(digest(sys.argv[1])==sys.argv[3],'Immutable setup job changed')
    job=read(sys.argv[1]);require(job['schemaVersion']=='bubo-media-provision-worker/v1','Unsupported setup protocol')
    PROJECT=no_links(job['projectRoot']);OWNED=[no_links(job['installRoot']),no_links(job['downloadRoot'])]
    for root in OWNED:owned(root)
    TOOLS={os.path.normcase(str(no_links(x['path']))):x['sha256'] for x in job['toolIdentities']};CHILD_ENV=child_environment();fresh_resources(job)
    catalog_path=Path(job['catalog']);require(digest(catalog_path)==job['catalogSha256'],'Catalog changed')
    catalog=read(catalog_path);backend=next(x for x in catalog['backends'] if x['id']==job['backend'])
    accepted=set()
    for identity in job['licenses']:
        require(digest(identity['path'])==identity['sha256'],'License evidence changed');evidence=read(identity['path'])
        require(evidence['schemaVersion']=='bubo-media-license-evidence/v1' and evidence['accepted'] is True and backend['id'] in evidence['backends'],'Applicable user license evidence required');accepted.update(evidence['licenseIds'])
    required={x['license'] for x in backend['files']}
    if backend['id']=='stable-audio-open':required.add('Python-2.0')
    if os.name=='nt':required.update(x['license'] for x in catalog['windowsRuntime']['files'] if backend['id'] in x['backends'])
    require(required<=accepted,'Selected model/Python/runtime license evidence is incomplete; no terms accepted by setup')
    install=Path(job['installRoot']);downloads=Path(job['downloadRoot']);require(not install.exists(),'Fresh installation identity required');require(install.is_absolute() and downloads.is_absolute(),'Absolute owned roots required')
    require(install.resolve() not in downloads.resolve().parents and downloads.resolve() not in install.resolve().parents and install.resolve()!=downloads.resolve(),'Setup roots overlap')
    windows=os.name=='nt';require(platform.machine().lower() in ('amd64','x86_64'),'Only pinned x86_64 platforms are supported')
    install.mkdir(parents=True);downloads.mkdir(parents=True,exist_ok=True);models=install/'models';models.mkdir()
    temporary=owned(install/'cache/tmp');temporary.mkdir(parents=True,exist_ok=True);CHILD_ENV.update(TEMP=str(temporary),TMP=str(temporary),TMPDIR=str(temporary))
    empty_config=owned(install/'cache/git-empty');empty_config.mkdir(exist_ok=False)
    CHILD_ENV.update(GIT_CONFIG_COUNT='4',GIT_CONFIG_KEY_0='core.hooksPath',GIT_CONFIG_VALUE_0=str(empty_config),GIT_CONFIG_KEY_1='init.templateDir',GIT_CONFIG_VALUE_1=str(empty_config),GIT_CONFIG_KEY_2='protocol.allow',GIT_CONFIG_VALUE_2='never',GIT_CONFIG_KEY_3='protocol.https.allow',GIT_CONFIG_VALUE_3='always',GIT_TERMINAL_PROMPT='0')
    for component in backend['files']:
        remote=component.get('sourcePath',component['path'])
        url='https://huggingface.co/'+component['repository']+'/resolve/'+component['revision']+'/'+urllib.parse.quote(remote,safe='/')
        cached=downloads/component['sha256'];download(url,cached,component['sha256'],component['bytes']);target=safe(models,component['path']);copy(cached,target)
    if backend['id']=='stable-audio-open':
        require(digest(job['environment'])==job['environmentSha256'],'Environment lock changed');spec=read(job['environment']);runtime=catalog['pythonRuntimes']['x86_64-pc-windows-msvc' if windows else 'x86_64-unknown-linux-gnu']
        archive=downloads/runtime['sha256'];download(runtime['url'],archive,runtime['sha256'],runtime['bytes']);base=install/'python';extract(archive,base)
        python=base/('python.exe' if windows else 'bin/python3.12');environment=install/'environment';wheels=install/'wheels';wheels.mkdir()
        for artifact in spec['artifacts']:download(artifact['url'],wheels/artifact['filename'],artifact['sha256'],artifact['size'])
        clean=CHILD_ENV.copy();clean['PIP_CACHE_DIR']=str(install/'cache/pip');TOOLS[os.path.normcase(str(no_links(python)))]=digest(python)
        executable=create_environment(python,environment,base,install/'cache',clean)
        requirements=install/'requirements.txt';requirements.write_text(''.join(x['name']+'=='+x['version']+' --hash=sha256:'+x['sha256']+'\n' for x in spec['artifacts']),encoding='utf-8')
        run([executable,'-I','-B','-m','pip','--isolated','install','--no-index','--find-links',wheels,'--only-binary=:all:','--require-hashes','--no-deps','--no-compile','--prefix',environment,'-r',requirements],clean)
        run([executable,'-I','-B','-m','pip','--isolated','check'],clean)
        alias=environment/'lib64'
        if alias.is_symlink():
            require(alias.resolve()==(environment/'lib').resolve(),'Unexpected venv lib64 alias');alias.unlink()
        scripts=environment/('Scripts' if windows else 'bin');keep={'python.exe','pythonw.exe'} if windows else {'python','python3','python3.12'}
        for path in scripts.iterdir():
            if path.is_file() and path.name not in keep:owned(path).unlink()
        if windows:
            # Existing licensed release CRT files are supplied beside the registered build environment.
            require(job.get('compilerEnvironment'),'Windows Python setup needs an existing registered VS compiler environment to locate pinned CRT files')
            copy_crt(job,catalog,[base,scripts],backend['id'])
        fresh_resources(job);doctor_env=clean.copy();doctor_env['CUDA_VISIBLE_DEVICES']=''
        doctor_cache=owned(install/'cache/doctor');doctor_cache.mkdir(parents=True,exist_ok=True)
        run([executable,'-I','-B','-X','utf8',job['sfxWorker'],'--doctor',models,doctor_cache,owned(install/'metadata-preflight.json')],doctor_env)
        details={'modelDirectory':str(models),'pythonBaseRoot':str(base),'environmentRoot':str(environment),'wheelhouseRoot':str(wheels),'pythonArchive':str(archive),'python':str(executable),'noticeDirectory':str(install/'notices') if windows else None}
    else:
        source=install/'source';git=job['git'];cmake=job['cmake'];pin=backend['source']
        run([git,'clone','--filter=blob:none','--no-checkout',pin['repository'],source]);run([git,'-C',source,'-c','core.longpaths=true','checkout','--detach',pin['revision']])
        run([git,'-C',source,'-c','core.longpaths=true','submodule','update','--init','ggml']);run([git,'-C',source/'ggml','-c','core.longpaths=true','submodule','update','--init','src/ggml-cuda/vendors/cutlass'])
        require(capture([git,'-C',source/'ggml','rev-parse','HEAD']).strip()==pin['ggmlRevision'],'ggml revision differs')
        require(capture([git,'-C',source/'ggml/src/ggml-cuda/vendors/cutlass','rev-parse','HEAD']).strip()==pin['cutlassRevision'],'CUTLASS revision differs')
        build=install/'build';env=CHILD_ENV.copy()
        if windows:
            compiler=job['compilerEnvironment'];require(not any(x in compiler for x in ['"','%','!','\r','\n']),'Unsupported compiler environment path')
            fresh_tool(compiler);shell=fresh_tool(job['commandShell'])
            command='"'+shell+'" /d /s /c ""'+compiler+'" >nul && set"'
            output=subprocess.check_output(command,executable=shell,text=True,env=env,stdin=subprocess.DEVNULL)
            env.update(line.split('=',1) for line in output.splitlines() if '=' in line and not line.startswith('='))
        cuda=Path(job['cudaRoot']);require(cuda.is_dir(),'Existing CUDA toolkit required');nvcc=cuda/('bin/nvcc.exe' if windows else 'bin/nvcc')
        version=capture([nvcc,'--version'],env);require('V13.2.78' in version,'Pinned CUDA13.2.78 compiler required');fresh_resources(job)
        env['PATH']=str(cuda/'bin')+os.pathsep+env.get('PATH','')
        env.pop('CUDACXX',None);env.pop('CUDAHOSTCXX',None)
        run([cmake,'-S',source,'-B',build,'-G','NMake Makefiles' if windows else 'Unix Makefiles','-DSD_CUDA=ON','-DSD_BUILD_EXAMPLES=OFF','-DSD_WEBP=OFF','-DSD_WEBM=OFF','-DGGML_CCACHE=OFF','-DCMAKE_BUILD_TYPE=Release','-DCMAKE_CUDA_COMPILER='+str(nvcc),'-DCUDAToolkit_ROOT='+str(cuda),'-DCMAKE_CUDA_ARCHITECTURES=120a-real','-DCMAKE_PROJECT_stable-diffusion_INCLUDE='+job['workerBuild']],env)
        run([cmake,'--build',build,'--target','bubo-flux-worker','--parallel','4'],env)
        bundle=owned(install/'runtime');bundle.mkdir();worker=build/('bin/bubo-flux-worker.exe' if windows else 'bin/bubo-flux-worker');copy(worker,bundle/worker.name)
        if windows:
            copy_crt(job,catalog,[bundle],backend['id'])
            for value in catalog['windowsRuntime']['files']:
                if value['license']!='NVIDIA-CUDA-EULA':continue
                path=no_links(cuda/'bin/x64'/value['path']);require(path.is_file() and digest(path)==value['sha256'],'Pinned CUDA BLAS runtime differs');copy(path,bundle/path.name)
            for name in ['LICENSE','EULA.txt']:copy(cuda/name,bundle/('CUDA-'+name))
        else:
            raise RuntimeError('Linux native DLL closure has not been validated; build is preserved for explicit inspection/registration, no complete installation claim')
        details={'modelDirectory':str(models),'nativeRuntimeRoot':str(bundle),'worker':str(bundle/worker.name),'sourceRevision':pin['revision'],'noticeDirectory':str(install/'notices') if windows else None}
    result=no_links(sys.argv[2]);require(result==no_links(job['resultPath']) and PROJECT in result.parents,'Setup result identity escaped its bound project path')
    with result.open('x',encoding='utf-8') as output:json.dump({'schemaVersion':'bubo-media-provision-worker-result/v1','status':'installed-pending-registration','backend':backend['id'],'paths':details,'generation':False,'licenseAcceptance':False,'promotion':False},output,indent=2);output.write('\n')

def copy_crt(job,catalog,destinations,backend):
    compiler=Path(job['compilerEnvironment']);vs=compiler.parents[3];redist=vs/'VC/Redist/MSVC/14.51.36231/x64'
    for value in catalog['windowsRuntime']['files']:
        if value['license']!='Microsoft-Visual-Studio-Redist' or backend not in value['backends']:continue
        folder='Microsoft.VC145.OpenMP' if value['path']=='vcomp140.dll' else 'Microsoft.VC145.CRT';source=redist/folder/value['path']
        require(source.is_file() and digest(source)==value['sha256'],'Pinned existing Visual C++ runtime differs')
        for destination in destinations:copy(source,destination/source.name)
    for destination in destinations:
        # Notices live outside the closed base/venv trees; they are inventoried during registration.
        notices=Path(job['installRoot'])/'notices';notices.mkdir(exist_ok=True)
        for name in ['Redist.txt','ThirdPartyNotices.txt']:copy(vs/'Licenses/1033'/name,notices/('VS-'+name))

if __name__=='__main__':
    try:main()
    except Exception as error:print('Bubo explicit media setup failed: '+str(error),file=sys.stderr);raise SystemExit(1)
