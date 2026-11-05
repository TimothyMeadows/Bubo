"""Actual owned Blender v2 motion/anchor export checks; no engine FX or human acceptance."""
import copy
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import struct
import subprocess
import sys
import tempfile
import unittest

sys.dont_write_bytecode=True
ROOT=Path(__file__).resolve().parents[1]
SCRIPTS=ROOT/'resources/model-to-action/blender'
BLENDER=os.environ.get('BLENDER_EXE') or shutil.which('blender') or 'C:/Program Files/Blender Foundation/Blender 4.5/blender.exe'
EVIDENCE=Path(os.environ.get('HEPHAERA_ACTION_V2_TEST_EVIDENCE_ROOT',ROOT/'.bubo/action-v2-native')).resolve()
EVIDENCE.relative_to(ROOT)


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def json_file(path,value):
    path.parent.mkdir(parents=True,exist_ok=True)
    path.write_text(json.dumps(value,indent=2)+'\n',encoding='utf-8')


def effect_cue(identifier,anchor,start,end,tail,projectile=False):
    layers=[{'id':'body','family':'projectile' if projectile else 'sprite','space':'world',
             'start':start,'stopAt':end,'retireAt':end+tail,'emissionDurationSeconds':end-start,
             'particleLifetimeSeconds':min(60,end-start),'tailSeconds':tail}]
    value={'id':identifier,'type':'effect','source':'synthetic-compiled-fx','anchor':anchor,'start':start,'end':end,
           'placement':'fixed-target-projectile' if projectile else 'anchor-follow',
           'transform':{'units':'meters','up':'+Y','forward':'+Z','handedness':'right','position':[.02,0,0],
                        'orientation':[0,0,0,1],'scale':1,'parentScale':'positive-uniform'},
           'lifetime':{'tailSeconds':tail,'pause':'follow-action','tailAttachment':'capture-world-on-stop' if projectile else 'follow-anchor','cancel':'retire-generation'},
           'retireAt':end+tail,'layers':layers}
    if projectile:
        value['target']='fixed-target';value['trail']={'reset':'teleport-or-discontinuity','maxSamples':8,'distanceMeters':.5}
        value['layers'].append({**layers[0],'id':'trail','family':'trail'})
    return value


def v2_plan(original,motion,rigid=False):
    value=copy.deepcopy(original);value['schemaVersion']='hephaera-action-package/v2';value['version']='v2-rigid' if rigid else 'v2'
    value['coordinateSystem'].update(units='meters',handedness='right',parentScale='positive-uniform')
    value['budgets'].update(maxEffectSources=1,maxEffectFiles=64,maxEffectDependencyBytes=16777216,maxEffectJsonBytes=1048576,maxTailSeconds=.2,maxTrailSamples=8)
    value['effectSources']=[{'id':'synthetic-compiled-fx','effectId':'fixture-effect',
        'package':{'path':'synthetic-metadata/effect.json','sha256':'a'*64},
        'profile':{'schemaVersion':'hephaera-three-vfx-profile/v1','path':'synthetic-metadata/profile.json','sha256':'b'*64},
        'resources':[],'correspondences':[],
        'ancestry':{'sourceClosureSha256':'c'*64,'productionReviewSha256':'d'*64,'rightsSha256':'e'*64}}]
    # These hashes are explicitly synthetic compiled metadata. This native suite
    # measures GLB motion/anchors; the C# VFX suites own real dependency admission.
    if rigid:
        action=copy.deepcopy(value['actions'][0]);step=copy.deepcopy(action['steps'][0]);duration=min(1.,step['sourceEnd']-step['sourceStart'])
        step.update(start=0.,end=duration,sourceEnd=step['sourceStart']+duration,blendIn=0.,rate=1,occurrence=0)
        action.update(duration=duration,steps=[step],markers=[marker for marker in action['markers'] if marker['at']<=duration])
        action['cues']=[effect_cue('rigid-release',motion['anchors'][0]['id'],.25,duration,.2,True)]
    else:
        action=copy.deepcopy(next(item for item in value['actions'] if item['id']=='charge-cast'))
        release=next(cue['start'] for cue in action['cues'] if cue['type']=='projectile')
        action['cues']=[effect_cue('charge',motion['anchors'][0]['id'],0.,release,0.),
                        effect_cue('release',motion['anchors'][0]['id'],release,action['duration'],.2,True)]
    action['retireAt']=action['duration']+.2;value['actions']=[action]
    value['targets']=[{'id':'fixed-target','mode':'fixed-world-point','maxDistance':100}]
    return value


