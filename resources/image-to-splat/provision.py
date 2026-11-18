"""Fixed explicit TripoSplat provisioning. No model imports or generation."""
from pathlib import Path
import configparser
import hashlib
import io
import json
import os
import platform
import posixpath
import re
import shutil
import stat
import sys
import tarfile
import types
import urllib.parse
import zipfile

MAX_DOCUMENT = 16 * 1024 * 1024
MAX_DOWNLOAD = 8 * 1024**3
MAX_WHEEL_EXPANSION = 24 * 1024**3
MAX_WHEEL_ENTRIES = 100000
MIN_FREE_DISK = 24 * 1024**3
SCHEMA = 'bubo-splat-provision-worker/v1'
JOB_KEYS = {'schemaVersion', 'projectRoot', 'installRoot', 'downloadRoot',
            'catalog', 'catalogSha256', 'helper', 'helperSha256', 'bootstrapPython',
            'runtimeSourceRoot', 'noticeSourceRoot', 'resourceIdentities', 'licenses'}
SOURCE_NAMES = {'model.py', 'triposplat.py', 'LICENSE'}
WEIGHT_NAMES = {'background_removal/birefnet.safetensors',
                'clip_vision/dino_v3_vit_h.safetensors',
                'diffusion_models/triposplat_fp16.safetensors',
                'vae/flux2-vae.safetensors', 'vae/triposplat_vae_decoder_fp16.safetensors'}
PACKAGE_NAMES = {'torch', 'torchvision', 'numpy', 'pillow', 'safetensors', 'tqdm',
                 'filelock', 'typing-extensions', 'sympy', 'networkx', 'jinja2',
                 'fsspec', 'setuptools', 'mpmath', 'markupsafe', 'colorama'}
CRT_NAMES = {'msvcp140.dll', 'msvcp140_codecvt_ids.dll', 'vcruntime140.dll',
             'vcruntime140_1.dll', 'msvcp140_atomic_wait.dll',
             'vcruntime140_threads.dll', 'vcomp140.dll'}
VS_NOTICE_NAMES = {'VS-Redist.txt', 'VS-ThirdPartyNotices.txt'}


def require(value, message):
    if not value:
        raise ValueError(message)


def sha256(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def hash_value(value):
    require(isinstance(value, str) and re.fullmatch('[0-9a-f]{64}', value),
            'Invalid SHA-256 identity')
    return value


def no_links(path):
    path = Path(os.path.abspath(path))
    for component in reversed([path, *path.parents]):
        try:
            info = component.lstat()
        except FileNotFoundError:
            continue
        require(not stat.S_ISLNK(info.st_mode) and
                not getattr(info, 'st_file_attributes', 0) & 0x400,
                'Linked provisioning path rejected')
    return path


def inside(path, root):
    return path == root or root in path.parents


def regular(path, maximum=None):
    path = no_links(path)
    require(path.is_file() and stat.S_ISREG(path.stat().st_mode), 'Regular input file required')
    if maximum is not None:
        require(0 < path.stat().st_size <= maximum, 'Input file exceeds its bound')
    return path


def directory(path):
    path = no_links(path)
    require(path.is_dir(), 'Existing input directory required')
    return path


def relative(value):
    require(isinstance(value, str) and value and '\\' not in value and
            not Path(value).is_absolute() and ':' not in value and
            all(part not in ('', '.', '..') and not part.endswith((' ', '.'))
                for part in value.split('/')), 'Unsafe relative setup path')
    require(not any(re.fullmatch(r'(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\..*)?',
                                 part, re.IGNORECASE) for part in value.split('/')),
            'Reserved setup path rejected')
    return value


def local(value, project):
    require(isinstance(value, str) and value and '\x00' not in value,
            'Local setup path required')
    path = Path(value)
    if not path.is_absolute():
        path = project / relative(value)
    path = no_links(path)
    require(inside(path, project), 'Setup input escaped project')
    return path


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, 'Duplicate JSON member')
        result[key] = value
    return result


def document_bytes(path, maximum=MAX_DOCUMENT):
    path = regular(path, maximum)
    with path.open('rb') as stream:
        require(stat.S_ISREG(os.fstat(stream.fileno()).st_mode), 'Regular document handle required')
        data = stream.read(maximum + 1)
    require(0 < len(data) <= maximum, 'Document bytes exceed their bound')
    return data


