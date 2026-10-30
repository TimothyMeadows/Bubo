"""Fixed, offline Qwen3-TTS 12Hz 1.7B profile. Never provisions or promotes.

Heavy imports occur only after admission and offline guards. The two reviewed
import adaptations exclude the unused 25Hz/SoX graph; selected 12Hz math is
unchanged. Python guards are cooperative controls, not an OS sandbox.
"""
from pathlib import Path
from contextlib import redirect_stderr, redirect_stdout
import gc, hashlib, io, json, math, os, random, socket, struct, sys, tempfile, time

SOURCE_REVISION = '022e286b98fbec7e1e916cb940cdf532cd9f488e'
PROTOCOL = 'bubo-voice-worker/v1'
LANGUAGES = set('chinese english french german italian japanese korean portuguese russian spanish'.split())
PRESETS = set('aiden dylan eric ono_anna ryan serena sohee uncle_fu vivian'.split())
MODELS = {
    'qwen3-tts-customvoice': ('custom_voice', '0c0e3051f131929182e2c023b9537f8b1c68adfe', '17a07f527a1c25ea30b4e023a184482a23d3e279d697b1dc81b1bde498d29cf9', '38b1d5971bdbd982b561cccec982669a53b0537c3cf5e9bd4778ed07bb2f5137'),
    'qwen3-tts-base': ('base', 'fd4b254389122332181a7c3db7f27e918eec64e3', 'b4f01752d15a488abde3e1ab44723ae4f4b9e68a4037257b098b3737893cc1f9', '38fc7fc51c5e776e840414b6fd443962e9411b9654888fd7913e4da643cb857c'),
}
TOKENIZER_SHA256 = '836b7b357f5ea43e889936a3709af68dfe3751881acefe4ecf0dbd30ba571258'
SOURCE_FILES = {
    'qwen_tts/__init__.py': 'ea52de59d070fde366467a6902d0edcfc1b0575b8c570a0c71020c41d6a593ed',
    'qwen_tts/core/__init__.py': 'c3d2f2f28cae7a0ec4d8dd8251470c8871acd2bf143d2fc239fcfbe8f2938497',
    'qwen_tts/core/models/__init__.py': '917d38d8fd7ed84de73703fd2394d535ca416e15694d1e50324e4a06d4022c82',
    'qwen_tts/core/models/configuration_qwen3_tts.py': 'f52867f14fde06a416dd14864d503ce6d13d0a08d5f5da30191e1c80c13f5d18',
    'qwen_tts/core/models/modeling_qwen3_tts.py': '25c42656bcf810f06ef6bc1839bd7083f3c8cfedac3a147c4060b4262b1c96a0',
    'qwen_tts/core/models/processing_qwen3_tts.py': '61472203188e46eda68d740c25f0f228ece24a61ecf942e57d6e49e78b4d6bcd',
    'qwen_tts/core/tokenizer_12hz/configuration_qwen3_tts_tokenizer_v2.py': '9e30c24394b00cb0366d7da3482b7436468acc1cd3da1a6fe614a1d34653a5e3',
    'qwen_tts/core/tokenizer_12hz/modeling_qwen3_tts_tokenizer_v2.py': '844e8dd8c0182ef9c6463c874631c22ef3c5a4fd1899dd657016164cc5379628',
    'qwen_tts/inference/qwen3_tts_model.py': 'e1da450732857c1f5fe3e36ebab85db2f6dc6a48caaff6973f463384e30275e4',
    'qwen_tts/inference/qwen3_tts_tokenizer.py': '4ac0307ea79d846a90e66d2a200a8eb5378e64179038384c6b019a8325769eda',
}
ADAPTATIONS = [
    {'path': 'qwen_tts/core/__init__.py', 'resource': 'core-init.py', 'originalSha256': '1b380d9de843b6d585d938c339d066136567ca7125412674234204af4386679e', 'replacementSha256': SOURCE_FILES['qwen_tts/core/__init__.py']},
    {'path': 'qwen_tts/inference/qwen3_tts_tokenizer.py', 'resource': 'tokenizer-init.py', 'originalSha256': 'ac2d855022a1bd21d33ab7b267ec952eef71f81d3f8d969a306139ff1a929515', 'replacementSha256': SOURCE_FILES['qwen_tts/inference/qwen3_tts_tokenizer.py']},
]
SAMPLING = {
    'maximumNewTokens': 'max_new_tokens', 'doSample': 'do_sample',
    'temperature': 'temperature', 'topK': 'top_k', 'topP': 'top_p',
    'repetitionPenalty': 'repetition_penalty', 'subtalkerDoSample': 'subtalker_dosample',
    'subtalkerTemperature': 'subtalker_temperature', 'subtalkerTopK': 'subtalker_top_k', 'subtalkerTopP': 'subtalker_top_p',
}
MAX_REFERENCE_BYTES = 20 * 1024 * 1024
MAX_REFERENCE_SECONDS = 30
# Pinned Qwen forces min_new_tokens=2. Transformers suppresses EOS while the
# generated length is <2, so the first possible EOS is the third new token.
MINIMUM_NEW_TOKENS = 3


class ProfileError(ValueError):
    """A fixed, non-sensitive profile diagnostic safe for worker logs."""


def require(value, message):
    if not value:
        raise ProfileError(message)


def unique(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, 'Duplicate worker JSON property')
        result[key] = value
    return result


def no_constant(_):
    raise ValueError('Nonfinite JSON')


def read(path):
    require(path.is_file() and path.stat().st_size <= 1048576, 'Missing/unbounded worker JSON')
    return json.loads(path.read_text(encoding='utf-8'), object_pairs_hook=unique, parse_constant=no_constant)


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def number(value, minimum, maximum, include_minimum=True):
    return type(value) in (int, float) and math.isfinite(value) and (value >= minimum if include_minimum else value > minimum) and value <= maximum


