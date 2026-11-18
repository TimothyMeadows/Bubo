"""Fixed offline TripoSplat worker. Setup, authorization and GPU ownership belong to Bubo.

Importing this module uses only the standard library. Native imports occur only
after strict job/file/image admission and offline guards. These process-local
guards do not establish an operating-system sandbox or aggregate GPU exclusion.
"""
from contextlib import redirect_stdout
from pathlib import Path
import gc
import hashlib
import importlib.abc
import importlib.metadata
import importlib.util
import io
import json
import math
import os
import random
import socket
import stat
import struct
import sys
import tempfile
import time
import warnings

sys.dont_write_bytecode = True
PROTOCOL = 'bubo-splat-worker/v1'
RESULT_PROTOCOL = 'bubo-splat-worker-result/v1'
CACHE_BYTE_LIMIT = 64 * 1024 * 1024
CACHE_ENTRY_LIMIT = 4096
SOURCE_REVISION = 'd8db9e018b413dd9c4a9fe22463781bf98e8e68d'
MODEL_REVISION = '56a96e603204ec410c4da60c13ea4fa09a2169a9'
SOURCE_FILES = {
    'model.py': 'e8cb5aaa95c5cb4907b37621c3f8148965131b7703ed52178edffdeabdf5b623',
    'triposplat.py': 'df17e9648f6e12988837311a207f06d24e302e5526632bc7d1596b68b8c89a35',
    'LICENSE': '9bca624970eb854fb4391320d52f91af0b4965970b309abed48d7159f9a262b9',
}
WEIGHTS = {
    'background_removal/birefnet.safetensors': '9ab37426bf4de0567af6b5d21b16151357149139362e6e8992021b8ce356a154',
    'clip_vision/dino_v3_vit_h.safetensors': 'a29ef35101a16966972a0d50732a6f3a608ff7cfffb2afa9bbe9007cb842cc53',
    'diffusion_models/triposplat_fp16.safetensors': 'c870b97ac1d6bc9177608a5ec625e19ef9f3c5019aa68f64b0fb7803abcd6d20',
    'vae/flux2-vae.safetensors': 'd64f3a68e1cc4f9f4e29b6e0da38a0204fe9a49f2d4053f0ec1fa1ca02f9c4b5',
    'vae/triposplat_vae_decoder_fp16.safetensors': 'ed0d0c3d43b599e326845d0ec70f3cf77be9a55e2d97627ac3b34d2830763cc8',
}
PROPERTIES = ('x', 'y', 'z', 'nx', 'ny', 'nz', 'f_dc_0', 'f_dc_1', 'f_dc_2',
              'opacity', 'scale_0', 'scale_1', 'scale_2', 'rot_0', 'rot_1', 'rot_2', 'rot_3')


class ProfileError(ValueError):
    pass


class NeedsSegmentation(ProfileError):
    pass


def require(condition, message):
    if not condition:
        raise ProfileError(message)


def fields(value, names, message):
    require(type(value) is dict and set(value) == set(names), message)


def integer(value, lower, upper):
    return type(value) is int and lower <= value <= upper


def number(value, lower, upper):
    return type(value) in (int, float) and math.isfinite(value) and lower <= value <= upper


def valid_hash(value):
    return type(value) is str and len(value) == 64 and all(c in '0123456789abcdef' for c in value)


def unique(pairs):
    result = {}
    for name, value in pairs:
        require(name not in result, 'Duplicate JSON field')
        result[name] = value
    return result


def strict_json(payload):
    require(type(payload) is bytes and 0 < len(payload) <= 1_048_576, 'Bounded UTF-8 JSON required')
    return json.loads(payload.decode('utf-8'), object_pairs_hook=unique,
                      parse_constant=lambda _: (_ for _ in ()).throw(ProfileError('Nonfinite JSON number')))


def local_path(value):
    require(type(value) is str and 0 < len(value) <= 4096 and not any(ord(c) < 32 for c in value), 'Invalid local path')
    require(not value.startswith(('\\\\', '//')), 'Network/device path aliases prohibited')
    path = Path(value)
    require(path.is_absolute(), 'Absolute admitted path required')
    parts = value.replace('\\', '/').split('/')
    require(not any(part in ('.', '..') for part in parts), 'Path dot segments prohibited')
    require(not any(':' in part for part in parts[1:]), 'Alternate data streams prohibited')
    require(not any(part.casefold() == '.git' for part in path.parts), 'Git metadata path prohibited')
    for candidate in (path, *path.parents):
        require(not candidate.is_symlink(), 'Symbolic path prohibited')
        if candidate.exists():
            require(not (getattr(candidate.lstat(), 'st_file_attributes', 0) & 0x400), 'Reparse path prohibited')
    return path


def inside(path, root):
    try:
        return os.path.commonpath([os.path.normcase(str(path)), os.path.normcase(str(root))]) == os.path.normcase(str(root))
    except ValueError:
        return False


def overlap(left, right):
    return inside(left, right) or inside(right, left)


