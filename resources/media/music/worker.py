"""Fixed ACE-Step 1.5 Turbo offline profile. No planning LM or CPU fallback.

Strict decoder adapted from ACE-Step ca1e85fe, generate_music_decode.py,
Copyright ACE-Step contributors, MIT (see resources/media/notices).
It retains latent validation/tiled GPU decoding, but preserves raw amplitude.
"""
from pathlib import Path
import gc,hashlib,json,math,os,socket,sys,time

SOURCE_REVISION='ca1e85fe9430179831e6bc6be790c332190a3866'
CODE={'configuration_acestep_v15.py':'5f15aa79fe793bba3c00b6992dd193a269954950603d5031ecd7703b0fa69da1',
      'modeling_acestep_v15_turbo.py':'71b22e01b490bd9e149e4c0c5da2e1f512126f3ab3316e0ae97779af84d19512'}
LANGUAGES=set('ar az bg bn ca cs da de el en es fa fi fr he hi hr ht hu id is it ja ko la lt ms ne nl no pa pl pt ro ru sa sk sr sv sw ta te th tl tr uk ur vi yue zh unknown'.split())

def require(value,message):
    if not value:raise ValueError(message)

def unique(pairs):
    result={}
    for key,value in pairs:
        require(key not in result,'Duplicate worker JSON property');result[key]=value
    return result

def read(path):
    require(path.is_file() and path.stat().st_size<=1048576,'Missing/unbounded worker JSON')
    return json.loads(path.read_text(encoding='utf-8'),object_pairs_hook=unique,parse_constant=lambda _:(_ for _ in ()).throw(ValueError('Nonfinite JSON')))

def digest(path):
    with path.open('rb') as stream:return hashlib.file_digest(stream,'sha256').hexdigest()

