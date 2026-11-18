"""Dependency-free mocked installer fixtures. No downloads, installs, imports or GPU use."""
from pathlib import Path
import copy
import hashlib
import io
import json
import os
import sys
import tarfile
import tempfile
import types
import zipfile
from unittest.mock import patch

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
module = types.ModuleType('bubo_splat_provision_fixtures')
module.__file__ = str(ROOT / 'resources/image-to-splat/provision.py')
exec(compile(Path(module.__file__).read_bytes(), module.__file__, 'exec'), module.__dict__)
PARENT = ROOT / '.bubo/splat-provision-contract'
PARENT.mkdir(parents=True, exist_ok=True)
checks = []
# These process-wide fixture guards make accidental real setup/network calls fail.
patch('urllib.request.OpenerDirector.open', side_effect=AssertionError('Network forbidden in fixtures')).start()
patch('subprocess.run', side_effect=AssertionError('Child installation forbidden in fixtures')).start()
patch('subprocess.check_output', side_effect=AssertionError('Child process forbidden in fixtures')).start()


def check(value, label):
    assert value, label
    checks.append(label)
    print('PASS: ' + label, flush=True)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2) + '\n', encoding='utf-8', newline='\n')


def wheel_bytes(name, entry=None):
    data = io.BytesIO()
    with zipfile.ZipFile(data, 'w') as archive:
        information = zipfile.ZipInfo('placeholder')
        information.filename = entry or name.replace('-', '_') + '/__init__.py'
        archive.writestr(information, b'# mocked fixture; never imported\n')
        if entry is None and name == 'tqdm':
            archive.writestr('tqdm-1.0.dist-info/entry_points.txt', '[console_scripts]\ntqdm=tqdm.cli:main\n')
    return data.getvalue()


def runtime_bytes():
    data = io.BytesIO()
    with tarfile.open(fileobj=data, mode='w:gz') as archive:
        for name, payload in [('python/python.exe', b'mocked base Python; never executed'),
                              ('python/python312.dll', b'mocked pinned Python DLL'),
                              ('python/Lib/venv/scripts/nt/python.exe', b'mocked archive venv launcher'),
                              ('python/Lib/venv/scripts/nt/pythonw.exe', b'mocked archive windowed venv launcher'),
                              ('python/Lib/venv/scripts/nt/activate.bat', b'mocked activation template'),
                              ('python/Lib/fixture.txt', b'owned extraction fixture')]:
            entry = tarfile.TarInfo(name)
            entry.size = len(payload)
            archive.addfile(entry, io.BytesIO(payload))
    return data.getvalue()