def fields(value, expected, message):
    require(isinstance(value, dict) and set(value) == set(expected), message)


def text(value, maximum, empty=False):
    return isinstance(value, str) and len(value) <= maximum and '\0' not in value and (empty or bool(value.strip()))


def local_path(value):
    require(isinstance(value, str) and '\0' not in value and '://' not in value and not value.startswith(('\\\\', '//')), 'Explicit local path required')
    path = Path(value)
    require(path.is_absolute() and '..' not in path.parts, 'Absolute canonical local path required')
    # Reject aliases before resolving; resolve() alone would erase the evidence.
    for part in (path, *path.parents):
        require(not part.is_symlink(), 'Symlink/reparse paths prohibited')
        if part.exists():
            stat = part.lstat()
            require(not part.is_symlink() and not getattr(stat, 'st_file_attributes', 0) & 0x400, 'Symlink/reparse paths prohibited')
    return path.resolve()


def inside(path, parent):
    return path == parent or parent in path.parents


def validate_identity(identity):
    fields(identity, ('path', 'sha256'), 'Exact local file identity required')
    require(isinstance(identity['sha256'], str) and len(identity['sha256']) == 64 and all(c in '0123456789abcdef' for c in identity['sha256']), 'Lowercase SHA256 required')
    return local_path(identity['path'])


def validate_item(item, backend):
    fields(item, ('id', 'prompt', 'seed', 'voice'), 'Unknown voice item setting')
    require(text(item['id'], 80) and text(item['prompt'], 4096), 'Bounded identity/spoken text required')
    require(type(item['seed']) is int and 0 <= item['seed'] <= 2147483647, 'Explicit nonnegative int32 seed required')
    voice = item['voice']
    fields(voice, ('language', 'preset', 'expression', 'sampling', 'reference'), 'Unknown voice control')
    require(isinstance(voice['language'], str) and voice['language'] in LANGUAGES, 'Explicit supported lowercase language required')
    require(text(voice['expression'], 512, empty=True), 'Bounded expression required')
    sampling = voice['sampling']
    fields(sampling, SAMPLING, 'Unknown autoregressive sampling setting')
    require(type(sampling['maximumNewTokens']) is int and MINIMUM_NEW_TOKENS <= sampling['maximumNewTokens'] <= 2048, 'Token cap must permit EOS after the two mandatory audio tokens')
    require(sampling['doSample'] is True and sampling['subtalkerDoSample'] is True, 'Explicit talker/subtalker sampling required')
    for name in ('temperature', 'subtalkerTemperature'):
        require(number(sampling[name], 0, 2, False), 'Unsupported temperature')
    for name in ('topK', 'subtalkerTopK'):
        require(type(sampling[name]) is int and 1 <= sampling[name] <= 200, 'Unsupported topK')
    for name in ('topP', 'subtalkerTopP'):
        require(number(sampling[name], 0, 1, False), 'Unsupported topP')
    require(number(sampling['repetitionPenalty'], 1, 2), 'Unsupported repetition penalty')
    if backend == 'qwen3-tts-customvoice':
        require(isinstance(voice['preset'], str) and voice['preset'] in PRESETS and voice['reference'] is None, 'CustomVoice requires supported preset and no reference')
    else:
        require(backend == 'qwen3-tts-base' and voice['preset'] is None and voice['expression'] == '', 'Base forbids presets/expression')
        fields(voice['reference'], ('audio', 'transcript', 'rights', 'consent'), 'Base requires reference/transcript/rights/consent')
        for identity in voice['reference'].values():
            validate_identity(identity)


def validate_job(job, result):
    fields(job, ('schemaVersion', 'operation', 'backend', 'modelDirectory', 'sourceDirectory', 'rootDirectory', 'outputDirectory', 'cacheDirectory', 'settings', 'maximumAudioSeconds', 'items'), 'Unknown worker fields')
    require(job['schemaVersion'] == PROTOCOL and job['operation'] in ('probe', 'generate') and job['backend'] in MODELS, 'Unsupported voice protocol/operation/backend')
    require(job['settings'] == {'device': 'cuda:0', 'precision': 'bfloat16', 'offload': 'none'}, 'Unsupported settings; no fallback')
    require(number(job['maximumAudioSeconds'], 0, sys.float_info.max, False), 'Finite explicit audio budget required')
    root, model, source, output, cache = [local_path(job[key]) for key in ('rootDirectory', 'modelDirectory', 'sourceDirectory', 'outputDirectory', 'cacheDirectory')]
    require(all(p.is_dir() for p in (root, model, source, output, cache)), 'Existing local roots required')
    require(inside(output, root) and inside(cache, root) and output != root and cache != root, 'Owned output/cache roots required')
    roots = (model, source, output, cache)
    require(all(not inside(a, b) for a in roots for b in roots if a != b) and len(set(roots)) == 4, 'Worker roots overlap')
    result = local_path(str(result))
    require(inside(result, root) and result.parent.is_dir() and not result.exists() and not inside(result, model) and not inside(result, source), 'Immutable result under owned root required')
    items = job['items']
    require(isinstance(items, list) and len(items) <= 4 and bool(items) == (job['operation'] == 'generate'), 'Finite batch or zero-sample probe required')
    for item in items:
        validate_item(item, job['backend'])
        if item['voice']['reference'] is not None:
            for identity in item['voice']['reference'].values():
                path = validate_identity(identity)
                require(inside(path, root) and not inside(path, output) and not inside(path, cache), 'Reference must be immutable host-local input')
    require(len({item['id'] for item in items}) == len(items), 'Duplicate item identity')
    return root, model, source, output, cache, result


