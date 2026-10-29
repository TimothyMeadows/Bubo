"""Adversarial fixed-profile checks. No weights, GPU, network or generation."""
from pathlib import Path
from contextlib import nullcontext,ExitStack
from types import SimpleNamespace
from unittest.mock import patch
import copy,importlib.util,json,os,socket,sys,tempfile,zipfile

ROOT=Path(__file__).resolve().parents[1];sys.dont_write_bytecode=True
spec=importlib.util.spec_from_file_location('music_worker',ROOT/'resources/media/music/worker.py');worker=importlib.util.module_from_spec(spec);spec.loader.exec_module(worker)
checks=[]
def check(value,name):
    assert value,name;checks.append(name);print('PASS:',name,flush=True)
def denied(action,name):
    try:action()
    except (ValueError,RuntimeError):check(True,name);return
    raise AssertionError('Unexpected admission: '+name)
item={'id':'owned','prompt':'Owned fixture caption','seed':3,'steps':8,'guidance':1,'seconds':20,'loopCandidate':False,'music':{'mode':'instrumental','lyrics':'','vocalLanguage':'unknown','bpm':100,'keyScale':'C major','timeSignature':'4','shift':3}}
worker.validate_item(item);check(True,'fixed instrumental profile admits explicit no-vocal marker inputs')
song=copy.deepcopy(item);song['music'].update(mode='supplied-lyrics',lyrics='[Verse]\nOwned fixture words',vocalLanguage='en');worker.validate_item(song)
check(True,'fixed song profile admits explicit supplied words')
for key,value,name in [('seed',9007199254740993,'unsafe inner seed rounding'),('seconds',20.1,'hidden duration rounding'),('guidance',7,'ineffective Turbo guidance')]:
    altered=copy.deepcopy(item);altered[key]=value;denied(lambda:worker.validate_item(altered),name)
for key,value,name in [('lyrics','unrequested words','instrumental cannot ignore lyrics'),('mode','automatic','music mode cannot select automatic planning'),('vocalLanguage','en','instrumental cannot imply vocal language')]:
    altered=copy.deepcopy(item);altered['music'][key]=value;denied(lambda:worker.validate_item(altered),name)
altered=copy.deepcopy(song);altered['music']['lyrics']=' ';denied(lambda:worker.validate_item(altered),'singing cannot invent lyrics')
altered=copy.deepcopy(song);altered['music']['thinking']=True;denied(lambda:worker.validate_item(altered),'unknown planning switch rejected')
denied(lambda:worker.StrictDecodeMixin()._decode_on_cpu(None),'direct CPU decode fallback fails')
denied(lambda:worker.StrictDecodeMixin()._tiled_decode_cpu_fallback(None),'tiled CPU decode fallback fails')

class Tokenizer:
    pad_token_id=0
    def __init__(self):self.calls=[]
    def __call__(self,text,**kwargs):
        self.calls.append(kwargs);return {'input_ids':list(range(len(text)))}
tokenizer=Tokenizer();strict=worker.StrictTokenizer(tokenizer)
strict('a'*256,truncation=True,max_length=256,padding='longest',return_tensors='pt')
check(len(strict.observations)==1 and strict.observations[0]['tokenCount']==256 and not tokenizer.calls[-1]['truncation'],'SIMULATED tokenizer retains all formatted tokens at exact caption limit')
denied(lambda:strict('a'*257,truncation=True,max_length=256),'SIMULATED caption token overflow fails instead of silently truncating')
denied(lambda:strict('a'*2049,truncation=True,max_length=2048),'SIMULATED lyric token overflow fails instead of silently truncating')
check(strict.pad_token_id==0,'SIMULATED tokenizer preserves upstream padding identity')

events=[];allocation={'allocated':8519680,'reserved':20971520}
def clear_workspace():events.append('clear');allocation['allocated']=0
def empty_cache():events.append('empty');allocation['reserved']=0
cleanup_torch=SimpleNamespace(_C=SimpleNamespace(_cuda_clearCublasWorkspaces=clear_workspace),cuda=SimpleNamespace(synchronize=lambda _:events.append('sync'),memory_allocated=lambda _:allocation['allocated'],memory_reserved=lambda _:allocation['reserved'],empty_cache=empty_cache))
cleanup=worker.release_workspace_cache(cleanup_torch)
check(cleanup['allocatedBeforeWorkspaceClearBytes']==8519680 and cleanup['endingAllocatedBytes']==0 and cleanup['endingReservedBytes']==0,'SIMULATED cleanup reports actual before/after allocation observations')
check(events==['sync','clear','empty','sync'],'SIMULATED workspace clear waits for GPU completion before releasing only library ownership')
denied(lambda:worker.release_workspace_cache(SimpleNamespace(cuda=cleanup_torch.cuda,_C=SimpleNamespace())),'missing pinned cleanup API fails explicitly')