class Fixture:
    def __init__(self, external_bundle=False):
        self.container = Path(tempfile.mkdtemp(prefix='owned-', dir=PARENT))
        self.root = self.container / 'project'
        self.root.mkdir()
        self.bundle = self.container / 'published-bundle' if external_bundle else self.root
        self.state = self.root / 'state'
        self.state.mkdir()
        self.install = self.root / 'new-install'
        self.downloads = self.root / 'download-cache'
        self.runtime = self.root / 'existing-crt'
        self.notices = self.root / 'existing-vs-notices'
        self.system = self.root / 'system'
        for path in (self.runtime, self.notices, self.system):
            path.mkdir()
        self.catalog_path = self.bundle / 'resources/image-to-splat/catalog.json'
        self.helper_path = self.bundle / 'resources/media/provision.py'
        self.helper_path.parent.mkdir(parents=True)
        self.helper_path.write_bytes((ROOT / 'resources/media/provision.py').read_bytes())
        self.notice_resource = self.bundle / 'resources/image-to-splat/notices/NOTICE.txt'
        self.notice_resource.parent.mkdir(parents=True)
        self.notice_resource.write_bytes(b'SYNTHETIC fixture notice; grants no actual agreement\n')
        self.worker_script = self.bundle / 'resources/image-to-splat/provision.py'
        self.worker_script.write_bytes(Path(module.__file__).read_bytes())
        self.evidence_path = self.root / 'inputs/agreement.txt'
        self.evidence_path.parent.mkdir()
        self.evidence_path.write_bytes(b'SYNTHETIC fixture; no human acceptance\n')
        self.bootstrap = self.root / 'bootstrap/python.exe'
        self.bootstrap.parent.mkdir()
        self.bootstrap.write_bytes(b'SYNTHETIC bootstrap identity; never executed\n')
        self.licence_path = self.root / 'inputs/licences.json'
        self.licence = {'schemaVersion': 'bubo-splat-license-evidence/v1', 'task': 'synthetic-task',
                        'backend': 'triposplat', 'accepted': True,
                        'licenseIds': ['triposplat', 'dinov3', 'flux2-autoencoder', 'birefnet', 'environment'],
                        'evidence': [{'path': str(self.evidence_path), 'sha256': module.sha256(self.evidence_path)}]}
        self.payloads = {}
        self.download_calls = []
        self.child_calls = []
        self.on_download = None
        self.before_base_execution = None
        self.after_environment_creation = None
        self.child_environment = None

        def artifact(name, payload, size_key='bytes'):
            url = 'https://huggingface.co/fixture/immutable/resolve/pin/' + name
            self.payloads[url] = payload
            return {'path': name, 'url': url, 'sha256': digest(payload), size_key: len(payload)}

        self.catalog = {'schemaVersion': 'bubo-splat-catalog/v1', 'backend': 'triposplat',
                        'source': {'files': [artifact(name, ('# fixture ' + name + '\n').encode())
                                             for name in sorted(module.SOURCE_NAMES)]},
                        'weights': [artifact(name, ('fixture checkpoint identity ' + name).encode())
                                    for name in sorted(module.WEIGHT_NAMES)],
                        'licenseComponents': [{'id': name} for name in self.licence['licenseIds']],
                        'environmentCandidates': {'win-amd64': {'python': artifact('python.tar.gz', runtime_bytes()),
                                                                'artifacts': []}},
                        'windowsRuntime': {'files': [], 'notices': []},
                        'notices': [{'resource': 'resources/image-to-splat/notices/NOTICE.txt',
                                     'path': 'components/NOTICE.txt',
                                     'sha256': module.sha256(self.notice_resource),
                                     'bytes': self.notice_resource.stat().st_size}]}
        self.catalog['environmentCandidates']['win-amd64']['python']['version'] = '3.12.16'
        for name in sorted(module.PACKAGE_NAMES):
            version = {'torch': '2.7.1+cu128', 'torchvision': '0.22.1+cu128'}.get(name, '1.0')
            filename = name.replace('-', '_') + '-' + version + '-py3-none-any.whl'
            row = artifact(filename, wheel_bytes(name), 'size')
            row.update(name=name, version=version, filename=filename)
            self.catalog['environmentCandidates']['win-amd64']['artifacts'].append(row)
        for names, location, key in [(module.CRT_NAMES, self.runtime, 'files'),
                                     (module.VS_NOTICE_NAMES, self.notices, 'notices')]:
            for name in sorted(names):
                content = ('synthetic existing runtime/notice ' + name).encode()
                (location / name).write_bytes(content)
                row = {'path': name, 'sha256': digest(content), 'bytes': len(content)}
                if key == 'files':
                    row.update(license='Microsoft-Visual-Studio-Redist', backends=['ace-step-turbo'])
                self.catalog['windowsRuntime'][key].append(row)
        self.job_path = self.state / 'job.json'
        self.result_path = self.state / 'result.json'
        self.job = {'schemaVersion': module.SCHEMA, 'projectRoot': str(self.root),
                    'installRoot': str(self.install), 'downloadRoot': str(self.downloads),
                    'catalog': str(self.catalog_path), 'catalogSha256': '',
                    'helper': str(self.helper_path), 'helperSha256': module.sha256(self.helper_path),
                    'bootstrapPython': {'path': str(self.bootstrap), 'sha256': module.sha256(self.bootstrap)},
                    'runtimeSourceRoot': str(self.runtime), 'noticeSourceRoot': str(self.notices),
                    'resourceIdentities': [{'path': str(self.catalog_path), 'sha256': ''},
                                           {'path': str(self.helper_path), 'sha256': module.sha256(self.helper_path)},
                                           {'path': str(self.notice_resource), 'sha256': module.sha256(self.notice_resource)},
                                           {'path': str(self.worker_script), 'sha256': module.sha256(self.worker_script)}],
                    'licenses': [{'path': str(self.licence_path), 'sha256': ''}]}
        self.save()

    def save(self):
        write_json(self.catalog_path, self.catalog)
        write_json(self.licence_path, self.licence)
        self.job['catalogSha256'] = module.sha256(self.catalog_path)
        self.job['resourceIdentities'][0]['sha256'] = self.job['catalogSha256']
        self.job['licenses'][0]['sha256'] = module.sha256(self.licence_path)
        write_json(self.job_path, self.job)

    def helper(self, path, expected):
        helper = ORIGINAL_LOAD(path, expected)
        require_main_not_called = lambda: (_ for _ in ()).throw(AssertionError('media main must never run'))
        helper.main = require_main_not_called

        def download(url, target, expected_hash, size):
            self.download_calls.append((url, str(target)))
            if self.on_download:
                self.on_download(helper, url, target)
            payload = self.payloads[url]
            assert digest(payload) == expected_hash and len(payload) == size
            target = helper.owned(target)
            target.parent.mkdir(parents=True, exist_ok=True)
            if target.exists():
                assert target.read_bytes() == payload
            else:
                target.write_bytes(payload)

        def create_environment(python, environment, base, state, clean):
            assert python == base / 'python.exe'
            self.child_environment = clean.copy()
            if self.before_base_execution:
                self.before_base_execution(base)
            helper.run([python, '-I', '-B', '-m', 'venv', '--without-pip', '--copies', environment], clean)
            scripts = environment / 'Scripts'
            scripts.mkdir(parents=True)
            for name in ('python.exe', 'pythonw.exe', 'activate.bat'):
                original = base / 'Lib/venv/scripts/nt' / name
                (scripts / name).write_bytes(original.read_bytes() if original.exists() else
                                            ('mocked ' + name + '; never executed').encode())
            executable = scripts / 'python.exe'
            (environment / 'pyvenv.cfg').write_text('home = ' + str(base) + '\n' +
                'include-system-site-packages = false\nversion = 3.12.16\n' +
                'executable = ' + str(base / 'python.exe') + '\n', encoding='utf-8', newline='\n')
            if self.after_environment_creation:
                self.after_environment_creation(environment)
            helper.TOOLS[os.path.normcase(str(executable))] = module.sha256(executable)
            helper.run([executable, '-I', '-B', '-c', '# mocked pip bootstrap', '--isolated', 'install',
                        '--no-index', '--only-binary=:all:', '--require-hashes', '--no-deps', '--no-compile'], clean)
            return executable

        def run(args, env=None):
            helper.fresh_tool(args[0])
            self.child_calls.append([str(value) for value in args])
            assert env['CUDA_VISIBLE_DEVICES'] == ''
            if 'install' in args:
                for name in ('pip.exe', 'tqdm.exe'):
                    (self.install / 'environment/Scripts' / name).write_bytes(b'mocked pinned-wheel entry point')

        helper.download, helper.create_environment, helper.run = download, create_environment, run
        return helper

    def run(self, job_hash=None, free_disk=100 * 1024**3):
        with patch.object(module, 'windows_host', return_value=True), \
                patch.object(module.sys, 'executable', str(self.bootstrap)), \
                patch.object(module, '__file__', str(self.worker_script)), \
                patch.object(module, 'load_helper', side_effect=self.helper), \
                patch.object(module.shutil, 'disk_usage', return_value=types.SimpleNamespace(free=free_disk)), \
                patch.dict(os.environ, {'SystemRoot': str(self.system), 'HF_TOKEN': 'SYNTHETIC_SECRET',
                                        'PIP_TARGET': str(self.root / 'injected'), 'PYTHONPATH': str(self.root / 'injected')}):
            return module.execute(self.job_path, self.result_path, job_hash or module.sha256(self.job_path))