class ExecutionGuard:
    """Audit Python writes/process launches, including import-time attempts."""
    def __init__(self, writable, result):
        self.writable = tuple(writable)
        self.result = result
        self.network_attempts = []

    def write(self, value):
        if isinstance(value, int):  # Already-open standard/native file descriptor.
            return
        path = local_path(os.path.abspath(os.fsdecode(value)))
        require(path == self.result or any(inside(path, root) for root in self.writable), 'Write outside owned attempt scope prohibited')

    def network(self, *args, **kwargs):
        self.network_attempts.append(True)
        raise RuntimeError('Offline worker attempted network access')

    def audit(self, event, args):
        # CPython platform.uname() uses gethostname() on Windows during Torch
        # import. This reads the local host name; it performs no DNS lookup or
        # connection. Every other socket operation remains guarded.
        if event.startswith('socket.') and event not in ('socket.__new__', 'socket.gethostname'):
            self.network()
        if event in ('subprocess.Popen', 'os.system', 'os.posix_spawn', 'os.posix_spawnp', 'os.fork', 'os.forkpty', 'os.exec') or event.startswith('os.spawn'):
            raise RuntimeError('External process launch prohibited')
        if event == 'open':
            _, mode, flags = args
            if (isinstance(mode, str) and any(char in mode for char in 'wax+')) or (isinstance(flags, int) and flags & (os.O_WRONLY | os.O_RDWR | os.O_CREAT | os.O_TRUNC | os.O_APPEND)):
                self.write(args[0])
        elif event in ('os.remove', 'os.rmdir', 'os.mkdir', 'os.chmod', 'os.utime', 'os.truncate'):
            self.write(args[0])
        elif event in ('os.rename', 'os.replace', 'os.link', 'os.symlink'):
            self.write(args[0]); self.write(args[1])


def offline(cache, result, output=None, install_audit=True):
    require(sys.version_info[:3] == (3, 12, 16), 'Dedicated pinned CPython3.12.16 required')
    for key in list(os.environ):
        if key.startswith(('QWEN_', 'HF_', 'HUGGINGFACE_', 'TRANSFORMERS_', 'NUMBA_', 'TORCH_', 'TRITON_')) or key in ('HUGGING_FACE_HUB_TOKEN', 'SPACE_ID'):
            os.environ.pop(key, None)
    directories = {'HF_HOME': 'hf', 'HF_HUB_CACHE': 'hf/hub', 'HF_ASSETS_CACHE': 'hf/assets', 'HF_MODULES_CACHE': 'hf-modules', 'TRANSFORMERS_CACHE': 'hf/transformers', 'TORCH_HOME': 'torch', 'XDG_CACHE_HOME': 'xdg', 'NUMBA_CACHE_DIR': 'numba', 'TEMP': 'tmp', 'TMP': 'tmp', 'TMPDIR': 'tmp', 'CUDA_CACHE_PATH': 'cuda', 'TORCHINDUCTOR_CACHE_DIR': 'inductor', 'TRITON_CACHE_DIR': 'triton', 'MPLCONFIGDIR': 'matplotlib'}
    for key, name in directories.items():
        directory = cache / name
        directory.mkdir(parents=True, exist_ok=True)
        os.environ[key] = str(directory)
    os.environ.update(HF_HUB_OFFLINE='1', TRANSFORMERS_OFFLINE='1', HF_HUB_DISABLE_TELEMETRY='1', HF_HUB_DISABLE_IMPLICIT_TOKEN='1', TOKENIZERS_PARALLELISM='false', PYTHONDONTWRITEBYTECODE='1', DO_NOT_TRACK='1')
    sys.dont_write_bytecode = True
    tempfile.tempdir = str(cache / 'tmp')
    guard = ExecutionGuard((cache, output) if output is not None else (cache,), result)
    # urllib3 2.8.0 otherwise probes IPv6 by binding ::1 during import. Voice
    # production never uses network capabilities; disable this irrelevant probe
    # in this worker process before imports, without allowing any bind or DNS.
    socket.has_ipv6 = False
    if install_audit:
        sys.addaudithook(guard.audit)
    socket.socket.connect = guard.network
    socket.socket.connect_ex = guard.network
    socket.create_connection = guard.network
    socket.getaddrinfo = guard.network
    socket.gethostbyname = guard.network
    socket.gethostbyname_ex = guard.network
    socket.gethostbyaddr = guard.network
    handles = [os.add_dll_directory(str(Path(sys.prefix) / 'Scripts')), os.add_dll_directory(sys.base_prefix)] if os.name == 'nt' else []
    return guard, handles


def snapshot(model, source, backend):
    for name, sha in SOURCE_FILES.items():
        require(digest(source / name) == sha, 'Pinned source/adaptation identity differs')
    config = read(model / 'config.json')
    require(digest(model / 'config.json') == MODELS[backend][2] and config.get('model_type') == 'qwen3_tts' and config.get('tokenizer_type') == 'qwen3_tts_tokenizer_12hz' and config.get('tts_model_type') == MODELS[backend][0] and config.get('tts_model_size') == '1b7', 'Selected model type/size/tokenizer differs')
    require(digest(model / 'speech_tokenizer/config.json') == 'ee65bb901c876664ab8707c487157aa1a6ee57c65969b28fb5ec9dc211e68167', 'Selected speech tokenizer config differs')
    require(not config.get('auto_map'), 'Remote model code prohibited')
    expected = {'config.json', 'generation_config.json', 'merges.txt', 'model.safetensors', 'preprocessor_config.json', 'tokenizer_config.json', 'vocab.json', 'speech_tokenizer/config.json', 'speech_tokenizer/configuration.json', 'speech_tokenizer/model.safetensors', 'speech_tokenizer/preprocessor_config.json', '.gitattributes', 'README.md'}
    require(all((model / name).is_file() for name in expected), 'Complete local model snapshot required')
    return {'revision': MODELS[backend][1], 'modelType': MODELS[backend][0], 'modelSize': '1b7', 'configSha256': MODELS[backend][2], 'weightsSha256': MODELS[backend][3], 'speechTokenizerWeightsSha256': TOKENIZER_SHA256, 'sourceRevision': SOURCE_REVISION, 'sourceFiles': SOURCE_FILES, 'sourceAdaptations': ADAPTATIONS}


