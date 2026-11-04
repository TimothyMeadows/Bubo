"""Opt-in real CLI assembly/readiness QA. All fixture design approvals are synthetic."""
from pathlib import Path
import argparse
import hashlib
import json
import re
import subprocess
import tempfile
from schematic_qa_numbers import same_native_numbers

ROOT = Path(__file__).resolve().parents[1]


def main():
    def case_id(value):
        if not re.fullmatch(r'[a-z0-9][a-z0-9-]{0,63}', value):
            raise argparse.ArgumentTypeError('case must be a bounded lowercase fixture identifier')
        return value

    parser = argparse.ArgumentParser()
    parser.add_argument('--cli', type=Path, default=ROOT/'artifacts/bubo/bubo.dll')
    parser.add_argument('--blender', type=Path, required=True)
    parser.add_argument('--case', action='append', type=case_id)
    parser.add_argument('--design-directory', type=Path, default=ROOT/'resources/schematic-to-model/examples-v2')
    parser.add_argument('--repeat', action='store_true')
    args = parser.parse_args()
    cli, blender = args.cli.resolve(strict=True), args.blender.resolve(strict=True)
    design_directory = args.design_directory.resolve(strict=True)
    if not design_directory.is_dir():
        parser.error('--design-directory must identify existing fixture inputs')
    parent = ROOT/'.bubo/sa'; parent.mkdir(parents=True, exist_ok=True)
    run = Path(tempfile.mkdtemp(prefix='r-', dir=parent))
    project = run/'p'; (project/'inputs').mkdir(parents=True)
    sha = lambda path: hashlib.sha256(path.read_bytes()).hexdigest()
    checks, calls, cases = [], [], {}

    def save(status):
        (run/'SUMMARY.json').write_text(json.dumps({'status': status, 'checks': checks, 'project': str(project), 'cases': cases,
            'scope': 'Actual public CLI, bounded Blender parts/assembly/diagnostic actions, independent reopening and native geometry/skin/clearance checks. Synthetic whole-design approval assertions only; no human asset/runtime acceptance, GLB export or promotion.'}, indent=2)+'\n', encoding='utf-8')

    def check(value, label):
        assert value, label
        checks.append(label); print('PASS:', label, flush=True)

    def invoke(operation, manifest=None, extra=(), expected=None):
        command = ['dotnet', str(cli)] if cli.suffix == '.dll' else [str(cli)]
        command += ['schematic-to-model', operation, '--api-version', '2', '--root', str(project), '--host-context', str(context_path)]
        if manifest:
            command += ['--manifest', manifest]
        command += list(extra)
        process = subprocess.run(command, cwd=ROOT, capture_output=True, text=True, encoding='utf-8', timeout=930)
        record = {'command': command, 'exitCode': process.returncode, 'stdout': process.stdout, 'stderr': process.stderr}
        calls.append(record); (run/f'call-{len(calls):03d}.json').write_text(json.dumps(record, indent=2)+'\n', encoding='utf-8')
        value = json.loads(process.stdout)
        check(process.returncode != 0 and expected in value.get('error', '') if expected else process.returncode == 0,
              'expected rejection: '+expected if expected else 'actual native CLI: '+operation)
        return value.get('data', value)

    policy = project/'policy.json'
    policy.write_text('{"purpose":"owned mechanical engineering QA; synthetic approvals do not accept assets"}\n', encoding='utf-8')
    context = {'schemaVersion': 'bubo-host-context/v1', 'projectRoot': str(project), 'task': 'qa',
        'roots': {'state': 'state', 'staging': 'staging', 'tools': ['tools'], 'inputs': ['inputs'], 'outputs': ['outputs']},
        'policy': {'pipeline': 'SCHEMATIC_TO_MODEL', 'styleSha256': 'a'*64,
            'engine': {'effectiveEngine': 'THREEJS', 'selectionSource': 'default', 'definitionSha256': 'b'*64, 'registrySha256': 'c'*64, 'contractSha256': None}, 'libraries': []},
        'references': [{'path': 'policy.json', 'sha256': sha(policy), 'kind': 'policy'}],
        'tools': [{'path': str(blender), 'sha256': sha(blender), 'kind': 'tool'}],
        'budgets': {'timeoutSeconds': 900, 'maxLogBytes': 8*1024*1024, 'maxPrivateBytes': 4*1024**3},
        'permissions': {'promotion': False, 'provisioning': False}}
    context_path = project/'context.json'

    def write_context():
        context_path.write_text(json.dumps(context, indent=2)+'\n', encoding='utf-8')

    def reference(path, kind='input'):
        relative = path.relative_to(project).as_posix()
        context['references'] = [item for item in context['references'] if item['path'] != relative]
        context['references'].append({'path': relative, 'sha256': sha(path), 'kind': kind}); write_context()

    def reports(status, motion):
        name = 'motion' if motion else 'assembly'
        author = project/status[name+'Report']; verified = project/status[name+'Verification']
        a, b = json.loads(author.read_bytes()), json.loads(verified.read_bytes())
        check(a == b, name+': fresh-process measurements exactly match producer')
        folder = author.parent
        check(sha(folder/a['sourceFile']) == a['sourceSha256'], name+': exact editable source identity')
        records = a['instances'] + [item for clip in a['clips'] for sample in clip['samples'] for item in sample['instances']]
        expected = {a['sourceFile'], author.name, verified.name}
        for item in records:
            expected.add(item['geometryFile'])
            check(sha(folder/item['geometryFile']) == item['geometrySha256'], name+': actual indexed geometry '+item['geometryFile'])
        check({p.name for p in folder.iterdir()} == expected, name+': complete bounded payload inventory')
        native = json.loads((project/status[name+'NativeVerification']).read_bytes())
        check(same_native_numbers(a, native['report']) and native['poses'] and all(p['clearance']['isValid'] for p in native['poses']), name+': native actual triangle/skin/clearance verification passed (typed JSON numbers within one ULP)')
        check(len({i['instanceId'] for i in a['instances']}) == len(a['instances']), name+': distinct physical instance identities')
        return a, native

    write_context()
    try:
        for name in args.case or ['vehicle', 'device', 'vessel', 'max-ids']:
            source = project/'inputs'/f'{name}.json'
            source_name = 'vehicle' if name == 'max-ids' else name
            source.write_bytes((design_directory/f'{source_name}.json').read_bytes())
            if name == 'max-ids':
                variant = json.loads(source.read_bytes()); variant['joints'][0]['id'] = 'j'*80
                source.write_text(json.dumps(variant, indent=2)+'\n', encoding='utf-8')
            spec = json.loads(source.read_bytes()); reference(source)
            initialized = invoke('init', extra=['--asset', name, '--version', 'v1', '--design', f'inputs/{name}.json'])
            manifest = initialized['manifest']
            preview = invoke('preview', manifest)
            check({'assembly-details.svg', 'flex-rigs.svg'} <= set(preview['artifacts']), name+': complete v2 policy sheets are approval-bound')
            invoke('assemble', manifest, ['--blender', str(blender)], 'approval')
            review = {'schemaVersion': 'bubo-schematic-review/v1', 'kind': 'whole-assembly-design', 'decision': 'approved', 'human': True,
                'reviewer': 'synthetic-assembly-engineering-qa', 'reason': 'Synthetic protocol assertion for an owned engineering fixture; no human asset/runtime acceptance.',
                'asset': name, 'version': 'v1', 'designSha256': preview['designSha256'], 'designFingerprint': preview['designFingerprint'], 'reviewedArtifacts': preview['artifacts']}
            evidence = project/'state'/f'{name}-review.json'
            evidence.write_text(json.dumps(review, indent=2)+'\n', encoding='utf-8'); reference(evidence, 'review')
            invoke('record', manifest, ['--kind', review['kind'], '--reviewer', review['reviewer'], '--evidence', evidence.relative_to(project).as_posix()])
            before = invoke('doctor', manifest, ['--blender', str(blender)])
            check(not before['assembly']['assemblyAvailable'], name+': approved drawing alone cannot admit missing part artifacts')
            parts = invoke('build-parts', manifest, ['--blender', str(blender)])
            doctor = invoke('doctor', manifest, ['--blender', str(blender)])
            check(doctor['assembly']['assemblyAvailable'] and not doctor['assembly']['motionProofAvailable'], name+': exact prospective assembly prerequisites checked before authoring')
            built = invoke('assemble', manifest, ['--blender', str(blender)])
            check(built['currentAssemblyValid'] and not built['currentMotionProofValid'], name+': assembled source does not inherit diagnostic proof')
            assembly_report, assembly_native = reports(built, False)
            proved = invoke('prove-motion', manifest, ['--blender', str(blender)])
            check(proved['currentAssemblyValid'] and proved['currentMotionProofValid'] and not proved['modelExportAvailable']
                  and not proved['humanModelAccepted'] and not proved['runtimeAcceptance'] and not proved['continuousCollisionCertified'], name+': sampled readiness retains separate export/human/runtime gates')
            motion_report, motion_native = reports(proved, True)
            if name == 'max-ids':
                check({'range-'+'j'*80, 'cycle-'+'j'*80} <= {c['id'] for c in motion_report['clips']},
                      'native compiler and actual workers preserve both 86-character generated clip identities')
            check(len(motion_native['poses']) == 1+sum(len(c['samples']) for c in motion_report['clips']), name+': every declared sample has native evaluated clearance evidence')
            check(len(motion_report['instances']) == len(spec['instances']) and {i['instanceId']: i['partId'] for i in motion_report['instances']} == {i['id']: i['partId'] for i in spec['instances']}, name+': complete approved physical inventory survives assembly and motion')
            current = invoke('status', manifest)['assembly']
            check(current['currentAssemblyValid'] and current['currentMotionProofValid'], name+': current source/approval/parts/scene/job/bundle/evidence freshness')
            cases[name] = {'manifest': manifest, 'parts': parts, 'assembly': built, 'motion': proved, 'rigidSampling': json.loads((project/proved['motionReport']).parent.parent.joinpath('job.json').read_bytes())['plan']['sampling']}
            save('in-progress')
            if args.repeat:
                rebuilt = invoke('assemble', manifest, ['--blender', str(blender)])
                check(rebuilt['currentAssemblyValid'] and not rebuilt['currentMotionProofValid'], name+': newer assembly invalidates the older motion proof')
                again = invoke('prove-motion', manifest, ['--blender', str(blender)])
                actual = json.loads((project/again['motionReport']).read_bytes())
                def geometry(value):
                    return [(i['instanceId'], i['geometrySha256']) for i in value['instances']] + [
                        (c['id'], s['index'], i['instanceId'], i['geometrySha256']) for c in value['clips'] for s in c['samples'] for i in s['instances']]
                check(geometry(actual) == geometry(motion_report), name+': repeat immutable assembly/proof preserves semantic geometry')
                cases[name]['repeatAssembly'] = rebuilt; cases[name]['repeatMotion'] = again
            save('in-progress')
        check(not (project/'assets').exists() and not list(project.rglob('*.glb')), 'no canonical GLB export or asset promotion in assembly delivery')
        save('passed'); print(json.dumps({'status': 'passed', 'checks': len(checks), 'evidence': str(run/'SUMMARY.json')}), flush=True)
    except BaseException:
        save('failed'); print('Preserved failed actual assembly CLI fixture at '+str(run), flush=True); raise


if __name__ == '__main__':
    main()
