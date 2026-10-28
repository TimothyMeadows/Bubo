"""Explicit host adapter for owned engineering fixtures, not a policy resolver.

It supplies synthetic host policy and receipt assertions to the real standalone
Bubo CLI. No OpenCaw checkout or instructions are loaded. Native fixture inference
and human receipts retain their explicit simulation labels.
"""
from pathlib import Path
import hashlib
import json
import os
import shutil
import subprocess

ROOT = Path(__file__).resolve().parents[1]
CLI = ROOT / 'src/Bubo.Cli/bin/Debug/net10.0/bubo.dll'
POLICY = 'fixture-policy.json'
_TOOL_EXPECTATIONS = {}


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def initialize(project, engine='THREEJS'):
    project = Path(project)
    file = project / POLICY
    # These are actual hash identities of explicitly synthetic local fixture
    # records, never claims of an OpenCaw definition or production acceptance.
    identities = {}
    for label, payload in (
        ('definition', {'id':engine,'kind':'owned-engine-fixture-definition'}),
        ('registry', {'engines':['THREEJS','BABYLONJS','PHASERJS','UNITY','UNREAL','GODOT'],'kind':'owned-fixture-registry'}),
        ('contract', {'selectedEngine':engine,'kind':'owned-fixture-contract'}),
    ):
        reference=project/f'fixture-engine-{label}.json'
        reference.write_text(json.dumps(payload,sort_keys=True)+'\n',encoding='utf-8')
        identities[label+'Sha256']=sha(reference)
    value = {'pipeline': 'PROOF_TO_MODEL', 'engine': {'effectiveEngine': engine, 'projectEngine': engine,
             'selectionSource': 'explicit-owned-test-fixture', **identities}, 'notes': 'Synthetic engineering fixture policy; no production acceptance.'}
    file.write_text(json.dumps(value, indent=2)+'\n', encoding='utf-8')
    return value['engine']


def context(project, task, arguments=()):
    project = Path(project).resolve()
    if not (project / POLICY).exists():
        initialize(project)
    policy = json.loads((project / POLICY).read_text(encoding='utf-8'))
    references = {POLICY: {'path': POLICY, 'sha256': sha(project / POLICY), 'kind': 'policy'}}
    tools = {}

    def add_tool(path):
        path = Path(path).resolve()
        if path.is_file():
            key = (str(project), str(path))
            # Pin the expectation on first registration. The production Bubo
            # boundary still rehashes actual bytes; rebuilding expectations on
            # each request would silently bless a changed fixture executable.
            if key not in _TOOL_EXPECTATIONS:
                _TOOL_EXPECTATIONS[key] = sha(path)
            tools[str(path)] = {'path': str(path), 'sha256': _TOOL_EXPECTATIONS[key], 'kind': 'fixture-tool'}

    blender = os.environ.get('BLENDER_EXE') or shutil.which('blender') or 'C:/Program Files/Blender Foundation/Blender 4.5/blender.exe'
    add_tool(blender)
    nvidia = shutil.which('nvidia-smi')
    if nvidia:
        add_tool(nvidia)
    for index, value in enumerate(arguments):
        if str(value) == '--blender' and index+1 < len(arguments):
            add_tool(arguments[index+1])
    for file in project.rglob('*.exe'):
        if file.is_file():
            add_tool(file)
    # The fixture toolchain itself declares an already built, hash-bound simulated
    # inference executable. No source path or callback is run by this adapter.
    toolchain_file = project / 'toolchain.json'
    if toolchain_file.is_file():
        toolchain = json.loads(toolchain_file.read_text(encoding='utf-8'))
        for kind in ('engine', 'blender'):
            file = toolchain.get(kind, {}).get('path')
            if file and (project / file).is_file():
                add_tool(project / file)
    input_flags = {'--rights', '--recipe', '--toolchain', '--selection', '--library-search', '--evidence', '--proof-set', '--classification',
                   '--finish-settings', '--finish-budget', '--revision', '--source-review', '--sources', '--proof', '--model', '--motion-package',
                   '--motion-source', '--mapping', '--brief', '--proof-metadata', '--source-metadata', '--editable-source', '--artifact', '--source'}
    for index, argument in enumerate(arguments):
        if str(argument) not in input_flags or index+1 >= len(arguments):
            continue
        file = Path(arguments[index+1])
        file = file if file.is_absolute() else project / file
        if file.is_file() and str(file.resolve()) not in tools:
            relative = file.resolve().relative_to(project).as_posix()
            references[relative] = {'path': relative, 'sha256': sha(file), 'kind': 'rights' if str(argument)=='--rights' else 'review' if str(argument) in ('--evidence','--source-review') else 'input'}
    inputs = [path.name for path in project.iterdir() if path.is_dir() and path.name not in ('.git','.opencaw','.art-staging','.local','assets')]
    # Existing originals may be direct root files; each is an exact read-only
    # reference instead of a broad project-root grant.
    for file in project.iterdir():
        if file.is_file() and file.name != 'host-context.json' and str(file.resolve()) not in tools and file.name not in references:
            references[file.name] = {'path':file.name,'sha256':sha(file),'kind':'policy' if file.name.startswith('fixture-engine-') else 'input'}
    value = {'schemaVersion':'bubo-host-context/v1','projectRoot':str(project),'task':task,
             'roots':{'state':f'.opencaw/.cache/tasks/{task}','staging':'.art-staging','tools':['.local/tools'], 'inputs':inputs, 'outputs':['assets/models']},
             'policy':{'pipeline':policy['pipeline'],'styleSha256':sha(project/POLICY),'engine':policy['engine'],'libraries':[]},
             'references':list(references.values()), 'tools':list(tools.values()),
             'budgets':{'timeoutSeconds':3600,'maxLogBytes':67108864},
             'permissions':{'provisioning':False,'promotion':True}}
    # Promotion=true here permits only the original explicitly synthetic fixture
    # promotion tests; workflow review gates still apply and no real acceptance is
    # created. Runtime clients must supply their own reviewed host permissions.
    path = project / 'host-context.json'
    path.write_text(json.dumps(value, indent=2)+'\n', encoding='utf-8')
    return path


def prepare(arguments, project, workflow):
    arguments = list(map(str, arguments))
    try:
        index = next(i for i, value in enumerate(arguments) if value.endswith(('bubo.dll','bubo.exe')))
    except StopIteration:
        return arguments, False
    operation = arguments[index+1]
    tail = arguments[index+2:]
    while '--baseline-root' in tail:
        i = tail.index('--baseline-root')
        del tail[i:i+2]
    task = tail[tail.index('--task')+1] if '--task' in tail else None
    if task is None and '--manifest' in tail:
        manifest = Path(project) / tail[tail.index('--manifest')+1]
        if manifest.is_file():
            task = json.loads(manifest.read_text(encoding='utf-8'))['task']
    task = task or 'test'
    host = context(project,task,tail)
    return arguments[:index+1] + [workflow,operation,'--api-version','1','--host-context',str(host),*tail], True


def unwrap(result):
    """Translate v1 result data only for unchanged legacy fixture assertions."""
    raw = result.stdout.decode('utf-8') if isinstance(result.stdout,bytes) else result.stdout
    if not raw.strip():
        return result
    value = json.loads(raw)
    if result.returncode == 0:
        data = value.get('data')
        text = json.dumps(data) if isinstance(data,(dict,list)) else str(data or '')
        result.stdout = text.encode('utf-8') if isinstance(result.stdout,bytes) else text
    return result