def read(path, expected_hash=None):
    data = document_bytes(path)
    if expected_hash is not None:
        require(hashlib.sha256(data).hexdigest() == hash_value(expected_hash),
                'Document bytes differ from their admitted identity')
    value = json.loads(data.decode('utf-8'), object_pairs_hook=unique_object,
                       parse_constant=lambda value: (_ for _ in ()).throw(ValueError('Nonfinite JSON number')))
    require(isinstance(value, dict), 'Setup document must be an object')
    return value


def exact(value, keys, label):
    require(isinstance(value, dict) and set(value) == set(keys), 'Invalid ' + label + ' members')


def identity(value, project):
    exact(value, {'path', 'sha256'}, 'file identity')
    path = regular(local(value['path'], project))
    require(sha256(path) == hash_value(value['sha256']), 'Input identity changed')
    return path


def resource_path(value, root):
    require(isinstance(value, str) and Path(value).is_absolute(), 'Absolute audited resource path required')
    path = regular(value, MAX_DOCUMENT)
    require(path != root and inside(path, root), 'Resource escaped the fixed runtime bundle')
    return path


def pinned_file(value, size_key='bytes'):
    require(isinstance(value, dict), 'Pinned artifact must be an object')
    hash_value(value.get('sha256'))
    size = value.get(size_key)
    require(type(size) is int and 0 < size <= MAX_DOWNLOAD, 'Invalid pinned artifact size')
    address = urllib.parse.urlsplit(value.get('url', ''))
    require(address.scheme == 'https' and address.hostname in
            {'github.com', 'raw.githubusercontent.com', 'huggingface.co',
             'files.pythonhosted.org', 'download.pytorch.org', 'download-r2.pytorch.org'}
            and address.port in (None, 443) and address.username is None and
            address.password is None and not address.fragment,
            'Unapproved pinned HTTPS artifact URL')
    return size


def windows_host():
    return os.name == 'nt' and platform.machine().lower() in ('amd64', 'x86_64')


def load_helper(path, expected):
    path = regular(path, MAX_DOCUMENT)
    data = document_bytes(path)
    require(hashlib.sha256(data).hexdigest() == expected, 'Provision helper changed')
    module = types.ModuleType('bubo_splat_pinned_provision_helper')
    module.__file__ = str(path)
    # A distinct module name prevents the media installer's main from running.
    exec(compile(data, str(path), 'exec'), module.__dict__)
    return module


def fresh_resources(resources):
    for path, expected in resources.items():
        require(sha256(regular(path, MAX_DOCUMENT)) == expected, 'Audited provisioning resource changed')


def licence_inputs(records, catalog, project):
    require(isinstance(records, list) and 0 < len(records) <= 16, 'Bounded licence evidence required')
    components = catalog.get('licenseComponents')
    require(isinstance(components, list) and 0 < len(components) <= 16,
            'Catalog licence components required')
    required = {row['id'] for row in components}
    require(len(required) == len(components) and all(isinstance(x, str) and x for x in required),
            'Invalid catalog licence component IDs')
    accepted, paths, tasks = set(), {}, set()
    for record in records:
        exact(record, {'path', 'sha256'}, 'licence identity')
        path = regular(local(record['path'], project), MAX_DOCUMENT)
        evidence = read(path, record['sha256'])
        require(path not in paths, 'Duplicate licence document identity')
        paths[path] = hash_value(record['sha256'])
        exact(evidence, {'schemaVersion', 'task', 'backend', 'accepted', 'licenseIds', 'evidence'},
              'licence evidence')
        require(evidence['schemaVersion'] == 'bubo-splat-license-evidence/v1' and
                evidence['backend'] == 'triposplat' and evidence['accepted'] is True,
                'Applicable explicit TripoSplat licence approval required')
        task = evidence['task']
        require(isinstance(task, str) and re.fullmatch('[A-Za-z0-9_.-]{1,160}', task),
                'Licence evidence task required')
        tasks.add(task)
        ids = evidence['licenseIds']
        require(isinstance(ids, list) and 0 < len(ids) <= 16 and
                all(isinstance(x, str) for x in ids) and len(set(ids)) == len(ids) and
                set(ids) <= required, 'Unknown or duplicated licence IDs')
        accepted.update(ids)
        references = evidence['evidence']
        require(isinstance(references, list) and 0 < len(references) <= 32,
                'Actual component agreement evidence required')
        for item in references:
            referenced = identity(item, project)
            require(referenced not in paths or paths[referenced] == item['sha256'],
                    'Conflicting component evidence identity')
            paths[referenced] = item['sha256']
    require(len(tasks) == 1 and accepted == required, 'Complete same-task component licence approval required')
    return paths