def validate_job(job, job_path, result_path):
    fields(job, ('schemaVersion', 'operation', 'projectRoot', 'input', 'sourceDirectory', 'modelDirectory',
                 'outputDirectory', 'cacheDirectory', 'settings', 'limits'), 'Unsupported worker job fields')
    require(job['schemaVersion'] == PROTOCOL and job['operation'] == 'generate', 'Unsupported worker job operation')
    fields(job['input'], ('path', 'sha256'), 'Exact input identity required')
    require(valid_hash(job['input']['sha256']), 'Input identity must be lowercase SHA-256')
    settings = job['settings']
    fields(settings, ('seed', 'steps', 'guidance', 'shift', 'erodeRadius', 'gaussianCounts'), 'Unsupported generation settings')
    require(integer(settings['seed'], 0, 9_007_199_254_740_991), 'Safe integer seed required')
    require(integer(settings['steps'], 10, 20), 'Sampling steps must be between 10 and 20')
    require(number(settings['guidance'], 1, 10) and number(settings['shift'], 1, 5), 'Bounded guidance and shift required')
    require(integer(settings['erodeRadius'], 0, 8), 'Bounded alpha erosion radius required')
    counts = settings['gaussianCounts']
    require(type(counts) is list and 1 <= len(counts) <= 2 and all(type(n) is int and n in (32768, 131072) for n in counts)
            and len(set(counts)) == len(counts), 'Only distinct selected density profiles are admitted')
    limits = job['limits']
    fields(limits, ('maxInputBytes', 'maxInputPixels', 'maxInputEdge', 'maxAspectRatio', 'maxOutputBytes',
                    'maxGpuAllocatedBytes', 'minGpuFreeBytes'), 'Unsupported worker resource limits')
    for name, lower, upper in (
        ('maxInputBytes', 1, 512 * 1024 * 1024), ('maxInputPixels', 1, 16_777_216),
        ('maxInputEdge', 2, 8192), ('maxOutputBytes', 1, 512 * 1024 * 1024),
        ('maxGpuAllocatedBytes', 1, 128 * 1024**3), ('minGpuFreeBytes', 0, 128 * 1024**3),
    ):
        require(integer(limits[name], lower, upper), 'Invalid finite worker resource limit: ' + name)
    require(number(limits['maxAspectRatio'], 1, 4), 'Input aspect limit must be between 1 and 4')
    require(sum(n * 68 + 4096 for n in counts) <= limits['maxOutputBytes'], 'Output budget cannot hold declared profiles')
    paths = {name: local_path(job[name]) for name in ('projectRoot', 'sourceDirectory', 'modelDirectory', 'outputDirectory', 'cacheDirectory')}
    paths.update(input=local_path(job['input']['path']), job=local_path(str(job_path)), result=local_path(str(result_path)))
    root = paths['projectRoot']
    require(root.is_dir() and all(inside(path, root) and path != root for key, path in paths.items() if key != 'projectRoot'),
            'All execution paths must remain inside the admitted project')
    require(all(paths[name].is_dir() for name in ('sourceDirectory', 'modelDirectory')), 'Existing dependency directories required')
    for name in ('input', 'job'):
        require(paths[name].is_file() and stat.S_ISREG(paths[name].stat().st_mode), 'Regular input/job file required')
    for name in ('outputDirectory', 'cacheDirectory'):
        require(not paths[name].exists() and paths[name].parent.is_dir(), 'Fresh output/cache directories with existing parents required')
    require(not paths['result'].exists() and paths['result'].parent.is_dir(), 'Fresh result file required')
    dependencies = ('sourceDirectory', 'modelDirectory', 'input', 'job', 'result')
    require(all(not overlap(paths[a], paths[b]) for i, a in enumerate(dependencies) for b in dependencies[i + 1:]),
            'Dependency, input, job and result paths overlap')
    require(not overlap(paths['outputDirectory'], paths['cacheDirectory']), 'Output/cache directories overlap')
    require(all(not overlap(paths[w], paths[r]) for w in ('outputDirectory', 'cacheDirectory') for r in dependencies),
            'Writable roots overlap immutable execution inputs')
    return paths


def verified_bytes(path, expected, maximum, exact_size=None):
    path = local_path(str(path))
    require(path.is_file() and stat.S_ISREG(path.stat().st_mode), 'Regular pinned input required')
    with path.open('rb') as stream:
        payload = stream.read(maximum + 1)
    require(0 < len(payload) <= maximum and (exact_size is None or len(payload) == exact_size), 'Pinned input byte envelope differs')
    require(hashlib.sha256(payload).hexdigest() == expected, 'Pinned input identity differs')
    return payload


def fixed_catalog():
    path = local_path(str(Path(__file__).absolute().with_name('catalog.json')))
    data = strict_json(path.read_bytes())
    require(data['schemaVersion'] == 'bubo-splat-catalog/v1' and data['source']['revision'] == SOURCE_REVISION
            and data['modelRevision'] == MODEL_REVISION, 'Unsupported fixed source/model catalog')
    require(len(data['source']['files']) == 3 and {f['path']: f['sha256'] for f in data['source']['files']} == SOURCE_FILES, 'Fixed source inventory differs')
    require(len(data['weights']) == 5 and {f['path']: f['sha256'] for f in data['weights']} == WEIGHTS, 'Fixed weight inventory differs')
    return data


def snapshot(paths, catalog):
    source, models = paths['sourceDirectory'], paths['modelDirectory']
    source_names = set()
    for path in source.iterdir():
        source_names.add(path.name)
        require(len(source_names) <= 3, 'Unexpected source inventory')
    require(source_names == set(SOURCE_FILES), 'Source must contain exactly the fixed source and licence files')
    source_bytes = {}
    for entry in catalog['source']['files']:
        source_bytes[entry['path']] = verified_bytes(source / entry['path'], entry['sha256'], 1_048_576, entry['bytes'])
    files = set()
    allowed_directories = {str(Path(name).parent).replace('\\', '/') for name in WEIGHTS}
    entries = 0
    for path in models.rglob('*'):
        entries += 1
        require(entries <= 9, 'Unexpected model directory inventory')
        local_path(str(path))
        if path.is_file():
            files.add(path.relative_to(models).as_posix())
        else:
            require(path.is_dir() and path.relative_to(models).as_posix() in allowed_directories, 'Unexpected model directory')
        require(len(files) <= 5, 'Unexpected model file inventory')
    require(files == set(WEIGHTS), 'Model directory must contain exactly five pinned weight files')
    for entry in catalog['weights']:
        path = local_path(str(models / entry['path']))
        require(path.stat().st_size == entry['bytes'], 'Weight byte size differs')
        digest = hashlib.sha256()
        observed = 0
        with path.open('rb') as stream:
            while block := stream.read(1_048_576):
                observed += len(block)
                require(observed <= entry['bytes'], 'Weight grew during inspection')
                digest.update(block)
        require(observed == entry['bytes'] and digest.hexdigest() == entry['sha256'], 'Pinned weight identity differs')
    return source_bytes


class ExecutionGuard:
    def __init__(self, paths):
        self.roots = (paths['outputDirectory'], paths['cacheDirectory'])
        self.result = paths['result']
        self.network_attempts = 0

    def write(self, value):
        if isinstance(value, int):
            return
        path = local_path(os.path.abspath(os.fsdecode(value)))
        require(path == self.result or any(inside(path, root) for root in self.roots), 'Write outside owned attempt scope')

    def network(self, *args, **kwargs):
        self.network_attempts += 1
        raise ProfileError('Offline worker network access prohibited')

    def audit(self, event, args):
        if event.startswith('socket.') and event not in ('socket.__new__', 'socket.gethostname'):
            self.network()
        if event in ('subprocess.Popen', 'os.system', 'os.posix_spawn', 'os.posix_spawnp', 'os.fork', 'os.forkpty', 'os.exec') or event.startswith('os.spawn'):
            raise ProfileError('Worker subprocess creation prohibited')
        if event == 'open':
            _, mode, flags = args
            if (isinstance(mode, str) and any(c in mode for c in 'wax+')) or (isinstance(flags, int) and flags & (os.O_WRONLY | os.O_RDWR | os.O_CREAT | os.O_TRUNC | os.O_APPEND)):
                self.write(args[0])
        elif event in ('os.remove', 'os.rmdir', 'os.mkdir', 'os.chmod', 'os.utime', 'os.truncate'):
            self.write(args[0])
        elif event in ('os.rename', 'os.replace', 'os.link', 'os.symlink'):
            self.write(args[0]); self.write(args[1])


