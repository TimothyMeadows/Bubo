"""Dependency-free CPU/mock worker fixtures. No real Pillow, Torch, models or GPU."""
from contextlib import ExitStack, contextmanager, nullcontext
from pathlib import Path
from types import ModuleType, SimpleNamespace
from unittest.mock import Mock, patch
import copy
import hashlib
import importlib.util
import json
import os
import stat
import struct
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.dont_write_bytecode = True
SPEC = importlib.util.spec_from_file_location('bubo_splat_worker', ROOT / 'resources/image-to-splat/worker.py')
worker = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(worker)


def digest(payload):
    return hashlib.sha256(payload).hexdigest()


@contextmanager
def fixture():
    parent = ROOT / '.bubo' / 'splat-worker-tests'
    parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='owned-', dir=parent) as temporary:
        root = Path(temporary).absolute()
        for name in ('source', 'model', 'state', 'inputs'):
            (root / name).mkdir()
        image = root / 'inputs' / 'owned.png'
        image.write_bytes(b'OWNED_SIMULATED_ENCODED_IMAGE')
        job_path, result_path = root / 'state/job.json', root / 'state/result.json'
        job = {
            'schemaVersion': worker.PROTOCOL, 'operation': 'generate', 'projectRoot': str(root),
            'input': {'path': str(image), 'sha256': digest(image.read_bytes())},
            'sourceDirectory': str(root / 'source'), 'modelDirectory': str(root / 'model'),
            'outputDirectory': str(root / 'output'), 'cacheDirectory': str(root / 'cache'),
            'settings': {'seed': 42, 'steps': 20, 'guidance': 3.0, 'shift': 3.0, 'erodeRadius': 1, 'gaussianCounts': [32768, 131072]},
            'limits': {'maxInputBytes': 1024 * 1024, 'maxInputPixels': 16_777_216, 'maxInputEdge': 8192,
                       'maxAspectRatio': 4, 'maxOutputBytes': 32 * 1024 * 1024,
                       'maxGpuAllocatedBytes': 12 * 1024**3, 'minGpuFreeBytes': 2 * 1024**3},
        }
        job_path.write_text(json.dumps(job), encoding='utf-8')
        yield root, job, job_path, result_path


def ply(count, row=None):
    row = row or (0, 0, 0, 0, 0, 0, .5, .5, .5, 0, -2, -2, -2, 1, 0, 0, 0)
    header = 'ply\nformat binary_little_endian 1.0\nelement vertex ' + str(count) + '\n'
    header += ''.join('property float ' + name + '\n' for name in worker.PROPERTIES) + 'end_header\n'
    return header.encode('ascii') + struct.pack('<17f', *row) * count


def simulated_memory_receipt(peak=96 * 1024**2, current=64 * 1024**2):
    return {'scope': 'worker-process; not aggregate job', 'peakCommittedBytes': peak, 'currentPrivateBytes': current,
            'peakWorkingSetBytes': 80 * 1024**2, 'currentWorkingSetBytes': 24 * 1024**2,
            'measurement': 'Windows process memory counters'}


def simulated_memory_api(values=None, success=1, pointer_size=8):
    """Inert ctypes/Win32 port: no ctypes extension import or OS API invocation."""
    class Structure:
        def __init__(self):
            for name, _ in self._fields_: setattr(self, name, 0)

    uint32, size_t, void_p, int32 = [SimpleNamespace(byte_width=size) for size in (4, pointer_size, pointer_size, 4)]

    def sizeof(value):
        return sum(field_type.byte_width for _, field_type in value._fields_) if hasattr(value, '_fields_') else value.byte_width

    captured = []

    def query(handle, counters, size):
        captured.append((handle, counters, size))
        for name, value in (values or {'PeakPagefileUsage': 96 * 1024**2, 'PrivateUsage': 64 * 1024**2,
                                      'PeakWorkingSetSize': 80 * 1024**2, 'WorkingSetSize': 24 * 1024**2}).items():
            setattr(counters, name, value)
        return success

    kernel = SimpleNamespace(GetCurrentProcess=Mock(return_value='SIMULATED_OWN_PROCESS_HANDLE'),
                             K32GetProcessMemoryInfo=Mock(side_effect=query))
    api = ModuleType('ctypes')
    api.Structure, api.c_uint32, api.c_size_t, api.c_void_p, api.c_int32 = Structure, uint32, size_t, void_p, int32
    api.sizeof, api.byref, api.POINTER = sizeof, lambda value: value, lambda value: ('SIMULATED_POINTER', value)
    api.WinDLL = Mock(return_value=kernel)
    return api, kernel, captured


class FakeMask:
    def __init__(self, size, box=None, extrema=(0, 255), erode_empty=False):
        self.size, self.box, self.extrema, self.erode_empty = size, box, extrema, erode_empty

    def getbbox(self):
        return self.box

    def getextrema(self):
        return self.extrema

    def filter(self, operation):
        box = None if self.erode_empty else self.box
        return FakeMask(self.size, box, self.extrema)


class FakeImage:
    def __init__(self, size=(64, 64), mode='RGBA', box='default', extrema=(0, 255), frames=1, erode_empty=False):
        self.size, self.mode, self.n_frames = size, mode, frames
        self.mask = FakeMask(size, (2, 2, size[0] - 2, size[1] - 2) if box == 'default' else box, extrema, erode_empty)
        self.loaded = False

    def __enter__(self):
        return self

    def __exit__(self, *args):
        return False

    def load(self):
        self.loaded = True

    def copy(self):
        return copy.deepcopy(self)

    def getchannel(self, name):
        if name != 'A':
            raise AssertionError('Unexpected mock channel')
        return self.mask

    def resize(self, size, resample):
        old = self.size
        box = self.mask.box
        if box is not None:
            box = tuple(round(value * size[index % 2] / old[index % 2]) for index, value in enumerate(box))
        return FakeImage(size, self.mode, box, self.mask.extrema, erode_empty=self.mask.erode_empty)

    def convert(self, mode):
        result = self.copy(); result.mode = mode
        return result

    def putalpha(self, mask):
        self.mask, self.mode = mask, 'RGBA'

    def crop(self, box):
        return FakeImage((box[2] - box[0], box[3] - box[1]), 'RGBA')

    def paste(self, image, mask):
        if image.size != self.size or mask is None:
            raise AssertionError('Invalid mock composite')

    def tobytes(self):
        return b'OWNED_SIMULATED_CONDITIONING_PIXELS'


