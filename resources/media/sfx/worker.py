"""Fixed bounded offline Stable Audio Open worker; never installs or promotes."""
from pathlib import Path
import gc,json,math,os,socket,sys,time

def unique(pairs):
    result={}
    for key,value in pairs:
        if key in result:raise ValueError('Duplicate worker JSON property')
        result[key]=value
    return result

def require(value,message):
    if not value:raise ValueError(message)

def read(path):
    require(path.is_file() and path.stat().st_size<=1048576,'Missing/unbounded worker JSON')
    return json.loads(path.read_text(encoding='utf-8'),object_pairs_hook=unique,parse_constant=lambda _:(_ for _ in ()).throw(ValueError('Nonfinite JSON')))

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
        require(0<length<len(buffer),'Cannot identify loaded runtime module');paths.append(buffer.value)
    return paths

def snapshot(model):
    model_index=read(model/'model_index.json')
    expected={'_class_name':'StableAudioPipeline','projection_model':['stable_audio','StableAudioProjectionModel'],'scheduler':['diffusers','CosineDPMSolverMultistepScheduler'],'text_encoder':['transformers','T5EncoderModel'],'tokenizer':['transformers','T5TokenizerFast'],'transformer':['diffusers','StableAudioDiTModel'],'vae':['diffusers','AutoencoderOobleck']}
    require(all(model_index.get(key)==value for key,value in expected.items()),'Snapshot component classes differ from the fixed backend')

def offline(cache):
    cache.mkdir(parents=True,exist_ok=True)
    os.environ.update(HF_HUB_OFFLINE='1',TRANSFORMERS_OFFLINE='1',HF_HUB_DISABLE_TELEMETRY='1',HF_HUB_DISABLE_IMPLICIT_TOKEN='1',TOKENIZERS_PARALLELISM='false',HF_HOME=str(cache/'hf'),TORCH_HOME=str(cache/'torch'),XDG_CACHE_HOME=str(cache/'xdg'))
    os.environ.pop('HF_TOKEN',None);os.environ.pop('HUGGING_FACE_HUB_TOKEN',None)
    attempts=[]
    def no_network(*args,**kwargs):
        attempts.append(True);raise RuntimeError('Offline worker attempted network access')
    socket.socket.connect=no_network;socket.socket.connect_ex=no_network;socket.create_connection=no_network
    socket.getaddrinfo=no_network;socket.gethostbyname=no_network;socket.gethostbyname_ex=no_network;socket.gethostbyaddr=no_network
    require(sys.version_info[:3]==(3,12,16),'Dedicated pinned CPython3.12.16 environment required')
    directories=[os.add_dll_directory(str(Path(sys.prefix)/'Scripts')),os.add_dll_directory(sys.base_prefix)] if os.name=='nt' else []
    return attempts,directories

def doctor():
    require(len(sys.argv)==5,'Usage: worker.py --doctor MODEL_DIRECTORY CACHE_DIRECTORY RESULT.json')
    model=Path(sys.argv[2]);cache=Path(sys.argv[3]);result=Path(sys.argv[4])
    require(model.is_absolute() and cache.is_absolute() and result.is_absolute() and not result.exists(),'Explicit immutable doctor paths required')
    snapshot(model);attempts,dll_directories=offline(cache)
    import torch,diffusers,transformers,torchsde
    from diffusers import StableAudioPipeline,StableAudioDiTModel,AutoencoderOobleck,CosineDPMSolverMultistepScheduler
    from diffusers.pipelines.stable_audio import StableAudioProjectionModel
    from diffusers.utils import requires_backends
    from transformers import T5EncoderModel,T5TokenizerFast
    for component in [StableAudioPipeline,StableAudioDiTModel,AutoencoderOobleck,CosineDPMSolverMultistepScheduler,StableAudioProjectionModel,T5EncoderModel,T5TokenizerFast]:
        requires_backends(component,getattr(component,'_backends',[]))
    scheduler=CosineDPMSolverMultistepScheduler.from_pretrained(str(model/'scheduler'),local_files_only=True)
    tokenizer=T5TokenizerFast.from_pretrained(str(model/'tokenizer'),local_files_only=True,trust_remote_code=False)
    require(torch.__version__=='2.15.0+cu132' and diffusers.__version__=='0.42.1' and transformers.__version__=='5.21.0' and torchsde.__version__=='0.2.6','Environment differs from pinned execution dependencies')
    require(not attempts,'Network attempt invalidates metadata preflight')
    with result.open('x',encoding='utf-8') as output:json.dump({'protocol':'bubo-sfx-doctor/v1','status':'pass','scheduler':type(scheduler).__name__,'tokenizer':type(tokenizer).__name__,'torch':torch.__version__,'diffusers':diffusers.__version__,'transformers':transformers.__version__,'torchsde':torchsde.__version__,'networkAttempts':0,'modelWeightsLoaded':False,'samplingPerformed':False,'cudaVisibility':os.environ.get('CUDA_VISIBLE_DEVICES','unspecified')},output,indent=2);output.write('\n')