def offline(paths, install_audit=True):
    guard = ExecutionGuard(paths)
    if install_audit:
        sys.addaudithook(guard.audit)
    paths['outputDirectory'].mkdir()
    paths['cacheDirectory'].mkdir()
    for key in list(os.environ):
        if key.startswith(('HF_', 'HUGGINGFACE_', 'TRANSFORMERS_', 'PIP_', 'AWS_', 'AZURE_', 'GITHUB_', 'GH_', 'OPENAI_', 'TORCH_', 'PYTORCH_', 'CUDA_', 'CUBLAS_', 'CUDNN_', 'NCCL_', 'TRITON_', 'NUMBA_')) or key in ('HUGGING_FACE_HUB_TOKEN', 'HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY'):
            os.environ.pop(key, None)
    for key, name in {'HF_HOME': 'hf', 'HF_HUB_CACHE': 'hf/hub', 'TORCH_HOME': 'torch', 'XDG_CACHE_HOME': 'xdg',
                      'TEMP': 'tmp', 'TMP': 'tmp', 'TMPDIR': 'tmp', 'CUDA_CACHE_PATH': 'cuda',
                      'TORCHINDUCTOR_CACHE_DIR': 'inductor', 'TRITON_CACHE_DIR': 'triton', 'MPLCONFIGDIR': 'matplotlib'}.items():
        target = paths['cacheDirectory'] / name
        target.mkdir(parents=True, exist_ok=True)
        os.environ[key] = str(target)
    os.environ.update(HF_HUB_OFFLINE='1', TRANSFORMERS_OFFLINE='1', HF_HUB_DISABLE_TELEMETRY='1',
                      HF_HUB_DISABLE_IMPLICIT_TOKEN='1', PYTHONDONTWRITEBYTECODE='1', DO_NOT_TRACK='1',
                      CUDA_VISIBLE_DEVICES='0', CUDA_DEVICE_ORDER='PCI_BUS_ID', CUDA_CACHE_MAXSIZE='67108864')
    tempfile.tempdir = str(paths['cacheDirectory'] / 'tmp')
    socket.has_ipv6 = False
    for name in ('connect', 'connect_ex', 'bind', 'listen'):
        setattr(socket.socket, name, guard.network)
    for name in ('create_connection', 'getaddrinfo', 'gethostbyname', 'gethostbyname_ex', 'gethostbyaddr'):
        setattr(socket, name, guard.network)
    handles = [os.add_dll_directory(str(Path(sys.prefix) / 'Scripts')), os.add_dll_directory(sys.base_prefix)] if os.name == 'nt' else []
    return guard, handles


def verify_environment(catalog):
    require(os.name == 'nt' and sys.maxsize > 2**32, 'Only the pinned Windows x64 environment is selected for this worker profile')
    environment = catalog['environmentCandidates']['win-amd64']
    require('.'.join(map(str, sys.version_info[:3])) == environment['python']['version'], 'Pinned Python version differs')
    actual = {}
    for entry in environment['artifacts']:
        observed = importlib.metadata.version(entry['name'])
        require(observed == entry['version'], 'Pinned distribution version differs')
        actual[entry['name'].lower().replace('_', '-')] = observed
    require(all(name in actual for name in ('torch', 'torchvision', 'numpy', 'safetensors', 'pillow', 'tqdm')), 'Incomplete runtime distribution closure')
    return {name: actual[name] for name in ('torch', 'torchvision', 'numpy', 'safetensors', 'pillow', 'tqdm')}


def dimensions(width, height, limits):
    require(type(width) is int and type(height) is int and width >= 2 and height >= 2, 'Image dimensions must be nondegenerate')
    require(width <= limits['maxInputEdge'] and height <= limits['maxInputEdge']
            and width * height <= limits['maxInputPixels'], 'Decoded image dimensions exceed limits')
    require(max(width, height) / min(width, height) <= limits['maxAspectRatio'], 'Image aspect ratio exceeds its bound')


def mask_bounds(image):
    require(image.mode == 'RGBA', 'RGBA foreground required')
    bounds = image.getchannel('A').getbbox()
    require(bounds is not None and bounds[2] - bounds[0] >= 2 and bounds[3] - bounds[1] >= 2, 'Foreground mask is empty or degenerate')
    return bounds


def decode_input(payload, limits):
    from PIL import Image, ImageFilter
    Image.MAX_IMAGE_PIXELS = limits['maxInputPixels']
    with warnings.catch_warnings():
        warnings.simplefilter('error', Image.DecompressionBombWarning)
        with Image.open(io.BytesIO(payload), formats=('PNG', 'JPEG', 'WEBP')) as source:
            dimensions(*source.size, limits)
            require(getattr(source, 'n_frames', 1) == 1 and source.mode in ('RGB', 'RGBA'), 'Single-frame RGB/RGBA image required')
            source.load()
            image = source.copy()
    if image.mode == 'RGBA':
        mask_bounds(image)
    return image, Image, ImageFilter


