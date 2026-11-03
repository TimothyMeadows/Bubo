"""Actual public design-only CLI fixtures. Synthetic host/review assertions, no model tools or generation."""
from pathlib import Path
import argparse
import hashlib
import json
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--cli', type=Path, default=ROOT/'artifacts/bubo/bubo.dll')
    args = parser.parse_args()
    cli = args.cli.resolve()
    assert cli.is_file(), 'Build the native CLI explicitly first'
    parent = ROOT/'.bubo/floor-plan-cli'
    parent.mkdir(parents=True, exist_ok=True)
    work = Path(tempfile.mkdtemp(prefix='run-', dir=parent))
    project = work/'p'
    project.mkdir()
    checks = []
    calls = []

    def check(value, message):
        assert value, message
        checks.append(message)
        print('PASS:', message, flush=True)

    def invoke(arguments, expected=None):
        command = ['dotnet', str(cli)] if cli.suffix == '.dll' else [str(cli)]
        result = subprocess.run([*command, *arguments], cwd=ROOT, capture_output=True, text=True,
                                encoding='utf-8', timeout=30)
        record = {'arguments': arguments, 'exitCode': result.returncode, 'stdout': result.stdout, 'stderr': result.stderr}
        calls.append(record)
        (work/f'call-{len(calls):03d}.json').write_text(json.dumps(record, indent=2)+'\n', encoding='utf-8')
        value = json.loads(result.stdout)
        if expected is not None:
            check(result.returncode != 0 and value['status'] == 'failed' and expected in value['error'], expected)
        else:
            check(result.returncode == 0, 'actual CLI call succeeded: '+' '.join(arguments[:2]))
        return value

    def sha(path):
        return hashlib.sha256(path.read_bytes()).hexdigest()

    capability = invoke(['capabilities', '--api-version', '2'])
    check(capability['apiVersions'] == [2] and 'floor-plan-to-model' in capability['workflows'], 'additive API2 floor-plan capability')
    check(capability['floorPlan'] == {'phase': 'architectural-export', 'operations': ['init', 'doctor', 'preview', 'validate', 'record', 'build', 'export', 'review', 'status'],
                                     'modelingAvailable': True, 'requiresApprovedConstructionPlanV2': True, 'modelExportAvailable': True}, 'portable export is declared while legacy designs and acceptance stay separately gated')
    invoke(['floor-plan-to-model', 'build', '--api-version', '2'], 'Explicit --root required')
    invoke(['floor-plan-to-model', 'export', '--api-version', '2'], 'Explicit --root required')
    invoke(['floor-plan-to-model', 'review', '--api-version', '2'], 'Explicit --root required')
    invoke(['floor-plan-to-model', 'promote', '--api-version', '2'], 'Unsupported workflow/operation')
    invoke(['floor-plan-to-model', 'preview', '--api-version', '2', '--callback', 'untrusted.py'], 'Unknown or inapplicable option')
    (project/'inputs').mkdir()
    policy = project/'policy.json'
    policy.write_text('{"purpose":"synthetic-floor-plan-contract-fixture"}\n', encoding='utf-8')
    context = {
        'schemaVersion': 'bubo-host-context/v1', 'projectRoot': str(project), 'task': 'design',
        'roots': {'state': 'state', 'staging': 'staging', 'tools': ['tools'], 'inputs': ['inputs'], 'outputs': ['outputs']},
        'policy': {'pipeline': 'FLOOR_PLAN_TO_MODEL', 'styleSha256': 'a'*64,
                   'engine': {'effectiveEngine': 'THREEJS', 'selectionSource': 'default',
                              'definitionSha256': 'b'*64, 'registrySha256': 'c'*64, 'contractSha256': None}, 'libraries': []},
        'references': [{'path': 'policy.json', 'sha256': sha(policy), 'kind': 'policy'}], 'tools': [],
        'budgets': {'timeoutSeconds': 30, 'maxLogBytes': 1048576},
        'permissions': {'promotion': False, 'provisioning': False}}
    context_path = project/'context.json'

    def write_context():
        context_path.write_text(json.dumps(context, indent=2)+'\n', encoding='utf-8')

    def reference(path, kind='input'):
        relative = path.relative_to(project).as_posix()
        context['references'] = [r for r in context['references'] if r['path'] != relative]
        context['references'].append({'path': relative, 'sha256': sha(path), 'kind': kind})
        write_context()

    write_context()
    common = ['--api-version', '2', '--root', str(project), '--host-context', str(context_path)]
    context['policy']['pipeline'] = 'PROOF_TO_MODEL'
    write_context()
    invoke(['floor-plan-to-model', 'init', *common, '--asset', 'wrong', '--version', 'v1', '--plan', 'inputs/missing.json'],
           'Host context must explicitly select FLOOR_PLAN_TO_MODEL')
    check(not (project/'state').exists() and not (project/'staging').exists(), 'wrong pipeline rejects before side effects')
    context['policy']['pipeline'] = 'FLOOR_PLAN_TO_MODEL'
    write_context()
    invoke(['floor-plan-to-model', 'init', *common, '--asset', 'wrong', '--version', 'v1', '--plan', 'inputs/unregistered.json'],
           'matching registered host reference')
    check(not (project/'state').exists(), 'unregistered plan rejects before execution state')

    packages = {}
    for name in ['single-storey', 'multi-storey', 'angled-courtyard', 'stacked-three-storey', 'windowed-single-storey']:
        source_name = 'single-storey' if name == 'windowed-single-storey' else name
        source = ROOT/'resources/floor-plan-to-model/examples'/f'{source_name}.json'
        target = project/'inputs'/f'{name}.json'
        target.write_bytes(source.read_bytes())
        if name == 'windowed-single-storey':
            windowed = json.loads(target.read_bytes())
            windowed['storeys'][0]['walls'][1]['openings'].append({'id': 'qa-east-window', 'kind': 'window', 'offset': 3.0, 'width': 1.4, 'sill': .9, 'head': 2.1, 'pivot': None, 'swing': None})
            target.write_text(json.dumps(windowed, indent=2)+'\n', encoding='utf-8')
        reference(target)
        init = invoke(['floor-plan-to-model', 'init', *common, '--asset', name, '--version', 'v1', '--plan', target.relative_to(project).as_posix()])['data']
        manifest = init['manifest']
        doctor = invoke(['floor-plan-to-model', 'doctor', *common, '--manifest', manifest])['data']
        check(doctor['vectorPreviewAvailable'] and not doctor['modelingAvailable'] and not doctor['modelExportAvailable'] and not doctor['provisioningRequired'], 'doctor reports design prerequisites only: '+name)
        check(not init['wholeBuildingApproved'] and not init['modelingAvailable'] and not init['modelExportAvailable'], 'initialization grants no approval/model claim: '+name)
        preview = invoke(['floor-plan-to-model', 'preview', *common, '--manifest', manifest])['data']
        plan = json.loads(source.read_bytes())
        check(preview['completeArchitecturalPreview'] and len(preview['drawings']) == len(plan['storeys'])+6+len(plan['stairs']), 'every architectural sheet produced: '+name)
        drawing_root = project/preview['drawingDirectory']
        for file, digest in preview['drawings'].items():
            check(sha(drawing_root/file) == digest, 'actual persisted drawing digest: '+name+'/'+file)
        package = json.loads((drawing_root/'preview-index.json').read_bytes())
        check(package['planSha256'] == sha(target) and package['fingerprint'] == preview['designFingerprint'], 'complete package binds actual original bytes: '+name)
        validation = invoke(['floor-plan-to-model', 'validate', *common, '--manifest', manifest])['data']
        check(validation['isValid'], 'native dimensional validation: '+name)
        check(not invoke(['floor-plan-to-model', 'status', *common, '--manifest', manifest])['data']['wholeBuildingApproved'], 'technical preview never approves design: '+name)
        packages[name] = {'manifest': manifest, 'preview': preview, 'package': package}

    invalid = json.loads((project/'inputs/single-storey.json').read_bytes())
    invalid['storeys'][0]['walls'][0]['openings'][0]['head'] = 1.3
    bad_plan = project/'inputs/bad-clearance.json'
    bad_plan.write_text(json.dumps(invalid), encoding='utf-8')
    reference(bad_plan)
    failure = invoke(['floor-plan-to-model', 'init', *common, '--asset', 'bad-clearance', '--version', 'v1', '--plan', 'inputs/bad-clearance.json'], 'Building plan has')
    check(not failure['validation']['isValid'] and failure['validation']['issues'], 'geometry failures return structured actionable native issues')
    check(not (project/'staging/floor-plan-to-model/bad-clearance/v1').exists(), 'invalid design creates no asset staging or implicit model')

    selected = packages['single-storey']
    preview = selected['preview']
    review = {
        'schemaVersion': 'bubo-floor-plan-review/v1', 'kind': 'whole-building-design', 'decision': 'approved',
        'human': True, 'reviewer': 'synthetic-contract-fixture',
        'reason': 'Synthetic API review assertion for protocol testing; not actual human quality acceptance.',
        'asset': 'single-storey', 'version': 'v1', 'planSha256': preview['planSha256'],
        'designFingerprint': preview['designFingerprint'], 'reviewedArtifacts': dict(preview['drawings'])}
    evidence = project/'state/review.json'
    evidence.write_text(json.dumps({**review, 'reviewedArtifacts': dict(list(review['reviewedArtifacts'].items())[1:])}), encoding='utf-8')
    reference(evidence, 'review')
    record_args = ['floor-plan-to-model', 'record', *common, '--manifest', selected['manifest'], '--kind', 'whole-building-design',
                   '--reviewer', review['reviewer'], '--evidence', 'state/review.json']
    invoke(record_args, 'exact complete architectural package')
    evidence.write_text(json.dumps(review), encoding='utf-8')
    reference(evidence, 'review')
    accepted = invoke(record_args)['data']
    check(accepted['wholeBuildingApproved'] and not accepted['modelingAvailable'], 'explicit synthetic whole-package review only; no automatic model capability')
    before = json.loads((project/selected['manifest']).read_bytes())['reviews'][0]
    original_snapshot = (project/before['evidence']).read_bytes()
    review['decision'] = 'rejected'
    evidence.write_text(json.dumps(review), encoding='utf-8')
    reference(evidence, 'review')
    rejected = invoke(record_args)['data']
    check(not rejected['wholeBuildingApproved'] and rejected['reviews'] == 2, 'later explicit rejection retained without older approval fallback')
    check((project/before['evidence']).read_bytes() == original_snapshot, 'overwritten caller path cannot erase immutable historical review bytes')
    altered = project/'inputs/single-storey.json'
    altered.write_bytes(altered.read_bytes()+b'\n')
    reference(altered)
    invoke(['floor-plan-to-model', 'status', *common, '--manifest', selected['manifest']], 'Design input changed')
    check(not list((project/'staging').rglob('*.blend')) and not list((project/'staging').rglob('*.glb')), 'design foundation creates no meshes, Blender source or runtime promotion')
    summary = {'status': 'passed', 'checks': len(checks), 'calls': len(calls), 'project': str(project), 'packages': packages,
               'scope': 'Actual API2 CLI and persisted SVGs; policy/reviewer assertions synthetic; no Blender/model/runtime acceptance.'}
    (work/'SUMMARY.json').write_text(json.dumps(summary, indent=2)+'\n', encoding='utf-8')
    print(json.dumps({'status': 'passed', 'checks': len(checks), 'evidence': str(work/'SUMMARY.json')}), flush=True)


if __name__ == '__main__':
    main()