ORIGINAL_LOAD = module.load_helper


def rejected(change, label, post_save=None):
    fixture = Fixture()
    change(fixture)
    fixture.save()
    if post_save:
        post_save(fixture)
    try:
        fixture.run()
    except (ValueError, RuntimeError, FileNotFoundError, zipfile.BadZipFile):
        check(not fixture.result_path.exists(), label)
        return fixture
    raise AssertionError('Unexpected admission: ' + label)


fixture = Fixture()
result = fixture.run()
check(result['status'] == 'installed-pending-registration' and
      all(result[key] is False for key in ('modelsLoaded', 'gpuUsed', 'generation', 'licenseAcceptance', 'promotion')),
      'mocked provisioning produces registration-pending identity without GPU/model/acceptance claims')
check(len(fixture.download_calls) == 25 and len(fixture.child_calls) == 4,
      'exact five weights, three sources, Python and sixteen wheels; only mocked venv/pip bootstrap/install/check')
check({path.relative_to(fixture.install / 'source').as_posix()
       for path in (fixture.install / 'source').rglob('*') if path.is_file()} == module.SOURCE_NAMES,
      'installed source is the closed three-file inference selection')
check(all((fixture.install / 'python' / name).read_bytes() == (fixture.runtime / name).read_bytes() and
          (fixture.install / 'environment/Scripts' / name).read_bytes() == (fixture.runtime / name).read_bytes()
          for name in module.CRT_NAMES), 'exact existing seven-file CRT copied into base and isolated interpreter directories')