def prepare_image(image, remove_background, image_api, image_filter, limits, erode_radius=1):
    # Match the reviewed fixed resize/erosion/bbox/crop/black-composite semantics.
    require(integer(erode_radius, 0, 8), 'Bounded alpha erosion radius required')
    width, height = image.size
    scale = 1024 / min(width, height)
    resized = (max(1, int(round(width * scale))), max(1, int(round(height * scale))))
    dimensions(*resized, limits)
    image = image.resize(resized, image_api.Resampling.LANCZOS)
    has_alpha = image.mode == 'RGBA' and image.getchannel('A').getextrema()[0] < 255
    if not has_alpha:
        if remove_background is None:
            raise NeedsSegmentation('Fixed BiRefNet segmentation required for opaque source')
        require(callable(remove_background), 'Fixed BiRefNet segmentation required for opaque source')
        image = remove_background(image.convert('RGB'))
        require(image.size == resized, 'Segmentation returned unexpected dimensions')
    if erode_radius > 0:
        image.putalpha(image.getchannel('A').filter(image_filter.MinFilter(2 * erode_radius + 1)))
    box = mask_bounds(image)
    # Pillow getbbox uses exclusive upper bounds; upstream nonzero alpha uses inclusive extrema.
    left, top, right, bottom = box[0], box[1], box[2] - 1, box[3] - 1
    cx, cy = (left + right) / 2, (top + bottom) / 2
    half = max(right - left, bottom - top) / 2 * 1.2
    crop = [int(cx - half), int(cy - half), int(cx + half), int(cy + half)]
    crop_width, crop_height = crop[2] - crop[0], crop[3] - crop[1]
    require(0 < crop_width <= limits['maxInputEdge'] and 0 < crop_height <= limits['maxInputEdge']
            and crop_width * crop_height <= limits['maxInputPixels'], 'Foreground crop exceeds decoded bounds')
    image = image.crop(crop).resize((1024, 1024), image_api.Resampling.LANCZOS)
    prepared = image_api.new('RGB', (1024, 1024), (0, 0, 0))
    prepared.paste(image, mask=image.getchannel('A'))
    return prepared, {'boundingBox': [left, top, right, bottom], 'crop': crop,
                      'maskSource': 'input-alpha' if has_alpha else 'birefnet',
                      'maskValidationStage': 'cpu-before-model-load' if has_alpha else 'after-birefnet-before-encoding'}


class FixedSourceLoader(importlib.abc.MetaPathFinder, importlib.abc.Loader):
    def __init__(self, source, payloads):
        self.source, self.payloads = source, payloads

    def find_spec(self, fullname, path=None, target=None):
        if fullname in ('model', 'triposplat'):
            return importlib.util.spec_from_loader(fullname, self, origin=str(self.source / (fullname + '.py')))
        return None

    def create_module(self, spec):
        return None

    def exec_module(self, module):
        name = module.__name__ + '.py'
        payload = self.payloads[name]
        require(hashlib.sha256(payload).hexdigest() == SOURCE_FILES[name], 'Executed source buffer identity differs')
        module.__file__ = str(self.source / name)
        exec(compile(payload, module.__file__, 'exec'), module.__dict__)


def load_backend(paths, source_bytes):
    require(not any(name in sys.modules for name in ('torch', 'model', 'triposplat')), 'Native backend imported before offline admission')
    finder = FixedSourceLoader(paths['sourceDirectory'], source_bytes)
    sys.meta_path.insert(0, finder)
    try:
        import torch
        import numpy as np
        import triposplat
        require(torch.version.cuda == '12.8', 'Pinned CUDA12.8 Torch build required')
        require(inside(local_path(str(Path(torch.__file__).absolute())), local_path(str(Path(sys.prefix).absolute()))), 'Torch origin is outside the verified environment')
        return torch, np, triposplat
    except BaseException:
        sys.meta_path.remove(finder)
        raise


def placements(pipe, torch):
    expected = {'dinov3': torch.bfloat16, 'vae_encoder': torch.bfloat16, 'rmbg': torch.float16,
                'flow_model': torch.float16, 'decoder': torch.float16}
    for name, dtype in expected.items():
        parameters = list(getattr(pipe, name).parameters())
        require(parameters and all(p.device.type == 'cuda' and p.device.index in (None, 0)
                                   and p.dtype == dtype for p in parameters), 'Native component placement or precision differs')
    return {name: str(dtype).removeprefix('torch.') for name, dtype in expected.items()}


def tensor_digest(tensors, torch, np):
    digest = hashlib.sha256()
    for name, tensor in sorted(tensors.items()):
        canonical = tensor.detach().to(device='cpu', dtype=torch.float32).contiguous().numpy()
        require(np.isfinite(canonical).all(), 'Nonfinite conditioning or sampled latent')
        digest.update(json.dumps({'name': name, 'shape': list(canonical.shape), 'encoding': 'float32-little-endian'},
                                 sort_keys=True, separators=(',', ':')).encode('ascii'))
        digest.update(canonical.astype('<f4', copy=False).tobytes())
    return digest.hexdigest()


def verify_ply(payload, count):
    require(type(payload) is bytes and 0 < len(payload) <= 512 * 1024 * 1024, 'Bounded binary Gaussian payload required')
    marker = b'end_header\n'
    end = payload.find(marker, 0, 65536)
    require(end >= 0, 'Missing bounded PLY header')
    end += len(marker)
    expected = 'ply\nformat binary_little_endian 1.0\nelement vertex ' + str(count) + '\n'
    expected += ''.join('property float ' + name + '\n' for name in PROPERTIES) + 'end_header\n'
    require(payload[:end] == expected.encode('ascii') and len(payload) == end + count * 68, 'Unexpected Gaussian SH0 representation/count')
    for index, row in enumerate(struct.iter_unpack('<17f', memoryview(payload)[end:])):
        for column, value in enumerate(row):
            require_finite(value, index, PROPERTIES[column])
            ceiling = 1_000_000 if column < 3 else 10000 if column < 9 else 80 if column == 9 else 30 if column < 13 else None
            if ceiling is not None and abs(value) > ceiling:
                raise ProfileError('Gaussian numeric profile exceeded at row ' + str(index) + ' property ' + PROPERTIES[column])
        require(1e-20 <= sum(value * value for value in row[13:]) <= 1e12,
                'Invalid Gaussian quaternion at row ' + str(index) + ' property rot_0')


def require_finite(value, index, name):
    if not math.isfinite(value):
        kind = 'NaN' if math.isnan(value) else '+Inf' if value > 0 else '-Inf'
        raise ProfileError('Nonfinite Gaussian payload at row ' + str(index) + ' property ' + name + ': ' + kind)


def array_float32_bytes(value, shape, name):
    require(tuple(value.shape) == shape, 'Unexpected Gaussian field shape: ' + name)
    encoded = value.astype('<f4', copy=False).tobytes()
    require(type(encoded) is bytes and len(encoded) == math.prod(shape) * 4,
            'Unexpected float32 Gaussian field length: ' + name)
    return encoded


def cpu_float32_tensor(value, shape, torch, name):
    value = value.detach().to(device='cpu', dtype=torch.float32).contiguous()
    require(value.dtype == torch.float32 and tuple(value.shape) == shape, 'Unexpected Gaussian tensor: ' + name)
    return value, array_float32_bytes(value.numpy(), shape, name)


def opacity_summary(encoded):
    minimum, maximum, bad, zero, one = None, None, 0, 0, 0
    for (value,) in struct.iter_unpack('<f', encoded):
        if math.isfinite(value):
            minimum = value if minimum is None else min(minimum, value)
            maximum = value if maximum is None else max(maximum, value)
            zero += value == 0
            one += value == 1
        else:
            bad += 1
    return minimum, maximum, bad, zero, one