class DiscardText:
    def write(self, value):
        return len(value)
    def flush(self):
        pass


def backend(source):
    require('qwen_tts' not in sys.modules, 'Backend imported before offline admission')
    sys.path.insert(0, str(source))
    with redirect_stdout(DiscardText()), redirect_stderr(DiscardText()):
        import torch, transformers, accelerate, librosa, numpy, soundfile, av
        from qwen_tts import Qwen3TTSModel
    expected = {'torch': '2.7.1+cu128' if os.name == 'nt' else '2.10.0+cu128', 'transformers': '4.57.3', 'accelerate': '1.12.0', 'librosa': '1.0.0', 'numpy': '2.5.4', 'soundfile': '0.14.0', 'av': '19.0.2'}
    actual = {name: str(sys.modules[name].__version__) for name in expected}
    require(actual == expected, 'Pinned backend dependency versions differ')
    require(not any(name in sys.modules for name in ('sox', 'torchaudio', 'onnxruntime', 'einops')), 'Unused25Hz dependency imported')
    return torch, numpy, soundfile, av, Qwen3TTSModel, actual


def source_modules(source):
    paths = {}
    for name, module in list(sys.modules.items()):
        if name == 'qwen_tts' or name.startswith('qwen_tts.'):
            require('tokenizer_25hz' not in name, 'Unused25Hz graph imported')
            location = getattr(module, '__file__', None)
            if location is None:  # namespace package; all executable children checked below
                require(all(inside(local_path(str(p)), source) for p in module.__path__), 'Unexpected source namespace')
                continue
            path = local_path(location)
            require(inside(path, source), 'Source imported from outside pinned tree')
            relative = path.relative_to(source).as_posix()
            require(relative in SOURCE_FILES and digest(path) == SOURCE_FILES[relative], 'Unexpected executable source module')
            paths[name] = {'path': str(path), 'sha256': SOURCE_FILES[relative]}
    return paths