def runtime_spec(catalog, catalog_path, resources, project):
    runtime = catalog.get('windowsRuntime')
    if runtime is None:
        media = no_links(catalog_path.parent.parent / 'media/catalog.json')
        require(media in resources, 'Bound media runtime catalog required')
        runtime = read(media, resources[media]).get('windowsRuntime')
    require(isinstance(runtime, dict), 'Pinned existing Windows CRT metadata required')
    files = [row for row in runtime.get('files', [])
             if row.get('license') == 'Microsoft-Visual-Studio-Redist' and
             ('backends' not in row or 'ace-step-turbo' in row['backends'] or 'triposplat' in row['backends'])]
    notices = runtime.get('notices', [])
    require(len(files) == 7 and {row['path'] for row in files} == CRT_NAMES,
            'Exact seven-file Windows CRT closure required')
    require(len(notices) == 2 and {row['path'] for row in notices} == VS_NOTICE_NAMES,
            'Exact existing Visual Studio notices required')
    for row in [*files, *notices]:
        relative(row['path'])
        hash_value(row['sha256'])
        require(type(row['bytes']) is int and 0 < row['bytes'] <= MAX_DOCUMENT,
                'Unbounded Windows runtime file')
    return files, notices


def verify_copy_input(root, record):
    path = regular(root / relative(record['path']))
    require(path.stat().st_size == record['bytes'] and sha256(path) == record['sha256'],
            'Pinned existing runtime or notice differs')
    return path


def wheel_expansion(paths):
    total, count = 0, 0
    for path in paths:
        seen = set()
        with zipfile.ZipFile(regular(path)) as archive:
            for entry in archive.infolist():
                count += 1
                require(count <= MAX_WHEEL_ENTRIES, 'Wheel entry budget exceeded')
                name = entry.orig_filename.rstrip('/')
                relative(name)
                key = name.casefold()
                require(key not in seen, 'Duplicated wheel entry')
                seen.add(key)
                mode = (entry.external_attr >> 16) & 0xF000
                require(mode in (0, stat.S_IFREG, stat.S_IFDIR), 'Linked or special wheel entry rejected')
                total += entry.file_size
                require(total <= MAX_WHEEL_EXPANSION, 'Wheel extraction budget exceeded')
    return total


def python_archive_inventory(archive, expected_hash):
    # Hash the exact compressed buffer subsequently inspected; extraction is
    # separately checked against these expected bytes before any interpreter runs.
    data = document_bytes(archive, 64 * 1024 * 1024)
    require(hashlib.sha256(data).hexdigest() == hash_value(expected_hash),
            'Pinned Python archive changed')
    files, directories, links, names = {}, set(), [], set()
    count = total = 0
    with tarfile.open(fileobj=io.BytesIO(data), mode='r:gz') as source:
        for entry in source:
            count += 1
            require(count <= 10000, 'Python archive entry limit exceeded')
            name = entry.name.rstrip('/')
            require(name == 'python' or name.startswith('python/'), 'Unexpected Python archive root')
            if name == 'python':
                require(entry.isdir(), 'Python archive root must be a directory')
                continue
            name = relative(name[7:])
            require(name.casefold() not in names, 'Duplicate Python archive entry')
            names.add(name.casefold())
            parent = posixpath.dirname(name)
            while parent:
                directories.add(parent)
                parent = posixpath.dirname(parent)
            if entry.isdir():
                directories.add(name)
            elif entry.isfile():
                require(entry.size >= 0, 'Invalid Python archive member size')
                total += entry.size
                require(total <= 1024**3, 'Python archive exceeds 1 GiB')
                stream = source.extractfile(entry)
                require(stream is not None, 'Missing Python archive content')
                hasher, length = hashlib.sha256(), 0
                with stream:
                    while chunk := stream.read(1024 * 1024):
                        length += len(chunk)
                        require(length <= entry.size, 'Python archive member exceeds declared size')
                        hasher.update(chunk)
                require(length == entry.size, 'Truncated Python archive member')
                files[name] = {'sha256': hasher.hexdigest(), 'bytes': length}
            else:
                require(entry.issym() or entry.islnk(), 'Special Python archive member rejected')
                require('\\' not in entry.linkname and not entry.linkname.startswith('/'),
                        'Unsafe Python archive link target')
                target = entry.linkname.removeprefix('python/') if entry.islnk() else \
                    posixpath.normpath(posixpath.join(posixpath.dirname(name), entry.linkname))
                links.append((name, relative(target)))
    for _ in range(32):
        pending = []
        for name, target in links:
            if target in files:
                files[name] = dict(files[target])
            else:
                pending.append((name, target))
        links = pending
        if not links:
            break
    require(not links, 'Unresolved pinned Python archive links')
    require({'python.exe', 'Lib/venv/scripts/nt/python.exe'} <= set(files),
            'Pinned base interpreter and Windows venv launcher required')
    return files, directories


