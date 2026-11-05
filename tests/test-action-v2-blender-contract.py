"""Pure admission/spool checks. These do not render or authenticate production review."""
from array import array
import copy
import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.dont_write_bytecode=True
ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'resources/model-to-action/blender'))

from core import positive_uniform_matrix,strict_json,validate_v2_cue
from composition import plan_signature
from review import review_times
from verification import read_spool,write_spool


def matrix():
    return [1.,0.,0.,0.,0.,1.,0.,0.,0.,0.,1.,0.,0.,0.,0.,1.]


def cue():
    return {'id':'release','type':'effect','source':'spell','anchor':'hand','start':0.,'end':1.,
            'placement':'anchor-follow','transform':{'units':'meters','up':'+Y','forward':'+Z','handedness':'right',
                'position':[0,0,0],'orientation':[0,0,0,1],'scale':1,'parentScale':'positive-uniform'},
            'lifetime':{'tailSeconds':.2,'pause':'follow-action','tailAttachment':'follow-anchor','cancel':'retire-generation'},
            'retireAt':1.,'layers':[{'id':'charge','family':'sprite','space':'world','start':0.,'stopAt':.8,'retireAt':1.,
                'emissionDurationSeconds':.8,'particleLifetimeSeconds':.8,'tailSeconds':.2}]}


def plan():
    return {'schemaVersion':'hephaera-action-package/v2','fps':4,'rig':{'id':'rig','rootNode':'Root'},
            'anchors':[{'id':'hand','node':'anchor.hand'}],'targets':[],
            'budgets':{'maxVerificationBytes':65536,'maxTailSeconds':.2,'maxTrailSamples':8},
            'effectSources':[{'id':'spell','effectId':'cast','package':{'sha256':'a'*64}}],
            'coordinateSystem':{'runtimeUp':'+Y','runtimeForward':'+Z','units':'meters'},
            'actions':[{'id':'cast','role':'presentation','clip':'cast','duration':1.,'retireAt':1.,
                'steps':[{'start':0.,'end':1.}],'cues':[cue()],'markers':[{'at':.375}]}]}


class ContractTests(unittest.TestCase):
    def test_actual_matrix_contract(self):
        positive_uniform_matrix(matrix())
        rotated=[0.,-2.,0.,3.,2.,0.,0.,4.,0.,0.,2.,5.,0.,0.,0.,1.]
        positive_uniform_matrix(rotated)
        for name,index,value in [('mirror',0,-1.),('nonuniform',5,2.),('shear',1,.2),('zero',0,0.),
                                 ('nonfinite',3,float('inf')),('not-affine',12,.1)]:
            with self.subTest(name=name):
                invalid=matrix();invalid[index]=value
                with self.assertRaises(ValueError):positive_uniform_matrix(invalid)

    def test_real_v2_cue_admission_and_no_legacy_substitution(self):
        action={'duration':1.};budget={'maxTailSeconds':.2,'maxTrailSamples':8}
        validate_v2_cue(cue(),action,{'spell':{}},{'hand'},{},budget)
        changes=[lambda c:c.update(type='orb'),lambda c:c.update(source='missing'),
                 lambda c:c.update(anchor='missing'),lambda c:c.update(shader='arbitrary'),
                 lambda c:c['transform'].update(units='centimeters'),lambda c:c['transform'].update(scale=-1),
                 lambda c:c['transform'].update(orientation=[0,0,0,2]),
                 lambda c:c['lifetime'].update(cancel='keep-tail'),
                 lambda c:c['layers'][0].update(stopAt=.7),lambda c:c.update(retireAt=.9),
                 lambda c:c.update(overrides=[{'layer':'charge','sizeMultiplier':1.1}]),
                 lambda c:c.update(overrides=[{'layer':'missing','colorMultiplier':[1,1,1,1]}])]
        for index,change in enumerate(changes):
            with self.subTest(index=index):
                invalid=cue();change(invalid)
                with self.assertRaises((ValueError,TypeError)):validate_v2_cue(invalid,action,{'spell':{}},{'hand'},{},budget)

    def test_explicit_beam_and_trail_policies(self):
        action={'duration':1.};budget={'maxTailSeconds':.2,'maxTrailSamples':8}
        beam=cue();beam.update(placement='beam',target='point');beam['layers'][0]['family']='beam'
        validate_v2_cue(beam,action,{'spell':{}},{'hand'},{'point':{'mode':'continuous-world-point'}},budget)
        beam['placement']='fixed-target'
        with self.assertRaises(ValueError):validate_v2_cue(beam,action,{'spell':{}},{'hand'},{'point':{'mode':'continuous-world-point'}},budget)
        trail=cue();trail['layers'][0]['family']='trail'
        with self.assertRaises(ValueError):validate_v2_cue(trail,action,{'spell':{}},{'hand'},{},budget)
        trail['trail']={'reset':'teleport-or-discontinuity','maxSamples':8,'distanceMeters':.5}
        validate_v2_cue(trail,action,{'spell':{}},{'hand'},{},budget)

    def test_complete_review_boundaries_and_plan_binding(self):
        value=plan();action=value['actions'][0]
        self.assertIn(.8,review_times(action,4));self.assertIn(.375,review_times(action,4))
        action['retireAt']=1.2;action['cues'][0]['retireAt']=1.2;action['cues'][0]['layers'][0]['retireAt']=1.2
        self.assertEqual(review_times(action,4)[-1],1.2)
        original=plan_signature(value);value['effectSources'][0]['package']['sha256']='b'*64
        self.assertNotEqual(original,plan_signature(value))
        value=plan();original=plan_signature(value);value['coordinateSystem']['units']='centimeters'
        self.assertNotEqual(original,plan_signature(value))

    def test_v2_spool_matrix_checks_and_complete_roundtrip(self):
        directory=ROOT/'.bubo';directory.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(prefix='action-v2-contract-',dir=directory) as temp:
            root=Path(temp);value=plan()
            sample={'meshes':{'mesh':array('d',[0.,0.,0.,1.,0.,0.,0.,1.,0.])},'anchors':{'hand':array('d',matrix())}}
            samples=[('cast',index,copy.deepcopy(sample)) for index in range(5)]
            write_spool(root,root/'verified',value,plan_signature(value),samples)
            with read_spool(root,root/'verified',value,plan_signature(value)) as (_,frames):self.assertEqual(len(list(frames)),5)
            invalid=copy.deepcopy(samples);invalid[2][2]['anchors']['hand'][0]=-1
            with self.assertRaises(ValueError):write_spool(root,root/'rejected',value,plan_signature(value),invalid)

    def test_duplicate_or_nonfinite_json_rejected(self):
        directory=ROOT/'.bubo';directory.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(prefix='action-v2-json-',dir=directory) as temp:
            path=Path(temp)/'proof.json'
            for text in ['{"id":1,"id":2}','{"id":NaN}']:
                path.write_text(text,encoding='utf-8')
                with self.assertRaises(ValueError):strict_json(path,1024)


if __name__=='__main__':
    print('Pure fixed-bundle contracts/spool checks; no actual Blender render or human acceptance.')
    unittest.main()
