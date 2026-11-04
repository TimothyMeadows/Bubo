"""Real public schematic design CLI; synthetic host/review assertions grant no asset acceptance."""
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
    cli = parser.parse_args().cli.resolve()
    assert cli.is_file(), 'Explicitly build the native CLI first'
    parent = ROOT/'.bubo/schematic-cli'
    parent.mkdir(parents=True, exist_ok=True)
    work = Path(tempfile.mkdtemp(prefix='run-', dir=parent))
    project = work/'p'
    (project/'inputs').mkdir(parents=True)
    checks = []
    calls = []

    def check(value, message):
        assert value, message
        checks.append(message)
        print('PASS:', message, flush=True)

    def invoke(arguments, expected=None):
        command = ['dotnet', str(cli)] if cli.suffix == '.dll' else [str(cli)]
        run = subprocess.run([*command, *arguments], cwd=ROOT, capture_output=True, text=True, encoding='utf-8', timeout=30)
        record = {'arguments': arguments, 'exitCode': run.returncode, 'stdout': run.stdout, 'stderr': run.stderr}
        calls.append(record)
        (work/f'call-{len(calls):03d}.json').write_text(json.dumps(record, indent=2)+'\n', encoding='utf-8')
        value = json.loads(run.stdout)
        if expected:
            check(run.returncode != 0 and value['status'] == 'failed' and expected in value['error'], expected)
        else:
            check(run.returncode == 0, 'actual CLI succeeded: '+' '.join(arguments[:2]))
        return value

    def sha(path):
        return hashlib.sha256(path.read_bytes()).hexdigest()

    capability = invoke(['capabilities', '--api-version', '2'])
    check('schematic-to-model' in capability['workflows'], 'additive schematic workflow in execution API2')
    check(capability['schematic'] == {
        'phase': 'assembly-readiness', 'operations': ['init', 'doctor', 'preview', 'validate', 'record', 'build-parts', 'assemble', 'prove-motion', 'status'],
        'partConstructionAvailable': True, 'assemblyAvailable': True, 'motionProofAvailable': True, 'requiresApprovedAssemblyV2': True, 'modelExportAvailable': False},
        'additive approved-v2 assembly capabilities keep canonical export separately gated')
    invoke(['schematic-to-model', 'build-parts', '--api-version', '2'], 'Explicit --root required')
    for operation in ['assemble', 'prove-motion']:
        invoke(['schematic-to-model', operation, '--api-version', '2'], 'Explicit --root required')
    for operation in ['export', 'review', 'promote']:
        invoke(['schematic-to-model', operation, '--api-version', '2'], 'Unsupported workflow/operation')
    invoke(['schematic-to-model', 'preview', '--api-version', '2', '--callback', 'untrusted.py'], 'Unknown or inapplicable option')
    policy = project/'policy.json'
    policy.write_text('{"purpose":"synthetic-schematic-design-test"}\n', encoding='utf-8')
    context = {
        'schemaVersion': 'bubo-host-context/v1', 'projectRoot': str(project), 'task': 'schematic',
        'roots': {'state': 'state', 'staging': 'staging', 'tools': ['tools'], 'inputs': ['inputs'], 'outputs': ['outputs']},
        'policy': {'pipeline': 'SCHEMATIC_TO_MODEL', 'styleSha256': 'a'*64,
                   'engine': {'effectiveEngine': 'THREEJS', 'selectionSource': 'default', 'definitionSha256': 'b'*64, 'registrySha256': 'c'*64, 'contractSha256': None}, 'libraries': []},
        'references': [{'path': 'policy.json', 'sha256': sha(policy), 'kind': 'policy'}], 'tools': [],
        'budgets': {'timeoutSeconds': 30, 'maxLogBytes': 1048576}, 'permissions': {'promotion': False, 'provisioning': False}}
    context_path = project/'context.json'

    def write_context():
        context_path.write_text(json.dumps(context, indent=2)+'\n', encoding='utf-8')

    def reference(path, kind='input'):
        relative = path.relative_to(project).as_posix()
        context['references'] = [x for x in context['references'] if x['path'] != relative]
        context['references'].append({'path': relative, 'sha256': sha(path), 'kind': kind})
        write_context()

    common = ['--api-version', '2', '--root', str(project), '--host-context', str(context_path)]
    context['policy']['pipeline'] = 'PROOF_TO_MODEL'
    write_context()
    invoke(['schematic-to-model', 'init', *common, '--asset', 'wrong', '--version', 'v1', '--design', 'inputs/missing.json'], 'explicitly select SCHEMATIC_TO_MODEL')
    check(not (project/'state').exists(), 'wrong pipeline rejects before execution writes')
    context['policy']['pipeline'] = 'SCHEMATIC_TO_MODEL'
    write_context()
    invoke(['schematic-to-model', 'init', *common, '--asset', 'missing', '--version', 'v1', '--design', 'inputs/missing.json'], 'matching registered host reference')
    packages = {}
    invalid = project/'inputs/invalid-design.json'
    bad = json.loads((ROOT/'resources/schematic-to-model/examples/vehicle.json').read_bytes())
    bad['parts'][0]['size']['x'] = -1
    invalid.write_text(json.dumps(bad, indent=2)+'\n', encoding='utf-8')
    reference(invalid)
    failure = invoke(['schematic-to-model', 'init', *common, '--asset', 'invalid', '--version', 'v1', '--design', 'inputs/invalid-design.json'], 'validation issue')
    check(failure['validation']['issues'] and all({'code', 'path', 'message'} <= set(issue) for issue in failure['validation']['issues']), 'invalid designs return actionable structured code/path diagnostics')
    for name in ['vehicle', 'device', 'vessel']:
        target = project/'inputs'/f'{name}.json'
        target.write_bytes((ROOT/'resources/schematic-to-model/examples'/f'{name}.json').read_bytes())
        reference(target)
        relative = target.relative_to(project).as_posix()
        manifest = f'state/schematic-to-model/{name}/v1/manifest.json'
        init = invoke(['schematic-to-model', 'init', *common, '--asset', name, '--version', 'v1', '--design', relative])['data']
        check(init['manifest'] == manifest and not init['wholeAssemblyApproved'], 'canonical immutable initialization without inherited approval')
        packages[name] = invoke(['schematic-to-model', 'preview', *common, '--manifest', manifest])['data']
        package = packages[name]
        check(package['completeSchematicPreview'] and not package['partConstructionAvailable'] and not package['modelExportAvailable'], 'complete design remains distinct from construction')
        for sheet, digest in package['artifacts'].items():
            check(sha(project/package['drawingDirectory']/sheet) == digest, f'{name}: actual artifact hash '+sheet)
        check(invoke(['schematic-to-model', 'validate', *common, '--manifest', manifest])['data']['isValid'], 'native design validation is valid')
        invoke(['schematic-to-model', 'status', *common, '--manifest', manifest])
        invoke(['schematic-to-model', 'doctor', *common, '--manifest', manifest])
        invoke(['schematic-to-model', 'init', *common, '--asset', name, '--version', 'v1', '--design', relative], 'version already exists')

    manifest = 'state/schematic-to-model/device/v1/manifest.json'
    package = packages['device']
    review = {'schemaVersion': 'bubo-schematic-review/v1', 'kind': 'whole-assembly-design', 'decision': 'approved', 'human': True,
              'reviewer': 'synthetic-cli-fixture', 'reason': 'Synthetic protocol assertion only; no actual human asset approval.',
              'asset': 'device', 'version': 'v1', 'designSha256': package['designSha256'], 'designFingerprint': package['designFingerprint'],
              'reviewedArtifacts': dict(package['artifacts'])}
    review_path = project/'state/review.json'
    review_path.write_text(json.dumps(review, indent=2)+'\n', encoding='utf-8')
    reference(review_path, 'review')
    approved = invoke(['schematic-to-model', 'record', *common, '--manifest', manifest, '--kind', 'whole-assembly-design', '--reviewer', review['reviewer'], '--evidence', 'state/review.json'])['data']
    check(approved['wholeAssemblyApproved'] and approved['partConstructionAvailable'] and not approved['runtimeAcceptance'], 'synthetic complete-design assertion admits explicit part construction without runtime acceptance')
    changed = project/'inputs/device.json'
    design = json.loads(changed.read_bytes())
    design['name'] = 'Revised device'
    changed.write_text(json.dumps(design, indent=2)+'\n', encoding='utf-8')
    reference(changed)
    invoke(['schematic-to-model', 'status', *common, '--manifest', manifest], 'Schematic changed')
    next_version = invoke(['schematic-to-model', 'init', *common, '--asset', 'device', '--version', 'v2', '--design', 'inputs/device.json', '--parent-manifest', manifest], 'matching registered host reference')
    reference(project/manifest)
    next_version = invoke(['schematic-to-model', 'init', *common, '--asset', 'device', '--version', 'v2', '--design', 'inputs/device.json', '--parent-manifest', manifest])['data']
    check(not next_version['wholeAssemblyApproved'], 'new version preserves ancestry without inheriting approval')
    (work/'result.json').write_text(json.dumps({'status': 'passed', 'checks': checks, 'evidence': str(work), 'syntheticApprovalOnly': True}, indent=2)+'\n', encoding='utf-8')
    print('Evidence:', work)


if __name__ == '__main__':
    main()