check(not (fixture.install / 'environment/Scripts/pip.exe').exists() and
      not (fixture.install / 'environment/Scripts/tqdm.exe').exists(),
      'unused environment launchers trimmed only inside the fresh installation')
check((fixture.install / 'notices/components/NOTICE.txt').read_bytes() == fixture.notice_resource.read_bytes() and
      all((fixture.install / 'notices' / name).is_file() for name in module.VS_NOTICE_NAMES),
      'component agreements and original Visual Studio notices copied exactly')
install_args = fixture.child_calls[2]
check(all(flag in install_args for flag in ('-I', '-B', '--isolated', '--no-index', '--only-binary=:all:',
                                          '--require-hashes', '--no-deps', '--no-compile')) and
      fixture.child_calls[3][-1] == 'check', 'fixed offline binary/hash pip arguments preserved')
check(not any(key in fixture.child_environment for key in ('HF_TOKEN', 'PIP_TARGET', 'PYTHONPATH')) and
      fixture.child_environment['TEMP'] == str(fixture.install / 'cache/tmp') and
      str(fixture.root / 'injected') not in fixture.child_environment['PATH'],
      'child processes omit credentials/injection paths and use owned temporary storage')

for label, alter in [
    ('unknown job field rejected', lambda f: f.job.update(unknown=True)),
    ('overlapping download/install roots rejected', lambda f: f.job.update(downloadRoot=str(f.install / 'downloads'))),
    ('install/input overlap rejected', lambda f: f.job.update(installRoot=str(f.root / 'resources'))),
    ('unknown licence IDs rejected', lambda f: f.licence['licenseIds'].append('blanket-mit')),
    ('missing component licence rejected', lambda f: f.licence['licenseIds'].remove('dinov3')),
    ('licence acceptance is never inferred', lambda f: f.licence.update(accepted=False)),
    ('media licence schema rejected', lambda f: f.licence.update(schemaVersion='bubo-media-license-evidence/v1')),
    ('wrong licence backend rejected', lambda f: f.licence.update(backend='ace-step-turbo')),
    ('licence evidence paths require project locality', lambda f: f.licence['evidence'][0].update(path='../outside')),
    ('extra model weight rejected', lambda f: f.catalog['weights'].append(copy.deepcopy(f.catalog['weights'][0]))),
    ('missing source file rejected', lambda f: f.catalog['source']['files'].pop()),
    ('missing torchvision wheel rejected', lambda f: f.catalog['environmentCandidates']['win-amd64']['artifacts'].pop()),
    ('arbitrary Python version rejected', lambda f: f.catalog['environmentCandidates']['win-amd64']['python'].update(version='3.14')),
    ('unapproved download origin rejected', lambda f: f.catalog['weights'][0].update(url='https://example.invalid/model')),
    ('aggregate download bound rejected', lambda f: f.catalog['weights'][0].update(bytes=module.MAX_DOWNLOAD)),
    ('missing CRT component rejected', lambda f: f.catalog['windowsRuntime']['files'].pop()),
    ('notice traversal rejected', lambda f: f.catalog['notices'][0].update(path='../escaped')),
    ('bootstrap identity mismatch rejected', lambda f: f.job['bootstrapPython'].update(sha256='0' * 64)),
]:
    denied = rejected(alter, label)
    check(not denied.download_calls, label + ' before network/package operations')

