"""Stdlib-only adversarial fixtures; no model, private audio, GPU or generation."""
from pathlib import Path
from contextlib import ExitStack, nullcontext
from types import SimpleNamespace
from unittest.mock import patch
import copy, hashlib, importlib.util, json, math, os, socket, struct, sys, tempfile, unittest

ROOT = Path(__file__).resolve().parents[1]
sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location('voice_worker', ROOT / 'resources/media/voice/worker.py')
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)


def temporary_fixture():
    return tempfile.TemporaryDirectory(prefix='.voice-worker-fixture-', dir=ROOT)


def sample():
    return {'id': 'owned-fixture', 'prompt': 'Owned fixture spoken words.', 'seed': 3,
            'voice': {'language': 'english', 'preset': 'ryan', 'expression': 'Calmly.', 'reference': None,
                      'sampling': {'maximumNewTokens': 128, 'doSample': True, 'temperature': .9, 'topK': 50, 'topP': 1., 'repetitionPenalty': 1.05, 'subtalkerDoSample': True, 'subtalkerTemperature': .9, 'subtalkerTopK': 50, 'subtalkerTopP': 1.}}}


def identity(path, value=b'owned fixture'):
    path.write_bytes(value)
    return {'path': str(path), 'sha256': hashlib.sha256(value).hexdigest()}


def clone(root):
    item = sample()
    item['voice'].update(preset=None, expression='', reference={name: identity(root / (name + '.wav' if name == 'audio' else name + '.txt')) for name in ('audio', 'transcript', 'rights', 'consent')})
    return item


def atom(kind, payload):
    return struct.pack('>I4s', len(payload) + 8, kind) + payload


def mp4(entry=None):
    if entry is None:
        entry = struct.pack('>I4sI', 12, b'url ', 1)
    body = atom(b'dref', bytes(4) + struct.pack('>I', 1) + entry)
    for name in (b'dinf', b'minf', b'mdia', b'trak', b'moov'):
        body = atom(name, body)
    return atom(b'ftyp', b'M4A ' + bytes(4)) + body + atom(b'mdat', b'OWNED_SYNTHETIC_NOT_AUDIO')


def urllib3_ipv6_probe(socket_module):
    # Pinned urllib3 2.8.0 util/connection.py _has_ipv6 branch, exercised with a
    # simulated socket interface. No installed urllib3 import or real socket.
    sock = None; has_ipv6 = False
    if socket_module.has_ipv6:
        try:
            sock = socket_module.socket(socket_module.AF_INET6)
            sock.bind(('::1', 0)); has_ipv6 = True
        except Exception:
            pass
    if sock:
        sock.close()
    return has_ipv6


class Sequence:
    """SIMULATED minimal talker sequence; never constructs a real tensor."""
    def __init__(self, tokens):
        self.tokens = tokens; self.ndim = 2; self.shape = (1, len(tokens))
    def __getitem__(self, index):
        return self
    def detach(self):
        return self
    def cpu(self):
        return self
    def tolist(self):
        return self.tokens