def main():
    require(len(sys.argv)==3,'Usage: worker.py JOB.json RESULT.json')
    job=read(Path(sys.argv[1]));result_path=Path(sys.argv[2])
    require(not result_path.exists(),'Immutable worker result already exists')
    require(set(job)=={'schemaVersion','operation','modelDirectory','outputDirectory','cacheDirectory','settings','items'},'Unknown/missing worker fields')
    require(job['schemaVersion']=='bubo-sfx-worker/v1' and job['operation'] in ('probe','generate'),'Unsupported worker protocol/operation')
    settings=job['settings'];require(set(settings)=={'device','precision','offload'},'Unknown execution setting')
    require(settings['device']=='cuda:0' and settings['precision'] in ('float16','float32') and settings['offload']=='none','Unsupported setting; no fallback')
    model=Path(job['modelDirectory']);output=Path(job['outputDirectory']);cache=Path(job['cacheDirectory'])
    require(all(p.is_absolute() for p in (model,output,cache)) and model.is_dir() and output.is_dir(),'Explicit local model/staging/cache paths required')
    require(model.resolve()!=output.resolve() and model.resolve() not in output.resolve().parents and output.resolve() not in model.resolve().parents,'Output/model overlap')
    items=job['items'];require(isinstance(items,list) and len(items)<=4,'Batch exceeds4items')
    require(bool(items)==(job['operation']=='generate'),'Probe must have zero items; generation requires a finite nonempty batch')
    ids=set()
    for item in items:
        require(set(item)=={'id','prompt','negativePrompt','seed','steps','guidance','seconds','loopCandidate'},'Unknown SFX item setting')
        require(isinstance(item['id'],str) and item['id'] not in ids and len(item['id'])<=80,'Duplicate/invalid item identity');ids.add(item['id'])
        require(isinstance(item['prompt'],str) and 0<len(item['prompt'])<=4096 and isinstance(item['negativePrompt'],str) and len(item['negativePrompt'])<=4096,'Explicit bounded prompt required')
        require(type(item['seed']) is int and 0<=item['seed']<=9223372036854775807,'Explicit nonnegative int64 seed required')
        require(type(item['steps']) is int and 1<=item['steps']<=200 and math.isfinite(item['guidance']) and 1<=item['guidance']<=12,'Unsupported inference setting')
        require(item['guidance']>1 or not item['negativePrompt'],'Negative prompts require classifier-free guidance greater than one')
        require(math.isfinite(item['seconds']) and .25<=item['seconds']<=47 and type(item['loopCandidate']) is bool,'Unsupported duration/loop intent')
    snapshot(model);network_attempts,dll_directories=offline(cache)
    import numpy as np
    import torch
    import diffusers
    import soundfile as sf
    from diffusers import StableAudioPipeline
    require(torch.__version__=='2.15.0+cu132' and diffusers.__version__=='0.42.1','Environment differs from pinned backend versions')
    require(torch.cuda.is_available(),'Requested CUDA unavailable; no CPU fallback')
    torch.cuda.set_device(0);torch.set_grad_enabled(False)
    properties=torch.cuda.get_device_properties(0);dtype=torch.float16 if settings['precision']=='float16' else torch.float32
    torch.cuda.reset_peak_memory_stats(0)
    loading_start=time.perf_counter()
    pipeline=StableAudioPipeline.from_pretrained(str(model),local_files_only=True,use_safetensors=True,trust_remote_code=False,torch_dtype=dtype)
    pipeline.to('cuda:0');torch.cuda.synchronize(0);loading=time.perf_counter()-loading_start
    module_paths=set(loaded_modules())
    components=[(name,getattr(pipeline,name)) for name in ('projection_model','text_encoder','transformer','vae')]
    observed=[name+':'+str(module.dtype).removeprefix('torch.') for name,module in components]
    require(all(module.dtype==dtype and next(module.parameters()).device.type=='cuda' for _,module in components),'Observed module dtype/device differs; no silent offload')
    sample_rate=int(pipeline.vae.sampling_rate);require(sample_rate==44100,'Unexpected model sample rate')
    outputs=[]
    for index,item in enumerate(items):
        path=output/f'{index:03d}.wav';require(not path.exists(),'Immutable waveform already exists')
        generator=torch.Generator(device='cuda:0').manual_seed(item['seed'])
        torch.cuda.synchronize(0);started=time.perf_counter()
        with torch.inference_mode():
            produced=pipeline(prompt=item['prompt'],negative_prompt=item['negativePrompt'],audio_start_in_s=0.,audio_end_in_s=float(item['seconds']),num_inference_steps=item['steps'],guidance_scale=float(item['guidance']),num_waveforms_per_prompt=1,generator=generator,output_type='np',return_dict=True)
        torch.cuda.synchronize(0);inference=time.perf_counter()-started
        module_paths.update(loaded_modules())
        require(produced.audios.shape[0]==1,'Backend produced hidden extra candidates')
        audio=np.asarray(produced.audios[0],dtype=np.float32).T
        require(audio.ndim==2 and audio.shape[1]==2 and abs(audio.shape[0]-round(item['seconds']*sample_rate))<=1 and bool(np.isfinite(audio).all()),'Invalid actual waveform')
        peak=float(np.max(np.abs(audio)));rms=float(np.sqrt(np.mean(np.square(audio,dtype=np.float64))))
        clipped=float(np.mean(np.abs(audio)>=1));silent=float(np.mean(np.abs(audio)<1e-5))
        # Preserve original amplitude. Never normalize away clipping or silence evidence.
        with path.open('xb') as stream:sf.write(stream,audio,sample_rate,format='WAV',subtype='FLOAT')
        outputs.append({'id':item['id'],'path':path.name,'inferenceSeconds':inference,'width':None,'height':None,'sampleRate':sample_rate,'channels':2,'frames':int(audio.shape[0]),'peak':peak,'rms':rms,'clippedFraction':clipped,'silentFraction':silent,'seed':int(generator.initial_seed()),'steps':item['steps'],'guidance':item['guidance'],'scheduler':type(pipeline.scheduler).__name__,'loopCandidate':item['loopCandidate'],'loopReviewed':False})
        del produced,audio,generator
    peak_memory=int(torch.cuda.max_memory_allocated(0))
    unloading_start=time.perf_counter();components.clear();pipeline=None;gc.collect();torch.cuda.empty_cache();torch.cuda.synchronize(0);unloading=time.perf_counter()-unloading_start
    remaining_memory=int(torch.cuda.memory_allocated(0));require(remaining_memory==0,'Model tensors remain allocated after batch unload')
    require(not network_attempts,'Network attempts invalidate offline execution')
    modules=sorted(module_paths|set(loaded_modules()))
    result={'protocol':'bubo-sfx-worker/v1','python':{'executable':sys.executable,'prefix':sys.prefix,'basePrefix':sys.base_prefix,'version':'.'.join(map(str,sys.version_info[:3]))},'loadedModules':modules,'execution':{'device':'cuda:0','deviceName':properties.name,'deviceUuid':str(getattr(properties,'uuid','unavailable')),'precision':settings['precision'],'offload':'none','componentPrecisions':observed},'loadingSeconds':loading,'unloadingSeconds':unloading,'outputs':outputs,'modelLoads':1,'unloaded':True,'peakTorchAllocatedBytes':peak_memory,'endingTorchAllocatedBytes':remaining_memory,'aggregateDriverMemoryMeasured':False,'networkAttempts':0,'operation':job['operation'],'humanReview':'pending','promotion':False}
    with result_path.open('x',encoding='utf-8') as stream:json.dump(result,stream,allow_nan=False,indent=2);stream.write('\n')

if __name__=='__main__':
    try:
        if len(sys.argv)>1 and sys.argv[1]=='--doctor':doctor()
        else:main()
    except Exception as error:
        print('Bubo SFX worker failed: '+str(error),file=sys.stderr);raise SystemExit(1)
