"""Opt-in real Blender construction/source-inspection QA. Synthetic design reviews, no promotion."""
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
    parser.add_argument('--blender', type=Path, required=True)
    parser.add_argument('--case', choices=['single-storey', 'multi-storey', 'angled-courtyard', 'stacked-three-storey'], action='append')
    parser.add_argument('--no-render', action='store_true')
    parser.add_argument('--repeat', action='store_true')
    parser.add_argument('--geometry-checks', type=int, help='Choose a finite fixture plan allowance before initialization/approval; never changes an existing version')
    args = parser.parse_args()
    cli = args.cli.resolve(strict=True)
    blender = args.blender.resolve(strict=True)
    runtime = cli.parent
    parent = ROOT/'.bubo/fpc'
    parent.mkdir(parents=True, exist_ok=True)
    work = Path(tempfile.mkdtemp(prefix='r-', dir=parent))
    project = work/'p'
    (project/'inputs').mkdir(parents=True)
    checks, calls, buildings = [], [], {}
    sha = lambda path: hashlib.sha256(path.read_bytes()).hexdigest()

    def check(value, label):
        assert value, label
        checks.append(label)
        print('PASS:', label, flush=True)

    def save_summary(status):
        result = {'status': status, 'checks': len(checks), 'calls': len(calls), 'project': str(project), 'blender': str(blender),
                  'buildings': buildings, 'scope': 'Actual Blender construction and optional independent source-load/CPU views; human review assertions synthetic; no GLB export, engine or asset acceptance.'}
        (work/'SUMMARY.json').write_text(json.dumps(result, indent=2)+'\n', encoding='utf-8')
        return result

    def invoke(arguments, expected=None):
        command = (['dotnet', str(cli)] if cli.suffix == '.dll' else [str(cli)]) + arguments
        result = subprocess.run(command, cwd=ROOT, capture_output=True, text=True, encoding='utf-8', timeout=330)
        call = {'arguments': arguments, 'exitCode': result.returncode, 'stdout': result.stdout, 'stderr': result.stderr}
        calls.append(call)
        (work/f'call-{len(calls):03d}.json').write_text(json.dumps(call, indent=2)+'\n', encoding='utf-8')
        value = json.loads(result.stdout)
        check(result.returncode != 0 and expected in value['error'] if expected else result.returncode == 0,
              ('expected rejection: '+expected) if expected else 'actual CLI: '+' '.join(arguments[:2]))
        return value.get('data', value)

    policy = project/'policy.json'
    policy.write_text('{"purpose":"synthetic owned procedural architectural QA; no human acceptance or promotion"}\n', encoding='utf-8')
    context = {
        'schemaVersion': 'bubo-host-context/v1', 'projectRoot': str(project), 'task': 'qa',
        'roots': {'state': 'state', 'staging': 'staging', 'tools': ['tools'], 'inputs': ['inputs'], 'outputs': ['outputs']},
        'policy': {'pipeline': 'FLOOR_PLAN_TO_MODEL', 'styleSha256': 'a'*64,
                   'engine': {'effectiveEngine': 'THREEJS', 'selectionSource': 'default', 'definitionSha256': 'b'*64,
                              'registrySha256': 'c'*64, 'contractSha256': None}, 'libraries': []},
        'references': [{'path': 'policy.json', 'sha256': sha(policy), 'kind': 'policy'}],
        'tools': [{'path': str(blender), 'sha256': sha(blender), 'kind': 'tool'}],
        'budgets': {'timeoutSeconds': 300, 'maxLogBytes': 8*1024*1024, 'maxPrivateBytes': 2*1024**3},
        'permissions': {'promotion': False, 'provisioning': False}}
    context_path = project/'context.json'

    def reference(path, kind='input'):
        relative = path.relative_to(project).as_posix()
        context['references'] = [r for r in context['references'] if r['path'] != relative]
        context['references'].append({'path': relative, 'sha256': sha(path), 'kind': kind})
        context_path.write_text(json.dumps(context, indent=2)+'\n', encoding='utf-8')

    context_path.write_text(json.dumps(context, indent=2)+'\n', encoding='utf-8')
    common = ['--api-version', '2', '--root', str(project), '--host-context', str(context_path)]
    try:
        for index, name in enumerate(args.case or ['single-storey', 'multi-storey', 'angled-courtyard']):
            asset = ['one', 'multi', 'court', 'stack'][['single-storey', 'multi-storey', 'angled-courtyard', 'stacked-three-storey'].index(name)]
            plan_path = project/'inputs'/f'{asset}.json'
            plan_path.write_bytes((ROOT/'resources/floor-plan-to-model/examples-v2'/f'{name}.json').read_bytes())
            plan = json.loads(plan_path.read_bytes())
            if args.geometry_checks is not None:
                assert 1 <= args.geometry_checks <= 5_000_000
                plan['budgets']['maxGeometryChecks'] = args.geometry_checks
                plan_path.write_text(json.dumps(plan, indent=2)+'\n', encoding='utf-8')
            reference(plan_path)
            init = invoke(['floor-plan-to-model', 'init', *common, '--asset', asset, '--version', 'v1', '--plan', f'inputs/{asset}.json'])
            manifest = init['manifest']
            preview = invoke(['floor-plan-to-model', 'preview', *common, '--manifest', manifest])
            check('construction-details.svg' in preview['drawings'], name+': complete v2 design includes explicit details')
            invoke(['floor-plan-to-model', 'build', *common, '--manifest', manifest, '--blender', str(blender)], 'explicit human approval')
            review = {'schemaVersion': 'bubo-floor-plan-review/v1', 'kind': 'whole-building-design', 'decision': 'approved',
                      'human': True, 'reviewer': 'synthetic-real-blender-qa',
                      'reason': 'Synthetic protocol assertion for owned engineering fixtures, not actual human architectural/model acceptance.',
                      'asset': asset, 'version': 'v1', 'planSha256': preview['planSha256'],
                      'designFingerprint': preview['designFingerprint'], 'reviewedArtifacts': preview['drawings']}
            evidence = project/'state'/f'{asset}-review.json'
            evidence.write_text(json.dumps(review, indent=2)+'\n', encoding='utf-8')
            reference(evidence, 'review')
            invoke(['floor-plan-to-model', 'record', *common, '--manifest', manifest, '--kind', review['kind'], '--reviewer', review['reviewer'], '--evidence', evidence.relative_to(project).as_posix()])
            doctor = invoke(['floor-plan-to-model', 'doctor', *common, '--manifest', manifest, '--blender', str(blender)])
            check(doctor['nativeReadiness'] == 'verified', name+': actual pinned Blender prerequisite probe')
            built = invoke(['floor-plan-to-model', 'build', *common, '--manifest', manifest, '--blender', str(blender)])
            check(built['currentBuildValid'] and not built['modelExportAvailable'] and not built['humanModelAccepted'] and not built['runtimeAcceptance'], name+': staged construction only')
            blend = project/built['editableSource']
            report = json.loads((project/built['report']).read_bytes())
            attempt = blend.parent.parent
            job = json.loads((attempt/'job.json').read_bytes())
            check(sha(blend) == report['blend']['sha256'], name+': editable source hash matches measured report')
            check(report['counts']['triangles'] == job['scene']['metrics']['triangleCount'] and report['counts']['meshes'] == len(job['scene']['meshes']), name+': actual Blender mesh inventory matches native geometry')
            check(report['maxWorldCoordinateError'] <= plan['numerics']['linearTolerance'], name+': actual Blender coordinates preserve approved tolerance')
            check(not report['independentReimport'] and not report['humanModelAccepted'] and not report['runtimeAccepted'], name+': construction receipt grants no later acceptance')
            check(invoke(['floor-plan-to-model', 'status', *common, '--manifest', manifest])['construction']['currentBuildValid'], name+': fresh output/job/tool/approval status')
            buildings[name] = {'manifest': manifest, 'preview': preview, 'build': built, 'blend': str(blend), 'report': str(project/built['report']), 'job': str(attempt/'job.json')}
            save_summary('in-progress')
            if args.repeat:
                second = invoke(['floor-plan-to-model', 'build', *common, '--manifest', manifest, '--blender', str(blender)])
                second_blend = project/second['editableSource']
                second_job = json.loads((second_blend.parent.parent/'job.json').read_bytes())
                second_report = json.loads((project/second['report']).read_bytes())
                check(job['scene'] == second_job['scene'], name+': repeat native semantic geometry is deterministic')
                check({m['id']: m['geometrySha256'] for m in report['meshes']} == {m['id']: m['geometrySha256'] for m in second_report['meshes']}, name+': repeat actual Blender geometry is deterministic')
                buildings[name]['repeatBuild'] = second
            if not args.no_render:
                reference(blend)
                render_dir = project/'state'/'views'/asset
                render_dir.mkdir(parents=True)
                render_job = {'schemaVersion': 'bubo-floor-plan-blender-review-job/v1', 'workspaceRoot': str(project),
                              'source': str(blend), 'sourceSha256': sha(blend), 'outputDirectory': str(render_dir/'output'),
                              'bundle': {file: sha(runtime/'resources/floor-plan-to-model/blender'/file) for file in ['review.py', 'admission.py']},
                              'limits': {'maxObjects': report['counts']['meshes']+report['counts']['anchors'],
                                         'maxVertices': plan['construction']['maxConstructionVertices'], 'maxTriangles': plan['budgets']['maxGeneratedTriangles'],
                                         'maxOutputBytes': plan['budgets']['maxExportBytes'], 'maxSourceBytes': plan['budgets']['maxExportBytes'],
                                         'maxJobBytes': 1048576, 'maxViews': len(plan['storeys'])+4, 'imageSize': 768},
                              'floors': [{'id': s['id'], 'elevation': s['elevation'], 'height': s['height']} for s in plan['storeys']]}
                job_path = render_dir/'job.json'
                job_path.write_text(json.dumps(render_job, indent=2)+'\n', encoding='utf-8')
                reference(job_path)
                command = ['dotnet', str(ROOT/'src/runtime/floor-plan-to-model/Tests/bin/Release/net10.0/Bubo.FloorPlan.Tests.dll'), '--review-construction-source', str(project), str(context_path), str(runtime), str(job_path), str(blender)]
                render = subprocess.run(command, cwd=ROOT, capture_output=True, text=True, encoding='utf-8', timeout=330)
                (work/f'render-{asset}.log').write_text(render.stdout+render.stderr, encoding='utf-8')
                check(render.returncode == 0, name+': actual independent source inspection/CPU views')
                inspected = json.loads((render_dir/'output/inspection.json').read_bytes())
                check(inspected['sourceIndependentlyLoaded'] and inspected['sourceUnchanged'] and not inspected['exportReimportVerified'], name+': independently loaded source stays unchanged, no GLB reimport claim')
                check(sha(blend) == render_job['sourceSha256'], name+': source bytes preserved through all renders')
                check(len(inspected['views']) == len(plan['storeys'])+4, name+': every declared floor plus exterior/roof/vertical cutaway')
                buildings[name]['inspection'] = str(render_dir/'output/inspection.json')
            save_summary('in-progress')
        check(not list(project.rglob('*.glb')) and not (project/'assets').exists(), 'no portable export or game promotion in construction stage')
        save_summary('passed')
        print(json.dumps({'status': 'passed', 'checks': len(checks), 'evidence': str(work/'SUMMARY.json')}), flush=True)
    except BaseException:
        save_summary('failed')
        print('Preserved failed actual Blender fixture at '+str(work), flush=True)
        raise


if __name__ == '__main__':
    main()