def verify_python_base(base, files, directories):
    observed_files, observed_directories = set(), set()
    pending = [directory(base)]
    while pending:
        current = pending.pop()
        for path in current.iterdir():
            path = no_links(path)
            name = path.relative_to(base).as_posix()
            mode = path.stat().st_mode
            if stat.S_ISDIR(mode):
                require(name in directories, 'Unexpected directory in pinned Python base')
                observed_directories.add(name)
                pending.append(path)
            else:
                require(stat.S_ISREG(mode) and name in files, 'Unexpected file in pinned Python base')
                expected = files[name]
                require(path.stat().st_size == expected['bytes'] and sha256(path) == expected['sha256'],
                        'Archive-derived Python base file changed')
                observed_files.add(name)
    require(observed_files == set(files) and observed_directories == directories,
            'Pinned Python base layout is incomplete')


def wheel_entrypoints(paths):
    names = {'pip.exe', 'pip3.exe', 'pip3.12.exe'}
    for path in paths:
        with zipfile.ZipFile(regular(path)) as archive:
            for entry in archive.infolist():
                if not entry.filename.endswith('.dist-info/entry_points.txt'):
                    continue
                require(entry.file_size <= 65536, 'Wheel entry-point metadata exceeds its bound')
                parser = configparser.ConfigParser(interpolation=None, strict=True)
                parser.optionxform = str
                parser.read_string(archive.read(entry).decode('utf-8'))
                for section in ('console_scripts', 'gui_scripts'):
                    if not parser.has_section(section):
                        continue
                    for name in parser[section]:
                        require(re.fullmatch('[A-Za-z0-9_.-]{1,128}', name),
                                'Unsafe pinned wheel entry-point name')
                        relative(name)
                        names.update({name + '.exe', name + '-script.py'})
    return names


def verify_venv_config(environment, base):
    data = document_bytes(environment / 'pyvenv.cfg', 8192)
    values = {}
    allowed = {'home', 'include-system-site-packages', 'version', 'executable', 'command', 'prompt'}
    for line in data.decode('utf-8').splitlines():
        if not line.strip():
            continue
        key, separator, value = line.partition('=')
        key, value = key.strip().lower(), value.strip()
        require(separator and key in allowed and key not in values, 'Malformed or duplicated venv configuration')
        values[key] = value
    require({'home', 'include-system-site-packages'} <= values.keys() and
            Path(values['home']).is_absolute() and no_links(values['home']) == base and
            values['include-system-site-packages'].lower() == 'false',
            'Venv must resolve only to its verified base without system site packages')
    if 'executable' in values:
        require(Path(values['executable']).is_absolute() and
                no_links(values['executable']) == base / 'python.exe', 'Venv executable redirects outside verified base')
    if 'version' in values:
        require(values['version'] == '3.12.16', 'Venv version differs from its pinned base')