def loaded_modules():
    if os.name!='nt':return []
    import ctypes
    from ctypes import wintypes
    kernel=ctypes.WinDLL('kernel32',use_last_error=True);kernel.GetCurrentProcess.restype=wintypes.HANDLE
    kernel.K32EnumProcessModules.argtypes=[wintypes.HANDLE,ctypes.POINTER(wintypes.HMODULE),wintypes.DWORD,ctypes.POINTER(wintypes.DWORD)];kernel.K32EnumProcessModules.restype=wintypes.BOOL
    kernel.GetModuleFileNameW.argtypes=[wintypes.HMODULE,wintypes.LPWSTR,wintypes.DWORD];kernel.GetModuleFileNameW.restype=wintypes.DWORD
    array=(wintypes.HMODULE*2048)();needed=wintypes.DWORD()
    require(bool(kernel.K32EnumProcessModules(kernel.GetCurrentProcess(),array,ctypes.sizeof(array),ctypes.byref(needed))) and needed.value<=ctypes.sizeof(array),'Cannot enumerate runtime modules')
    paths=[]
    for index in range(needed.value//ctypes.sizeof(wintypes.HMODULE)):
        buffer=ctypes.create_unicode_buffer(32768);length=kernel.GetModuleFileNameW(array[index],buffer,len(buffer))
        require(0<length<len(buffer),'Cannot identify runtime module');paths.append(buffer.value)
    return paths

def offline(cache,model):
    # No inherited ACE override can change the admitted execution profile.
    for key in list(os.environ):
        if key.startswith('ACESTEP_') or key in ('HF_TOKEN','HUGGING_FACE_HUB_TOKEN','SPACE_ID'):os.environ.pop(key,None)
    os.environ.update(HF_HUB_OFFLINE='1',TRANSFORMERS_OFFLINE='1',HF_HUB_DISABLE_TELEMETRY='1',HF_HUB_DISABLE_IMPLICIT_TOKEN='1',TOKENIZERS_PARALLELISM='false',HF_HOME=str(cache/'hf'),HF_MODULES_CACHE=str(cache/'hf-modules'),TORCH_HOME=str(cache/'torch'),XDG_CACHE_HOME=str(cache/'xdg'),ACESTEP_PROJECT_ROOT=str(cache/'project'),ACESTEP_CHECKPOINTS_DIR=str(model),ACESTEP_VAE_ON_CPU='0')
    attempts=[]
    def no_network(*args,**kwargs):
        attempts.append(True);raise RuntimeError('Offline worker attempted network access')
    socket.socket.connect=no_network;socket.socket.connect_ex=no_network;socket.create_connection=no_network
    socket.getaddrinfo=no_network;socket.gethostbyname=no_network;socket.gethostbyname_ex=no_network;socket.gethostbyaddr=no_network
    require(sys.version_info[:3]==(3,12,16),'Dedicated pinned CPython3.12.16 required')
    directories=[os.add_dll_directory(str(Path(sys.prefix)/'Scripts')),os.add_dll_directory(sys.base_prefix)] if os.name=='nt' else []
    return attempts,directories

def snapshot(model):
    # The .NET verifier binds the complete eighteen-file model tree and source ZIP.
    for name,sha in CODE.items():require(digest(model/'acestep-v15-turbo'/name)==sha,'Admitted local model code differs')
    require({x.name for x in model.iterdir()}=={'acestep-v15-turbo','vae','Qwen3-Embedding-0.6B','.originals'},'Unexpected model component, including optional planning LM')

def validate_item(item):
    require(set(item)=={'id','prompt','seed','steps','guidance','seconds','loopCandidate','music'},'Unknown music item setting')
    require(isinstance(item['id'],str) and 0<len(item['id'])<=80 and isinstance(item['prompt'],str) and 0<len(item['prompt'])<=512,'Bounded identity/caption required')
    require(type(item['seed']) is int and 0<=item['seed']<=2147483647 and type(item['steps']) is int and 1<=item['steps']<=8 and item['guidance']==1,'Unsupported explicit sampling settings')
    require(type(item['seconds']) in (int,float) and math.isfinite(item['seconds']) and 10<=item['seconds']<=600 and item['seconds']==int(item['seconds']) and type(item['loopCandidate']) is bool,'Whole-second bounded duration required')
    m=item['music'];require(set(m)=={'mode','lyrics','vocalLanguage','bpm','keyScale','timeSignature','shift'},'Unknown music control')
    require(m['mode'] in ('instrumental','supplied-lyrics') and isinstance(m['lyrics'],str) and len(m['lyrics'])<=4096 and '\0' not in m['lyrics'],'Explicit bounded lyrics/mode required')
    require((m['lyrics']=='' and m['vocalLanguage']=='unknown') if m['mode']=='instrumental' else (bool(m['lyrics'].strip()) and m['vocalLanguage'] in LANGUAGES-{'unknown'}),'Mode/lyrics/language mismatch')
    require(m['bpm'] is None or type(m['bpm']) is int and 30<=m['bpm']<=300,'Unsupported tempo')
    import re
    require(isinstance(m['keyScale'],str) and (m['keyScale']=='' or re.fullmatch(r'[A-G](#|b)? (major|minor)',m['keyScale'],re.I)) and m['timeSignature'] in ('','2','3','4','6') and math.isfinite(m['shift']) and 1<=m['shift']<=5,'Unsupported metadata/shift')

class StrictDecodeMixin:
    """GPU-only VAE decode with raw amplitude and no hidden peak normalization."""
    def _decode_on_cpu(self,latents):raise RuntimeError('CPU VAE fallback prohibited')
    def _tiled_decode_cpu_fallback(self,latents):raise RuntimeError('CPU VAE fallback prohibited')

    def _decode_generate_music_pred_latents(self,pred_latents,progress,use_tiled_decode,time_costs):
        import torch
        if progress:progress(.8,desc='Decoding audio...')
        started=time.perf_counter()
        with torch.inference_mode():
            require(next(self.vae.parameters()).device.type=='cuda' and not self.use_mlx_vae,'Strict CUDA VAE required')
            pred_latents_cpu=pred_latents.detach().cpu()
            decode=pred_latents.transpose(1,2).contiguous().to(device='cuda:0',dtype=self.vae.dtype)
            del pred_latents
            self._empty_cache()
            pred_wavs=self.tiled_decode(decode) if use_tiled_decode else self.vae.decode(decode).sample
            require(next(self.vae.parameters()).device.type=='cuda','VAE left selected device')
            del decode
            pred_wavs=pred_wavs.float()
            self._empty_cache()
        gc.collect();torch.cuda.synchronize(0)
        time_costs['vae_decode_time_cost']=time.perf_counter()-started
        time_costs['total_time_cost']+=time_costs['vae_decode_time_cost']
        time_costs['offload_time_cost']=self.current_offload_cost
        return pred_wavs,pred_latents_cpu,time_costs

class StrictTokenizer:
    """Check the actual formatted conditioning, including instruction/metadata."""
    def __init__(self,tokenizer):self.tokenizer=tokenizer;self.observations=[]
    def __getattr__(self,name):return getattr(self.tokenizer,name)
    def __call__(self,text,*args,**kwargs):
        if kwargs.get('truncation'):
            limit=kwargs.get('max_length');require(limit in (256,2048),'Unregistered conditioning token limit')
            full=dict(kwargs);full.update(truncation=False,padding=False,return_tensors=None);full.pop('max_length',None)
            encoded=self.tokenizer(text,*args,**full)['input_ids']
            require(isinstance(text,str) and len(encoded)<=limit,'Formatted caption/lyrics exceed token limit; refusing silent truncation')
            self.observations.append({'text':text,'tokenCount':len(encoded),'maximumTokens':limit,'truncated':False})
            kwargs=dict(kwargs,truncation=False);kwargs.pop('max_length',None)
        return self.tokenizer(text,*args,**kwargs)

def backend(source,model):
    # This exact closed tree was checked against the pinned upstream ZIP by .NET.
    sys.path.insert(0,str(source))
    import torch,diffusers,transformers,torchaudio
    from acestep.handler import AceStepHandler
    from acestep.inference import generate_music,GenerationParams,GenerationConfig
    require(torch.__version__==('2.7.1+cu128' if os.name=='nt' else '2.10.0+cu128') and torchaudio.__version__==torch.__version__ and diffusers.__version__=='0.39.0' and transformers.__version__=='4.57.6','Pinned backend versions differ')
    class StrictHandler(StrictDecodeMixin,AceStepHandler):
        def _resolve_initialize_device(self,requested_device):
            require(requested_device=='cuda' and torch.cuda.is_available(),'Requested CUDA unavailable; initialization fallback prohibited');return 'cuda'
        def _ensure_models_present(self,*,checkpoint_path,config_path,prefer_source,vae_variant=None):
            require(Path(checkpoint_path).resolve()==model.resolve() and config_path=='acestep-v15-turbo' and vae_variant=='official','Unexpected model selection')
            snapshot(model);return None
        @staticmethod
        def _sync_model_code_if_needed(config_path,checkpoint_path):
            require(config_path=='acestep-v15-turbo' and Path(checkpoint_path).resolve()==model.resolve(),'Unexpected code selection');snapshot(model)
    return torch,StrictHandler,generate_music,GenerationParams,GenerationConfig

def placements(handler,torch):
    observed=[]
    for name in ('model','vae','text_encoder'):
        module=getattr(handler,name);parameters=list(module.parameters())
        require(parameters and all(p.device.type=='cuda' and (not p.is_floating_point() or p.dtype==torch.bfloat16) for p in parameters),'Observed model placement/precision differs: '+name)
        observed.append(name+':bfloat16')
        del parameters,module
    require(not handler.offload_to_cpu and not handler.offload_dit_to_cpu and not handler.quantization and not handler.use_mlx_dit and not handler.use_mlx_vae,'Unexpected offload/quantization/backend')
    require(getattr(handler.model.config,'_attn_implementation',None)=='sdpa','Unrequested attention fallback')
    return observed

def doctor():
    require(len(sys.argv)==6,'Usage: worker.py --doctor MODEL SOURCE CACHE RESULT.json')
    model,source,cache,result=[Path(x) for x in sys.argv[2:]]
    require(all(p.is_absolute() and p.is_dir() for p in (model,source,cache)) and result.is_absolute() and not result.exists(),'Explicit existing immutable doctor paths required')
    snapshot(model);attempts,dll_directories=offline(cache,model)
    torch,Handler,generate_music,Params,Config=backend(source,model)
    require(not attempts,'Network attempt invalidates import preflight')
    with result.open('x',encoding='utf-8') as stream:json.dump({'protocol':'bubo-music-doctor/v1','status':'pass','sourceRevision':SOURCE_REVISION,'torch':torch.__version__,'networkAttempts':0,'modelWeightsLoaded':False,'samplingPerformed':False,'planningLmLoaded':False,'cudaVisibility':os.environ.get('CUDA_VISIBLE_DEVICES','unspecified')},stream,indent=2);stream.write('\n')

def main():
    require(len(sys.argv)==3,'Usage: worker.py JOB.json RESULT.json')
    job=read(Path(sys.argv[1]));result_path=Path(sys.argv[2]);require(not result_path.exists(),'Immutable result already exists')
    require(set(job)=={'schemaVersion','operation','modelDirectory','sourceDirectory','outputDirectory','cacheDirectory','settings','items'},'Unknown worker fields')
    require(job['schemaVersion']=='bubo-music-worker/v1' and job['operation'] in ('probe','generate'),'Unsupported protocol/operation')
    require(job['settings']=={'device':'cuda:0','precision':'bfloat16','offload':'none'},'Unsupported settings; no fallback')
    model,source,output,cache=[Path(job[k]) for k in ('modelDirectory','sourceDirectory','outputDirectory','cacheDirectory')]
    require(all(p.is_absolute() and p.is_dir() for p in (model,source,output,cache)),'Explicit existing local paths required')
    roots=[p.resolve() for p in (model,source,output,cache)]
    require(all(a!=b and a not in b.parents for a in roots for b in roots if a is not b),'Worker roots overlap')
    items=job['items'];require(isinstance(items,list) and len(items)<=4 and bool(items)==(job['operation']=='generate'),'Bounded batch or zero-sample probe required')
    for item in items:validate_item(item)
    require(len({i['id'] for i in items})==len(items),'Duplicate item identity')
    snapshot(model);network_attempts,dll_directories=offline(cache,model)
    torch,Handler,generate_music,Params,Config=backend(source,model)
    import numpy as np
    import soundfile as sf
    require(torch.cuda.is_available() and torch.cuda.is_bf16_supported(),'Requested CUDA/bfloat16 unavailable')
    torch.cuda.set_device(0);torch.set_grad_enabled(False);torch.cuda.reset_peak_memory_stats(0)
    properties=torch.cuda.get_device_properties(0)
    torch.cuda.synchronize(0);started=time.perf_counter();handler=Handler()
    status,ok=handler.initialize_service(project_root=None,config_path='acestep-v15-turbo',device='cuda',use_flash_attention=False,compile_model=False,offload_to_cpu=False,offload_dit_to_cpu=False,quantization=None,prefer_source=None,use_mlx_dit=False,vae_checkpoint='official')
    require(ok,'Model initialization failed: '+status)
    handler.text_tokenizer=StrictTokenizer(handler.text_tokenizer)
    torch.cuda.synchronize(0);loading=time.perf_counter()-started;observed=placements(handler,torch)
    module_paths=set(loaded_modules());outputs=[]
    for index,item in enumerate(items):
        m=item['music'];path=output/f'{index:03d}.wav';metadata_path=output/f'{index:03d}.settings.json'
        require(not path.exists() and not metadata_path.exists(),'Immutable output already exists')
        params=Params(task_type='text2music',caption=item['prompt'],lyrics='[Instrumental]' if m['mode']=='instrumental' else m['lyrics'],instrumental=m['mode']=='instrumental',vocal_language=m['vocalLanguage'],bpm=m['bpm'],keyscale=m['keyScale'],timesignature=m['timeSignature'],duration=float(item['seconds']),seed=item['seed'],inference_steps=item['steps'],guidance_scale=1.,shift=float(m['shift']),infer_method='ode',sampler_mode='euler',thinking=False,use_cot_metas=False,use_cot_caption=False,use_cot_language=False,use_cot_lyrics=False,enable_normalization=False,fade_in_duration=0.,fade_out_duration=0.,latent_shift=0.,latent_rescale=1.,dcw_enabled=False)
        config=Config(batch_size=1,allow_lm_batch=False,use_random_seed=False,seeds=[item['seed']],audio_format='wav32')
        handler.text_tokenizer.observations.clear();torch.cuda.synchronize(0);started=time.perf_counter()
        with torch.inference_mode():produced=generate_music(handler,None,params,config,save_dir=None)
        torch.cuda.synchronize(0);inference=time.perf_counter()-started
        require(produced.success,'Generation failed: '+str(produced.error));require(len(produced.audios)==1,'Hidden extra candidates')
        extra=produced.extra_outputs;row=produced.audios[0]
        require(extra.get('lm_metadata') is None and str(extra.get('seed_value'))==str(item['seed']) and row['params']['seed']==item['seed'],'Actual LM/inner seed differs')
        require(row['sample_rate']==48000,'Unexpected native sample rate');tensor=row['tensor']
        require(tensor.device.type=='cpu' and tensor.dtype==torch.float32,'Unexpected returned tensor')
        audio=np.asarray(tensor.numpy(),dtype=np.float32).T
        require(audio.ndim==2 and audio.shape[1]==2 and abs(audio.shape[0]-round(item['seconds']*48000))<=1 and bool(np.isfinite(audio).all()),'Invalid native waveform')
        observed=placements(handler,torch);module_paths.update(loaded_modules())
        # FLOAT preserves excursions; no normalization, PCM clipping, fade or resample.
        with path.open('xb') as stream:sf.write(stream,audio,48000,format='WAV',subtype='FLOAT')
        conditioning=handler.text_tokenizer.observations
        require(len(conditioning)==2 and [x['maximumTokens'] for x in conditioning]==[256,2048],'Unexpected conditioning call count/limits')
        metadata={'protocol':'bubo-music-settings/v1','sourceRevision':SOURCE_REVISION,'mode':m['mode'],'planningLmLoaded':False,'lmMetadata':None,'innerSeed':int(extra['seed_value']),'params':params.to_dict(),'config':config.to_dict(),'normalization':False,'cpuDecode':False,'attention':'sdpa','conditioning':conditioning,'actualOutputCount':1,'sampleRate':48000,'componentPrecisions':observed,'humanReview':'pending','promotion':False}
        with metadata_path.open('x',encoding='utf-8') as stream:json.dump(metadata,stream,allow_nan=False,indent=2);stream.write('\n')
        outputs.append({'id':item['id'],'path':path.name,'metadata':metadata_path.name,'inferenceSeconds':inference,'seed':item['seed'],'steps':item['steps'],'guidance':1.,'scheduler':'ode/euler','loopReviewed':False})
        del produced,extra,row,tensor,audio,params,config,conditioning
    peak_memory=int(torch.cuda.max_memory_allocated(0));module_paths.update(loaded_modules())
    started=time.perf_counter();handler=None;gc.collect();torch.cuda.empty_cache();torch.cuda.synchronize(0);unloading=time.perf_counter()-started
    remaining=int(torch.cuda.memory_allocated(0));require(remaining==0,'Model tensors remain after unload');require(not network_attempts,'Network attempts invalidate execution')
    result={'protocol':'bubo-music-worker/v1','python':{'executable':sys.executable,'prefix':sys.prefix,'basePrefix':sys.base_prefix,'version':'.'.join(map(str,sys.version_info[:3]))},'loadedModules':sorted(module_paths|set(loaded_modules())),'execution':{'device':'cuda:0','deviceName':properties.name,'deviceUuid':str(getattr(properties,'uuid','unavailable')),'precision':'bfloat16','offload':'none','componentPrecisions':observed},'loadingSeconds':loading,'unloadingSeconds':unloading,'outputs':outputs,'modelLoads':1,'unloaded':True,'peakTorchAllocatedBytes':peak_memory,'endingTorchAllocatedBytes':remaining,'aggregateDriverMemoryMeasured':False,'networkAttempts':0,'planningLmLoaded':False,'sourceRevision':SOURCE_REVISION,'operation':job['operation'],'humanReview':'pending','promotion':False}
    with result_path.open('x',encoding='utf-8') as stream:json.dump(result,stream,allow_nan=False,indent=2);stream.write('\n')

if __name__=='__main__':
    try:
        if len(sys.argv)>1 and sys.argv[1]=='--doctor':doctor()
        else:main()
    except Exception as error:
        print('Bubo music worker failed: '+str(error),file=sys.stderr);raise SystemExit(1)