class _GaussianExportView:
    """Read-only field facade for the exact pinned exporter, without its sigmoid inverse.

    A finite raw logit plus its finite stored bias is the intended PLY logit.
    Rounding sigmoid to FP16 can destroy that value before inverse activation.
    Never clip/repair raw values or mutate the model/source to work around it.
    """
    __slots__ = ('get_xyz', '_features_dc', 'get_opacity', '_inverse_opacity_activation',
                 'get_scaling', '_rotation', 'rots_bias')

    def __init__(self, gaussian, cached, logits):
        for name in ('get_xyz', '_features_dc', 'get_scaling', '_rotation', 'rots_bias'):
            object.__setattr__(self, name, getattr(gaussian, name))
        object.__setattr__(self, 'get_opacity', cached)

        def exact_inverse(value):
            require(value is cached, 'Pinned opacity export accessor identity differs')
            return logits

        object.__setattr__(self, '_inverse_opacity_activation', exact_inverse)

    def __setattr__(self, name, value):
        raise ProfileError('Gaussian export facade is read-only')


def serialize_gaussian(gaussian, count, torch, diagnostics):
    """Fixed SH0 f4 serialization. Diagnostic rows and indices are zero-based."""
    require(integer(count, 1, 131072) and gaussian.sh_degree == 0
            and tuple(gaussian.construct_list_of_attributes()) == PROPERTIES, 'Unexpected Gaussian export profile')
    require(type(diagnostics) is list and len(diagnostics) < 2, 'Bounded opacity diagnostics required')
    raw, raw_bytes = cpu_float32_tensor(gaussian._opacity, (count, 1), torch, 'opacity')
    bias, bias_bytes = cpu_float32_tensor(gaussian.opacity_bias_val, (), torch, 'opacity bias')
    cached = gaussian.get_opacity
    _, cached_bytes = cpu_float32_tensor(cached, (count, 1), torch, 'cached opacity')
    raw_min, raw_max, raw_bad, _, _ = opacity_summary(raw_bytes)
    _, _, cached_bad, cached_zero, cached_one = opacity_summary(cached_bytes)
    bias_value = struct.unpack('<f', bias_bytes)[0]
    diagnostics.append({'gaussianCount': count, 'rawFiniteMinimum': raw_min, 'rawFiniteMaximum': raw_max,
                        'rawNonfiniteCount': raw_bad, 'cachedZeroCount': cached_zero, 'cachedOneCount': cached_one,
                        'cachedNonfiniteCount': cached_bad, 'opacityBias': bias_value if math.isfinite(bias_value) else None})
    require_finite(bias_value, 0, 'opacity [bias]')
    for index, ((raw_value,), (cached_value,)) in enumerate(zip(struct.iter_unpack('<f', raw_bytes), struct.iter_unpack('<f', cached_bytes))):
        require_finite(raw_value, index, 'opacity')
        require_finite(cached_value, index, 'opacity [cached probability]')
        require(0 <= cached_value <= 1, 'Invalid cached Gaussian probability at row ' + str(index) + ' property opacity')
    logits = raw + bias
    _, direct = cpu_float32_tensor(logits, (count, 1), torch, 'opacity')
    for index, (value,) in enumerate(struct.iter_unpack('<f', direct)):
        require_finite(value, index, 'opacity')
        require(abs(value) <= 80, 'Gaussian numeric profile exceeded at row ' + str(index) + ' property opacity')
    # The verified pinned method owns all original coordinates, SH0, scale and
    # quaternion transforms. The facade's inverse accepts only the same cached
    # tensor and returns already validated logits without evaluating logit(p).
    export = type(gaussian)._get_ply_data(_GaussianExportView(gaussian, cached, logits),
                                           transform=type(gaussian)._DEFAULT_TRANSFORM)
    require(type(export) is tuple and len(export) == 6, 'Unexpected pinned Gaussian field inventory')
    widths = (3, 3, 3, 1, 3, 4)
    fields = [array_float32_bytes(value, (count, width), 'export group ' + str(index))
              for index, (value, width) in enumerate(zip(export, widths))]
    header = 'ply\nformat binary_little_endian 1.0\nelement vertex ' + str(count) + '\n'
    header += ''.join('property float ' + name + '\n' for name in PROPERTIES) + 'end_header\n'
    output = bytearray(header.encode('ascii'))
    output.extend(bytes(count * 68))
    offset = len(output) - count * 68
    for row in range(count):
        column = 0
        for encoded, width in zip(fields, widths):
            output[offset + row * 68 + column * 4:offset + row * 68 + (column + width) * 4] = encoded[row * width * 4:(row + 1) * width * 4]
            column += width
    encoded = bytes(output)
    verify_ply(encoded, count)
    return encoded


def check_gpu(torch, limits, expected_total=None):
    """Stage/flow-callback sample, not a continuous free-memory reservation.

    Recheck the selected visible CUDA device and actual free memory as well as
    this worker's allocator peak. Neither counter measures aggregate VRAM use
    continuously; concurrent applications can change free memory between samples.
    """
    require(integer(torch.cuda.current_device(), 0, 0), 'Selected CUDA device changed')
    observed = torch.cuda.mem_get_info(0)
    require(type(observed) in (tuple, list) and len(observed) == 2, 'Invalid CUDA memory counters')
    free, total = observed
    require(integer(total, 1, 2**63 - 1) and integer(free, 0, total), 'Invalid CUDA memory counters')
    require(expected_total is None or total == expected_total, 'Selected CUDA device memory capacity changed')
    require(free >= limits['minGpuFreeBytes'] and limits['maxGpuAllocatedBytes'] <= total,
            'Sampled residual GPU headroom/profile budget unavailable')
    peak = torch.cuda.max_memory_allocated(0)
    require(integer(peak, 0, total), 'Invalid Torch GPU allocation counter')
    require(peak <= limits['maxGpuAllocatedBytes'], 'Torch GPU allocation budget exceeded')
    return free, total