def verify_venv_scripts(environment, archive_files, crt, generated_names=None):
    expected = {'python.exe': archive_files['Lib/venv/scripts/nt/python.exe']}
    windowed = archive_files.get('Lib/venv/scripts/nt/pythonw.exe')
    if windowed is not None:
        expected['pythonw.exe'] = windowed
    crt_files = {row['path']: row for row in crt}
    templates = set()
    for name in archive_files:
        if name.startswith(('Lib/venv/scripts/common/', 'Lib/venv/scripts/nt/')):
            tail = name.split('/')[-1]
            if '/' not in name.removeprefix('Lib/venv/scripts/common/').removeprefix('Lib/venv/scripts/nt/') and \
                    not tail.lower().endswith(('.exe', '.dll', '.pyd')):
                templates.add(tail)
    optional = templates | (generated_names or set())
    seen, removable = set(), []
    for path in directory(environment / 'Scripts').iterdir():
        path = regular(path)
        name = path.name
        require(name not in seen, 'Duplicated venv Scripts sibling')
        seen.add(name)
        if name in expected or name in crt_files:
            item = expected.get(name) or crt_files[name]
            require(path.stat().st_size == item['bytes'] and sha256(path) == item['sha256'],
                    'Untrusted native sibling beside venv Python')
        else:
            require(not name.lower().endswith(('.dll', '.pyd')) and name in optional,
                    'Unknown file beside venv Python')
            removable.append(path)
    require(set(expected) <= seen, 'Pinned venv launcher closure is incomplete')
    return removable


def bind_python_execution(helper, base, environment, archive_files, archive_directories, crt, inputs,
                          generated_names):
    expected_base = {name: dict(value) for name, value in archive_files.items()}
    expected_base.update({row['path']: {'sha256': row['sha256'], 'bytes': row['bytes']} for row in crt})
    original_run = helper.run
    base_executable = no_links(base / 'python.exe')
    launcher = no_links(environment / 'Scripts/python.exe')
    expected_tools = {base_executable: archive_files['python.exe'],
                      launcher: archive_files['Lib/venv/scripts/nt/python.exe']}

    def pinned_run(args, env=None):
        fresh_resources(inputs)
        verify_python_base(base, expected_base, archive_directories)
        executable = no_links(args[0])
        require(executable in expected_tools, 'Unexpected provisioning child executable')
        expected = expected_tools[executable]
        require(regular(executable).stat().st_size == expected['bytes'] and
                sha256(executable) == expected['sha256'], 'Archive-derived Python executable changed')
        if executable == launcher:
            verify_venv_config(environment, base)
            verify_venv_scripts(environment, archive_files, crt)
        # Override the helper's observed self-admission with trusted archive ancestry.
        helper.TOOLS[os.path.normcase(str(executable))] = expected['sha256']
        result = original_run(args, env)
        if executable == launcher:
            verify_venv_config(environment, base)
            installing = 'install' in [str(value) for value in args]
            unused = verify_venv_scripts(environment, archive_files, crt,
                                         generated_names if installing else None)
            if installing:
                for path in unused:
                    helper.owned(path).unlink()
        return result

    helper.run = pinned_run
    verify_python_base(base, expected_base, archive_directories)
    helper.TOOLS[os.path.normcase(str(base_executable))] = archive_files['python.exe']['sha256']
    return expected_tools[launcher]


def clean_environment(helper, install):
    clean = helper.child_environment()
    for key in list(clean):
        if key.upper() in {'PATH', 'TEMP', 'TMP'}:
            clean.pop(key)
    system = os.environ.get('SystemRoot') or os.environ.get('SYSTEMROOT')
    require(system, 'Windows system runtime location required')
    system = directory(system)
    temporary = helper.owned(install / 'cache/tmp')
    temporary.mkdir(parents=True, exist_ok=False)
    clean.update(PATH=os.pathsep.join([str(system / 'System32'), str(system)]),
                 TEMP=str(temporary), TMP=str(temporary), TMPDIR=str(temporary),
                 PIP_CACHE_DIR=str(install / 'cache/pip'), PIP_NO_INDEX='1',
                 PYTHONDONTWRITEBYTECODE='1', CUDA_VISIBLE_DEVICES='',
                 HF_HUB_OFFLINE='1', TRANSFORMERS_OFFLINE='1', DIFFUSERS_OFFLINE='1')
    return clean