if os.name != 'nt':
    fixture = Fixture()
    bootstrap_alias = fixture.root / 'bootstrap-link'
    bootstrap_alias.symlink_to(fixture.bootstrap)
    fixture.job['bootstrapPython']['path'] = str(bootstrap_alias)
    fixture.save()
    try:
        fixture.run()
    except ValueError as error:
        check(str(error) == 'Linked provisioning path rejected' and
              not fixture.download_calls and not fixture.child_calls and
              not fixture.install.exists() and not fixture.result_path.exists(),
              'linked bootstrap rejected before any setup mutation or child/network operation')
    else:
        raise AssertionError('Linked bootstrap was admitted')

rejected(lambda f: None, 'changed helper rejected before source execution',
         lambda f: f.helper_path.write_bytes(b'raise AssertionError("must not execute")\n'))
rejected(lambda f: None, 'changed component agreement rejected',
         lambda f: f.notice_resource.write_bytes(b'changed'))
rejected(lambda f: None, 'changed actual licence evidence rejected',
         lambda f: f.evidence_path.write_bytes(b'changed'))
fixture = Fixture()
fixture.result_path.write_bytes(b'preserve existing result')
try:
    fixture.run()
except ValueError:
    check(fixture.result_path.read_bytes() == b'preserve existing result' and not fixture.install.exists(),
          'existing result is preserved before setup mutation')
else:
    raise AssertionError('Existing result was admitted')

fixture = Fixture()
fixture.install.mkdir()
try:
    fixture.run()
except ValueError:
    check(fixture.install.is_dir() and not fixture.download_calls, 'existing installation preserved without retry/reset')
else:
    raise AssertionError('Existing installation was admitted')

fixture = Fixture()
with patch.object(module, 'windows_host', return_value=False):
    try:
        module.execute(fixture.job_path, fixture.result_path, module.sha256(fixture.job_path))
    except ValueError:
        check(not fixture.install.exists(), 'unsupported native platform rejected without fallback')
    else:
        raise AssertionError('Unsupported platform admitted')

fixture = Fixture()
with patch.object(module, 'windows_host', return_value=True), \
        patch.object(module, '__file__', str(fixture.worker_script)), \
        patch.object(module.shutil, 'disk_usage', return_value=types.SimpleNamespace(free=0)):
    try:
        module.execute(fixture.job_path, fixture.result_path, module.sha256(fixture.job_path))
    except ValueError:
        check(not fixture.install.exists(), 'insufficient disk rejected before downloads or environment creation')
    else:
        raise AssertionError('Insufficient disk admitted')

