"""Opt-in fault injection into owned synthetic export fixtures; never production assets.

Original bytes are restored after each deliberate mutation; each CLI result and
before/after identity is retained separately. No Blender is launched here.
"""
from pathlib import Path
import argparse
import copy
import hashlib
import json
import os
import stat
import subprocess
import tempfile
import uuid

ROOT = Path(__file__).resolve().parents[1]


def owned_path(value, scope, *, exists=True):
    """Reject lexical escape and links before resolving a direct fixture write."""
    path = Path(os.path.abspath(value))
    scope = Path(os.path.abspath(scope))
    assert path.is_relative_to(scope), 'Fixture path escapes its owned root'
    current = scope
    for part in ('', *path.relative_to(scope).parts):
        if part:
            current /= part
        try:
            info = current.lstat()
        except FileNotFoundError:
            if not exists:
                continue
            raise
        assert not stat.S_ISLNK(info.st_mode) and not getattr(info, 'st_file_attributes', 0) & 0x400, 'Linked fixture path'
    assert path.resolve(strict=exists).is_relative_to(scope)
    return path


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--cli', type=Path, default=ROOT/'artifacts/bubo/bubo.dll')
    parser.add_argument('--summary', required=True, type=Path)
    args = parser.parse_args()
    cli = args.cli.resolve(strict=True)
    summary = owned_path(args.summary, ROOT/'.bubo/fpe')
    source = json.loads(summary.read_bytes())
    assert source['status'] == 'passed'
    project = owned_path(source['project'], ROOT/'.bubo/fpc')
    parent = ROOT/'.bubo/fpe-lifecycle'; parent.mkdir(parents=True, exist_ok=True)
    run = Path(tempfile.mkdtemp(prefix='r-', dir=parent))
    checks, mutations, calls = [], [], []
    sha = lambda data: hashlib.sha256(data).hexdigest()
    manifest = next(iter(source['buildings'].values()))['manifest']
    state_path = owned_path(project/manifest, project)
    assert state_path.name == 'manifest.json' and state_path.is_relative_to(project/'state/floor-plan-to-model')
    exports_path = owned_path(state_path.parent/'exports.json', project)
    original_state = json.loads(exports_path.read_bytes())
    exported, views = original_state['exports'][-1], original_state['views'][-1]
    assert exported['status'] == views['status'] == 'complete'
    # Resolve the actual sealed leaf from the immutable successful harness receipt.
    package_dir = owned_path(Path(next(iter(source['buildings'].values()))['package']).parent, project)
    assert package_dir == owned_path(project/exported['directory']/'package', project)
    context_path = owned_path(project/'context.json', project)
    common = ['--api-version', '2', '--root', str(project), '--host-context', str(context_path), '--manifest', manifest]

    def save(status):
        (run/'SUMMARY.json').write_text(json.dumps({'status': status, 'checks': checks, 'mutations': mutations,
            'sourceSummary': str(summary), 'sourceSummarySha256': sha(summary.read_bytes()),
            'scope': 'Actual public CLI failure/freshness admission over owned synthetic fixture; no new human/model/runtime acceptance or Blender execution.'}, indent=2)+'\n', encoding='utf-8')

    def check(value, label):
        assert value, label
        checks.append(label); print('PASS:', label, flush=True)

    def invoke(expect_success=True, operation='status', extra=(), errors=()):
        try:
            result = subprocess.run(['dotnet', str(cli), 'floor-plan-to-model', operation, *common, *extra], cwd=ROOT,
                                    capture_output=True, text=True, encoding='utf-8', timeout=180)
        except subprocess.TimeoutExpired as error:
            decode = lambda value: value.decode('utf-8', errors='replace') if isinstance(value, bytes) else value
            calls.append({'operation': operation, 'extra': extra, 'timeoutSeconds': 180,
                          'stdout': decode(error.stdout), 'stderr': decode(error.stderr)})
            (run/f'call-{len(calls):03d}.json').write_text(json.dumps(calls[-1], indent=2)+'\n', encoding='utf-8')
            raise
        calls.append({'operation': operation, 'extra': extra, 'exitCode': result.returncode, 'stdout': result.stdout, 'stderr': result.stderr})
        (run/f'call-{len(calls):03d}.json').write_text(json.dumps(calls[-1], indent=2)+'\n', encoding='utf-8')
        value = json.loads(result.stdout)
        check((result.returncode == 0) == expect_success, 'status succeeds' if expect_success else 'tampered evidence rejected by actual CLI')
        assert value['schemaVersion'] == 'bubo-execution-result/v1' and value['apiVersion'] == 2
        assert value['workflow'] == 'floor-plan-to-model' and value['operation'] == operation
        if not expect_success:
            assert value['status'] == 'failed' and errors and any(text.lower() in value['error'].lower() for text in errors), 'Unexpected failure family: '+value.get('error', '')
        data = value.get('data', value)
        return data.get('export') if expect_success and operation == 'status' else data

    def restored_status():
        value = invoke()
        check(value['currentExportValid'] and value['currentViewsValid'] and not value['runtimeAcceptance'], 'restored complete fixture is current without runtime acceptance')
        return value

    def mutate_file(path, label):
        path = owned_path(path, project)
        original = path.read_bytes()
        assert original
        record = {'label': label, 'path': str(path), 'originalSha256': sha(original)}
        mutations.append(record)
        try:
            changed = bytearray(original)
            changed[len(changed)//2] ^= 1
            path.write_bytes(changed)
            assert path.stat().st_size == len(original)
            record['mutatedSha256'] = sha(path.read_bytes()); save('in-progress')
            invoke(False, errors=['changed', 'differs', 'hash', 'fingerprint', 'mismatch']); check(True, label+' (same byte length)')
        finally:
            path.write_bytes(original)
            record['restoredSha256'] = sha(path.read_bytes())
            assert record['restoredSha256'] == record['originalSha256']
            save('in-progress')

    try:
        restored_status()
        for key, latest, prefix, expected_export in [('exports', exported, 'export', False), ('views', views, 'export-views', True)]:
            for status in ['failed', 'running']:
                original = exports_path.read_bytes()
                injected = copy.deepcopy(original_state); attempt = copy.deepcopy(latest)
                attempt['directory'] = str(Path(latest['directory']).parent/(prefix+'-'+uuid.uuid4().hex)).replace('\\', '/')
                attempt['status'] = status; attempt['error'] = 'Explicit synthetic interruption/failure injection'
                injected[key].append(attempt)
                try:
                    exports_path.write_text(json.dumps(injected, indent=2)+'\n', encoding='utf-8')
                    value = invoke()
                    check(value['currentExportValid'] == expected_export and not value['currentViewsValid']
                          and not value['independentModelReviewed'] and not value['humanModelAccepted'],
                          'latest '+key+' '+status+' cannot fall back to older success')
                finally:
                    exports_path.write_bytes(original)
                restored_status()

        construction_dir = owned_path(project/exported['constructionDirectory'], project)
        candidates = [construction_dir/'job.json', construction_dir/'output/report.json', construction_dir/'output/building.blend',
                      package_dir/'package.json', package_dir/'visual.glb', package_dir/'collision.glb', package_dir/'semantics.json',
                      project/exported['directory']/'produce/output/producer-report.json',
                      project/exported['directory']/'verify-visual/output/verification.json',
                      project/exported['directory']/'verify-collision/output/verification.json',
                      project/views['directory']/'output/views.json',
                      project/views['directory']/'output'/views['artifacts'][0]['path']]
        # The source report's canonical name comes from its saved producer output.
        producer_job = json.loads((project/exported['directory']/'produce/job.json').read_bytes())
        candidates[1] = Path(producer_job['inputs']['constructionReport']['path'])
        candidates += [Path(producer_job['inputs']['plan']['path']), Path(producer_job['inputs']['constructionJob']['path'])]
        for path in dict.fromkeys(candidates):
            mutate_file(path, 'changed exact evidence: '+path.name)
        for name, directory in [('unlisted.bin', False), ('unlisted-directory', True)]:
            extra = owned_path(package_dir/name, project, exists=False)
            try:
                extra.mkdir() if directory else extra.write_bytes(b'undeclared')
                invoke(False, errors=['unexpected', 'missing files']); check(True, 'closed payload rejects '+name)
            finally:
                extra.rmdir() if directory else extra.unlink()
        if os.name == 'nt':
            for target in [package_dir, package_dir/'visual.glb']:
                stream = Path(str(target)+':qa-hidden')
                before = sha((package_dir/'visual.glb').read_bytes())
                try:
                    stream.write_bytes(b'not visible in the ordinary default stream')
                    check(sha((package_dir/'visual.glb').read_bytes()) == before, 'ADS leaves default file identity unchanged')
                    invoke(False, errors=['alternate data stream']); check(True, 'actual completed package rejects named stream on '+target.name)
                finally:
                    stream.unlink()
        current = restored_status()
        original_context, state_bytes = context_path.read_bytes(), exports_path.read_bytes()
        try:
            def record(kind, decision, reviewer, independent=None, expected=True, errors=()):
                evidence = owned_path(project/'state'/('synthetic-model-review-'+uuid.uuid4().hex+'.json'), project, exists=False)
                document = {'schemaVersion': 'bubo-floor-plan-model-review/v1', 'kind': kind, 'decision': decision,
                    'reviewer': reviewer, 'role': 'synthetic-protocol-fixture', 'human': kind == 'human-model',
                    'reason': 'Synthetic review assertion for executable gate testing only; no actual human model acceptance. '+uuid.uuid4().hex,
                    'packageSha256': exported['packageSha256'], 'visualVerificationSha256': exported['visualVerificationSha256'],
                    'collisionVerificationSha256': exported['collisionVerificationSha256'], 'viewsSha256': views['viewsSha256'],
                    'independentReviewSha256': independent, 'artifacts': current['reviewArtifacts']}
                evidence.write_text(json.dumps(document, indent=2)+'\n', encoding='utf-8')
                context = json.loads(context_path.read_bytes())
                context['references'].append({'path': evidence.relative_to(project).as_posix(), 'sha256': sha(evidence.read_bytes()), 'kind': 'review'})
                context_path.write_text(json.dumps(context, indent=2)+'\n', encoding='utf-8')
                result = invoke(expected, 'record', ['--kind', kind, '--reviewer', reviewer, '--evidence', evidence.relative_to(project).as_posix()],
                                errors=errors)
                return result, evidence, sha(evidence.read_bytes())

            record('human-model', 'approved', 'synthetic-human', expected=False, errors=['Expected lowercase SHA-256'])
            check(True, 'human model assertion requires current independent approval')
            record('independent-model', 'approved', exported['modelAuthor'], expected=False, errors=['Independent review must be separate'])
            check(True, 'sealed author cannot self-approve independent model review')
            first, first_path, independent_hash = record('independent-model', 'approved', 'synthetic-independent-reviewer')
            check(first['independentModelReviewed'] and not first['humanModelAccepted'], 'synthetic independent decision grants no human acceptance')
            human, _, _ = record('human-model', 'approved', 'synthetic-human', independent_hash)
            check(human['humanModelAccepted'] and not human['runtimeAcceptance'], 'synthetic exact-bound human gate exercises no runtime promotion')
            rejected, _, _ = record('independent-model', 'rejected', 'synthetic-independent-reviewer')
            check(not rejected['independentModelReviewed'] and not rejected['humanModelAccepted'], 'latest independent rejection overrides older approvals')
            replacement, replacement_path, replacement_hash = record('independent-model', 'approved', 'synthetic-independent-reviewer')
            check(replacement['independentModelReviewed'] and not replacement['humanModelAccepted'], 'new independent approval cannot inherit older human approval')
            record('human-model', 'approved', 'synthetic-human', independent_hash, expected=False, errors=['current independently approved review identity'])
            check(True, 'stale independent identity cannot approve a human model decision')
            mutate_file(replacement_path, 'submitted current independent review must remain exact')
            saved_state = json.loads(exports_path.read_bytes())
            mutate_file(project/saved_state['reviews'][-1]['evidence'], 'immutable current independent review snapshot must remain exact')
            human_rejected, _, _ = record('human-model', 'rejected', 'synthetic-human', replacement_hash)
            check(not human_rejected['humanModelAccepted'], 'explicit current human rejection remains a rejection')
        finally:
            exports_path.write_bytes(state_bytes)
            context_path.write_bytes(original_context)
        final = restored_status()
        check(not final['independentModelReviewed'] and not final['humanModelAccepted'], 'all synthetic decision injections restored; no real model acceptance recorded')
        check(not (project/'assets').exists(), 'fault checks never promote into a game')
        save('passed'); print(json.dumps({'status': 'passed', 'checks': len(checks), 'evidence': str(run/'SUMMARY.json')}), flush=True)
    except BaseException:
        save('failed'); raise


if __name__ == '__main__':
    main()