class FakeImageApi:
    DecompressionBombWarning = UserWarning
    Resampling = SimpleNamespace(LANCZOS='LANCZOS')
    MAX_IMAGE_PIXELS = None

    def __init__(self, image=None):
        self.image = image or FakeImage()

    def open(self, stream, formats):
        if formats != ('PNG', 'JPEG', 'WEBP'):
            raise AssertionError('Image codec allowlist changed')
        return self.image

    @staticmethod
    def new(mode, size, colour):
        return FakeImage(size, mode)


FAKE_FILTER = SimpleNamespace(MinFilter=lambda size: ('minimum', size))


class FakeArray:
    shape = (1, 2, 3)

    def astype(self, dtype, copy):
        if dtype != '<f4' or copy is not False:
            raise AssertionError('Canonical tensor byte type changed')
        return self

    def tobytes(self):
        return b'OWNED_SIMULATED_TENSOR_BYTES'


class FakeTensor:
    def detach(self): return self
    def to(self, **kwargs): return self
    def contiguous(self): return self
    def numpy(self): return FakeArray()


class FakeCuda:
    def __init__(self, state):
        self.state = state
        self.allocated = self.reserved = 0
        self.peak = self.peak_reserved = 1024

    def is_available(self): return True
    def is_bf16_supported(self): return True
    def set_device(self, index): self.state['gpuCalls'] += 1
    def get_device_properties(self, index): return SimpleNamespace(name='SIMULATED_NOT_A_DEVICE')
    def mem_get_info(self, index): return (14 * 1024**3, 16 * 1024**3)
    def memory_allocated(self, index): return self.allocated
    def memory_reserved(self, index): return self.reserved
    def max_memory_allocated(self, index): return self.peak
    def max_memory_reserved(self, index): return self.peak_reserved
    def reset_peak_memory_stats(self, index): pass
    def set_per_process_memory_fraction(self, fraction, index): self.state['fraction'] = fraction
    def manual_seed_all(self, seed): self.state['cudaSeed'] = seed
    def synchronize(self, index): pass
    def empty_cache(self): self.reserved = 0


class FakeTorch:
    bfloat16, float16, float32 = 'torch.bfloat16', 'torch.float16', 'torch.float32'
    version = SimpleNamespace(cuda='12.8')

    def __init__(self, state):
        self.state = state
        self.cuda = FakeCuda(state)
        self._C = SimpleNamespace(_cuda_clearCublasWorkspaces=self.clear)

    def clear(self):
        self.state['cleanups'] += 1
        self.cuda.allocated = 1 if self.state.get('leak') else 0

    def manual_seed(self, seed): self.state['torchSeed'] = seed
    def inference_mode(self): return nullcontext()
    def Generator(self, device): return SimpleNamespace(manual_seed=lambda seed: ('SIMULATED_GENERATOR', seed))


class FakeGaussian:
    sh_degree = 0

    def __init__(self, count, state):
        self.count, self.state = count, state
        self.get_xyz = SimpleNamespace(shape=(count, 3))

    def to_ply_bytes(self):
        return ply(self.count) + bytes(self.state.get('extraBytes', 0))


class FakePipe:
    def __init__(self, torch, state, **kwargs):
        self.state, self.torch = state, torch
        state['loads'] += 1
        torch.cuda.allocated, torch.cuda.reserved = 256, 512
        parameter = lambda dtype: SimpleNamespace(device=SimpleNamespace(type='cuda', index=0), dtype=dtype)
        for name, dtype in {'dinov3': torch.bfloat16, 'vae_encoder': torch.bfloat16, 'rmbg': torch.float16,
                            'flow_model': torch.float16, 'decoder': torch.float16}.items():
            component = SimpleNamespace(parameters=lambda dtype=dtype: iter([parameter(dtype)]))
            setattr(self, name, component)
        self.rmbg.remove_background = self.remove_background

    def remove_background(self, image):
        self.state['segments'] += 1
        return FakeImage(image.size, box=None if self.state.get('emptyMask') else 'default')

    def encode_image(self, image, generator):
        self.state['encodes'] += 1
        return {'feature1': FakeTensor(), 'feature2': FakeTensor()}

    def sample_latent(self, cond, **kwargs):
        self.state['inferences'] += 1
        kwargs['callback'](1, kwargs['steps'])
        return {'latent': self.state['latent']}

    def decode_latent(self, latent, num_gaussians):
        self.state['decodes'].append(num_gaussians)
        self.state['decodedLatents'].append(latent)
        if self.state.get('failDecode') == num_gaussians:
            raise RuntimeError('SIMULATED_PRIVATE_BACKEND_FAILURE')
        return FakeGaussian(num_gaussians, self.state)


def mock_native(paths, image=None, **overrides):
    state = {'loads': 0, 'segments': 0, 'encodes': 0, 'inferences': 0, 'decodes': [], 'decodedLatents': [],
             'cleanups': 0, 'gpuCalls': 0, 'latent': FakeTensor(), **overrides}
    torch = FakeTorch(state)
    np = SimpleNamespace(random=SimpleNamespace(seed=lambda seed: state.update(numpySeed=seed)),
                         isfinite=lambda array: SimpleNamespace(all=lambda: True))
    source = SimpleNamespace(TripoSplatPipeline=lambda **kwargs: FakePipe(torch, state, **kwargs))
    versions = {name: 'SIMULATED_VERSION' for name in ('torch', 'torchvision', 'numpy', 'safetensors', 'pillow', 'tqdm')}

    def fake_offline(_paths):
        _paths['outputDirectory'].mkdir(); _paths['cacheDirectory'].mkdir()
        return SimpleNamespace(network_attempts=0), []

    stack = ExitStack()
    stack.enter_context(patch.object(worker, 'offline', side_effect=fake_offline))
    stack.enter_context(patch.object(worker, 'verify_environment', return_value=versions))
    stack.enter_context(patch.object(worker, 'decode_input', return_value=(image or FakeImage(), FakeImageApi(), FAKE_FILTER)))
    backend = stack.enter_context(patch.object(worker, 'load_backend', return_value=(torch, np, source)))
    stack.enter_context(patch.object(worker, 'snapshot', return_value={}))
    stack.enter_context(patch.object(worker, 'loaded_modules', return_value=[str(ROOT / 'SIMULATED_RUNTIME_MODULE.dll')]))
    stack.enter_context(patch.object(worker, 'memory_observations', side_effect=lambda: simulated_memory_receipt()))
    return stack, state, torch, backend