fixture = Fixture()
def interrupt_download(helper, url, target):
    partial = helper.owned(target.with_name(target.name + '.partial'))
    partial.parent.mkdir(parents=True, exist_ok=True)
    partial.write_bytes(b'partial preserved for reconciliation')
    raise RuntimeError('mocked interrupted download')
fixture.on_download = interrupt_download
try:
    fixture.run()
except RuntimeError:
    check(fixture.install.exists() and any(fixture.downloads.glob('*.partial')) and not fixture.result_path.exists(),
          'interrupted setup preserves partial downloads and failed installation without completion receipt')
else:
    raise AssertionError('Interrupted setup succeeded')

fixture = Fixture()
def change_resource(helper, url, target):
    if len(fixture.download_calls) == 1:
        fixture.notice_resource.write_bytes(b'changed during mocked acquisition')
fixture.on_download = change_resource
try:
    fixture.run()
except ValueError:
    check(len(fixture.download_calls) == 1 and not fixture.result_path.exists(),
          'audited resources revalidated between acquisitions')
else:
    raise AssertionError('Changed resource admitted')

for label, source_path in [('licence evidence', lambda f: f.evidence_path),
                           ('existing CRT', lambda f: f.runtime / next(iter(module.CRT_NAMES)))]:
    fixture = Fixture()
    def change_bound_input(helper, url, target):
        if len(fixture.download_calls) == 1:
            source_path(fixture).write_bytes(b'changed during mocked acquisition')
    fixture.on_download = change_bound_input
    try:
        fixture.run()
    except ValueError:
        check(len(fixture.download_calls) == 1 and not fixture.result_path.exists(),
              label + ' revalidated during setup before later executable operations')
    else:
        raise AssertionError('Changed bound input admitted')

fixture = Fixture()
fixture.result_path = fixture.runtime / 'result.json'
try:
    fixture.run()
except ValueError:
    check(not fixture.install.exists(), 'result cannot be published into an input runtime root')
else:
    raise AssertionError('Input-root result admitted')

fixture = Fixture()
reparse = fixture.root / 'mocked-reparse'
reparse.mkdir()
original_lstat = Path.lstat
def reparse_lstat(path, *args, **kwargs):
    if path == reparse:
        return types.SimpleNamespace(st_mode=0o40755, st_file_attributes=0x400)
    return original_lstat(path, *args, **kwargs)
with patch.object(Path, 'lstat', reparse_lstat):
    try:
        module.no_links(reparse / 'child')
    except ValueError:
        check(True, 'mocked Windows reparse ancestor rejected without following it')
    else:
        raise AssertionError('Reparse ancestor admitted')

for bad_name in ('../escaped.py', 'aliased\\file.py'):
    fixture = Fixture()
    wheel = fixture.downloads / 'adversarial.whl'
    fixture.downloads.mkdir()
    wheel.write_bytes(wheel_bytes('fixture', bad_name))
    try:
        module.wheel_expansion([wheel])
    except ValueError:
        check(not (fixture.root / 'escaped.py').exists(), 'wheel central-directory traversal/alias rejected: ' + bad_name)
    else:
        raise AssertionError('Unsafe wheel entry admitted')

fixture = Fixture()
fixture.downloads.mkdir()
wheel = fixture.downloads / 'oversized.whl'
wheel.write_bytes(wheel_bytes('fixture'))
with patch.object(module, 'MAX_WHEEL_EXPANSION', 1):
    try:
        module.wheel_expansion([wheel])
    except ValueError:
        check(True, 'wheel expansion bound enforced before pip extraction')
    else:
        raise AssertionError('Oversized wheel admitted')