class CacheBudget:
    """Bound owned cache/temp data at stage samples; this is not an OS disk quota.

    Count every file and directory toward the entry ceiling. The CUDA driver
    cache knob limits only that cache; the complete tree still needs these
    samples. Failed caches are retained, without trimming or automatic retry.
    """
    def __init__(self, root):
        self.root = root
        self.byte_limit, self.entry_limit = CACHE_BYTE_LIMIT, CACHE_ENTRY_LIMIT
        self.maximum_observed_bytes = 0

    def observe(self):
        root = local_path(str(self.root))
        info = root.lstat()
        require(stat.S_ISDIR(info.st_mode) and not (getattr(info, 'st_file_attributes', 0) & 0x400),
                'Owned cache directory required')
        pending, entries, total = [root], 0, 0
        while pending:
            directory = pending.pop()
            # Never materialize an unbounded directory listing, recurse through
            # links, read file contents, or silently omit inaccessible entries.
            with os.scandir(directory) as children:
                for child in children:
                    entries += 1
                    require(entries <= self.entry_limit, 'Cache entry limit exceeded')
                    # Windows DirEntry.stat may leave link counts at zero;
                    # obtain full non-following metadata for hard-link checks.
                    path = local_path(child.path)
                    info = path.lstat()
                    require(not stat.S_ISLNK(info.st_mode) and not (getattr(info, 'st_file_attributes', 0) & 0x400),
                            'Cache links or reparse points prohibited')
                    require(inside(path, root), 'Cache entry escaped its owned directory')
                    if stat.S_ISDIR(info.st_mode):
                        pending.append(path)
                    else:
                        require(stat.S_ISREG(info.st_mode) and info.st_nlink == 1 and info.st_size >= 0,
                                'Cache entries must be singly linked ordinary files or directories')
                        total += info.st_size
                        self.maximum_observed_bytes = max(self.maximum_observed_bytes, total)
                        require(total <= self.byte_limit, 'Cache byte limit exceeded')

    def observations(self):
        return {'maximumObservedBytes': self.maximum_observed_bytes, 'byteLimit': self.byte_limit,
                'fileLimit': self.entry_limit, 'measurement': 'stage-sampled; not OS quota'}


def cleanup_gpu(torch):
    torch.cuda.synchronize(0)
    before, reserved = int(torch.cuda.memory_allocated(0)), int(torch.cuda.memory_reserved(0))
    clear = getattr(torch._C, '_cuda_clearCublasWorkspaces', None)
    require(callable(clear), 'Pinned CUDA workspace cleanup unavailable')
    clear(); torch.cuda.empty_cache(); torch.cuda.synchronize(0)
    return {'allocatedBeforeWorkspaceClearBytes': before, 'reservedBeforeWorkspaceClearBytes': reserved,
            'endingAllocatedBytes': int(torch.cuda.memory_allocated(0)), 'endingReservedBytes': int(torch.cuda.memory_reserved(0)),
            'mechanism': 'torch._C._cuda_clearCublasWorkspaces'}