class SplatWorkerTests(unittest.TestCase):
    def test_import_has_no_native_dependencies(self):
        self.assertFalse(any(name in sys.modules for name in ('torch', 'numpy', 'torchvision', 'PIL', 'safetensors', 'triposplat', 'model')))

    def test_strict_json_rejects_duplicate_nonfinite_trailing_and_wrong_encoding(self):
        for payload in (b'{"a":1,"a":2}', b'{"a":NaN}', b'{"a":Infinity}', b'{}{}', b'\xff'):
            with self.subTest(payload=payload), self.assertRaises((ValueError, UnicodeError)):
                worker.strict_json(payload)

    def test_valid_job_is_read_only_admission(self):
        with fixture() as (_, job, path, result):
            paths = worker.validate_job(job, path, result)
            self.assertFalse(paths['outputDirectory'].exists())
            self.assertFalse(paths['cacheDirectory'].exists())

    def test_job_unknown_fields_and_types_are_rejected(self):
        with fixture() as (_, good, path, result):
            for name, value in [('extra', 1), ('operation', 'inspect'), ('schemaVersion', 'v0'), ('projectRoot', False)]:
                bad = copy.deepcopy(good); bad[name] = value
                with self.subTest(name=name), self.assertRaises(worker.ProfileError): worker.validate_job(bad, path, result)
            bad = copy.deepcopy(good); bad['input']['extra'] = 'untrusted callback'
            with self.assertRaises(worker.ProfileError): worker.validate_job(bad, path, result)

    def test_settings_are_bounded_and_exactly_typed(self):
        cases = [('seed', True), ('seed', -1), ('seed', 9_007_199_254_740_992), ('steps', 20.0), ('steps', 9), ('steps', 21),
                 ('guidance', True), ('guidance', 0), ('guidance', 10.1), ('shift', float('nan')), ('shift', .9), ('shift', 5.1),
                 ('erodeRadius', True), ('erodeRadius', -1), ('erodeRadius', 9),
                 ('gaussianCounts', []), ('gaussianCounts', [True]), ('gaussianCounts', [65536]),
                 ('gaussianCounts', [32768, 32768]), ('gaussianCounts', [32768, 131072, 32768])]
        with fixture() as (_, good, path, result):
            for name, value in cases:
                bad = copy.deepcopy(good); bad['settings'][name] = value
                with self.subTest(name=name, value=value), self.assertRaises(worker.ProfileError): worker.validate_job(bad, path, result)

    def test_declared_sampling_and_erosion_endpoints_are_admitted_without_native_work(self):
        with fixture() as (_, good, path, result):
            for name, endpoints in (('steps', (10, 20)), ('guidance', (1, 10)), ('shift', (1, 5)), ('erodeRadius', (0, 8))):
                for value in endpoints:
                    job = copy.deepcopy(good); job['settings'][name] = value
                    with self.subTest(name=name, value=value):
                        paths = worker.validate_job(job, path, result)
                        self.assertFalse(paths['outputDirectory'].exists())

    def test_every_limit_has_finite_bounds_and_no_bool_alias(self):
        with fixture() as (_, good, path, result):
            for name in good['limits']:
                for value in (True, -1, float('inf'), 2**100):
                    bad = copy.deepcopy(good); bad['limits'][name] = value
                    with self.subTest(name=name, value=value), self.assertRaises(worker.ProfileError): worker.validate_job(bad, path, result)
            bad = copy.deepcopy(good); bad['limits']['maxOutputBytes'] = 100
            with self.assertRaises(worker.ProfileError): worker.validate_job(bad, path, result)

    def test_execution_paths_remain_projectlocal_fresh_and_disjoint(self):
        with fixture() as (root, good, path, result):
            for name, value in [('sourceDirectory', good['modelDirectory']), ('cacheDirectory', good['outputDirectory']),
                                ('outputDirectory', str(root / 'source' / 'new')), ('modelDirectory', str(root.parent)),
                                ('outputDirectory', str(root / 'missing-parent' / 'output'))]:
                bad = copy.deepcopy(good); bad[name] = value
                with self.subTest(name=name, value=value), self.assertRaises(worker.ProfileError): worker.validate_job(bad, path, result)
            result.write_text('foreign result', encoding='utf-8')
            with self.assertRaises(worker.ProfileError): worker.validate_job(good, path, result)
            self.assertEqual(result.read_text(encoding='utf-8'), 'foreign result')

    def test_relative_dot_git_and_link_aliases_are_rejected(self):
        with fixture() as (root, good, path, result):
            for value in ('relative.png', '//server/share/model', '\\\\server\\share\\model', str(root) + '/inputs/../outside.png', str(root / '.git' / 'config'), str(root / 'input:stream')):
                with self.subTest(value=value), self.assertRaises(worker.ProfileError): worker.local_path(value)
            with patch.object(worker.Path, 'is_symlink', return_value=True):
                with self.assertRaises(worker.ProfileError): worker.local_path(str(root / 'inputs'))

    def test_input_buffer_hash_and_size_are_checked_before_native_entry(self):
        with fixture() as (_, job, path, result):
            body = path.read_bytes()
            self.assertEqual(worker.verified_bytes(path, digest(body), len(body)), body)
            with self.assertRaises(worker.ProfileError): worker.verified_bytes(path, '0' * 64, len(body))
            with self.assertRaises(worker.ProfileError): worker.verified_bytes(path, digest(body), len(body) - 1)
            with patch.object(worker, 'execute') as native:
                with self.assertRaises(worker.ProfileError): worker.main([str(path), str(result), '0' * 64])
                native.assert_not_called()

    def test_fixed_source_loader_executes_verified_buffer_not_a_path_reobservation(self):
        with fixture() as (root, _, _, _):
            original = b"marker = 'original'\n"
            swapped = b"marker = 'swapped!'\n"
            self.assertEqual(len(original), len(swapped))
            with patch.object(worker, 'SOURCE_FILES', {'model.py': digest(original)}):
                loader = worker.FixedSourceLoader(root / 'source', {'model.py': original})
                module = ModuleType('model'); loader.exec_module(module)
                self.assertEqual(module.marker, 'original')
                loader = worker.FixedSourceLoader(root / 'source', {'model.py': swapped})
                module = ModuleType('model')
                with self.assertRaises(worker.ProfileError): loader.exec_module(module)
                self.assertFalse(hasattr(module, 'marker'))
                self.assertIsNone(loader.find_spec('foreign_model'))

    def test_real_catalog_has_exact_reviewed_source_and_weight_metadata(self):
        catalog = worker.fixed_catalog()
        self.assertEqual(len(catalog['source']['files']), 3)
        self.assertEqual(len(catalog['weights']), 5)
        self.assertEqual(catalog['source']['revision'], worker.SOURCE_REVISION)
        self.assertEqual(catalog['modelRevision'], worker.MODEL_REVISION)

    def test_snapshot_hashes_every_owned_simulated_source_and_weight_and_rejects_additions(self):
        with fixture() as (_, job, path, result):
            paths = worker.validate_job(job, path, result)
            source = {'model.py': b'# OWNED_SOURCE_ONE\n', 'triposplat.py': b'# OWNED_SOURCE_TWO\n', 'LICENSE': b'OWNED_NOTICE'}
            weight = {name: ('OWNED_SIMULATED_WEIGHT_' + str(index)).encode('ascii') for index, name in enumerate(worker.WEIGHTS)}
            catalog = {'source': {'files': []}, 'weights': []}
            for group, directory in ((source, paths['sourceDirectory']), (weight, paths['modelDirectory'])):
                entries = catalog['source']['files'] if group is source else catalog['weights']
                for name, payload in group.items():
                    target = directory / name; target.parent.mkdir(parents=True, exist_ok=True); target.write_bytes(payload)
                    entries.append({'path': name, 'sha256': digest(payload), 'bytes': len(payload)})
            with patch.object(worker, 'SOURCE_FILES', {name: digest(payload) for name, payload in source.items()}), \
                    patch.object(worker, 'WEIGHTS', {name: digest(payload) for name, payload in weight.items()}):
                self.assertEqual(worker.snapshot(paths, catalog), source)
                selected = next(iter(weight)); selected_path = paths['modelDirectory'] / selected
                selected_path.write_bytes(b'x' * len(weight[selected]))
                with self.assertRaises(worker.ProfileError): worker.snapshot(paths, catalog)
                selected_path.write_bytes(weight[selected])
                (paths['modelDirectory'] / 'unregistered').mkdir()
                with self.assertRaises(worker.ProfileError): worker.snapshot(paths, catalog)

    def test_installed_distribution_version_mismatch_is_rejected_without_importing_native_packages(self):
        names = ('torch', 'torchvision', 'numpy', 'safetensors', 'pillow', 'tqdm')
        environment = {'python': {'version': '.'.join(map(str, sys.version_info[:3]))},
                       'artifacts': [{'name': name, 'version': 'SIMULATED_PIN'} for name in names]}
        catalog = {'environmentCandidates': {'win-amd64': environment}}
        with patch.object(worker.os, 'name', 'nt'), patch.object(worker.importlib.metadata, 'version', return_value='SIMULATED_PIN'):
            self.assertEqual(set(worker.verify_environment(catalog)), set(names))
        with patch.object(worker.os, 'name', 'nt'), patch.object(worker.importlib.metadata, 'version', return_value='WRONG_PIN'):
            with self.assertRaises(worker.ProfileError): worker.verify_environment(catalog)

    def test_offline_guard_denies_dependencies_subprocesses_dns_and_writes(self):
        with fixture() as (_, job, path, result):
            paths = worker.validate_job(job, path, result)
            guard = worker.ExecutionGuard(paths)
            guard.write(str(paths['cacheDirectory'] / 'owned.tmp'))
            guard.write(str(result))
            with self.assertRaises(worker.ProfileError): guard.write(str(paths['sourceDirectory'] / 'model.py'))
            with self.assertRaises(worker.ProfileError): guard.audit('subprocess.Popen', ('ignored',))
            with self.assertRaises(worker.ProfileError): guard.audit('socket.bind', ())
            with self.assertRaises(worker.ProfileError): guard.audit('socket.getaddrinfo', ())
            self.assertEqual(guard.network_attempts, 2)

    def test_offline_environment_clears_credentials_and_blocks_socket_ports(self):
        with fixture() as (_, job, path, result), ExitStack() as stack:
            paths = worker.validate_job(job, path, result)
            stack.enter_context(patch.dict(os.environ, {'HF_TOKEN': 'OWNED_TEST_SENTINEL', 'GITHUB_TOKEN': 'OWNED_TEST_SENTINEL'}))
            stack.enter_context(patch.object(worker.socket, 'has_ipv6', worker.socket.has_ipv6))
            stack.enter_context(patch.object(worker.tempfile, 'tempdir', worker.tempfile.tempdir))
            for name in ('connect', 'connect_ex', 'bind', 'listen'):
                stack.enter_context(patch.object(worker.socket.socket, name, getattr(worker.socket.socket, name)))
            for name in ('create_connection', 'getaddrinfo', 'gethostbyname', 'gethostbyname_ex', 'gethostbyaddr'):
                stack.enter_context(patch.object(worker.socket, name, getattr(worker.socket, name)))
            stack.enter_context(patch.object(worker.os, 'add_dll_directory', return_value=SimpleNamespace(close=lambda: None), create=True))
            guard, _ = worker.offline(paths, install_audit=False)
            self.assertNotIn('HF_TOKEN', os.environ); self.assertNotIn('GITHUB_TOKEN', os.environ)
            self.assertEqual(os.environ['HF_HUB_OFFLINE'], '1'); self.assertFalse(worker.socket.has_ipv6)
            self.assertEqual(os.environ['CUDA_CACHE_MAXSIZE'], '67108864')
            for operation in (lambda: worker.socket.getaddrinfo('unused.invalid', 1), lambda: worker.socket.socket.bind(None, ('127.0.0.1', 1))):
                with self.assertRaises(worker.ProfileError): operation()
            self.assertEqual(guard.network_attempts, 2)

    def test_cpu_image_header_guards_precede_decode(self):
        limits = {'maxInputPixels': 100, 'maxInputEdge': 10, 'maxAspectRatio': 4}
        for size in ((0, 10), (1, 10), (11, 10), (10, 11), (2, 10), (10, 2)):
            with self.subTest(size=size), self.assertRaises(worker.ProfileError): worker.dimensions(*size, limits)
        worker.dimensions(10, 10, limits)

    def test_decode_input_rejects_animation_modes_and_degenerate_alpha_before_native(self):
        limits = {'maxInputPixels': 16_777_216, 'maxInputEdge': 8192, 'maxAspectRatio': 4}
        for image in (FakeImage(frames=2), FakeImage(mode='CMYK'), FakeImage(box=None), FakeImage(box=(1, 1, 2, 40))):
            api = FakeImageApi(image)
            fake_pil = ModuleType('PIL'); fake_pil.Image, fake_pil.ImageFilter = api, FAKE_FILTER
            with self.subTest(image=image.mode), patch.dict(sys.modules, {'PIL': fake_pil}), self.assertRaises(worker.ProfileError):
                worker.decode_input(b'OWNED_SIMULATED_IMAGE', limits)

    def test_alpha_preparation_checks_eroded_mask_and_records_cpu_phase(self):
        limits = {'maxInputPixels': 16_777_216, 'maxInputEdge': 8192, 'maxAspectRatio': 4}
        prepared, facts = worker.prepare_image(FakeImage(), None, FakeImageApi(), FAKE_FILTER, limits)
        self.assertEqual(prepared.size, (1024, 1024)); self.assertEqual(facts['maskSource'], 'input-alpha')
        self.assertEqual(facts['maskValidationStage'], 'cpu-before-model-load')
        with self.assertRaises(worker.ProfileError): worker.prepare_image(FakeImage(erode_empty=True), None, FakeImageApi(), FAKE_FILTER, limits)
        with self.assertRaises(worker.NeedsSegmentation): worker.prepare_image(FakeImage(mode='RGB'), None, FakeImageApi(), FAKE_FILTER, limits)

    def test_requested_erosion_zero_bypasses_and_positive_radius_selects_exact_filter(self):
        limits = {'maxInputPixels': 16_777_216, 'maxInputEdge': 8192, 'maxAspectRatio': 4}
        with patch.object(FAKE_FILTER, 'MinFilter', side_effect=lambda size: ('minimum', size)) as filtering:
            prepared, _ = worker.prepare_image(FakeImage(erode_empty=True), None, FakeImageApi(), FAKE_FILTER, limits, erode_radius=0)
            self.assertEqual(prepared.size, (1024, 1024)); filtering.assert_not_called()
            worker.prepare_image(FakeImage(), None, FakeImageApi(), FAKE_FILTER, limits, erode_radius=2)
            filtering.assert_called_once_with(5)

    def test_rgb_resize_bounds_are_checked_before_mock_native_entry(self):
        with fixture() as (_, job, path, result):
            job['limits']['maxInputEdge'] = 512
            paths = worker.validate_job(job, path, result)
            stack, state, _, backend = mock_native(paths, image=FakeImage(mode='RGB'))
            with stack: observed = worker.execute(job, paths, {}, {}, b'OWNED_SIMULATED_IMAGE')
            backend.assert_not_called(); self.assertEqual(state['gpuCalls'], 0)
            self.assertEqual(observed['status'], 'failed'); self.assertEqual(observed['failureStage'], 'cpu-image-admission')

    def test_gaussian_payload_requires_exact_sh0_properties_size_and_finite_scalars(self):
        worker.verify_ply(ply(2), 2)
        for payload in (ply(2)[:-1], ply(2) + b'x', ply(2).replace(b'property float x', b'property float q', 1)):
            with self.subTest(size=len(payload)), self.assertRaises(worker.ProfileError): worker.verify_ply(payload, 2)
        for index, value in ((0, float('nan')), (6, float('inf')), (9, 81), (10, -31), (13, 0)):
            row = [0, 0, 0, 0, 0, 0, .5, .5, .5, 0, -2, -2, -2, 1, 0, 0, 0]
            row[index] = value
            with self.subTest(index=index, value=value), self.assertRaises(worker.ProfileError): worker.verify_ply(ply(2, row), 2)

    def test_one_seeded_latent_is_reused_for_two_simulated_density_decodes(self):
        with fixture() as (_, job, path, result):
            paths = worker.validate_job(job, path, result)
            stack, state, _, _ = mock_native(paths)
            with stack:
                observed = worker.execute(job, paths, {}, {}, b'OWNED_SIMULATED_IMAGE')
            self.assertEqual(observed['status'], 'succeeded')
            self.assertEqual((state['loads'], state['encodes'], state['inferences']), (1, 1, 1))
            self.assertEqual(state['decodes'], [32768, 131072])
            self.assertTrue(all(latent is state['latent'] for latent in state['decodedLatents']))
            self.assertEqual(observed['stochasticInferences'], 1)
            self.assertEqual(observed['cleanup']['endingAllocatedBytes'], 0)
            self.assertEqual(observed['execution']['decoderSampling'], 'systematic; global seeded RNG')
            self.assertEqual({p.name for p in paths['outputDirectory'].iterdir()}, {'gaussian-32768.ply', 'gaussian-131072.ply'})
            for output in observed['outputs']:
                payload = (paths['outputDirectory'] / output['path']).read_bytes()
                self.assertEqual(output['sha256'], digest(payload)); self.assertEqual(output['bytes'], len(payload))
            self.assertEqual(json.loads(result.read_text(encoding='utf-8'))['status'], 'succeeded')

    def test_rgb_generated_empty_mask_fails_before_encoding_and_still_cleans_up(self):
        with fixture() as (_, job, path, result):
            paths = worker.validate_job(job, path, result)
            stack, state, _, _ = mock_native(paths, image=FakeImage(mode='RGB'), emptyMask=True)
            with stack: observed = worker.execute(job, paths, {}, {}, b'OWNED_SIMULATED_IMAGE')
            self.assertEqual(observed['status'], 'failed'); self.assertEqual(observed['failureStage'], 'foreground-preprocessing')
            self.assertEqual((state['segments'], state['encodes'], state['inferences']), (1, 0, 0))
            self.assertEqual(state['cleanups'], 1); self.assertEqual(observed['cleanup']['endingAllocatedBytes'], 0)

    def test_cpu_alpha_failure_never_enters_even_the_mocked_gpu_port(self):
        with fixture() as (_, job, path, result):
            paths = worker.validate_job(job, path, result)
            stack, state, _, backend = mock_native(paths)
            with stack, patch.object(worker, 'decode_input', side_effect=worker.ProfileError('Foreground mask is empty')):
                observed = worker.execute(job, paths, {}, {}, b'OWNED_SIMULATED_IMAGE')
            backend.assert_not_called(); self.assertEqual(state['gpuCalls'], 0)
            self.assertEqual(observed['status'], 'failed'); self.assertIsNone(observed['cleanup'])

    def test_simulated_backend_failure_retains_first_output_and_does_not_retry(self):
        with fixture() as (_, job, path, result):
            paths = worker.validate_job(job, path, result)
            stack, state, _, _ = mock_native(paths, failDecode=131072)
            with stack: observed = worker.execute(job, paths, {}, {}, b'OWNED_SIMULATED_IMAGE')
            self.assertEqual(observed['status'], 'failed'); self.assertEqual(state['inferences'], 1)
            self.assertEqual(state['decodes'], [32768, 131072]); self.assertEqual(len(observed['outputs']), 1)
            self.assertNotIn('SIMULATED_PRIVATE_BACKEND_FAILURE', observed['failure'])
            self.assertEqual(state['cleanups'], 1)

    def test_simulated_cleanup_leak_invalidates_finished_outputs(self):
        with fixture() as (_, job, path, result):
            job['settings']['gaussianCounts'] = [32768]
            paths = worker.validate_job(job, path, result)
            stack, _, _, _ = mock_native(paths, leak=True)
            with stack: observed = worker.execute(job, paths, {}, {}, b'OWNED_SIMULATED_IMAGE')
            self.assertEqual(observed['status'], 'failed'); self.assertEqual(observed['failureStage'], 'cleanup')
            self.assertEqual(len(observed['outputs']), 1); self.assertEqual(observed['cleanup']['endingAllocatedBytes'], 1)

    def test_output_byte_quota_is_checked_before_publication(self):
        with fixture() as (_, job, path, result):
            job['settings']['gaussianCounts'] = [32768]
            job['limits']['maxOutputBytes'] = 32768 * 68 + 4096
            paths = worker.validate_job(job, path, result)
            stack, _, _, _ = mock_native(paths, extraBytes=5000)
            with stack: observed = worker.execute(job, paths, {}, {}, b'OWNED_SIMULATED_IMAGE')
            self.assertEqual(observed['status'], 'failed'); self.assertEqual(observed['outputs'], [])
            self.assertEqual(list(paths['outputDirectory'].iterdir()), [])

    def test_gpu_budget_checks_use_only_simulated_allocator_observations(self):
        state = {'gpuCalls': 0, 'cleanups': 0}
        torch = FakeTorch(state)
        worker.check_gpu(torch, {'maxGpuAllocatedBytes': 1024})
        with self.assertRaises(worker.ProfileError): worker.check_gpu(torch, {'maxGpuAllocatedBytes': 1023})
        self.assertEqual(state['gpuCalls'], 0)

    def test_cache_budget_reports_fixed_profile_and_monotonic_sampled_bytes(self):
        self.assertEqual(worker.CACHE_BYTE_LIMIT, 67_108_864)
        self.assertEqual(worker.CACHE_ENTRY_LIMIT, 4096)
        with fixture() as (root, _, _, _):
            cache = root / 'cache'; cache.mkdir(); (cache / 'nested').mkdir()
            payload = cache / 'nested/owned.tmp'; payload.write_bytes(b'0123456789')
            budget = worker.CacheBudget(cache); budget.observe()
            payload.write_bytes(b'x'); budget.observe()
            self.assertEqual(budget.observations(), {'maximumObservedBytes': 10, 'byteLimit': 67_108_864,
                                                    'fileLimit': 4096, 'measurement': 'stage-sampled; not OS quota'})

    def test_cache_budget_rejects_oversized_file_without_deletion(self):
        with fixture() as (root, _, _, _), patch.object(worker, 'CACHE_BYTE_LIMIT', 64):
            cache = root / 'cache'; cache.mkdir()
            payload = cache / 'oversized.tmp'; payload.write_bytes(bytes(65))
            budget = worker.CacheBudget(cache)
            with self.assertRaisesRegex(worker.ProfileError, 'Cache byte limit'): budget.observe()
            self.assertEqual(budget.observations()['maximumObservedBytes'], 65)
            self.assertEqual(payload.stat().st_size, 65)

    def test_cache_budget_counts_nested_directories_as_entries(self):
        with fixture() as (root, _, _, _), patch.object(worker, 'CACHE_ENTRY_LIMIT', 3):
            cache = root / 'cache'; (cache / 'nested').mkdir(parents=True)
            for index in range(3): (cache / 'nested' / str(index)).write_bytes(b'x')
            with self.assertRaisesRegex(worker.ProfileError, 'Cache entry limit'): worker.CacheBudget(cache).observe()
            self.assertEqual(len(list((cache / 'nested').iterdir())), 3)

    def test_cache_budget_rejects_link_reparse_special_and_multiple_link_entries(self):
        observations = ((stat.S_IFLNK, 0, 1), (stat.S_IFDIR, 0x400, 1),
                        (stat.S_IFIFO, 0, 1), (stat.S_IFREG, 0, 2))
        with fixture() as (root, _, _, _):
            cache = root / 'cache'; cache.mkdir()
            for mode, attributes, links in observations:
                info = SimpleNamespace(st_mode=mode, st_file_attributes=attributes, st_nlink=links, st_size=1)
                child = SimpleNamespace(path=str(cache / 'SIMULATED_ENTRY'), stat=lambda **_: info)
                original = Path.lstat

                def simulated_lstat(path, *args, **kwargs):
                    return info if str(path) == child.path else original(path, *args, **kwargs)

                with self.subTest(mode=mode, attributes=attributes, links=links), \
                     patch.object(worker.os, 'scandir', return_value=nullcontext(iter([child]))), \
                     patch.object(Path, 'lstat', new=simulated_lstat), \
                     self.assertRaises(worker.ProfileError):
                    worker.CacheBudget(cache).observe()

    def test_simulated_cache_samples_cover_load_conditioning_callback_decodes_and_cleanup(self):
        with fixture() as (_, job, path, result):
            paths = worker.validate_job(job, path, result)
            stack, _, _, _ = mock_native(paths)
            original = worker.CacheBudget.observe
            with stack, patch.object(worker.CacheBudget, 'observe', autospec=True, side_effect=original) as observation:
                observed = worker.execute(job, paths, {}, {}, b'OWNED_SIMULATED_IMAGE')
            self.assertEqual(observed['status'], 'succeeded')
            # Initial admission, model load, conditioning, one mock flow step,
            # both density decodes and final cleanup all sample the real tree.
            self.assertEqual(observation.call_count, 7)
            self.assertEqual(observed['cacheObservations']['maximumObservedBytes'], 0)

    def test_simulated_flow_callback_enforces_cache_budget_and_retains_failed_cache(self):
        with fixture() as (_, job, path, result), patch.object(worker, 'CACHE_BYTE_LIMIT', 64):
            paths = worker.validate_job(job, path, result)
            stack, state, _, _ = mock_native(paths)
            original = FakePipe.sample_latent

            def grow_cache(pipe, cond, **kwargs):
                (paths['cacheDirectory'] / 'retained.tmp').write_bytes(bytes(65))
                return original(pipe, cond, **kwargs)

            with stack, patch.object(FakePipe, 'sample_latent', new=grow_cache):
                observed = worker.execute(job, paths, {}, {}, b'OWNED_SIMULATED_IMAGE')
            self.assertEqual(observed['status'], 'failed'); self.assertEqual(observed['failureStage'], 'latent-inference')
            self.assertEqual((state['inferences'], state['decodes'], state['cleanups']), (1, [], 1))
            self.assertEqual(observed['cleanup']['endingAllocatedBytes'], 0)
            self.assertEqual(observed['cacheObservations']['maximumObservedBytes'], 65)
            self.assertEqual((paths['cacheDirectory'] / 'retained.tmp').stat().st_size, 65)

    def test_simulated_final_cache_growth_invalidates_outputs_after_gpu_cleanup(self):
        with fixture() as (_, job, path, result), patch.object(worker, 'CACHE_BYTE_LIMIT', 64):
            job['settings']['gaussianCounts'] = [32768]
            paths = worker.validate_job(job, path, result)
            stack, state, torch, _ = mock_native(paths)
            original = torch._C._cuda_clearCublasWorkspaces

            def grow_cache_on_cleanup():
                original()
                (paths['cacheDirectory'] / 'retained-final.tmp').write_bytes(bytes(65))

            with stack, patch.object(torch._C, '_cuda_clearCublasWorkspaces', side_effect=grow_cache_on_cleanup):
                observed = worker.execute(job, paths, {}, {}, b'OWNED_SIMULATED_IMAGE')
            self.assertEqual(observed['status'], 'failed'); self.assertEqual(observed['failureStage'], 'cache-final-cleanup')
            self.assertEqual(state['cleanups'], 1); self.assertEqual(len(observed['outputs']), 1)
            self.assertEqual(observed['cleanup']['endingAllocatedBytes'], 0)
            self.assertEqual(observed['cacheObservations']['maximumObservedBytes'], 65)

    def test_module_sample_union_is_bounded_and_contains_only_absolute_observations(self):
        first, second = str(ROOT / 'SIMULATED_FIRST.dll'), str(ROOT / 'SIMULATED_SECOND.dll')
        self.assertEqual(worker.module_union([], [first]), [first])
        self.assertEqual(len(worker.module_union([first], [first.upper(), second])), 2)
        for invalid in ([], ['relative.dll'], [first + '\n'], [None], [first] * 2049):
            with self.subTest(sample=invalid[:1]), self.assertRaises(worker.ProfileError):
                worker.module_union([], invalid)
        maximum = [str(ROOT / ('SIMULATED_' + str(index) + '.dll')) for index in range(2048)]
        self.assertEqual(len(worker.module_union([], maximum)), 2048)
        with self.assertRaises(worker.ProfileError):
            worker.module_union(maximum, [str(ROOT / 'SIMULATED_OVERFLOW.dll')])

    def test_memory_counter_windows_abi_and_commit_resident_fields_use_only_mocked_calls(self):
        api, kernel, captured = simulated_memory_api()
        with patch.dict(sys.modules, {'ctypes': api}), patch.object(worker.os, 'name', 'nt'):
            observed = worker.memory_observations()
        self.assertEqual(observed, simulated_memory_receipt())
        api.WinDLL.assert_called_once_with('kernel32', use_last_error=True)
        self.assertEqual(kernel.GetCurrentProcess.argtypes, [])
        self.assertIs(kernel.GetCurrentProcess.restype, api.c_void_p)
        self.assertIs(kernel.K32GetProcessMemoryInfo.restype, api.c_int32)
        self.assertEqual(kernel.K32GetProcessMemoryInfo.call_count, 1)
        handle, counters, size = captured[0]
        self.assertEqual((handle, size, counters.cb), ('SIMULATED_OWN_PROCESS_HANDLE', 80, 80))
        self.assertEqual([name for name, _ in counters._fields_], ['cb', 'PageFaultCount', 'PeakWorkingSetSize',
                         'WorkingSetSize', 'QuotaPeakPagedPoolUsage', 'QuotaPagedPoolUsage', 'QuotaPeakNonPagedPoolUsage',
                         'QuotaNonPagedPoolUsage', 'PagefileUsage', 'PeakPagefileUsage', 'PrivateUsage'])
        self.assertTrue(all(kind is api.c_uint32 for _, kind in counters._fields_[:2]))
        self.assertTrue(all(kind is api.c_size_t for _, kind in counters._fields_[2:]))

    def test_memory_observation_rejects_unsupported_abi_without_querying_mock_os(self):
        api, kernel, _ = simulated_memory_api(pointer_size=4)
        with patch.dict(sys.modules, {'ctypes': api}), patch.object(worker.os, 'name', 'nt'), \
             self.assertRaisesRegex(worker.ProfileError, 'ABI differs'):
            worker.memory_observations()
        kernel.K32GetProcessMemoryInfo.assert_not_called()

    def test_memory_observation_failed_mock_win32_call_is_rejected(self):
        api, _, _ = simulated_memory_api(success=0)
        with patch.dict(sys.modules, {'ctypes': api}), patch.object(worker.os, 'name', 'nt'), \
             self.assertRaisesRegex(worker.ProfileError, 'observation unavailable'):
            worker.memory_observations()

    def test_memory_observation_rejects_invalid_commit_or_resident_counter_relations(self):
        for change in ({'PrivateUsage': 0}, {'PeakPagefileUsage': 1}, {'PeakWorkingSetSize': 1},
                       {'WorkingSetSize': 0}, {'PeakPagefileUsage': 2**63}, {'cb': 79}):
            values = {'PeakPagefileUsage': 96 * 1024**2, 'PrivateUsage': 64 * 1024**2,
                      'PeakWorkingSetSize': 80 * 1024**2, 'WorkingSetSize': 24 * 1024**2, **change}
            api, _, _ = simulated_memory_api(values=values)
            with self.subTest(change=change), patch.dict(sys.modules, {'ctypes': api}), \
                 patch.object(worker.os, 'name', 'nt'), self.assertRaises(worker.ProfileError):
                worker.memory_observations()

    def test_simulated_final_memory_receipt_is_after_cleanup_and_retains_lifetime_peak(self):
        with fixture() as (_, job, path, result):
            job['settings']['gaussianCounts'] = [32768]
            paths = worker.validate_job(job, path, result)
            stack, state, _, _ = mock_native(paths)
            observations = []

            def observe():
                observations.append(state['cleanups'])
                return simulated_memory_receipt(peak=(96 if len(observations) == 1 else 128) * 1024**2,
                                                current=(64 if len(observations) == 1 else 32) * 1024**2)

            with stack, patch.object(worker, 'memory_observations', side_effect=observe):
                observed = worker.execute(job, paths, {}, {}, b'OWNED_SIMULATED_IMAGE')
            self.assertEqual(observed['status'], 'succeeded'); self.assertEqual(observations, [0, 1])
            self.assertEqual(observed['memoryObservations'], simulated_memory_receipt(peak=128 * 1024**2, current=32 * 1024**2))

    def test_simulated_initial_memory_measurement_failure_blocks_native_entry(self):
        with fixture() as (_, job, path, result):
            paths = worker.validate_job(job, path, result)
            stack, state, _, backend = mock_native(paths)
            with stack, patch.object(worker, 'memory_observations', side_effect=[worker.ProfileError('Simulated unavailable memory observation'),
                                                                              simulated_memory_receipt()]):
                observed = worker.execute(job, paths, {}, {}, b'OWNED_SIMULATED_IMAGE')
            self.assertEqual(observed['status'], 'failed'); self.assertEqual(observed['failureStage'], 'memory-observation-before-native')
            self.assertEqual(state['gpuCalls'], 0); backend.assert_not_called()

    def test_simulated_final_memory_measurement_failure_rejects_successful_outputs(self):
        with fixture() as (_, job, path, result):
            job['settings']['gaussianCounts'] = [32768]
            paths = worker.validate_job(job, path, result)
            stack, state, _, _ = mock_native(paths)
            with stack, patch.object(worker, 'memory_observations', side_effect=[simulated_memory_receipt(),
                                                                              worker.ProfileError('Simulated unavailable final memory observation')]):
                observed = worker.execute(job, paths, {}, {}, b'OWNED_SIMULATED_IMAGE')
            self.assertEqual(observed['status'], 'failed'); self.assertEqual(observed['failureStage'], 'memory-observation-final-cleanup')
            self.assertIsNone(observed['memoryObservations']); self.assertEqual(len(observed['outputs']), 1)
            self.assertEqual((state['inferences'], state['cleanups']), (1, 1))
            self.assertEqual(observed['cleanup']['endingAllocatedBytes'], 0)

    def test_simulated_native_module_samples_cover_load_inference_and_cleanup_phases(self):
        with fixture() as (_, job, path, result):
            job['settings']['gaussianCounts'] = [32768]
            paths = worker.validate_job(job, path, result)
            stack, _, _, _ = mock_native(paths)
            samples = [[str(ROOT / ('SIMULATED_PHASE_' + str(index) + '.dll'))] for index in range(3)]
            with stack, patch.object(worker, 'loaded_modules', side_effect=samples) as observation:
                observed = worker.execute(job, paths, {}, {}, b'OWNED_SIMULATED_IMAGE')
            self.assertEqual(observed['status'], 'succeeded')
            self.assertEqual(observation.call_count, 3)
            self.assertEqual(set(observed['loadedModules']), {sample[0] for sample in samples})

    def test_simulated_module_observation_failure_blocks_success_and_preserves_cleanup(self):
        with fixture() as (_, job, path, result):
            paths = worker.validate_job(job, path, result)
            stack, state, _, _ = mock_native(paths)
            with stack, patch.object(worker, 'loaded_modules', side_effect=[worker.ProfileError('Simulated module sample failure'),
                                                                          [str(ROOT / 'SIMULATED_AFTER_CLEANUP.dll')]]):
                observed = worker.execute(job, paths, {}, {}, b'OWNED_SIMULATED_IMAGE')
            self.assertEqual(observed['status'], 'failed')
            self.assertEqual(observed['failureStage'], 'module-observation-model-load')
            self.assertEqual(state['inferences'], 0); self.assertEqual(state['cleanups'], 1)
            self.assertEqual(observed['cleanup']['endingAllocatedBytes'], 0)

    def test_source_bytes_are_lf_and_worker_has_no_imported_gpu_runtime(self):
        source = (ROOT / 'resources/image-to-splat/worker.py').read_bytes()
        self.assertNotIn(b'\r', source)
        self.assertFalse(any(name in sys.modules for name in ('torch', 'numpy', 'torchvision', 'triposplat', 'model')))


if __name__ == '__main__':
    unittest.main(verbosity=2)