fixture = Fixture()
duplicate = fixture.state / 'duplicate.json'
duplicate.write_text('{"value":1,"value":2}', encoding='utf-8')
try:
    module.read(duplicate)
except ValueError:
    check(True, 'duplicate JSON fields rejected')
else:
    raise AssertionError('Duplicate JSON admitted')

original_document_bytes = module.document_bytes
for label, selector, replacement in [
    ('job', lambda f: f.job_path, lambda f: dict(f.job, installRoot=str(f.root / 'replacement-install'))),
    ('catalog', lambda f: f.catalog_path, lambda f: dict(f.catalog, unapprovedReplacement=True)),
    ('licence', lambda f: f.licence_path, lambda f: dict(f.licence, task='replacement-task')),
]:
    fixture = Fixture()
    selected = selector(fixture)
    unapproved = (json.dumps(replacement(fixture)) + '\n').encode()
    observed = []
    def substitute_buffer(path, maximum=module.MAX_DOCUMENT):
        if Path(path) == selected:
            observed.append(path)
            return unapproved
        return original_document_bytes(path, maximum)
    with patch.object(module, 'document_bytes', side_effect=substitute_buffer):
        try:
            fixture.run()
        except ValueError:
            check(len(observed) == 1 and not fixture.install.exists() and not fixture.download_calls,
                  label + ' rejects a substituted read buffer despite an unchanged approved file')
        else:
            raise AssertionError('Unapproved ' + label + ' buffer admitted')

document = fixture.state / 'same-buffer.json'
approved_bytes = b'{"approved":true}'
replacement_bytes = b'{"approved":false}'
document.write_bytes(approved_bytes)
def replace_after_read(path, maximum=module.MAX_DOCUMENT):
    data = original_document_bytes(path, maximum)
    if Path(path) == document:
        document.write_bytes(replacement_bytes)
    return data
with patch.object(module, 'document_bytes', side_effect=replace_after_read):
    parsed = module.read(document, digest(approved_bytes))
check(parsed == {'approved': True} and document.read_bytes() == replacement_bytes,
      'document hash and strict JSON parse use the same bounded buffer after disk replacement')

fixture = Fixture()
try:
    fixture.run(free_disk=module.MIN_FREE_DISK - 1)
except ValueError:
    check(not fixture.install.exists() and not fixture.download_calls,
          '24 GiB minimum enforced even when three-times-download calculation is smaller')
else:
    raise AssertionError('Below-24-GiB setup admitted')

fixture = Fixture(external_bundle=True)
before_bundle = {path.relative_to(fixture.bundle).as_posix(): module.sha256(path)
                 for path in fixture.bundle.rglob('*') if path.is_file()}
external_result = fixture.run(free_disk=module.MIN_FREE_DISK)
after_bundle = {path.relative_to(fixture.bundle).as_posix(): module.sha256(path)
                for path in fixture.bundle.rglob('*') if path.is_file()}
check(external_result['status'] == 'installed-pending-registration' and before_bundle == after_bundle and
      not module.inside(fixture.catalog_path, fixture.root),
      'exact audited external runtime bundle remains read-only while setup writes only host-owned roots')

fixture = Fixture(external_bundle=True)
fixture.licence['evidence'] = [{'path': str(fixture.notice_resource),
                                'sha256': module.sha256(fixture.notice_resource)}]
fixture.save()
try:
    fixture.run()
except ValueError:
    check(not fixture.install.exists(), 'external resource exception does not allow external rights evidence')
else:
    raise AssertionError('External rights path admitted')

for member in ('python.exe', 'python312.dll', 'Lib/venv/scripts/nt/python.exe'):
    fixture = Fixture()
    def replace_before_base(base):
        path = base / member
        data = path.read_bytes()
        path.write_bytes(bytes([data[0] ^ 1]) + data[1:])
    fixture.before_base_execution = replace_before_base
    try:
        fixture.run()
    except ValueError:
        check(not fixture.child_calls and not fixture.result_path.exists(),
              'archive-derived identity rejects same-size ' + member + ' replacement before first execution')
    else:
        raise AssertionError('Replaced archive member admitted: ' + member)