def loaded_modules():
    """Bounded current-process Windows sample, not continuous module observation."""
    require(os.name == 'nt', 'Windows runtime module observations required')
    import ctypes
    from ctypes import wintypes
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.GetCurrentProcess.restype = wintypes.HANDLE
    kernel.K32EnumProcessModules.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.HMODULE),
                                           wintypes.DWORD, ctypes.POINTER(wintypes.DWORD)]
    kernel.K32EnumProcessModules.restype = wintypes.BOOL
    kernel.GetModuleFileNameW.argtypes = [wintypes.HMODULE, wintypes.LPWSTR, wintypes.DWORD]
    kernel.GetModuleFileNameW.restype = wintypes.DWORD
    modules = (wintypes.HMODULE * 2048)()
    needed = wintypes.DWORD()
    size = ctypes.sizeof(modules)
    observed = kernel.K32EnumProcessModules(kernel.GetCurrentProcess(), modules, size, ctypes.byref(needed))
    require(bool(observed) and 0 < needed.value <= size and needed.value % ctypes.sizeof(wintypes.HMODULE) == 0,
            'Cannot enumerate bounded runtime modules')
    paths = []
    for index in range(needed.value // ctypes.sizeof(wintypes.HMODULE)):
        buffer = ctypes.create_unicode_buffer(32768)
        length = kernel.GetModuleFileNameW(modules[index], buffer, len(buffer))
        require(0 < length < len(buffer), 'Cannot identify runtime module')
        paths.append(buffer.value)
    return paths


def memory_observations():
    """Own-process commit and resident counters; neither aggregate-job RAM nor VRAM.

    Windows retains PeakPagefileUsage and PeakWorkingSetSize over the process
    lifetime, including peaks between stage samples. See PROCESS_MEMORY_COUNTERS_EX:
    https://learn.microsoft.com/en-us/windows/win32/api/psapi/ns-psapi-process_memory_counters_ex
    """
    require(os.name == 'nt' and sys.maxsize > 2**32, 'Windows x64 process memory observations required')
    import ctypes

    class ProcessMemoryCountersEx(ctypes.Structure):
        _fields_ = [('cb', ctypes.c_uint32), ('PageFaultCount', ctypes.c_uint32),
                    ('PeakWorkingSetSize', ctypes.c_size_t), ('WorkingSetSize', ctypes.c_size_t),
                    ('QuotaPeakPagedPoolUsage', ctypes.c_size_t), ('QuotaPagedPoolUsage', ctypes.c_size_t),
                    ('QuotaPeakNonPagedPoolUsage', ctypes.c_size_t), ('QuotaNonPagedPoolUsage', ctypes.c_size_t),
                    ('PagefileUsage', ctypes.c_size_t), ('PeakPagefileUsage', ctypes.c_size_t),
                    ('PrivateUsage', ctypes.c_size_t)]

    size = ctypes.sizeof(ProcessMemoryCountersEx)
    require(ctypes.sizeof(ctypes.c_uint32) == 4 and ctypes.sizeof(ctypes.c_size_t) == 8 and size == 80,
            'Windows x64 process memory counter ABI differs')
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.GetCurrentProcess.argtypes = []
    kernel.GetCurrentProcess.restype = ctypes.c_void_p
    kernel.K32GetProcessMemoryInfo.argtypes = [ctypes.c_void_p, ctypes.POINTER(ProcessMemoryCountersEx), ctypes.c_uint32]
    kernel.K32GetProcessMemoryInfo.restype = ctypes.c_int32
    counters = ProcessMemoryCountersEx()
    counters.cb = size
    require(bool(kernel.K32GetProcessMemoryInfo(kernel.GetCurrentProcess(), ctypes.byref(counters), size))
            and counters.cb == size, 'Windows process memory observation unavailable')
    peak, current = int(counters.PeakPagefileUsage), int(counters.PrivateUsage)
    peak_working, current_working = int(counters.PeakWorkingSetSize), int(counters.WorkingSetSize)
    require(integer(current, 1, 2**63 - 1) and integer(peak, current, 2**63 - 1)
            and integer(current_working, 1, 2**63 - 1) and integer(peak_working, current_working, 2**63 - 1),
            'Windows process memory counters are invalid')
    return {'scope': 'worker-process; not aggregate job', 'peakCommittedBytes': peak, 'currentPrivateBytes': current,
            'peakWorkingSetBytes': peak_working, 'currentWorkingSetBytes': current_working,
            'measurement': 'Windows process memory counters'}


def module_union(observed, current):
    """Combine phase samples only; the receiving installation owner admits each actual path."""
    require(type(observed) is list and type(current) is list and 1 <= len(current) <= 2048,
            'Nonempty bounded runtime module sample required')
    paths = {}
    for value in (*observed, *current):
        require(type(value) is str and 0 < len(value) < 32768 and not any(ord(c) < 32 for c in value)
                and Path(value).is_absolute(), 'Runtime module sample must contain bounded absolute paths')
        paths.setdefault(value.replace('/', '\\').casefold(), value)
        require(len(paths) <= 2048, 'Runtime module sample union exceeds its bound')
    return sorted(paths.values(), key=str.casefold)


def execute(job, paths, catalog, source_bytes, payload):
    guard, dll_handles = offline(paths)
    cache = CacheBudget(paths['cacheDirectory'])
    result = {'schemaVersion': RESULT_PROTOCOL, 'status': 'failed', 'operation': 'generate', 'backend': 'triposplat',
              'sourceRevision': SOURCE_REVISION, 'modelRevision': MODEL_REVISION, 'inputSha256': job['input']['sha256'],
              'settings': job['settings'], 'python': {'version': '.'.join(map(str, sys.version_info[:3])), 'executable': sys.executable},
              'versions': {}, 'device': None, 'preprocessing': None, 'loadingSeconds': 0.0, 'preprocessingSeconds': 0.0,
              'encodingSeconds': 0.0, 'inferenceSeconds': 0.0, 'unloadingSeconds': 0.0, 'densityDecoding': [],
              'latentSha256': None, 'conditioningSha256': None, 'outputs': [], 'modelLoads': 0, 'stochasticInferences': 0,
              'peakTorchAllocatedBytes': 0, 'peakTorchReservedBytes': 0, 'cleanup': None, 'networkAttempts': 0,
              'aggregateDriverMemoryMeasured': False, 'loadedModules': [], 'cacheObservations': cache.observations(),
              'memoryObservations': None, 'opacityDiagnostics': [], 'failureStage': None, 'failure': None, 'humanReview': 'pending', 'promotion': False}
    stage, torch, pipe, cond, sample, latent, generator, gaussian = 'environment', None, None, None, None, None, None, None
    cuda_ready = False
    failure = None
    try:
        stage = 'cache-preflight'
        cache.observe()
        stage = 'environment'
        result['versions'] = verify_environment(catalog)
        stage = 'cpu-image-admission'
        started = time.perf_counter()
        image, image_api, image_filter = decode_input(payload, job['limits'])
        original_size, original_mode = image.size, image.mode
        prepared, preprocessing = None, None
        try:
            prepared, preprocessing = prepare_image(image, None, image_api, image_filter, job['limits'],
                                                     erode_radius=job['settings']['erodeRadius'])
        except NeedsSegmentation:
            # RGB/opaque alpha needs BiRefNet, but its initial resize bounds
            # have already been checked on CPU before any native import.
            prepared, preprocessing = None, None
        result['preprocessingSeconds'] += time.perf_counter() - started
        stage = 'memory-observation-before-native'
        memory_observations()
        stage = 'native-import'
        torch, np, source = load_backend(paths, source_bytes)
        require(torch.cuda.is_available() and torch.cuda.is_bf16_supported(), 'CUDA and native bfloat16 support required')
        torch.cuda.set_device(0); cuda_ready = True
        properties = torch.cuda.get_device_properties(0)
        free, total = check_gpu(torch, job['limits'])
        require(integer(torch.cuda.memory_allocated(0), 0, 0), 'Fresh worker CUDA allocation state required')
        torch.cuda.set_per_process_memory_fraction(job['limits']['maxGpuAllocatedBytes'] / total, 0)
        torch.cuda.reset_peak_memory_stats(0)
        result['device'] = {'type': 'cuda', 'index': 0, 'name': properties.name, 'totalBytes': total, 'initialFreeBytes': free}
        random.seed(job['settings']['seed']); np.random.seed(job['settings']['seed'] % 2**32)
        torch.manual_seed(job['settings']['seed']); torch.cuda.manual_seed_all(job['settings']['seed'])
        stage = 'model-loading'
        started = time.perf_counter()
        model_root = paths['modelDirectory']
        with torch.inference_mode(), redirect_stdout(sys.stderr):
            pipe = source.TripoSplatPipeline(
                ckpt_path=str(model_root / 'diffusion_models/triposplat_fp16.safetensors'),
                decoder_path=str(model_root / 'vae/triposplat_vae_decoder_fp16.safetensors'),
                dinov3_path=str(model_root / 'clip_vision/dino_v3_vit_h.safetensors'),
                flux2_vae_encoder_path=str(model_root / 'vae/flux2-vae.safetensors'),
                rmbg_path=str(model_root / 'background_removal/birefnet.safetensors'), device='cuda:0')
            torch.cuda.synchronize(0)
            result['loadingSeconds'] = time.perf_counter() - started
            result['modelLoads'] = 1
            stage = 'cache-model-load'
            cache.observe()
            stage = 'module-observation-model-load'
            result['loadedModules'] = module_union(result['loadedModules'], loaded_modules())
            component_precisions = placements(pipe, torch)
            result['execution'] = {'device': 'cuda:0', 'offload': 'none', 'torchCuda': torch.version.cuda,
                                   'decoderSampling': 'systematic; global seeded RNG', 'globalSeed': job['settings']['seed'],
                                   'componentPrecisions': component_precisions}
            stage = 'gpu-budget-model-load'
            check_gpu(torch, job['limits'], total)
            stage = 'foreground-preprocessing'
            if prepared is None:
                started = time.perf_counter()
                prepared, preprocessing = prepare_image(image, pipe.rmbg.remove_background, image_api, image_filter, job['limits'],
                                                         erode_radius=job['settings']['erodeRadius'])
                torch.cuda.synchronize(0)
                result['preprocessingSeconds'] += time.perf_counter() - started
            result['preprocessing'] = dict(preprocessing, inputWidth=original_size[0], inputHeight=original_size[1], mode=original_mode,
                                           preparedWidth=prepared.size[0], preparedHeight=prepared.size[1],
                                           preparedPixelSha256=hashlib.sha256(prepared.tobytes()).hexdigest())
            generator = torch.Generator(device='cuda:0').manual_seed(job['settings']['seed'])
            stage = 'conditioning'
            started = time.perf_counter()
            cond = pipe.encode_image(prepared, generator=generator)
            torch.cuda.synchronize(0); result['encodingSeconds'] = time.perf_counter() - started
            result['conditioningSha256'] = tensor_digest(cond, torch, np)
            check_gpu(torch, job['limits'], total)
            cache.observe()
            stage = 'latent-inference'
            started = time.perf_counter()

            def inference_checkpoint(_step, _total):
                check_gpu(torch, job['limits'], total)
                cache.observe()

            sample = pipe.sample_latent(cond, steps=job['settings']['steps'], guidance_scale=job['settings']['guidance'],
                                        shift=job['settings']['shift'], generator=generator, show_progress=False,
                                        callback=inference_checkpoint)
            torch.cuda.synchronize(0); result['inferenceSeconds'] = time.perf_counter() - started
            result['stochasticInferences'] = 1
            stage = 'module-observation-inference'
            result['loadedModules'] = module_union(result['loadedModules'], loaded_modules())
            latent = sample['latent']
            result['latentSha256'] = tensor_digest({'latent': latent}, torch, np)
            total_bytes = 0
            for count in job['settings']['gaussianCounts']:
                stage = 'density-' + str(count)
                started = time.perf_counter()
                gaussian = pipe.decode_latent(latent, num_gaussians=count)
                torch.cuda.synchronize(0)
                result['densityDecoding'].append({'gaussianCount': count, 'seconds': time.perf_counter() - started})
                check_gpu(torch, job['limits'], total)
                cache.observe()
                require(gaussian.sh_degree == 0 and int(gaussian.get_xyz.shape[0]) == count, 'Decoder produced an unexpected Gaussian profile/count')
                encoded = serialize_gaussian(gaussian, count, torch, result['opacityDiagnostics'])
                require(total_bytes + len(encoded) <= job['limits']['maxOutputBytes'], 'Aggregate output byte limit exceeded')
                name = 'gaussian-' + str(count) + '.ply'
                target = local_path(str(paths['outputDirectory'] / name))
                with target.open('xb') as stream:
                    stream.write(encoded)
                total_bytes += len(encoded)
                result['outputs'].append({'path': name, 'sha256': hashlib.sha256(encoded).hexdigest(), 'bytes': len(encoded), 'gaussianCount': count})
                gaussian = None
            stage = 'source-and-inventory-recheck'
            snapshot(paths, catalog)
            require({path.name for path in paths['outputDirectory'].iterdir()} == {entry['path'] for entry in result['outputs']}, 'Unexpected worker output inventory')
            require(guard.network_attempts == 0, 'Offline execution was violated')
    except Exception as error:
        failure = str(error) if isinstance(error, ProfileError) else type(error).__name__ + '; backend diagnostics suppressed'
        result['failureStage'], result['failure'] = stage, failure
    finally:
        # Drop every owning model/tensor reference before collecting; do not retain backend traceback objects.
        pipe = cond = sample = latent = generator = gaussian = None
        gc.collect()
        if torch is not None and cuda_ready:
            started = time.perf_counter()
            try:
                result['peakTorchAllocatedBytes'] = int(torch.cuda.max_memory_allocated(0))
                result['peakTorchReservedBytes'] = int(torch.cuda.max_memory_reserved(0))
                result['cleanup'] = cleanup_gpu(torch)
                require(result['cleanup']['endingAllocatedBytes'] == 0 and result['cleanup']['endingReservedBytes'] == 0,
                        'CUDA allocated or reserved bytes remain after unload')
                result['loadedModules'] = module_union(result['loadedModules'], loaded_modules())
            except Exception:
                result['failureStage'], result['failure'] = 'cleanup', 'GPU cleanup or runtime module observation unavailable or incomplete'
                failure = 'cleanup'
            result['unloadingSeconds'] = time.perf_counter() - started
        try:
            cache.observe()
        except Exception:
            if failure is None:
                result['failureStage'], result['failure'] = 'cache-final-cleanup', 'Cache budget or safe inventory unavailable'
                failure = 'cache-final-cleanup'
        result['cacheObservations'] = cache.observations()
        try:
            # Query after native/cache cleanup; lifetime commit/working-set
            # peaks retain allocations that have already been released.
            result['memoryObservations'] = memory_observations()
        except Exception:
            result['memoryObservations'] = None
            if failure is None:
                result['failureStage'], result['failure'] = 'memory-observation-final-cleanup', 'Windows process memory observation unavailable or invalid'
                failure = 'memory-observation-final-cleanup'
        result['networkAttempts'] = guard.network_attempts
        if guard.network_attempts and failure is None:
            result['failureStage'], result['failure'] = 'offline', 'Offline execution was violated'
        if failure is None and guard.network_attempts == 0:
            result['status'] = 'succeeded'
        with local_path(str(paths['result'])).open('x', encoding='utf-8', newline='\n') as stream:
            json.dump(result, stream, allow_nan=False, sort_keys=True, indent=2)
            stream.write('\n')
        # Keep Windows DLL directory handles alive through all imports and cleanup.
        _ = dll_handles
    return result


def main(argv=None):
    arguments = sys.argv[1:] if argv is None else argv
    require(len(arguments) == 3, 'Usage: worker.py JOB_JSON RESULT_JSON JOB_SHA256')
    job_path, result_path = local_path(arguments[0]), local_path(arguments[1])
    require(valid_hash(arguments[2]), 'Exact registered job identity required')
    payload = verified_bytes(job_path, arguments[2], 1_048_576)
    job = strict_json(payload)
    paths = validate_job(job, job_path, result_path)
    catalog = fixed_catalog()
    source_bytes = snapshot(paths, catalog)
    image = verified_bytes(paths['input'], job['input']['sha256'], job['limits']['maxInputBytes'])
    result = execute(job, paths, catalog, source_bytes, image)
    return 0 if result['status'] == 'succeeded' else 1


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except ProfileError as error:
        print('Splat worker admission failed: ' + str(error), file=sys.stderr)
        raise SystemExit(1)
    except Exception as error:
        print('Splat worker failed: ' + type(error).__name__, file=sys.stderr)
        raise SystemExit(1)
