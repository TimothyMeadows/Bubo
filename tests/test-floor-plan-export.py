"""Opt-in actual construction/export/clean-import/view QA; review assertions stay synthetic."""
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
    parser.add_argument('--case', action='append', choices=['single-storey', 'multi-storey', 'angled-courtyard'])
    parser.add_argument('--no-render', action='store_true')
    parser.add_argument('--geometry-checks', type=int, default=4_000_000,
                        help='Finite full-export engineering allowance selected in each fresh plan before its synthetic design approval')
    args = parser.parse_args()
    parent = ROOT/'.bubo/fpe'; parent.mkdir(parents=True, exist_ok=True)
    run = Path(tempfile.mkdtemp(prefix='r-', dir=parent))
    cli, blender = args.cli.resolve(strict=True), args.blender.resolve(strict=True)
    assert 1 <= args.geometry_checks <= 5_000_000
    command = ['python', '-B', '-X', 'utf8', str(ROOT/'tests/test-floor-plan-construction.py'), '--cli', str(cli), '--blender', str(blender), '--no-render', '--repeat', '--geometry-checks', str(args.geometry_checks)]
    for case in args.case or ['single-storey', 'multi-storey', 'angled-courtyard']:
        command += ['--case', case]
    construction = subprocess.run(command, cwd=ROOT, capture_output=True, text=True, encoding='utf-8', timeout=1200)
    (run/'construction.log').write_text(construction.stdout+construction.stderr, encoding='utf-8')
    assert construction.returncode == 0, 'Construction fixture failed: '+str(run/'construction.log')
    receipt = json.loads(construction.stdout.splitlines()[-1])
    original = Path(receipt['evidence']).resolve(strict=True)
    assert original.is_relative_to(ROOT)
    source = json.loads(original.read_bytes())
    project = Path(source['project']); context = project/'context.json'
    common = ['--api-version', '2', '--root', str(project), '--host-context', str(context)]
    checks, calls, results = [], [], {}
    sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest()

    def save(status):
        result = {'status': status, 'checks': len(checks), 'constructionSummary': str(original), 'project': str(project), 'fixtureGeometryChecks': args.geometry_checks,
                  'buildings': results, 'scope': 'Actual Blender construction, sealed visual/collision export, independent fresh-process imports and optional complete views; synthetic design approvals, no human model/runtime acceptance or promotion.'}
        (run/'SUMMARY.json').write_text(json.dumps(result, indent=2)+'\n', encoding='utf-8')

    def check(value, label):
        assert value, label
        checks.append(label); print('PASS:', label, flush=True)

    def invoke(arguments, expected=None):
        result = subprocess.run(['dotnet', str(cli), 'floor-plan-to-model', *arguments, *common], cwd=ROOT,
                                capture_output=True, text=True, encoding='utf-8', timeout=330)
        call = {'arguments': arguments, 'exitCode': result.returncode, 'stdout': result.stdout, 'stderr': result.stderr}
        calls.append(call); (run/f'call-{len(calls):03d}.json').write_text(json.dumps(call, indent=2)+'\n', encoding='utf-8')
        value = json.loads(result.stdout)
        check(result.returncode != 0 and expected in value['error'] if expected else result.returncode == 0,
              ('expected rejection: '+expected) if expected else 'actual CLI: '+' '.join(arguments[:1]))
        return value.get('data', value)

    try:
        for name, building in source['buildings'].items():
            manifest = building['manifest']; plan = json.loads((project/manifest).read_bytes())
            plan = json.loads((project/plan['originalPlan']).read_bytes())
            invoke(['export', '--manifest', manifest, '--blender', str(blender)], 'Missing --author')
            exported = invoke(['export', '--manifest', manifest, '--blender', str(blender), '--author', 'synthetic-engineering-model-builder'])
            check(exported['currentExportValid'] and exported['portableVerified'] and not exported['currentViewsValid']
                  and not exported['humanModelAccepted'] and not exported['runtimeAcceptance'], name+': portable technical proof grants no model/runtime acceptance')
            package_path = project/exported['packageManifest']; package = json.loads(package_path.read_bytes()); folder = package_path.parent
            check(sha(package_path) == exported['packageSha256'], name+': exact external sealed package identity')
            check(package['ancestry']['modelAuthor'] == 'synthetic-engineering-model-builder', name+': creator identity is sealed for later independent review')
            expected = {f['path'] for f in package['files']} | {'package.json'}
            check({p.relative_to(folder).as_posix() for p in folder.rglob('*') if p.is_file()} == expected, name+': exact complete portable payload inventory')
            for file in package['files']:
                path = folder/file['path']; check(path.stat().st_size == file['bytes'] and sha(path) == file['sha256'], name+': immutable payload '+file['path'])
            current_build = building.get('repeatBuild', building['build'])
            check(sha(folder/'building.blend') == sha(project/current_build['editableSource']), name+': latest exact editable source bytes preserved')
            semantics = json.loads((folder/'semantics.json').read_bytes())
            check(semantics['plan'] == plan and semantics['clearanceBasis'] == 'construction-source', name+': exact approved dimensions and honest clearance provenance')
            attempt = folder.parent; verification = {}
            for representation in ['visual', 'collision']:
                glb = folder/(representation+'.glb'); header = glb.read_bytes()[:12]
                check(header[:4] == b'glTF' and int.from_bytes(header[4:8], 'little') == 2 and int.from_bytes(header[8:12], 'little') == glb.stat().st_size,
                      name+': actual self-contained '+representation+' GLB')
                path = attempt/('verify-'+representation)/'output/verification.json'; report = json.loads(path.read_bytes()); verification[representation] = str(path)
                measured = report['measured']
                check(report['representation'] == representation and measured['cleanProcessImport'] and measured['passed']
                      and not report['humanModelAccepted'] and not report['runtimeAccepted'], name+': actual separate fresh-process '+representation+' reimport')
                check(measured['raw']['maxWorldCoordinateError'] <= plan['numerics']['linearTolerance']
                      and measured['imported']['maxWorldCoordinateError'] <= plan['numerics']['linearTolerance'], name+': raw and actual imported dimensional tolerance '+representation)
            results[name] = {'manifest': manifest, 'package': str(package_path), 'packageSha256': exported['packageSha256'], 'verifications': verification}
            save('in-progress')
            if not args.no_render:
                reviewed = invoke(['review', '--manifest', manifest, '--blender', str(blender)])
                check(reviewed['currentViewsValid'] and not reviewed['independentModelReviewed'] and not reviewed['humanModelAccepted'], name+': complete views create no approval')
                views_path = project/reviewed['views']; views = json.loads(views_path.read_bytes())
                check(len(views['measured']['views']) == len(plan['storeys'])+len(plan['stairs'])+6, name+': every floor, four elevations, roof and all required sections')
                for image in views['measured']['views']:
                    check(sha(views_path.parent/image['path']) == image['sha256'], name+': review image '+image['path'])
                results[name]['views'] = str(views_path)
            status = invoke(['status', '--manifest', manifest])['export']
            check(status['currentExportValid'] and not status['humanModelAccepted'] and not status['runtimeAcceptance'], name+': fresh status retains separate acceptance gates')
            save('in-progress')
        check(not (project/'assets').exists(), 'no game asset copy or runtime promotion')
        save('passed'); print(json.dumps({'status': 'passed', 'checks': len(checks), 'evidence': str(run/'SUMMARY.json')}), flush=True)
    except BaseException:
        save('failed'); print('Preserved actual export failure at '+str(run), flush=True); raise


if __name__ == '__main__':
    main()