class NativeV2(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not Path(BLENDER).is_file():raise unittest.SkipTest('BLOCKED: existing Blender4.5 unavailable; no installation')
        EVIDENCE.mkdir(parents=True,exist_ok=True);cls.work=Path(tempfile.mkdtemp(prefix='run-',dir=EVIDENCE));cls.project=cls.work/'p';cls.calls=[]
        cls.run_blender(SCRIPTS.parents[2]/'tests/fixtures/model-action/create_assets.py',['--output',str(cls.project)],'owned-source')
        cls.motion=json.loads((cls.project/'source/motion-package.json').read_text());base=json.loads((cls.project/'compiled-plan.json').read_text())
        cls.plan=v2_plan(base,cls.motion);cls.review={'fps':4,'resolution':[64,64],'cameras':['front'],'targets':{'fixed-target':[2,1.3,2]}}
        cls.records={}
        cls.records['inspect']=cls.stage('inspect','source/asset.glb')
        cls.records['compose']=cls.stage('compose','source/asset.glb')
        cls.records['export']=cls.stage('export',cls.records['compose']['artifacts']['blend'])
        cls.records['verify']=cls.verify(cls.records['export'])
        cls.records['review']=cls.stage('review',cls.records['export']['artifacts']['glb'])
        json_file(cls.work/'fixture-results.json',cls.records)
        (EVIDENCE/'latest.txt').write_text(str(cls.work)+'\n',encoding='utf-8')

    @classmethod
    def run_blender(cls,script,extra,name,success=True,timeout=180):
        args=[BLENDER,'--factory-startup','--background','--disable-autoexec','--python-exit-code','1','--python',str(script),'--',*extra]
        process=subprocess.Popen(args,stdout=subprocess.PIPE,stderr=subprocess.STDOUT)
        try:output,_=process.communicate(timeout=timeout)
        except subprocess.TimeoutExpired:
            process.kill();output,_=process.communicate();(cls.work/(name+'.interrupted.log')).write_bytes(output)
            cls.calls.append({'args':args,'exitCode':process.returncode,'interrupted':True});json_file(cls.work/'commands.json',cls.calls)
            raise
        log=cls.work/(name+'.log');log.write_bytes(output);cls.calls.append({'args':args,'exitCode':process.returncode,'log':str(log.relative_to(ROOT))})
        json_file(cls.work/'commands.json',cls.calls)
        if (process.returncode==0)!=success:raise AssertionError(log.read_text(errors='replace')[-6500:])
        return output

    @classmethod
    def stage(cls,operation,source,plan=None,motion=None,success=True,verification=None,output=None,change=None):
        selected=plan or cls.plan;motion=motion or cls.motion;identifier=operation+'-'+str(len(cls.calls))
        folder=output or '.art-staging/model-to-action/sequence-fixture/'+selected['version']+'/attempts/'+identifier
        job={'schemaVersion':1,'root':str(cls.project),'operation':operation,'input':source,'outputDir':folder,'result':folder+'/result.json',
             'asset':'sequence-fixture','version':selected['version'],'profile':motion['profile'],'motionPackage':motion,'plan':selected,'review':cls.review}
        if verification:job['verification']=verification
        if change:job.update(change)
        path=cls.project/('job-'+identifier+'.json');json_file(path,job)
        raw=cls.run_blender(SCRIPTS/'action.py',['--job',str(path)],identifier,success)
        result=cls.project/job['result']
        if not success:
            if result.exists():raise AssertionError('Rejected native job emitted a passing-looking result')
            return raw
        report=json.loads(result.read_text())
        for relative in report['artifacts'].values():
            if not (cls.project/relative).is_file():raise AssertionError('Missing actual native artifact: '+relative)
        return report

    @classmethod
    def verify(cls,exported,plan=None,motion=None,success=True,proof_sha=None):
        glb=exported['artifacts']['glb'];proof=exported['artifacts']['exportProof']
        return cls.stage('verify-export',glb,plan,motion,success,
                         {'path':proof,'sha256':proof_sha or sha(cls.project/proof)},
                         str(Path(glb).parent/'independent').replace('\\','/'))

    def clone_export(self,name):
        original=self.records['export'];old=Path(original['artifacts']['glb']).parent
        new=Path('.art-staging/model-to-action/sequence-fixture/v2/attempts/negative-'+name)
        target=self.project/new;target.mkdir(parents=True)
        shutil.copy2(self.project/old/'asset.glb',target/'asset.glb')
        shutil.copytree(self.project/old/'verification',target/'verification')
        proof_path=target/'verification/export-proof.json';proof=json.loads(proof_path.read_text());proof['model']['path']=(new/'asset.glb').as_posix()
        for filename,item in proof['spool'].items():item['path']=(new/'verification'/filename).as_posix()
        json_file(proof_path,proof)
        return {'artifacts':{'glb':(new/'asset.glb').as_posix(),'exportProof':(new/'verification/export-proof.json').as_posix()}}

    def test_01_actual_skinned_and_fresh_process_export(self):
        measured=self.records['verify']['metrics'];self.assertTrue(measured['freshProcess'] and measured['actualGlb'])
        self.assertGreater(measured['allBakedFramesCompared'],24);self.assertLessEqual(measured['maximumAnchorMatrixError'],.001)
        self.assertTrue(measured['completeSpoolChecksumsVerified'] and measured['materialBindingsVerified'])
        package=json.loads((self.project/self.records['export']['artifacts']['package']).read_text())
        self.assertEqual(package['schemaVersion'],'hephaera-action-package/v2');self.assertGreater(package['actions'][0]['retireAt'],package['actions'][0]['duration'])
        self.assertTrue(all(cue['type']=='effect' for cue in package['actions'][0]['cues']))

    def test_02_complete_motion_anchor_review_without_vfx_proxies(self):
        report=json.loads((self.project/self.records['review']['artifacts']['reviewReport']).read_text())
        self.assertEqual(report['cueRendering'],'motion-and-anchors-only');self.assertEqual(report['actualEngineFX'],'not-rendered')
        frames=report['packets'][0]['frames'];times={round(frame['time'],9) for frame in frames};action=self.plan['actions'][0]
        self.assertIn(round(action['retireAt'],9),times)
        for cue in action['cues']:
            for layer in cue['layers']:
                for key in ('start','stopAt','retireAt'):self.assertIn(round(layer[key],9),times)
        self.assertTrue(all(not frame['cueProxies'] and frame['engineFXRendered'] is False for frame in frames))
        last=frames[-1]['anchorMatricesBlender'];at_end=next(frame['anchorMatricesBlender'] for frame in frames if abs(frame['time']-action['duration'])<1e-7)
        self.assertEqual(last,at_end)

    def test_03_actual_rigid_launcher_has_no_fake_skin(self):
        entry=json.loads((self.project/'fixtures.json').read_text())['mechanical']['ship']
        motion=json.loads((self.project/entry['motionPackage']).read_text());original=json.loads((self.project/entry['compiledPlan']).read_text())
        plan=v2_plan(original,motion,True);composed=self.stage('compose',entry['sourceModel'],plan,motion)
        exported=self.stage('export',composed['artifacts']['blend'],plan,motion);verified=self.verify(exported,plan,motion)
        self.assertEqual(exported['metrics']['independentReimport']['skinCount'],0);self.assertTrue(verified['metrics']['freshProcess'])
        self.assertEqual(verified['metrics']['after']['bones'],0)

    def test_04_proof_model_and_spool_mutations_rejected(self):
        for name,relative in [('proof','verification/export-proof.json'),('model','asset.glb'),('spool','verification/poses.bin')]:
            with self.subTest(name=name):
                exported=self.clone_export(name);root=self.project/Path(exported['artifacts']['glb']).parent
                proof_sha=sha(root/'verification/export-proof.json');path=root/relative;raw=bytearray(path.read_bytes());raw[-1]^=1;path.write_bytes(raw)
                output=self.verify(exported,success=False,proof_sha=proof_sha)
                self.assertTrue(b'hash' in output.lower() or b'glb' in output.lower() or b'checksum' in output.lower())

    def test_05_current_plan_and_proof_path_drift_rejected(self):
        exported=self.clone_export('plan-drift');plan=copy.deepcopy(self.plan);plan['actions'][0]['cues'][0]['transform']['position'][0]+=.01
        output=self.verify(exported,plan=plan,success=False);self.assertIn(b'current plan',output)
        output=self.stage('verify-export',self.records['export']['artifacts']['glb'],success=False,
                          verification={'path':'../outside.json','sha256':'a'*64})
        self.assertIn(b'relative path',output)

    def changed_scale_source(self,name,scales,times,parent=False,compensate=False):
        raw=(self.project/'source/asset.glb').read_bytes();length=struct.unpack_from('<I',raw,12)[0]
        document=json.loads(raw[20:20+length]);binary=bytearray(raw[20+length+8:][:document['buffers'][0]['byteLength']])
        anchor=next(index for index,node in enumerate(document['nodes']) if node.get('name')==self.motion['anchors'][0]['node'])
        selected=next(index for index,node in enumerate(document['nodes']) if anchor in node.get('children',[])) if parent else anchor
        first_clip=self.plan['actions'][0]['steps'][0]['clip'];clip=next(clip for clip in self.motion['clips'] if clip['id']==first_clip)
        animation=next(item for item in document['animations'] if item['name']==clip['action'])
        def accessor(values,kind):
            flat=[value for row in values for value in (row if isinstance(row,list) else [row])]
            binary.extend(b'\0'*((-len(binary))%4));begin=len(binary);binary.extend(struct.pack('<'+'f'*len(flat),*flat))
            view=len(document['bufferViews']);document['bufferViews'].append({'buffer':0,'byteOffset':begin,'byteLength':len(flat)*4})
            index=len(document['accessors']);document['accessors'].append({'bufferView':view,'componentType':5126,'count':len(values),'type':kind})
            return index
        animation['channels']=[channel for channel in animation['channels'] if channel['target']!={'node':selected,'path':'scale'}]
        sampler=len(animation['samplers']);animation['samplers'].append({'input':accessor(times,'SCALAR'),'output':accessor(scales,'VEC3'),'interpolation':'LINEAR'})
        animation['channels'].append({'sampler':sampler,'target':{'node':selected,'path':'scale'}})
        if compensate:
            animation['channels']=[channel for channel in animation['channels'] if channel['target']!={'node':anchor,'path':'scale'}]
            sampler=len(animation['samplers']);animation['samplers'].append({'input':accessor([0.,1.],'SCALAR'),'output':accessor([[-1,1,1],[-1,1,1]],'VEC3'),'interpolation':'LINEAR'})
            animation['channels'].append({'sampler':sampler,'target':{'node':anchor,'path':'scale'}})
        document['buffers'][0]['byteLength']=len(binary);binary.extend(b'\0'*((-len(binary))%4));encoded=json.dumps(document,separators=(',',':')).encode();encoded+=b' '*((-len(encoded))%4)
        value=struct.pack('<III',0x46546c67,2,12+8+len(encoded)+8+len(binary))+struct.pack('<II',len(encoded),0x4e4f534a)+encoded+struct.pack('<II',len(binary),0x004e4942)+binary
        path='source/'+name+'.glb';(self.project/path).write_bytes(value);return path

    def test_06_actual_mirrored_and_between_frame_nonuniform_anchor_rejected(self):
        for name,scales,times,parent,compensate in [('mirrored',[[-1,1,1],[-1,1,1]],[0.,1.],False,False),
                                 ('between-frame',[[1,1,1],[1.5,1,1],[1,1,1],[1,1,1]],[0.,1/96,1/48,1.],False,False),
                                 ('parent-mirror-compensated',[[-1,1,1],[-1,1,1]],[0.,1.],True,True),
                                 ('parent-between-frame',[[1,1,1],[1.5,1,1],[1,1,1],[1,1,1]],[0.,1/96,1/48,1.],True,False)]:
            with self.subTest(name=name):
                source=self.changed_scale_source(name,scales,times,parent,compensate);output=self.stage('inspect',source,success=False)
                self.assertTrue(b'mirrored' in output.lower() or b'uniform' in output.lower())

    def test_07_interruption_has_no_completed_result(self):
        folder='.art-staging/model-to-action/sequence-fixture/v2/attempts/interrupted-owned-check'
        job={'schemaVersion':1,'root':str(self.project),'operation':'inspect','input':'source/asset.glb','outputDir':folder,
             'result':folder+'/result.json','asset':'sequence-fixture','version':'v2','profile':self.motion['profile'],
             'motionPackage':self.motion,'plan':self.plan,'review':self.review}
        path=self.project/'interrupted-job.json';json_file(path,job)
        with self.assertRaises(subprocess.TimeoutExpired):self.run_blender(SCRIPTS/'action.py',['--job',str(path)],'interrupted-owned-check',timeout=.05)
        self.assertFalse((self.project/job['result']).exists())


if __name__=='__main__':
    print('Actual original GLB motion/export/anchor checks; effect dependency metadata is synthetic; engine FX and human acceptance are not established.')
    unittest.main(verbosity=2)