def execute(job_path, result_path, expected_job_hash):
    job_path = regular(job_path, MAX_DOCUMENT)
    job = read(job_path, expected_job_hash)
    exact(job, JOB_KEYS, 'provisioning job')
    require(job['schemaVersion'] == SCHEMA, 'Unsupported provisioning protocol')
    require(windows_host(), 'Pinned TripoSplat provisioning supports Windows x86_64 only')
    project = directory(job['projectRoot'])
    require(Path(job['projectRoot']).is_absolute() and inside(job_path, project),
            'Absolute original project and local job required')
    install, downloads = [local(job[key], project) for key in ('installRoot', 'downloadRoot')]
    require(all(Path(job[key]).is_absolute() for key in ('installRoot', 'downloadRoot')) and
            install != project and downloads != project and
            not inside(install, downloads) and not inside(downloads, install),
            'Fresh disjoint absolute install/download roots required')
    require(not install.exists() and (not downloads.exists() or downloads.is_dir()),
            'Preserve existing installation identity; select a new root')
    own_script = regular(__file__, MAX_DOCUMENT)
    require(own_script.name == 'provision.py' and own_script.parent.name == 'image-to-splat' and
            own_script.parent.parent.name == 'resources', 'Canonical installed provisioner location required')
    resource_root = directory(own_script.parent.parent)
    catalog_path = resource_path(job['catalog'], resource_root)
    helper_path = resource_path(job['helper'], resource_root)
    require(catalog_path == resource_root / 'image-to-splat/catalog.json' and
            helper_path == resource_root / 'media/provision.py', 'Exact fixed runtime resources required')
    require(sha256(helper_path) == hash_value(job['helperSha256']), 'Helper identity changed')
    catalog = read(catalog_path, job['catalogSha256'])
    require(catalog.get('schemaVersion') == 'bubo-splat-catalog/v1' and
            catalog.get('backend') == 'triposplat', 'Pinned TripoSplat catalog required')
    resources = {}
    require(isinstance(job['resourceIdentities'], list) and 2 <= len(job['resourceIdentities']) <= 256,
            'Bounded audited resource identities required')
    for record in job['resourceIdentities']:
        exact(record, {'path', 'sha256'}, 'resource identity')
        path = resource_path(record['path'], resource_root)
        require(sha256(path) == hash_value(record['sha256']), 'Audited resource identity changed')
        require(path not in resources, 'Duplicated audited resource')
        resources[path] = record['sha256']
    require(resources.get(catalog_path) == job['catalogSha256'] and
            resources.get(helper_path) == job['helperSha256'], 'Catalog/helper must be audited resources')
    licence_paths = licence_inputs(job['licenses'], catalog, project)
    runtime_root = directory(local(job['runtimeSourceRoot'], project))
    notice_root = directory(local(job['noticeSourceRoot'], project))
    crt, vs_notices = runtime_spec(catalog, catalog_path, resources, project)
    copy_inputs = [(verify_copy_input(runtime_root, row), row['path']) for row in crt]
    notice_inputs = [(verify_copy_input(notice_root, row), row['path']) for row in vs_notices]
    component_notices = catalog.get('notices')
    require(isinstance(component_notices, list) and 1 <= len(component_notices) <= 64,
            'Pinned component agreement notices required')
    notice_names = set(VS_NOTICE_NAMES)
    for row in component_notices:
        exact(row, {'resource', 'path', 'sha256', 'bytes'}, 'component notice')
        name = relative(row['path'])
        require(name.casefold() not in {x.casefold() for x in notice_names}, 'Duplicate component notice')
        notice_names.add(name)
        source = resource_path(str(resource_root.parent / relative(row['resource'])), resource_root)
        require(resources.get(source) == hash_value(row['sha256']), 'Component notice resource not admitted')
        source = regular(source, MAX_DOCUMENT)
        require(type(row['bytes']) is int and source.stat().st_size == row['bytes'], 'Component notice size differs')
        notice_inputs.append((source, name))
    bootstrap = job['bootstrapPython']
    exact(bootstrap, {'path', 'sha256'}, 'bootstrap Python')
    require(isinstance(bootstrap['path'], str) and Path(bootstrap['path']).is_absolute(),
            'Explicit existing bootstrap Python required')
    bootstrap_path = regular(bootstrap['path'])
    require(sha256(bootstrap_path) == hash_value(bootstrap['sha256']) and
            bootstrap_path == no_links(sys.executable), 'Registered bootstrap Python identity differs')
    result = local(str(result_path), project)
    require(result != project and not result.exists(), 'Fresh result identity required')
    directory(result.parent)
    protected = [job_path, resource_root, catalog_path, helper_path, bootstrap_path, runtime_root, notice_root,
                 *resources, *licence_paths]
    for root in (install, downloads):
        require(not any(inside(item, root) or inside(root, item) for item in protected),
                'Owned setup roots overlap inputs')
        require(not inside(result, root) and not inside(root, result), 'Result overlaps setup roots')
    require(not any(inside(result, item) or inside(item, result) for item in protected),
            'Result overlaps setup inputs')
    source_files = catalog.get('source', {}).get('files', [])
    weights = catalog.get('weights', [])
    require(len(source_files) == 3 and {row['path'] for row in source_files} == SOURCE_NAMES and
            len(weights) == 5 and {row['path'] for row in weights} == WEIGHT_NAMES,
            'Exact inference source and five-weight closure required')
    environment = catalog.get('environmentCandidates', {}).get('win-amd64', {})
    runtime = environment.get('python', {})
    require(runtime.get('version') == '3.12.16', 'Pinned Python 3.12.16 required')
    artifacts = environment.get('artifacts', [])
    require(len(artifacts) == 16 and {row['name'] for row in artifacts} == PACKAGE_NAMES,
            'Exact sixteen-wheel isolated environment required')
    names = set()
    for row in artifacts:
        name = relative(row['filename'])
        require('/' not in name and name.endswith('.whl') and name.casefold() not in names,
                'Invalid or duplicate wheel filename')
        names.add(name.casefold())
        require(isinstance(row.get('version'), str) and re.fullmatch('[A-Za-z0-9.+_-]+', row['version']),
                'Invalid pinned wheel version')
    require(next(row['version'] for row in artifacts if row['name'] == 'torch') == '2.7.1+cu128' and
            next(row['version'] for row in artifacts if row['name'] == 'torchvision') == '0.22.1+cu128',
            'Pinned matching Torch/TorchVision CUDA 12.8 pair required')
    total = sum(pinned_file(row) for row in [*source_files, *weights, runtime])
    total += sum(pinned_file(row, 'size') for row in artifacts)
    require(all(row['bytes'] <= 1024 * 1024 for row in source_files),
            'Inference source text exceeds its bound')
    require(total <= MAX_DOWNLOAD, 'Aggregate download budget exceeded')
    require(shutil.disk_usage(project).free >= max(MIN_FREE_DISK, 3 * total + 1024**3),
            'Insufficient free disk for bounded download/extraction and retained attempts')
    verified_inputs = dict(resources)
    verified_inputs.update(licence_paths)
    verified_inputs[job_path] = hash_value(expected_job_hash)
    verified_inputs[bootstrap_path] = bootstrap['sha256']
    for root, rows in ((runtime_root, crt), (notice_root, vs_notices)):
        for row in rows:
            verified_inputs[root / row['path']] = row['sha256']
    fresh_resources(verified_inputs)
    helper = load_helper(helper_path, job['helperSha256'])
    helper.PROJECT, helper.OWNED = project, [install, downloads]
    helper.TOOLS = {os.path.normcase(str(bootstrap_path)): bootstrap['sha256']}
    install.mkdir(parents=True, exist_ok=False)
    downloads.mkdir(parents=True, exist_ok=True)
    helper.CHILD_ENV = clean_environment(helper, install)
    models, source, wheels, notices = [install / name for name in ('models', 'source', 'wheels', 'notices')]
    for path in (models, source, wheels, notices):
        helper.owned(path).mkdir(exist_ok=False)

    def acquire(row, target, size_key='bytes'):
        fresh_resources(verified_inputs)
        helper.download(row['url'], target, row['sha256'], row[size_key])
        require(sha256(regular(target)) == row['sha256'] and target.stat().st_size == row[size_key],
                'Acquired artifact identity differs')

    for collection, destination in ((weights, models), (source_files, source)):
        for row in collection:
            cached = helper.owned(downloads / row['sha256'])
            acquire(row, cached)
            helper.copy(cached, helper.safe(destination, relative(row['path'])))
    archive = helper.owned(downloads / runtime['sha256'])
    acquire(runtime, archive)
    archive_files, archive_directories = python_archive_inventory(archive, runtime['sha256'])
    base = install / 'python'
    helper.extract(archive, base)  # Audited helper bounds this archive to 1 GiB / 10,000 entries.
    base_python = regular(base / 'python.exe')
    for source_path, name in copy_inputs:
        helper.copy(source_path, base / name)
    for row in artifacts:
        acquire(row, helper.safe(wheels, row['filename']), 'size')
    expansion = wheel_expansion([wheels / row['filename'] for row in artifacts])
    generated_names = wheel_entrypoints([wheels / row['filename'] for row in artifacts])
    require(shutil.disk_usage(install).free >= expansion + 1024**3,
            'Insufficient free disk for bounded wheel extraction')
    fresh_resources(verified_inputs)
    for row in artifacts:
        target = regular(wheels / row['filename'])
        require(target.stat().st_size == row['size'] and sha256(target) == row['sha256'],
                'Pinned wheel changed before environment installation')
    environment_root = install / 'environment'
    launcher_identity = bind_python_execution(helper, base, environment_root, archive_files,
                                               archive_directories, crt, verified_inputs, generated_names)
    executable = helper.create_environment(base_python, environment_root, base, install / 'cache', helper.CHILD_ENV)
    require(no_links(executable) == no_links(environment_root / 'Scripts/python.exe'),
            'Unexpected isolated environment interpreter')
    require(regular(executable).stat().st_size == launcher_identity['bytes'] and
            sha256(executable) == launcher_identity['sha256'], 'Pinned venv launcher differs')
    requirements = helper.owned(install / 'requirements.txt')
    with requirements.open('x', encoding='utf-8', newline='\n') as output:
        output.write(''.join(row['name'] + '==' + row['version'] + ' --hash=sha256:' +
                             row['sha256'] + '\n' for row in artifacts))
    fresh_resources(verified_inputs)
    helper.run([executable, '-I', '-B', '-m', 'pip', '--isolated', 'install', '--no-index',
                '--find-links', wheels, '--only-binary=:all:', '--require-hashes', '--no-deps',
                '--no-compile', '--prefix', environment_root, '-r', requirements], helper.CHILD_ENV)
    fresh_resources(verified_inputs)
    helper.run([executable, '-I', '-B', '-m', 'pip', '--isolated', 'check'], helper.CHILD_ENV)
    scripts = directory(environment_root / 'Scripts')
    for path in scripts.iterdir():
        path = no_links(path)
        require(path.is_file(), 'Unexpected environment Scripts directory entry')
        if path.name.lower() not in ('python.exe', 'pythonw.exe'):
            helper.owned(path).unlink()
    for source_path, name in copy_inputs:
        fresh_resources(verified_inputs)
        helper.copy(source_path, scripts / name)
    for source_path, name in notice_inputs:
        helper.copy(source_path, helper.safe(notices, name))
    fresh_resources(verified_inputs)
    for collection, root, size_key in ((weights, models, 'bytes'), (source_files, source, 'bytes'),
                                       (artifacts, wheels, 'size')):
        for row in collection:
            path = regular(root / row.get('filename', row.get('path')))
            require(path.stat().st_size == row[size_key] and sha256(path) == row['sha256'],
                    'Installed pinned artifact changed before registration handoff')
    require(sha256(job_path) == expected_job_hash and not result.exists(), 'Provision job/result identity changed')
    no_links(result)
    outcome = {'schemaVersion': 'bubo-splat-provision-worker-result/v1',
               'status': 'installed-pending-registration', 'backend': 'triposplat',
               'paths': {'modelDirectory': str(models), 'sourceDirectory': str(source),
                         'environmentRoot': str(environment_root), 'pythonBaseRoot': str(base),
                         'wheelhouseRoot': str(wheels), 'pythonArchive': str(archive),
                         'noticeDirectory': str(notices),
                         'python': {'path': str(executable), 'sha256': sha256(regular(executable))}},
               'downloadBytes': total, 'wheelExpansionBytes': expansion,
               'modelsLoaded': False, 'gpuUsed': False, 'generation': False,
               'licenseAcceptance': False, 'promotion': False}
    with result.open('x', encoding='utf-8', newline='\n') as output:
        json.dump(outcome, output, indent=2)
        output.write('\n')
    return outcome


def main():
    require(len(sys.argv) == 4, 'Usage: provision.py JOB.json RESULT.json JOB_SHA256')
    execute(sys.argv[1], sys.argv[2], sys.argv[3])


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        print('Bubo explicit TripoSplat setup failed: ' + str(error), file=sys.stderr)
        raise SystemExit(1)