class VoiceWorkerTests(unittest.TestCase):
    def test_import_is_stdlib_only(self):
        self.assertFalse(any(name in sys.modules for name in ('torch', 'numpy', 'av', 'qwen_tts', 'soundfile', 'librosa')))

    def test_custom_and_base_admission(self):
        worker.validate_item(sample(), 'qwen3-tts-customvoice')
        with temporary_fixture() as temporary:
            worker.validate_item(clone(Path(temporary)), 'qwen3-tts-base')

    def test_simulated_base_absent_speakers_require_empty_config_customvoice_keeps_exact_nine(self):
        # SIMULATED configuration/placement observations; no model import/load.
        parameter = SimpleNamespace(device=SimpleNamespace(type='cuda', index=0), dtype='bfloat16', is_floating_point=lambda: True)
        class Component:
            def __init__(self): self.config = SimpleNamespace(_attn_implementation='sdpa')
            def parameters(self): return iter([parameter])
            def modules(self): return iter([self])
        model = Component()
        model.config.talker_config = SimpleNamespace(spk_id={})
        model.tts_model_type = 'base'; model.tts_model_size = '1b7'; model.tokenizer_type = 'qwen3_tts_tokenizer_12hz'
        model.talker = Component(); model.talker.code_predictor = Component(); model.speaker_encoder = Component()
        codec = Component(); codec.encoder = Component(); codec.decoder = Component()
        model.speech_tokenizer = SimpleNamespace(model=codec, get_model_type=lambda: 'qwen3_tts_tokenizer_12hz', get_input_sample_rate=lambda: 24000, get_output_sample_rate=lambda: 24000, get_decode_upsample_rate=lambda: 1920)
        model.speaker_encoder_sample_rate = 24000
        reported = {'speakers': None}
        wrapper = SimpleNamespace(model=model, get_supported_languages=lambda: worker.LANGUAGES | {'auto'}, get_supported_speakers=lambda: reported['speakers'])
        torch = SimpleNamespace(bfloat16='bfloat16')
        for speakers in (None, [], ()):
            reported['speakers'] = speakers
            self.assertEqual(worker.placements(wrapper, torch, 'qwen3-tts-base'), ['talker:bfloat16', 'speech-tokenizer:bfloat16', 'speaker-encoder:bfloat16'])
        reported['speakers'] = ['ryan']
        with self.assertRaises(ValueError): worker.placements(wrapper, torch, 'qwen3-tts-base')
        reported['speakers'] = None; model.config.talker_config.spk_id = {'ryan': 3061}
        with self.assertRaises(ValueError): worker.placements(wrapper, torch, 'qwen3-tts-base')
        model.tts_model_type = 'custom_voice'; model.speaker_encoder = None
        for speakers in (None, [], list(worker.PRESETS - {'ryan'}), list(worker.PRESETS | {'unknown'})):
            reported['speakers'] = speakers
            with self.assertRaises(ValueError): worker.placements(wrapper, torch, 'qwen3-tts-customvoice')
        reported['speakers'] = list(worker.PRESETS)
        self.assertEqual(worker.placements(wrapper, torch, 'qwen3-tts-customvoice'), ['talker:bfloat16', 'speech-tokenizer:bfloat16'])

    def test_reject_ignored_or_unsupported_controls(self):
        for name, value in [('seconds', 3), ('steps', 8), ('guidance', 1), ('negativePrompt', ''), ('loopCandidate', False), ('width', 512), ('seed', True), ('seed', 2147483648), ('prompt', ' '), ('prompt', '\0')]:
            with self.subTest(name=name, value=value):
                item = sample(); item[name] = value
                with self.assertRaises(ValueError): worker.validate_item(item, 'qwen3-tts-customvoice')
        for name, value in [('language', 'auto'), ('language', 'English'), ('preset', 'unknown'), ('preset', None), ('reference', {}), ('expression', None), ('voiceDesign', True)]:
            with self.subTest(name=name):
                item = sample(); item['voice'][name] = value
                with self.assertRaises(ValueError): worker.validate_item(item, 'qwen3-tts-customvoice')

    def test_sampling_boundaries_and_types(self):
        for name, value in [('maximumNewTokens', 0), ('maximumNewTokens', 1), ('maximumNewTokens', 2), ('maximumNewTokens', 2049), ('maximumNewTokens', True), ('doSample', False), ('doSample', 1), ('subtalkerDoSample', False), ('temperature', 0), ('temperature', float('nan')), ('temperature', True), ('subtalkerTemperature', 2.1), ('topK', 1.), ('topK', 201), ('subtalkerTopK', False), ('topP', 0), ('topP', float('inf')), ('subtalkerTopP', 1.1), ('repetitionPenalty', .99), ('repetitionPenalty', 2.01), ('extra', 1)]:
            with self.subTest(name=name, value=value):
                item = sample(); item['voice']['sampling'][name] = value
                with self.assertRaises(ValueError): worker.validate_item(item, 'qwen3-tts-customvoice')

    def test_clone_never_accepts_preset_expression_or_missing_evidence(self):
        with temporary_fixture() as temporary:
            root = Path(temporary); good = clone(root)
            for name in ('audio', 'transcript', 'rights', 'consent'):
                item = copy.deepcopy(good); del item['voice']['reference'][name]
                with self.assertRaises(ValueError): worker.validate_item(item, 'qwen3-tts-base')
            for name, value in [('preset', 'ryan'), ('expression', 'happy'), ('reference', None)]:
                item = copy.deepcopy(good); item['voice'][name] = value
                with self.assertRaises(ValueError): worker.validate_item(item, 'qwen3-tts-base')
            item = copy.deepcopy(good); item['voice']['reference']['audio']['path'] = 'https://example.invalid/a.wav'
            with self.assertRaises(ValueError): worker.validate_item(item, 'qwen3-tts-base')

    def test_json_duplicates_nonfinite_and_bounded_reads(self):
        with temporary_fixture() as temporary:
            path = Path(temporary) / 'job.json'
            for value in ('{"schemaVersion":1,"schemaVersion":2}', '{"value":NaN}', '{"value":Infinity}'):
                path.write_text(value)
                with self.assertRaises(ValueError): worker.read(path)
            path.write_bytes(b' ' * 1048577)
            with self.assertRaises(ValueError): worker.read(path)

    def test_job_paths_budget_and_immutable_input_separation(self):
        with temporary_fixture() as temporary:
            root = Path(temporary)
            model, source, output, cache = [root / name for name in ('model', 'source', 'output', 'cache')]
            for path in (model, source, output, cache): path.mkdir()
            job = {'schemaVersion': worker.PROTOCOL, 'operation': 'generate', 'backend': 'qwen3-tts-customvoice', 'rootDirectory': str(root), 'modelDirectory': str(model), 'sourceDirectory': str(source), 'outputDirectory': str(output), 'cacheDirectory': str(cache), 'settings': {'device': 'cuda:0', 'precision': 'bfloat16', 'offload': 'none'}, 'maximumAudioSeconds': 10, 'items': [sample()]}
            result = root / 'result.json'
            worker.validate_job(job, result)
            for key, value in [('cacheDirectory', str(output)), ('outputDirectory', str(root)), ('maximumAudioSeconds', float('nan')), ('maximumAudioSeconds', True), ('items', [sample()] * 5), ('operation', 'probe')]:
                bad = copy.deepcopy(job); bad[key] = value
                with self.assertRaises(ValueError): worker.validate_job(bad, result)
            bad = copy.deepcopy(job); bad['items'].append(sample())
            with self.assertRaises(ValueError): worker.validate_job(bad, result)
            bad = copy.deepcopy(job); bad.update(backend='qwen3-tts-base', items=[clone(cache)])
            with self.assertRaises(ValueError): worker.validate_job(bad, result)
            result.write_text('{}')
            with self.assertRaises(ValueError): worker.validate_job(job, result)

    def test_reference_freshness_size_and_no_path_aliases(self):
        with temporary_fixture() as temporary:
            root = Path(temporary); path = root / 'owned.txt'; value = identity(path)
            self.assertEqual(worker.verified_bytes(value, 100), b'owned fixture')
            path.write_bytes(b'changed fixture')
            with self.assertRaises(ValueError): worker.verified_bytes(value, 100)
            with self.assertRaises(ValueError): worker.verified_bytes(identity(path, b'a' * 101), 100)
            for value in ('https://example.invalid/a', 'relative.wav', '//example.invalid/share/a.wav', str(root / '..' / 'escape.wav')):
                with self.assertRaises(ValueError): worker.local_path(value)
            try:
                alias = root / 'alias'; alias.symlink_to(path)
            except OSError:
                pass  # Windows fixture user may lack symlink privilege.
            else:
                with self.assertRaises(ValueError): worker.local_path(str(alias))

    def test_mp4_self_contained_data_reference_and_adversarial_containers(self):
        self.assertEqual(worker.validate_mp4(mp4()), 1)
        for entry in (struct.pack('>I4sI', 12, b'url ', 0), atom(b'url ', bytes(4) + b'https://example.invalid/a'), struct.pack('>I4sI', 12, b'urn ', 1), struct.pack('>I4sI', 99, b'url ', 1)):
            with self.assertRaises(ValueError): worker.validate_mp4(mp4(entry))
        for value in (mp4()[:-1], b'RIFF' + bytes(64), atom(b'ftyp', bytes(4)) + struct.pack('>I4s', 999, b'moov')):
            with self.assertRaises(ValueError): worker.validate_mp4(value)
        body = atom(b'free', b'')
        for _ in range(14): body = atom(b'moov', body)
        with self.assertRaises(ValueError): worker.validate_mp4(atom(b'ftyp', bytes(4)) + body)

    def test_decoder_uses_only_verified_bytes_and_rejects_external_io(self):
        with temporary_fixture() as temporary:
            root = Path(temporary); reference = {name: identity(root / (name + '.m4a' if name == 'audio' else name + '.txt'), mp4() if name == 'audio' else b'Owned fixture exact text') for name in ('audio', 'transcript', 'rights', 'consent')}
            calls = []
            def opened(value, **kwargs):
                calls.append((value.getvalue(), kwargs))
                self.assertEqual(kwargs['format'], 'mov')
                self.assertEqual(kwargs['options'], {'protocol_whitelist': '', 'enable_drefs': '0', 'use_absolute_path': '0'})
                kwargs['io_open']('file:///never-read', 0, {})
            fake_av = SimpleNamespace(open=opened)
            with self.assertRaises(RuntimeError): worker.decode_reference(reference, root, 0, None, None, fake_av)
            self.assertEqual(calls[0][0], mp4())
            calls.clear(); Path(reference['consent']['path']).write_text('changed')
            with self.assertRaises(ValueError): worker.decode_reference(reference, root, 0, None, None, fake_av)
            self.assertEqual(calls, [])

    def test_simulated_reference_decode_downmix_preserves_amplitude_and_hash_only_ancestry(self):
        # SIMULATED PyAV/NumPy interface. Checks orchestration and privacy; this
        # does not certify native decoding/resampling or real media quality.
        class Array:
            dtype = 'float32'
            def __init__(self, values, rows=None):
                self.values = values; self.rows = rows
                self.shape = (rows, len(values) // rows) if rows else (len(values),)
            def __getitem__(self, index):
                width = self.shape[1]
                return Array(self.values[index * width:(index + 1) * width])
            def copy(self): return Array(list(self.values), self.rows)
            def astype(self, *args, **kwargs): return self
            def tobytes(self): return struct.pack('<' + 'f' * len(self.values), *self.values)
            def reshape(self, *args): return Array(self.values, 1)
        class Frame:
            def __init__(self, array):
                self.array = array; self.samples = array.shape[1]; self.sample_rate = 24000
                self.layout = SimpleNamespace(channels=list(range(array.shape[0])))
            def to_ndarray(self): return self.array
        class Resampler:
            def __init__(self, **kwargs): self.options = kwargs
            def resample(self, frame): return [] if frame is None else [frame]
        np = SimpleNamespace(float32='float32', mean=lambda value, **kwargs: Array([(left + right) / 2 for left, right in zip(value.values[:24000], value.values[24000:])]), isfinite=lambda value: SimpleNamespace(all=lambda: all(math.isfinite(x) for x in value.values)), concatenate=lambda chunks: Array([value for chunk in chunks for value in chunk.values]))
        writes = []
        def write(stream, array, rate, **kwargs):
            writes.append((array, rate, kwargs)); stream.write(b'OWNED-SIMULATED-WAV' + array.tobytes())
        sf = SimpleNamespace(write=write)
        codec = SimpleNamespace(sample_rate=24000, channels=2, name='aac')
        stream = SimpleNamespace(codec_context=codec, duration=24000, time_base=1 / 24000)
        class Streams(list):
            audio = [stream]
        class Container:
            streams = Streams([stream])
            def __enter__(self): return self
            def __exit__(self, *args): pass
            def decode(self, selected):
                self.selected = selected
                return [Frame(Array([1.2] * 24000 + [.2] * 24000, 2))]
        av = SimpleNamespace(open=lambda *args, **kwargs: Container(), AudioResampler=Resampler, AudioFrame=SimpleNamespace(from_ndarray=lambda array, **kwargs: Frame(array)), __version__='SIMULATED', library_versions={'libswresample': (1, 2, 3)})
        with temporary_fixture() as temporary:
            root = Path(temporary); transcript = 'Owned exact private fixture transcript\n'
            reference = {name: identity(root / (name + '.m4a' if name == 'audio' else name + '.txt'), mp4() if name == 'audio' else transcript.encode()) for name in ('audio', 'transcript', 'rights', 'consent')}
            audio, actual_transcript, metadata = worker.decode_reference(reference, root, 0, np, sf, av)
            self.assertEqual(actual_transcript, transcript)
            self.assertTrue(all(abs(value - .7) < 1e-6 for value in audio.values))
            self.assertEqual(writes[0][1:], (24000, {'format': 'WAV', 'subtype': 'FLOAT'}))
            self.assertEqual(metadata['decodedSha256'], worker.digest(root / 'reference-000.wav'))
            self.assertEqual(metadata['decodedPcmSha256'], hashlib.sha256(audio.tobytes()).hexdigest())
            self.assertNotIn(transcript, json.dumps(metadata)); self.assertNotIn(str(root), json.dumps(metadata))
            self.assertFalse(metadata['normalization']); self.assertFalse(metadata['trim']); self.assertFalse(metadata['denoise'])
            self.assertEqual(metadata['downmix'], 'arithmetic-mean-of-source-channels')

    def test_eos_observed_from_actual_sequence_and_cap_truncation_rejected(self):
        observed = worker.termination([42, 43, 2150], 4, 2150)
        self.assertEqual(observed['reason'], 'eos'); self.assertFalse(observed['lengthCapReached'])
        self.assertTrue(worker.termination([42, 43, 2150], 3, 2150)['lengthCapReached'])
        item = sample(); item['voice']['sampling']['maximumNewTokens'] = 3
        worker.validate_item(item, 'qwen3-tts-customvoice')
        for tokens, maximum in [([42, 43, 44], 3), ([42], 3), ([2150, 42, 43], 3), ([42, 2150, 2150], 3), ([42, 43, 2150], 2), ([42, 2150], 3)]:
            with self.assertRaises(ValueError): worker.termination(tokens, maximum, 2150)

    def test_simulated_talker_hook_checks_real_forwarded_settings_restores_and_releases(self):
        settings = sample()['voice']['sampling']; events = []
        def generate(**kwargs):
            events.append(kwargs); return SimpleNamespace(sequences=Sequence([42, 43, 2150]))
        talker = SimpleNamespace(generate=generate)
        args = {worker.SAMPLING[key]: value for key, value in settings.items()}
        args.update(inputs_embeds=object(), min_new_tokens=2, eos_token_id=2150, return_dict_in_generate=True)
        with worker.TalkerObservation(talker, settings) as observer:
            talker.generate(**args)
            with self.assertRaises(ValueError): talker.generate(**args)
        self.assertIs(talker.generate, generate); self.assertIsNone(observer.original); self.assertIsNone(observer.talker)
        self.assertEqual(observer.observations[0]['generatedTokens'], 3)
        bad = dict(args, top_k=1)
        with worker.TalkerObservation(talker, settings):
            with self.assertRaises(ValueError): talker.generate(**bad)
        self.assertIs(talker.generate, generate)
        talker.generate = lambda **kwargs: SimpleNamespace(sequences=Sequence([42, 43]))
        with self.assertRaises(ValueError):
            with worker.TalkerObservation(talker, settings): talker.generate(**args)

    def test_simulated_rng_sets_all_four_sources_before_item(self):
        events = []; initial = {'seed': None}
        def manual(seed): initial['seed'] = seed; events.append(('torch', seed))
        torch = SimpleNamespace(manual_seed=manual, initial_seed=lambda: initial['seed'], cuda=SimpleNamespace(manual_seed_all=lambda seed: events.append(('cuda', seed)), default_generators=[SimpleNamespace(initial_seed=lambda: initial['seed'])]))
        np = SimpleNamespace(random=SimpleNamespace(seed=lambda seed: events.append(('numpy', seed))))
        with patch.object(worker.random, 'seed', lambda seed: events.append(('python', seed))):
            evidence = worker.seed_process(7, torch, np)
        self.assertEqual(events, [('python', 7), ('numpy', 7), ('torch', 7), ('cuda', 7)])
        self.assertEqual(evidence['cudaInitialSeeds'], [7]); self.assertFalse(evidence['crossPlatformBitwiseReproducibility'])

    def test_offline_caches_credentials_and_dns(self):
        with temporary_fixture() as temporary, ExitStack() as stack:
            root = Path(temporary); cache = root / 'cache'; cache.mkdir()
            stack.enter_context(patch.dict(os.environ, {'QWEN_DTYPE': 'float32', 'NUMBA_CACHE_DIR': 'outside', 'HF_TOKEN': 'OWNED_SENTINEL', 'HUGGINGFACE_API_TOKEN': 'OWNED_SENTINEL', 'SPACE_ID': 'OWNED_SENTINEL'}))
            stack.enter_context(patch.object(worker.sys, 'version_info', (3, 12, 16)))
            stack.enter_context(patch.object(worker.sys, 'prefix', str(root)))
            stack.enter_context(patch.object(worker.sys, 'base_prefix', str(root)))
            stack.enter_context(patch.object(worker.tempfile, 'tempdir', None))
            stack.enter_context(patch.object(socket, 'has_ipv6', True))
            if os.name == 'nt': stack.enter_context(patch.object(worker.os, 'add_dll_directory', lambda _: nullcontext()))
            for target, name in [(socket.socket, 'connect'), (socket.socket, 'connect_ex'), (socket, 'create_connection'), (socket, 'getaddrinfo'), (socket, 'gethostbyname'), (socket, 'gethostbyname_ex'), (socket, 'gethostbyaddr')]: stack.enter_context(patch.object(target, name))
            guard, handles = worker.offline(cache, root / 'result.json', install_audit=False)
            self.assertIs(socket.has_ipv6, False)
            def forbidden_socket(*args):
                self.fail('Disabled import capability probe must not construct a socket')
            probe_socket = SimpleNamespace(has_ipv6=socket.has_ipv6, AF_INET6=socket.AF_INET6, socket=forbidden_socket)
            self.assertFalse(urllib3_ipv6_probe(probe_socket))
            self.assertEqual(guard.network_attempts, [])
            self.assertTrue(worker.offline_profile()['ipv6CapabilityProbeDisabled'])
            self.assertFalse(any(name in os.environ for name in ('HF_TOKEN', 'HUGGINGFACE_API_TOKEN', 'QWEN_DTYPE', 'SPACE_ID')))
            for key in ('HF_HOME', 'HF_MODULES_CACHE', 'TORCH_HOME', 'NUMBA_CACHE_DIR', 'TEMP', 'TMP', 'TMPDIR', 'CUDA_CACHE_PATH', 'TORCHINDUCTOR_CACHE_DIR', 'TRITON_CACHE_DIR'):
                self.assertTrue(worker.inside(Path(os.environ[key]), cache), key)
            self.assertEqual(os.environ['HF_HUB_OFFLINE'], '1'); self.assertEqual(os.environ['TRANSFORMERS_OFFLINE'], '1')
            with self.assertRaises(RuntimeError): socket.getaddrinfo('example.invalid', 443)
            self.assertEqual(len(guard.network_attempts), 1)

    def test_cooperative_guard_rejects_outside_writes_processes_and_network(self):
        with temporary_fixture() as temporary:
            root = Path(temporary); cache = root / 'cache'; cache.mkdir(); result = root / 'result.json'
            guard = worker.ExecutionGuard((cache,), result)
            guard.audit('open', (str(cache / 'owned'), 'w', os.O_WRONLY))
            guard.audit('open', (str(result), 'x', os.O_CREAT))
            guard.audit('open', (str(root / 'immutable'), 'r', os.O_RDONLY))
            for event, args in [('open', (str(root / 'immutable'), 'w', os.O_WRONLY)), ('os.remove', (str(root / 'immutable'), -1)), ('os.rename', (str(cache / 'owned'), str(root / 'escaped'), -1, -1)), ('os.mkdir', (str(root / 'escaped'), 511, -1))]:
                with self.assertRaises(ValueError): guard.audit(event, args)
            for event in ('subprocess.Popen', 'os.system', 'os.posix_spawn', 'os.fork'):
                with self.assertRaises(RuntimeError): guard.audit(event, ())
            with self.assertRaises(RuntimeError): guard.audit('socket.connect', ())
            self.assertEqual(len(guard.network_attempts), 1)

    def test_torch_import_local_hostname_audit_is_allowed_but_dns_and_socket_io_remain_blocked(self):
        with temporary_fixture() as temporary:
            root = Path(temporary)
            guard = worker.ExecutionGuard((root,), root / 'result.json')
            # Exact CPython audit event from platform.machine()->uname()->_node()
            # on Windows. No host name or machine-specific information is read.
            guard.audit('socket.gethostname', ())
            self.assertEqual(guard.network_attempts, [])
            events = ('socket.getaddrinfo', 'socket.gethostbyname', 'socket.gethostbyaddr', 'socket.getnameinfo', 'socket.connect', 'socket.bind', 'socket.sendto', 'socket.sethostname')
            for event in events:
                with self.subTest(event=event), self.assertRaises(RuntimeError):
                    guard.audit(event, ())
            self.assertEqual(len(guard.network_attempts), len(events))

    def test_simulated_urllib3_swallowed_probe_exception_still_records_bind_attempt(self):
        with temporary_fixture() as temporary:
            root = Path(temporary); guard = worker.ExecutionGuard((root,), root / 'result.json')
            events = []
            sock = SimpleNamespace(bind=lambda address: guard.audit('socket.bind', (address,)), close=lambda: events.append('closed'))
            simulated_socket = SimpleNamespace(has_ipv6=True, AF_INET6=socket.AF_INET6, socket=lambda family: sock)
            self.assertFalse(urllib3_ipv6_probe(simulated_socket))
            self.assertEqual(events, ['closed'])
            self.assertEqual(len(guard.network_attempts), 1, 'Caught dependency errors never forgive network attempts')
            simulated_socket.has_ipv6 = False
            self.assertFalse(urllib3_ipv6_probe(simulated_socket))
            self.assertEqual(len(guard.network_attempts), 1, 'Disabling a later probe never resets prior attempts')

    def test_simulated_cleanup_measures_before_and_after_workspace_release(self):
        events = []; allocation = {'allocated': 8519680, 'reserved': 20971520}
        def clear(): events.append('clear'); allocation['allocated'] = 0
        def empty(): events.append('empty'); allocation['reserved'] = 0
        torch = SimpleNamespace(_C=SimpleNamespace(_cuda_clearCublasWorkspaces=clear), cuda=SimpleNamespace(synchronize=lambda _: events.append('sync'), memory_allocated=lambda _: allocation['allocated'], memory_reserved=lambda _: allocation['reserved'], empty_cache=empty))
        cleanup = worker.release_workspace_cache(torch)
        self.assertEqual(events, ['sync', 'clear', 'empty', 'sync'])
        self.assertEqual(cleanup, {'mechanism': 'torch._C._cuda_clearCublasWorkspaces', 'allocatedBeforeWorkspaceClearBytes': 8519680, 'reservedBeforeWorkspaceClearBytes': 20971520, 'endingAllocatedBytes': 0, 'endingReservedBytes': 0})
        with self.assertRaises(ValueError): worker.release_workspace_cache(SimpleNamespace(cuda=torch.cuda, _C=SimpleNamespace()))
        allocation['allocated'] = 4
        torch._C._cuda_clearCublasWorkspaces = lambda: None
        self.assertEqual(worker.release_workspace_cache(torch)['endingAllocatedBytes'], 4, 'Live allocations must never be reported as zero')

    def test_simulated_probe_is_zero_sample_and_failed_batch_has_private_cleanup_without_success(self):
        for fail in (False, True):
            with self.subTest(fail=fail), temporary_fixture() as temporary, ExitStack() as stack:
                root = Path(temporary)
                paths = {name: root / name for name in ('model', 'source', 'output', 'cache')}
                for path in paths.values(): path.mkdir()
                job = {'schemaVersion': worker.PROTOCOL, 'operation': 'generate' if fail else 'probe', 'backend': 'qwen3-tts-customvoice', 'rootDirectory': str(root), 'modelDirectory': str(paths['model']), 'sourceDirectory': str(paths['source']), 'outputDirectory': str(paths['output']), 'cacheDirectory': str(paths['cache']), 'settings': {'device': 'cuda:0', 'precision': 'bfloat16', 'offload': 'none'}, 'maximumAudioSeconds': 10, 'items': [sample()] if fail else []}
                job_path = root / 'job.json'; job_path.write_text(json.dumps(job)); result = root / 'result.json'
                counts = {'loads': 0, 'generations': 0}
                def load(*args, **kwargs): counts['loads'] += 1; return SimpleNamespace()
                def generate(*args, **kwargs): counts['generations'] += 1; raise RuntimeError('PRIVATE_TRANSCRIPT_SENTINEL PRIVATE_TENSOR_SENTINEL')
                cuda = SimpleNamespace(is_available=lambda: True, is_bf16_supported=lambda: True, set_device=lambda _: None, reset_peak_memory_stats=lambda _: None, get_device_properties=lambda _: SimpleNamespace(name='SIMULATED', uuid='SIMULATED'), synchronize=lambda _: None, max_memory_allocated=lambda _: 4, memory_allocated=lambda _: 0, memory_reserved=lambda _: 0, empty_cache=lambda: None)
                torch = SimpleNamespace(cuda=cuda, bfloat16='bfloat16', set_grad_enabled=lambda _: None, _C=SimpleNamespace(_cuda_clearCublasWorkspaces=lambda: None))
                stack.enter_context(patch.object(worker.sys, 'argv', ['worker.py', str(job_path), str(result)]))
                stack.enter_context(patch.object(worker, 'snapshot', lambda *args: {}))
                stack.enter_context(patch.object(worker, 'offline', lambda *args: (SimpleNamespace(network_attempts=[]), [])))
                stack.enter_context(patch.object(worker, 'backend', lambda *args: (torch, None, None, None, SimpleNamespace(from_pretrained=load), {'torch': 'SIMULATED'})))
                stack.enter_context(patch.object(worker, 'placements', lambda *args: ['SIMULATED:bfloat16']))
                stack.enter_context(patch.object(worker, 'loaded_modules', lambda: []))
                stack.enter_context(patch.object(worker, 'source_modules', lambda *args: {}))
                stack.enter_context(patch.object(worker, 'generate_one', generate))
                if fail:
                    with self.assertRaisesRegex(ValueError, 'private backend diagnostics suppressed'): worker.main()
                    self.assertFalse(result.exists())
                else:
                    worker.main()
                    evidence = json.loads(result.read_text())
                    self.assertEqual(evidence['outputs'], []); self.assertEqual(evidence['modelLoads'], 1)
                    self.assertTrue(evidence['unloaded']); self.assertEqual(evidence['endingTorchAllocatedBytes'], 0)
                self.assertEqual(counts, {'loads': 1, 'generations': int(fail)})
                cleanup = json.loads((paths['output'] / 'cleanup.json').read_text())
                self.assertEqual(cleanup['failed'], fail); self.assertEqual(cleanup['endingAllocatedBytes'], 0)
                self.assertNotIn('PRIVATE_TRANSCRIPT_SENTINEL', json.dumps(cleanup)); self.assertNotIn('PRIVATE_TENSOR_SENTINEL', json.dumps(cleanup))

    def test_reviewed_adaptations_are_exact_and_exclude_sox_graph(self):
        for adaptation in worker.ADAPTATIONS:
            path = ROOT / 'resources/media/voice/source-adaptations' / adaptation['resource']
            self.assertEqual(worker.digest(path), adaptation['replacementSha256'])
            source = path.read_text()
            compile(source, str(path), 'exec')
            self.assertNotIn('from .tokenizer_25hz', source)
            self.assertNotIn('Qwen3TTSTokenizerV1Config,', source)
            self.assertNotIn('import sox', source)
            self.assertIn('SPDX-License-Identifier: Apache-2.0', source)


if __name__ == '__main__':
    unittest.main(verbosity=2)