def loaded_modules():
    if os.name != 'nt':
        maps = Path('/proc/self/maps')
        require(maps.is_file(), 'Native module enumeration unavailable')
        return sorted({line.split(None, 5)[5].strip() for line in maps.read_text().splitlines() if len(line.split(None, 5)) == 6 and 'x' in line.split(None, 5)[1] and line.split(None, 5)[5].startswith('/')})
    import ctypes
    from ctypes import wintypes
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.GetCurrentProcess.restype = wintypes.HANDLE
    kernel.K32EnumProcessModules.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.HMODULE), wintypes.DWORD, ctypes.POINTER(wintypes.DWORD)]
    kernel.K32EnumProcessModules.restype = wintypes.BOOL
    kernel.GetModuleFileNameW.argtypes = [wintypes.HMODULE, wintypes.LPWSTR, wintypes.DWORD]
    kernel.GetModuleFileNameW.restype = wintypes.DWORD
    array = (wintypes.HMODULE * 2048)(); needed = wintypes.DWORD()
    require(bool(kernel.K32EnumProcessModules(kernel.GetCurrentProcess(), array, ctypes.sizeof(array), ctypes.byref(needed))) and needed.value <= ctypes.sizeof(array), 'Cannot enumerate runtime modules')
    paths = []
    for index in range(needed.value // ctypes.sizeof(wintypes.HMODULE)):
        buffer = ctypes.create_unicode_buffer(32768)
        length = kernel.GetModuleFileNameW(array[index], buffer, len(buffer))
        require(0 < length < len(buffer), 'Cannot identify runtime module')
        paths.append(buffer.value)
    return paths


def placements(wrapper, torch, backend_id):
    model = wrapper.model
    require(model.tts_model_type == MODELS[backend_id][0] and model.tts_model_size == '1b7' and model.tokenizer_type == 'qwen3_tts_tokenizer_12hz', 'Actual model identity differs')
    require(set(wrapper.get_supported_languages()) == LANGUAGES | {'auto'}, 'Actual model languages differ')
    speakers = wrapper.get_supported_speakers()
    if backend_id == 'qwen3-tts-base':
        # The pinned Base config has no speaker IDs and normally reports [].
        # The official wrapper also permits None for absent speaker support;
        # accept it only while the actual Base configuration remains empty.
        require(model.config.talker_config.spk_id == {} and (speakers is None or set(speakers) == set()), 'Actual Base preset capabilities differ')
    else:
        require(speakers is not None and set(speakers) == PRESETS, 'Actual CustomVoice preset capabilities differ')
    tokenizer = model.speech_tokenizer
    require(tokenizer.get_model_type() == 'qwen3_tts_tokenizer_12hz' and tokenizer.get_input_sample_rate() == 24000 and tokenizer.get_output_sample_rate() == 24000 and tokenizer.get_decode_upsample_rate() == 1920, 'Actual tokenizer identity/rates differ')
    require(model.speaker_encoder_sample_rate == 24000, 'Actual speaker encoder rate differs')
    modules = [('talker', model.talker), ('speech-tokenizer', tokenizer.model)]
    if backend_id == 'qwen3-tts-base':
        modules.append(('speaker-encoder', model.speaker_encoder))
    else:
        require(model.speaker_encoder is None, 'Unrequested speaker encoder')
    observed = []
    for name, module in modules:
        count = 0
        for parameter in module.parameters():
            count += 1
            require(parameter.device.type == 'cuda' and parameter.device.index == 0 and (not parameter.is_floating_point() or parameter.dtype == torch.bfloat16), 'Actual component placement/precision differs')
        require(count > 0 and not any(hasattr(m, '_hf_hook') for m in module.modules()), 'Empty component or unrequested offload hook')
        observed.append(name + ':bfloat16')
    for module in (model, model.talker, model.talker.code_predictor, tokenizer.model, tokenizer.model.encoder, tokenizer.model.decoder):
        require(module.config._attn_implementation == 'sdpa', 'Unrequested attention fallback')
    return observed


def verified_bytes(identity, maximum):
    path = validate_identity(identity)
    require(path.is_file() and 0 < path.stat().st_size <= maximum, 'Missing/unbounded reference evidence')
    with path.open('rb') as stream:
        before = os.fstat(stream.fileno())
        value = stream.read(maximum + 1)
        after = os.fstat(stream.fileno())
    require(0 < len(value) <= maximum and before.st_size == after.st_size == len(value) and before.st_mtime_ns == after.st_mtime_ns and hashlib.sha256(value).hexdigest() == identity['sha256'], 'Reference evidence changed before use')
    return value


def validate_mp4(payload):
    """Reject external ISO-BMFF data references before native demuxing."""
    containers = {b'moov', b'trak', b'mdia', b'minf', b'dinf'}
    count = 0; references = 0
    def boxes(start, end, depth):
        nonlocal count, references
        require(depth <= 12, 'Excessive reference container nesting')
        while start < end:
            count += 1
            require(count <= 4096 and end - start >= 8, 'Malformed/unbounded reference container')
            size, kind = struct.unpack_from('>I4s', payload, start); header = 8
            if size == 1:
                require(end - start >= 16, 'Malformed extended container')
                size = struct.unpack_from('>Q', payload, start + 8)[0]; header = 16
            elif size == 0:
                size = end - start
            require(size >= header and start + size <= end, 'Invalid reference container length')
            position = start + header; limit = start + size
            if kind in containers:
                boxes(position, limit, depth + 1)
            elif kind == b'dref':
                require(limit - position >= 8 and payload[position:position + 4] == bytes(4), 'Malformed data reference table')
                entries = struct.unpack_from('>I', payload, position + 4)[0]; position += 8
                require(1 <= entries <= 16, 'Unbounded data reference table')
                for _ in range(entries):
                    require(limit - position >= 12, 'Malformed data reference')
                    length, entry_kind, flags = struct.unpack_from('>I4sI', payload, position)
                    require(length == 12 and entry_kind == b'url ' and flags == 1, 'External data references prohibited')
                    position += length; references += 1
                require(position == limit, 'Trailing data reference bytes')
            start = limit
    require(len(payload) >= 12 and payload[4:8] == b'ftyp', 'Explicit M4A/MP4 container required')
    boxes(0, len(payload), 0)
    require(references > 0, 'Self-contained audio data reference required')
    return references


def decode_reference(reference, cache, index, np, sf, av):
    # Hash all four admitted originals immediately before decode. Only hashes and
    # conversion metadata leave this operation; the exact transcript stays private.
    values = {name: verified_bytes(identity, MAX_REFERENCE_BYTES if name == 'audio' else (65536 if name == 'transcript' else 1048576)) for name, identity in reference.items()}
    transcript = values['transcript'].decode('utf-8', errors='strict')
    require(text(transcript, 4096), 'Bounded exact UTF8 reference transcript required')
    payload = values['audio']; suffix = Path(reference['audio']['path']).suffix.lower()
    require(suffix in ('.m4a', '.aac', '.wav'), 'Unsupported local reference format')
    if suffix == '.m4a':
        drefs = validate_mp4(payload); format_name = 'mov'
    elif suffix == '.wav':
        require(payload[:4] == b'RIFF' and payload[8:12] == b'WAVE', 'Explicit WAV container required')
        drefs = 0; format_name = 'wav'
    else:
        require(len(payload) >= 7 and payload[0] == 255 and payload[1] & 0xF6 == 0xF0, 'Explicit ADTS AAC required')
        drefs = 0; format_name = 'aac'
    def forbid_io(*args, **kwargs):
        raise RuntimeError('External decoder data access prohibited')
    options = {'protocol_whitelist': '', 'enable_drefs': '0', 'use_absolute_path': '0'}
    chunks = []; frames = 0; source_samples = 0
    with av.open(io.BytesIO(payload), mode='r', format=format_name, options=options, io_open=forbid_io) as container:
        require(len(container.streams) == 1 and len(container.streams.audio) == 1, 'Exactly one audio stream required')
        stream = container.streams.audio[0]
        codec = stream.codec_context
        source_rate = int(codec.sample_rate)
        source_channels = int(codec.channels)
        require(codec.name == 'aac' if format_name in ('mov', 'aac') else codec.name.startswith('pcm_'), 'Unexpected reference codec')
        require(8000 <= source_rate <= 192000 and source_channels in (1, 2), 'Unsupported reference rate/channels')
        if stream.duration is not None:
            require(number(float(stream.duration * stream.time_base), 0, MAX_REFERENCE_SECONDS + 1, False), 'Reference container exceeds duration budget')
        converter = av.AudioResampler(format='fltp', layout='mono' if source_channels == 1 else 'stereo', rate=source_rate)
        resampler = av.AudioResampler(format='fltp', layout='mono', rate=24000)
        def append(frame):
            nonlocal frames
            array = frame.to_ndarray()
            require(array.shape == (1, frame.samples) and array.dtype == np.float32 and bool(np.isfinite(array).all()), 'Invalid decoded reference samples')
            frames += int(frame.samples)
            require(frames <= 24000 * MAX_REFERENCE_SECONDS, 'Decoded reference exceeds duration budget')
            chunks.append(array[0].copy())
        for frame in container.decode(stream):
            require(frame.sample_rate == source_rate and len(frame.layout.channels) == source_channels, 'Reference format changed midstream')
            source_samples += int(frame.samples)
            require(source_samples <= source_rate * MAX_REFERENCE_SECONDS, 'Reference source exceeds duration budget')
            for planar in converter.resample(frame):
                array = planar.to_ndarray()
                require(array.shape == (source_channels, planar.samples) and array.dtype == np.float32 and bool(np.isfinite(array).all()), 'Invalid source reference samples')
                mono = np.mean(array, axis=0, dtype=np.float32).reshape(1, -1)
                converted = av.AudioFrame.from_ndarray(mono, format='fltp', layout='mono'); converted.sample_rate = source_rate
                for decoded in resampler.resample(converted):
                    append(decoded)
        require(not converter.resample(None), 'Unexpected delayed sample-format conversion')
        for decoded in resampler.resample(None):
            append(decoded)
        codec_name = codec.name
    require(24000 <= frames <= 24000 * MAX_REFERENCE_SECONDS, 'Reference must decode to1..30seconds')
    audio = np.concatenate(chunks).astype(np.float32, copy=False)
    decoded_path = cache / f'reference-{index:03d}.wav'
    with decoded_path.open('xb') as stream:
        sf.write(stream, audio, 24000, format='WAV', subtype='FLOAT')
    ancestry = {'originalSha256': reference['audio']['sha256'], 'transcriptSha256': reference['transcript']['sha256'], 'rightsSha256': reference['rights']['sha256'], 'consentSha256': reference['consent']['sha256'], 'decodedSha256': digest(decoded_path), 'decodedPcmSha256': hashlib.sha256(audio.astype('<f4', copy=False).tobytes()).hexdigest(), 'decodedFile': decoded_path.name, 'decoder': 'PyAV', 'decoderVersion': av.__version__, 'decoderLibraries': {name: list(version) for name, version in av.library_versions.items()}, 'container': format_name, 'codec': codec_name, 'sourceSampleRate': source_rate, 'sourceChannels': source_channels, 'sampleRate': 24000, 'channels': 1, 'frames': frames, 'sampleFormat': 'float32', 'sampleFormatConversion': 'libswresample planar float32 full-scale PCM conversion', 'downmix': 'arithmetic-mean-of-source-channels', 'resampling': 'libswresample-mono-float32-to24000Hz', 'externalDataReferences': False, 'selfContainedDataReferences': drefs, 'denoise': False, 'trim': False, 'normalization': False, 'xVectorOnlyMode': False}
    return audio, transcript, ancestry


def termination(tokens, maximum, eos):
    require(isinstance(tokens, list) and MINIMUM_NEW_TOKENS <= len(tokens) <= maximum and all(type(token) is int for token in tokens), 'Unexpected actual talker sequence')
    require(tokens[-1] == eos and eos not in tokens[:-1], 'Missing EOS/length-cap truncation; output rejected')
    return {'generatedTokens': len(tokens), 'audioCodeFrames': len(tokens) - 1, 'maximumNewTokens': maximum, 'eosTokenId': eos, 'eosObserved': True, 'lengthCapReached': len(tokens) == maximum, 'reason': 'eos', 'truncated': False, 'observation': 'actual-talker-generate-sequences-before-wrapper-code-filter'}


class TalkerObservation:
    """Observe actual sequences before upstream drops EOS; retain no tensors."""
    def __init__(self, talker, settings):
        self.talker = talker; self.settings = settings; self.observations = []

    def __enter__(self):
        self.original = self.talker.generate
        self.had_attribute = 'generate' in self.talker.__dict__
        self.previous = self.talker.__dict__.get('generate')
        self.talker.generate = self.generate
        return self

    def generate(self, *args, **kwargs):
        require(not self.observations and not args and kwargs.get('input_ids') is None and kwargs.get('inputs_embeds') is not None, 'Unexpected talker invocation/batch')
        require(all(kwargs.get(SAMPLING[key]) == value for key, value in self.settings.items()) and kwargs.get('min_new_tokens') == 2 and kwargs.get('eos_token_id') == 2150 and kwargs.get('return_dict_in_generate') is True, 'Actual talker sampling differs')
        produced = self.original(**kwargs)
        require(produced.sequences.ndim == 2 and produced.sequences.shape[0] == 1, 'Hidden extra talker candidates')
        observed = termination(produced.sequences[0].detach().cpu().tolist(), self.settings['maximumNewTokens'], 2150)
        self.observations.append(observed)
        return produced

    def __exit__(self, *args):
        if self.had_attribute:
            self.talker.generate = self.previous
        else:
            del self.talker.generate
        self.talker = self.original = self.previous = None


def seed_process(seed, torch, np):
    random.seed(seed); np.random.seed(seed); torch.manual_seed(seed); torch.cuda.manual_seed_all(seed)
    require(torch.initial_seed() == seed and all(int(value.initial_seed()) == seed for value in torch.cuda.default_generators), 'Actual process RNG seed differs')
    return {'seed': seed, 'python': 'random.seed', 'numpy': 'numpy.random.seed', 'torch': 'torch.manual_seed', 'cuda': 'torch.cuda.manual_seed_all', 'torchInitialSeed': int(torch.initial_seed()), 'cudaInitialSeeds': [int(value.initial_seed()) for value in torch.cuda.default_generators], 'scope': 'per-item-process-rng', 'crossPlatformBitwiseReproducibility': False}


def generate_one(wrapper, item, index, job, output, cache, identities, torch, np, sf, av):
    voice = item['voice']; settings = voice['sampling']
    path = output / f'{index:03d}.wav'; metadata_path = output / f'{index:03d}.settings.json'
    require(not path.exists() and not metadata_path.exists(), 'Immutable output already exists')
    rng = seed_process(item['seed'], torch, np)
    kwargs = {SAMPLING[key]: value for key, value in settings.items()}
    reference = None
    torch.cuda.synchronize(0); started = time.perf_counter()
    # Untrusted backend diagnostics cannot disclose transcript/conditioning.
    with redirect_stdout(DiscardText()), redirect_stderr(DiscardText()), torch.inference_mode(), TalkerObservation(wrapper.model.talker, settings) as observer:
        if job['backend'] == 'qwen3-tts-base':
            audio, transcript, reference = decode_reference(voice['reference'], cache, index, np, sf, av)
            prompt = wrapper.create_voice_clone_prompt(ref_audio=(audio, 24000), ref_text=transcript, x_vector_only_mode=False)
            require(len(prompt) == 1 and prompt[0].x_vector_only_mode is False and prompt[0].icl_mode is True and prompt[0].ref_text == transcript, 'Actual clone conditioning mode differs')
            produced, rate = wrapper.generate_voice_clone(text=item['prompt'], language=voice['language'], voice_clone_prompt=prompt, non_streaming_mode=True, **kwargs)
            del prompt, audio, transcript
        else:
            produced, rate = wrapper.generate_custom_voice(text=item['prompt'], language=voice['language'], speaker=voice['preset'], instruct=voice['expression'], non_streaming_mode=True, **kwargs)
    torch.cuda.synchronize(0); elapsed = time.perf_counter() - started
    require(len(observer.observations) == 1 and rate == 24000 and isinstance(produced, list) and len(produced) == 1, 'Actual output/termination count or native rate differs')
    observed = observer.observations[0]
    audio = np.asarray(produced[0])
    maximum_frames = math.floor(min(job['maximumAudioSeconds'], settings['maximumNewTokens'] * .08) * 24000)
    require(audio.dtype == np.float32 and audio.ndim == 1 and 0 < audio.shape[0] <= maximum_frames and audio.nbytes <= maximum_frames * 4 and bool(np.isfinite(audio).all()), 'Invalid/budget-exceeding native mono float32 waveform')
    # Upstream Base's proportional reference-prefix cut can round by one sample.
    require(abs(audio.shape[0] - observed['audioCodeFrames'] * 1920) <= (1 if reference is not None else 0), 'Native waveform/code duration differs')
    require(bool((np.abs(audio) <= 1).all()), 'Pinned codec intrinsic clamp behavior differs')
    components = placements(wrapper, torch, job['backend'])
    measurements = {'sampleRate': rate, 'channels': 1, 'frames': int(audio.shape[0]), 'peak': float(np.max(np.abs(audio))), 'rms': float(np.sqrt(np.mean(np.square(audio, dtype=np.float64)))), 'clippedFraction': float(np.mean(np.abs(audio) >= 1)), 'silentFraction': float(np.mean(np.abs(audio) < 1e-5))}
    with path.open('xb') as stream:
        sf.write(stream, audio, rate, format='WAV', subtype='FLOAT')
    require(path.stat().st_size <= audio.nbytes + 4096, 'Unexpected waveform byte budget')
    metadata = {'protocol': 'bubo-voice-settings/v1', 'backend': job['backend'], 'sourceRevision': SOURCE_REVISION, 'identities': identities, 'text': item['prompt'], 'language': voice['language'], 'preset': voice['preset'], 'expression': voice['expression'], 'sampling': settings, 'upstreamSampling': kwargs, 'nonStreamingMode': True, 'rng': rng, 'termination': observed, 'reference': reference, 'measurements': measurements, 'componentPrecisions': components, 'attention': 'sdpa', 'normalization': False, 'intrinsicDecoderClamp': {'minimum': -1, 'maximum': 1, 'source': 'Qwen3TTSTokenizerV2Decoder.forward', 'preserved': True}, 'referencePrefixRemoval': 'upstream-proportional-code-prefix' if reference is not None else None, 'actualOutputCount': 1, 'humanReview': 'pending', 'promotion': False}
    write_json(metadata_path, metadata)
    return {'id': item['id'], 'path': path.name, 'metadata': metadata_path.name, 'inferenceSeconds': elapsed, 'seed': item['seed'], 'scheduler': 'autoregressive-sampling', 'sampling': settings, 'termination': observed, **measurements}


def release_workspace_cache(torch):
    # Pinned Torch documents process-wide cuBLAS handle/stream workspaces.
    # Synchronize first; clearing library workspaces cannot free live tensors.
    torch.cuda.synchronize(0)
    before = int(torch.cuda.memory_allocated(0)); reserved_before = int(torch.cuda.memory_reserved(0))
    clear = getattr(torch._C, '_cuda_clearCublasWorkspaces', None)
    require(callable(clear), 'Pinned cuBLAS workspace cleanup unavailable')
    clear(); torch.cuda.empty_cache(); torch.cuda.synchronize(0)
    return {'mechanism': 'torch._C._cuda_clearCublasWorkspaces', 'allocatedBeforeWorkspaceClearBytes': before, 'reservedBeforeWorkspaceClearBytes': reserved_before, 'endingAllocatedBytes': int(torch.cuda.memory_allocated(0)), 'endingReservedBytes': int(torch.cuda.memory_reserved(0))}


def write_json(path, value):
    with path.open('x', encoding='utf-8') as stream:
        json.dump(value, stream, allow_nan=False, indent=2); stream.write('\n')


def python_identity():
    return {'executable': sys.executable, 'prefix': sys.prefix, 'basePrefix': sys.base_prefix, 'version': '.'.join(map(str, sys.version_info[:3]))}


def offline_profile():
    return {'ipv6CapabilityProbeDisabled': socket.has_ipv6 is False, 'mechanism': 'process-local socket.has_ipv6=False before guarded imports', 'reason': 'skip unused urllib3 bind-based import capability probe', 'bindConnectAndDnsBlocked': True}


def main():
    require(len(sys.argv) == 3, 'Usage: worker.py JOB.json RESULT.json')
    job = read(Path(sys.argv[1]))
    root, model, source, output, cache, result_path = validate_job(job, Path(sys.argv[2]))
    identities = snapshot(model, source, job['backend'])
    guard, dll_handles = offline(cache, result_path, output)
    torch, np, sf, av, Model, versions = backend(source)
    require(torch.cuda.is_available() and torch.cuda.is_bf16_supported(), 'Requested CUDA/bfloat16 unavailable')
    torch.cuda.set_device(0); torch.set_grad_enabled(False); torch.cuda.reset_peak_memory_stats(0)
    properties = torch.cuda.get_device_properties(0)
    wrapper = None; outputs = []; components = []; failure = None; loading = 0; stage = 'model-loading'
    module_paths = set(loaded_modules())
    started = time.perf_counter()
    try:
        with redirect_stdout(DiscardText()), redirect_stderr(DiscardText()):
            wrapper = Model.from_pretrained(str(model), device_map='cuda:0', dtype=torch.bfloat16, attn_implementation='sdpa', local_files_only=True, trust_remote_code=False, use_safetensors=True, weights_only=True)
        torch.cuda.synchronize(0); loading = time.perf_counter() - started
        stage = 'placement-verification'
        components = placements(wrapper, torch, job['backend'])
        for index, item in enumerate(job['items']):
            stage = 'item-' + str(index)
            outputs.append(generate_one(wrapper, item, index, job, output, cache, identities, torch, np, sf, av))
            module_paths.update(loaded_modules())
        stage = 'source-verification'
        imported_source = source_modules(source)
    except Exception as error:
        # Never retain the traceback or stringify backend errors: either can
        # retain/disclose private transcripts or live CUDA conditioning tensors.
        failure = str(error) if isinstance(error, ProfileError) else type(error).__name__ + '; private backend diagnostics suppressed'
    peak = int(torch.cuda.max_memory_allocated(0)); module_paths.update(loaded_modules())
    started = time.perf_counter(); wrapper = None; gc.collect()
    cleanup = release_workspace_cache(torch); unloading = time.perf_counter() - started
    write_json(output / 'cleanup.json', {'schemaVersion': 'bubo-voice-cleanup/v1', 'loadingSeconds': loading, 'unloadingSeconds': unloading, 'outputs': outputs, 'peakTorchAllocatedBytes': peak, 'failed': failure is not None, 'failureStage': stage if failure else None, 'failure': failure, **cleanup})
    require(cleanup['endingAllocatedBytes'] == 0, 'CUDA allocations remain after unload')
    require(not guard.network_attempts, 'Network attempts invalidate execution')
    require(failure is None, 'Voice execution failed at ' + stage + ': ' + (failure or ''))
    actual_python = python_identity()
    result = {'protocol': PROTOCOL, 'python': actual_python, 'actualPython': actual_python, 'backend': job['backend'], 'sourceRevision': SOURCE_REVISION, 'versions': versions, 'sourceModules': imported_source, 'loadedModules': sorted(module_paths | set(loaded_modules())), 'execution': {'device': 'cuda:0', 'deviceName': properties.name, 'deviceUuid': str(getattr(properties, 'uuid', 'unavailable')), 'precision': 'bfloat16', 'offload': 'none', 'componentPrecisions': components, 'attention': 'sdpa'}, 'offlineProfile': offline_profile(), 'loadingSeconds': loading, 'unloadingSeconds': unloading, 'outputs': outputs, 'modelLoads': 1, 'unloaded': True, 'cleanup': cleanup, 'peakTorchAllocatedBytes': peak, 'endingTorchAllocatedBytes': cleanup['endingAllocatedBytes'], 'aggregateDriverMemoryMeasured': False, 'networkAttempts': 0, 'operation': job['operation'], 'humanReview': 'pending', 'promotion': False}
    write_json(result_path, result)


def doctor():
    require(len(sys.argv) == 6, 'Usage: worker.py --doctor MODEL SOURCE CACHE RESULT.json')
    model, source, cache, result = [local_path(value) for value in sys.argv[2:]]
    require(all(path.is_dir() for path in (model, source, cache)) and result.parent.is_dir() and not result.exists(), 'Existing immutable doctor paths required')
    require(all(not inside(a, b) for a in (model, source, cache) for b in (model, source, cache) if a != b) and len({model, source, cache}) == 3 and not inside(result, model) and not inside(result, source), 'Doctor roots overlap immutable inputs')
    config = read(model / 'config.json')
    selected = [key for key, value in MODELS.items() if value[0] == config.get('tts_model_type')]
    require(len(selected) == 1, 'Unsupported doctor model')
    snapshot(model, source, selected[0])
    guard, dll_handles = offline(cache, result)
    torch, np, sf, av, Model, versions = backend(source)
    modules = source_modules(source)
    require(not guard.network_attempts, 'Network attempt invalidates import preflight')
    write_json(result, {'protocol': 'bubo-voice-doctor/v1', 'status': 'pass', 'sourceRevision': SOURCE_REVISION, 'backend': selected[0], 'python': python_identity(), 'versions': versions, 'sourceModules': modules, 'loadedModules': loaded_modules(), 'offlineProfile': offline_profile(), 'networkAttempts': 0, 'modelWeightsLoaded': False, 'samplingPerformed': False})


if __name__ == '__main__':
    try:
        doctor() if len(sys.argv) > 1 and sys.argv[1] == '--doctor' else main()
    except Exception as error:
        # Do not emit backend exception text, transcripts, tensors or traceback.
        diagnostic = str(error) if isinstance(error, ProfileError) else type(error).__name__ + '; private diagnostics suppressed'
        print('Bubo voice worker failed: ' + diagnostic, file=sys.stderr)
        raise SystemExit(1)
