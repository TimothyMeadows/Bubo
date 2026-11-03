"""Opt-in real public CLI and owned Blender part QA; fixture approval is synthetic."""
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
    parser.add_argument('--case', action='append', choices=['vehicle', 'device', 'vessel'])
    parser.add_argument('--repeat', action='store_true', help='Compare actual semantic geometry from two distinct attempts')
    args = parser.parse_args()
    cli, blender = args.cli.resolve(strict=True), args.blender.resolve(strict=True)
    parent = ROOT/'.bubo/sp'; parent.mkdir(parents=True, exist_ok=True)
    run = Path(tempfile.mkdtemp(prefix='r-', dir=parent))
    project = run/'p'; (project/'inputs').mkdir(parents=True)
    checks, calls, results = [], [], {}
    sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest()

    def save(status):
        (run/'SUMMARY.json').write_text(json.dumps({
            'status': status, 'checks': checks, 'project': str(project), 'cases': results,
            'scope': 'Actual public native CLI, bounded Blender construction and clean-process prototype reopening. Synthetic whole-design approvals only; no human model/runtime acceptance or promotion.'
        }, indent=2)+'\n', encoding='utf-8')

    def check(value, label):
        assert value, label
        checks.append(label); print('PASS:', label, flush=True)

    def invoke(operation, manifest=None, extra=(), expected=None):
        arguments = ['schematic-to-model', operation, '--api-version', '2', '--root', str(project), '--host-context', str(context_path)]
        if manifest:
            arguments += ['--manifest', manifest]
        arguments += list(extra)
        command = ['dotnet', str(cli)] if cli.suffix == '.dll' else [str(cli)]
        process = subprocess.run([*command, *arguments], cwd=ROOT, capture_output=True,
                                 text=True, encoding='utf-8', timeout=330)
        call = {'arguments': arguments, 'exitCode': process.returncode, 'stdout': process.stdout, 'stderr': process.stderr}
        calls.append(call); (run/f'call-{len(calls):03d}.json').write_text(json.dumps(call, indent=2)+'\n', encoding='utf-8')
        value = json.loads(process.stdout)
        check(process.returncode != 0 and expected in value.get('error', '') if expected else process.returncode == 0,
              'expected rejection: '+expected if expected else 'actual CLI: '+operation)
        return value.get('data', value)

    policy = project/'policy.json'
    policy.write_text('{"purpose":"owned schematic construction engineering QA; no asset acceptance"}\n', encoding='utf-8')
    context = {
        'schemaVersion': 'bubo-host-context/v1', 'projectRoot': str(project), 'task': 'qa',
        'roots': {'state': 'state', 'staging': 'staging', 'tools': ['tools'], 'inputs': ['inputs'], 'outputs': ['outputs']},
        'policy': {'pipeline': 'SCHEMATIC_TO_MODEL', 'styleSha256': 'a'*64,
                   'engine': {'effectiveEngine': 'THREEJS', 'selectionSource': 'default', 'definitionSha256': 'b'*64,
                              'registrySha256': 'c'*64, 'contractSha256': None}, 'libraries': []},
        'references': [{'path': 'policy.json', 'sha256': sha(policy), 'kind': 'policy'}],
        'tools': [{'path': str(blender), 'sha256': sha(blender), 'kind': 'tool'}],
        'budgets': {'timeoutSeconds': 300, 'maxLogBytes': 8*1024*1024, 'maxPrivateBytes': 4*1024**3},
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
    try:
        for name in args.case or ['vehicle', 'device', 'vessel']:
            source = project/'inputs'/f'{name}.json'
            source.write_bytes((ROOT/'resources/schematic-to-model/examples'/f'{name}.json').read_bytes())
            spec = json.loads(source.read_bytes()); reference(source)
            initialized = invoke('init', extra=['--asset', name, '--version', 'v1', '--design', f'inputs/{name}.json'])
            manifest = initialized['manifest']
            preview = invoke('preview', manifest)
            invoke('build-parts', manifest, ['--blender', str(blender)], 'human approval of the exact complete schematic design package')
            check(not (project/manifest).with_name('parts.json').exists(), name+': missing approval starts no native part attempt')
            review = {'schemaVersion': 'bubo-schematic-review/v1', 'kind': 'whole-assembly-design', 'decision': 'approved',
                      'human': True, 'reviewer': 'synthetic-real-blender-qa',
                      'reason': 'Synthetic protocol assertion for owned engineering fixtures; no real human design/model/runtime acceptance.',
                      'asset': name, 'version': 'v1', 'designSha256': preview['designSha256'],
                      'designFingerprint': preview['designFingerprint'], 'reviewedArtifacts': preview['artifacts']}
            evidence = project/'state'/f'{name}-review.json'
            evidence.write_text(json.dumps(review, indent=2)+'\n', encoding='utf-8'); reference(evidence, 'review')
            invoke('record', manifest, ['--kind', review['kind'], '--reviewer', review['reviewer'], '--evidence', evidence.relative_to(project).as_posix()])
            doctor = invoke('doctor', manifest, ['--blender', str(blender)])
            check(doctor['nativeReadiness'] == 'verified' and doctor['blenderVersion'].startswith('4.5.'), name+': actual registered Blender and fixed bundle verified')
            memory = context['budgets'].pop('maxPrivateBytes'); write_context()
            invoke('build-parts', manifest, ['--blender', str(blender)], 'explicit aggregate committed-memory ceiling')
            context['budgets']['maxPrivateBytes'] = memory; write_context()
            built = invoke('build-parts', manifest, ['--blender', str(blender)])
            check(built['currentBuildValid'] and built['independentReimport'] and built['definitions'] == len(spec['parts'])
                  and built['physicalOccurrences'] == len(spec['instances']), name+': every definition constructed before independently identified occurrences')
            check(not any(built[k] for k in ['assemblyAvailable', 'motionProofAvailable', 'modelExportAvailable', 'humanModelAccepted', 'runtimeAcceptance']),
                  name+': prototypes grant no assembly, export or asset acceptance')
            folder = project/built['directory']; report = json.loads((project/built['report']).read_bytes())
            verified = json.loads((project/built['verification']).read_bytes())
            check(report == verified and report['instances'] == spec['instances'], name+': independent fresh-process measurements and exact physical map agree')
            expected_files = {'parts-report.json', 'parts-verification.json'}
            payload_bytes, weighted = 0, 0
            for part in report['parts']:
                for key, digest in [('blendFile', 'blendSha256'), ('geometryFile', 'geometrySha256')]:
                    file = folder/part[key]; expected_files.add(part[key]); payload_bytes += file.stat().st_size
                    check(sha(file) == part[digest], name+': actual payload '+part[key])
                binary = (folder/part['geometryFile']).read_bytes()
                vertices, triangles = struct.unpack_from('<II', binary, 8)
                check(binary[:8] == b'BUBOPRT1' and len(binary) == 16+vertices*24+triangles*12
                      and vertices == part['vertexCount'] and triangles == part['triangleCount']
                      and part['manifold'] and part['volume'] > 0, name+': measured closed positive-volume part '+part['id'])
                weighted += triangles*sum(i['partId'] == part['id'] for i in spec['instances'])
            check({p.name for p in folder.iterdir()} == expected_files and payload_bytes == report['totalBytes']
                  and weighted == report['weightedTriangles'] <= spec['budgets']['maxGeneratedTriangles'], name+': complete inventory and physical-quantity weighted budgets')
            check(invoke('status', manifest)['parts']['currentBuildValid'], name+': current design/approval/job/tool/bundle/output freshness')
            results[name] = {'manifest': manifest, 'build': built, 'report': str(project/built['report']), 'verification': str(project/built['verification'])}
            save('in-progress')
            if args.repeat:
                repeated = invoke('build-parts', manifest, ['--blender', str(blender)])
                repeat_report = json.loads((project/repeated['report']).read_bytes())
                check(repeated['directory'] != built['directory'] and
                      {p['id']: p['geometrySha256'] for p in report['parts']} == {p['id']: p['geometrySha256'] for p in repeat_report['parts']},
                      name+': distinct immutable attempts reproduce semantic geometry')
                built = repeated; folder = project/built['directory']; report = repeat_report
                results[name]['repeatBuild'] = repeated
            target = folder/report['parts'][0]['geometryFile']; original = target.read_bytes()
            try:
                target.write_bytes(original+b'tamper')
                invoke('status', manifest, expected='source hash changed')
            finally:
                target.write_bytes(original)
            check(invoke('status', manifest)['parts']['currentBuildValid'], name+': restoring owned fault-injection bytes restores measured freshness')
            original = source.read_bytes()
            try:
                changed = json.loads(original); changed['name'] += ' changed'; source.write_text(json.dumps(changed)+'\n', encoding='utf-8'); reference(source)
                invoke('status', manifest, expected='Schematic changed')
            finally:
                source.write_bytes(original); reference(source)
            check(invoke('status', manifest)['parts']['currentBuildValid'], name+': retained source identity remains exact after fixture restoration')
            save('in-progress')
        check(not list(project.rglob('*.glb')) and not (project/'assets').exists(), 'no canonical model export or game asset promotion')
        save('passed'); print(json.dumps({'status': 'passed', 'checks': len(checks), 'evidence': str(run/'SUMMARY.json')}), flush=True)
    except BaseException:
        save('failed'); print('Preserved actual CLI fixture failure at '+str(run), flush=True); raise


if __name__ == '__main__':
    main()