fixture = Fixture()
def replace_launcher(environment):
    path = environment / 'Scripts/python.exe'
    data = path.read_bytes()
    path.write_bytes(bytes([data[0] ^ 1]) + data[1:])
fixture.after_environment_creation = replace_launcher
try:
    fixture.run()
except ValueError:
    check(len(fixture.child_calls) == 1 and not fixture.result_path.exists(),
          'venv launcher remains bound to archive bytes despite helper self-admitting its replacement hash')
else:
    raise AssertionError('Self-admitted replacement venv launcher admitted')

fixture = Fixture()
fixture.before_base_execution = lambda base: (base / 'injected.dll').write_bytes(b'mocked unexpected DLL')
try:
    fixture.run()
except ValueError:
    check(not fixture.child_calls, 'complete archive-derived Python layout rejects added DLL before execution')
else:
    raise AssertionError('Added Python base DLL admitted')

for label, mutate in [
    ('home redirects to another base', lambda f, e: (e / 'pyvenv.cfg').write_text(
        'home = ' + str(f.root / 'foreign-base') + '\ninclude-system-site-packages = false\n', encoding='utf-8')),
    ('system site packages enabled', lambda f, e: (e / 'pyvenv.cfg').write_text(
        'home = ' + str(f.install / 'python') + '\ninclude-system-site-packages = true\n', encoding='utf-8')),
    ('duplicated home key', lambda f, e: (e / 'pyvenv.cfg').write_text(
        'home = ' + str(f.install / 'python') + '\nhome = ' + str(f.install / 'python') +
        '\ninclude-system-site-packages = false\n', encoding='utf-8')),
    ('foreign DLL', lambda f, e: (e / 'Scripts/foreign.dll').write_bytes(b'not an admitted CRT')),
    ('foreign PYD', lambda f, e: (e / 'Scripts/foreign.pyd').write_bytes(b'not an admitted native sibling')),
    ('foreign executable', lambda f, e: (e / 'Scripts/foreign.exe').write_bytes(b'not an admitted launcher')),
    ('unknown Python sibling', lambda f, e: (e / 'Scripts/sitecustomize.py').write_bytes(b'# never executed')),
    ('changed pythonw launcher', lambda f, e: (e / 'Scripts/pythonw.exe').write_bytes(
        b'X' * (e / 'Scripts/pythonw.exe').stat().st_size)),
    ('untrusted known CRT name', lambda f, e: (e / 'Scripts/msvcp140.dll').write_bytes(b'not the pinned CRT')),
]:
    fixture = Fixture()
    fixture.after_environment_creation = lambda environment: mutate(fixture, environment)
    try:
        fixture.run()
    except ValueError:
        check(len(fixture.child_calls) == 1 and not fixture.result_path.exists(),
              'venv rejects ' + label + ' before first pip bootstrap execution')
    else:
        raise AssertionError('Unsafe venv admitted: ' + label)

for key in ('installRoot', 'downloadRoot', 'result'):
    fixture = Fixture()
    forbidden = fixture.root / 'resources/unlisted-owned-root'
    if key == 'result':
        fixture.result_path = forbidden
    else:
        fixture.job[key] = str(forbidden)
        fixture.save()
    try:
        fixture.run()
    except ValueError:
        check(not forbidden.exists() and not fixture.download_calls,
              key + ' rejects unlisted destinations beneath the complete immutable resource directory')
    else:
        raise AssertionError('Runtime resource-root destination admitted: ' + key)

print(json.dumps({'status': 'pass', 'checks': len(checks), 'network': False,
                  'softwareInstalled': False, 'modelsLoaded': False, 'gpuUsed': False,
                  'sampling': False, 'acceptance': 'synthetic fixtures only'}), flush=True)