class Tensor:
    def __init__(self,amplitude=1.25):self.amplitude=amplitude;self.moves=[]
    def detach(self):return self
    def cpu(self):self.moves.append('cpu-buffer');return self
    def transpose(self,*args):return self
    def contiguous(self):return self
    def to(self,**kwargs):self.moves.append(kwargs);return self
    def float(self):return self
class Handler(worker.StrictDecodeMixin):
    def __init__(self,device='cuda'):
        self.wav=Tensor();self.use_mlx_vae=False;self.current_offload_cost=0;self.decode_calls=0
        self.vae=SimpleNamespace(parameters=lambda:iter([SimpleNamespace(device=SimpleNamespace(type=device))]),dtype='bfloat16',decode=lambda _:SimpleNamespace(sample=self.wav))
    def _empty_cache(self):pass
    def tiled_decode(self,latent):self.decode_calls+=1;return self.wav
fake_torch=SimpleNamespace(inference_mode=nullcontext,cuda=SimpleNamespace(synchronize=lambda _:None))
with patch.dict(sys.modules,torch=fake_torch):
    for tiled in [True,False]:
        handler=Handler();latent=Tensor();costs={'total_time_cost':1.}
        wav,retained,times=handler._decode_generate_music_pred_latents(latent,None,tiled,costs)
        check(wav.amplitude==1.25 and times['total_time_cost']>=1 and {'device':'cuda:0','dtype':'bfloat16'} in latent.moves,'SIMULATED strict decode preserves over-one amplitude and explicit GPU decode')
    denied(lambda:Handler('cpu')._decode_generate_music_pred_latents(Tensor(),None,True,{'total_time_cost':1.}),'SIMULATED decoder rejects CPU placement before decoding')

with tempfile.TemporaryDirectory(prefix='bubo-music-contract-') as temporary:
    root=Path(temporary);cache=root/'cache';cache.mkdir();model=root/'models';model.mkdir()
    with ExitStack() as stack:
        stack.enter_context(patch.dict(os.environ,{'ACESTEP_VAE_ON_CPU':'1','ACESTEP_DTYPE':'float32','HF_TOKEN':'OWNED_SENTINEL','SPACE_ID':'OWNED_SENTINEL'}))
        stack.enter_context(patch.object(worker.sys,'version_info',(3,12,16)))
        stack.enter_context(patch.object(worker.sys,'prefix',str(root)))
        stack.enter_context(patch.object(worker.sys,'base_prefix',str(root)))
        if os.name=='nt':stack.enter_context(patch.object(worker.os,'add_dll_directory',lambda _:nullcontext()))
        for target,name in [(socket.socket,'connect'),(socket.socket,'connect_ex'),(socket,'create_connection'),(socket,'getaddrinfo'),(socket,'gethostbyname'),(socket,'gethostbyname_ex'),(socket,'gethostbyaddr')]:stack.enter_context(patch.object(target,name))
        attempts,handles=worker.offline(cache,model)
        check(os.environ['ACESTEP_VAE_ON_CPU']=='0' and 'ACESTEP_DTYPE' not in os.environ and 'HF_TOKEN' not in os.environ and 'SPACE_ID' not in os.environ,'offline profile removes inherited precision/CPU/credential/hosted overrides')
        check(os.environ['HF_HUB_OFFLINE']=='1' and os.environ['TRANSFORMERS_OFFLINE']=='1' and os.environ['HF_MODULES_CACHE'].startswith(str(cache)),'offline module caches remain in owned mutable scope')
        denied(lambda:socket.getaddrinfo('example.invalid',443),'offline DNS blocked before lookup')
        check(len(attempts)==1,'network attempts recorded and invalidate worker success')
print(json.dumps({'status':'pass','checks':len(checks),'gpu':False,'modelLoading':False,'generation':False,'listeningReview':False}))
