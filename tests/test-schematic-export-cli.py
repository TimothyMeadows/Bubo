"""Opt-in actual export/reimport/review QA. Synthetic fixture decisions never accept production assets."""
from pathlib import Path
import argparse
import hashlib
import json
import struct
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--cli', type=Path, default=ROOT/'artifacts/bubo/bubo.dll')
    parser.add_argument('--blender', type=Path, required=True)
    parser.add_argument('--case', action='append', choices=['vehicle', 'device', 'vessel', 'max-ids'])
    parser.add_argument('--prepared-summary', type=Path)
    args = parser.parse_args()
    cli, blender = args.cli.resolve(strict=True), args.blender.resolve(strict=True)
    parent = ROOT/'.bubo/se'; parent.mkdir(parents=True, exist_ok=True)
    run = Path(tempfile.mkdtemp(prefix='r-', dir=parent))
    checks, cases, calls = [], {}, []
    sha = lambda path: hashlib.sha256(path.read_bytes()).hexdigest()

    def check(value, label):
        assert value, label
        checks.append(label); print('PASS:', label, flush=True)

    def save(status):
        (run/'SUMMARY.json').write_text(json.dumps({'status': status, 'checks': checks, 'cases': cases,
            'preparation': str(preparation), 'project': str(project),
            'scope': 'Actual CLI and selected Blender: source construction, independent GLB import, whole diagnostic sampling and model review views. Synthetic review protocol only; no human asset/runtime acceptance or promotion.'}, indent=2)+'\n', encoding='utf-8')

    if args.prepared_summary:
        preparation = args.prepared_summary.resolve(strict=True)
    else:
        prep_command = ['python', '-B', '-X', 'utf8', str(ROOT/'tests/test-schematic-assembly-cli.py'), '--cli', str(cli), '--blender', str(blender)]
        for name in args.case or ['vehicle', 'device', 'vessel', 'max-ids']:
            prep_command += ['--case', name]
        result = subprocess.run(prep_command, cwd=ROOT, capture_output=True, text=True, encoding='utf-8', timeout=7200)
        (run/'preparation.stdout.log').write_text(result.stdout, encoding='utf-8')
        (run/'preparation.stderr.log').write_text(result.stderr, encoding='utf-8')
        assert result.returncode == 0, 'Actual current source preparation failed; raw logs retained at '+str(run)
        summary = json.loads(result.stdout.strip().splitlines()[-1])
        preparation = Path(summary['evidence']).resolve(strict=True)
    prepared = json.loads(preparation.read_bytes())
    assert prepared['status'] == 'passed', 'Preparation must pass actual construction and sampled readiness'
    project = Path(prepared['project']).resolve(strict=True)
    project.relative_to(ROOT)
    context_path = project/'context.json'; context = json.loads(context_path.read_bytes())
    assert context['permissions'] == {'promotion': False, 'provisioning': False}

    def reference(path):
        relative = path.relative_to(project).as_posix()
        context['references'] = [r for r in context['references'] if r['path'] != relative]
        context['references'].append({'path': relative, 'sha256': sha(path), 'kind': 'review'})
        context_path.write_text(json.dumps(context, indent=2)+'\n', encoding='utf-8')

    def invoke(operation, manifest, extra=(), expected=None):
        command = ['dotnet', str(cli)] if cli.suffix == '.dll' else [str(cli)]
        command += ['schematic-to-model', operation, '--api-version', '2', '--root', str(project), '--host-context', str(context_path), '--manifest', manifest, *extra]
        result = subprocess.run(command, cwd=ROOT, capture_output=True, text=True, encoding='utf-8', timeout=930)
        calls.append({'command': command, 'exitCode': result.returncode, 'stdout': result.stdout, 'stderr': result.stderr})
        (run/f'call-{len(calls):03d}.json').write_text(json.dumps(calls[-1], indent=2)+'\n', encoding='utf-8')
        value = json.loads(result.stdout)
        check(result.returncode != 0 and expected in value.get('error', '') if expected else result.returncode == 0,
              'expected rejection: '+expected if expected else 'actual public CLI: '+operation)
        return value.get('data', value)

    def gltf(path):
        with path.open('rb') as stream:
            magic, version, length, json_length, chunk_type = struct.unpack('<5I', stream.read(20))
            assert magic == 0x46546C67 and version == 2 and length == path.stat().st_size and chunk_type == 0x4E4F534A
            return json.loads(stream.read(json_length))

    try:
        for name in args.case or list(prepared['cases']):
            manifest = prepared['cases'][name]['manifest']
            exported = invoke('export', manifest, ['--blender', str(blender), '--author', 'synthetic-export-author'])
            check(exported['currentExportValid'] and exported['portableVerified'] and not exported['currentViewsValid'] and not exported['humanModelAccepted'], name+': portable verification grants no model acceptance')
            package_path = project/exported['packageManifest']; package = json.loads(package_path.read_bytes()); directory = package_path.parent
            check(sha(package_path) == exported['packageSha256'] and all(sha(directory/f['path']) == f['sha256'] and (directory/f['path']).stat().st_size == f['bytes'] for f in package['files']), name+': complete sealed payload byte inventory')
            rest = gltf(directory/'assembly.glb'); diagnostic = gltf(directory/'diagnostic-motion/diagnostic.glb')
            check(not rest.get('animations') and len(diagnostic.get('animations', [])) > 0, name+': canonical rest and diagnostic motion remain separate')
            components = package['components']; nodes = {n['name']: n for n in rest['nodes']}
            design = json.loads((directory/'assembly-design.json').read_bytes())
            check({c['instanceId']: c['partId'] for c in components} == {i['id']: i['partId'] for i in design['instances']}
                  and len({c['node'] for c in components}) == len(components)
                  and all(c['node'] in nodes and 'mesh' in nodes[c['node']] for c in components), name+': every approved physical occurrence remains separately addressable')
            flexible = [c for c in components if c['kind'] == 'flexible']
            check(len({nodes[c['node']]['skin'] for c in flexible}) == len(flexible), name+': every flexible occurrence retains its independently owned skin')
            if name == 'max-ids':
                semantic = {a.get('extras', {}).get('bubo.clipId') for a in diagnostic['animations']}
                check({'range-'+'j'*80, 'cycle-'+'j'*80} <= semantic, 'full maximum-length semantic clip identities survive short portable animation names')
            for label in ['rest', 'diagnostic']:
                certificate = json.loads((project/exported[label+'Verification']).read_bytes())
                check(certificate['verifiedPoses'] > 0 and certificate['workChecks'] > 0 and certificate['report']['raw'] and certificate['report']['imported'], name+': raw and fresh imported '+label+' measured certificate')
            viewed = invoke('review', manifest, ['--blender', str(blender)])
            check(viewed['currentViewsValid'] and not viewed['independentModelReviewed'], name+': complete model views precede independent decisions')
            current = invoke('status', manifest)['export']
            check(current['currentExportValid'] and current['currentViewsValid'] and current['reviewArtifacts'], name+': status revalidates delivered files, motion and complete review inventory')
            model_hash = next(f['sha256'] for f in package['files'] if f['path'] == 'assembly.glb')
            review = {'schemaVersion': 'bubo-schematic-model-review/v1', 'kind': 'independent-model', 'decision': 'approved', 'reviewer': 'synthetic-independent-export-qa',
                'role': 'synthetic-fixture', 'independent': True, 'human': False, 'reason': 'Synthetic lifecycle assertion after automated real output verification; no human quality acceptance.',
                'modelSha256': model_hash, 'packageSha256': current['packageSha256'], 'restVerificationSha256': current['restVerificationSha256'],
                'diagnosticVerificationSha256': current['diagnosticVerificationSha256'], 'viewsSha256': current['viewsSha256'], 'independentReviewSha256': None,
                'reviewedArtifacts': current['reviewArtifacts']}
            evidence = project/'state'/f'{name}-model-review.json'; evidence.write_text(json.dumps(review, indent=2)+'\n', encoding='utf-8'); reference(evidence)
            recorded = invoke('record', manifest, ['--kind', review['kind'], '--reviewer', review['reviewer'], '--evidence', evidence.relative_to(project).as_posix()])
            check(recorded['independentModelReviewed'] and not recorded['humanModelAccepted'] and not recorded['runtimeAcceptance'] and recorded['mechanicalHandoff'], name+': independent review issues preserve-only handoff without human/runtime acceptance')
            handoff = json.loads((project/recorded['mechanicalHandoff']).read_bytes())
            check(handoff['bindings']['nodes'] == rest['nodes'] and handoff['bindings']['skins'] == rest.get('skins', []), name+': receiving handoff binds actual rest graph and skin identity')
            original = (directory/'assembly.glb').read_bytes()
            try:
                altered = bytearray(original); altered[-1] ^= 1; (directory/'assembly.glb').write_bytes(altered)
                invoke('status', manifest, expected='Sealed payload changed')
            finally:
                (directory/'assembly.glb').write_bytes(original)
            after = invoke('status', manifest)['export']
            check(after['independentModelReviewed'] and not after['humanModelAccepted'], name+': exact restored bytes preserve current evidence without automatic human acceptance')
            cases[name] = {'manifest': manifest, 'export': exported, 'review': viewed, 'record': recorded, 'status': after}
            save('in-progress')
        check(not (project/'assets').exists(), 'no runtime asset promotion')
        save('passed'); print(json.dumps({'status': 'passed', 'checks': len(checks), 'evidence': str(run/'SUMMARY.json')}), flush=True)
    except BaseException:
        save('failed'); print('Preserved failed actual export CLI fixture at '+str(run), flush=True); raise


if __name__ == '__main__':
    main()
