"""Actual compiled CLI + fixed Blender action-v2 integration on original fixtures.

Select existing --cli, --blender, --seed (EffectsV2Tests output), and
--model-fixture (actual model-action Blender fixture project). No installation,
generation provider, authentic human approval, engine rendering or asset promotion.
"""
from pathlib import Path
import argparse
import datetime
import hashlib
import json
import shutil
import subprocess
import tempfile
import time

import host_fixture

ROOT = Path(__file__).resolve().parents[1]


def sha(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def read(path):
    return json.loads(Path(path).read_text(encoding='utf-8'))


def write(path, value):
    Path(path).parent.mkdir(parents=True, exist_ok=True)
    Path(path).write_text(json.dumps(value, indent=2) + '\n', encoding='utf-8')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('cli', 'blender', 'seed', 'model-fixture'):
        parser.add_argument('--' + name, required=True, type=Path)
    parser.add_argument('--output', type=Path, default=ROOT / '.bubo/action-v2-cli')
    args = parser.parse_args()
    for path in (args.cli, args.blender, args.seed / 'fx-seed-recipe.json', args.model_fixture / 'source/motion-package.json'):
        if not path.is_file():
            raise ValueError('Explicit existing prerequisite missing: ' + str(path))
    args.output = args.output.resolve()
    if not args.output.is_relative_to(ROOT):
        raise ValueError('Fixture output must remain in this native source checkout')
    args.output.mkdir(parents=True, exist_ok=True)
    project = Path(tempfile.mkdtemp(prefix='run-', dir=args.output))
    receipts = project / 'receipts'
    receipts.mkdir()
    calls = []
    for folder in ('inputs/fx', 'private/fx'):
        shutil.copytree(args.seed / folder, project / folder)
    shutil.copytree(args.model_fixture / 'source', project / 'source')
    engine = host_fixture.initialize(project)
    policy = read(project / host_fixture.POLICY)
    style_hash = sha(project / host_fixture.POLICY)
    recipe = read(args.seed / 'fx-seed-recipe.json')
    recipe['actions'][0]['steps'][0]['clip'] = 'cast'
    recipe['actions'][0]['cues'][0]['anchor'] = 'hand.cast'
    recipe['review']['resolution'] = [128, 128]
    recipe['review']['fps'] = 4
    recipe['budgets']['timeoutSeconds'] = 180
    recipe['budgets']['maxRuntimeBytes'] = 32 * 1024**2
    source = recipe['effectSources'][0]
    package = read(project / source['package']['path'])
    package['styleSha256'] = style_hash
    package['engine'] = {'id': 'THREEJS', **{key: engine[key] for key in ('contractSha256', 'definitionSha256', 'registrySha256')}}
    write(project / source['package']['path'], package)
    source['package']['sha256'] = sha(project / source['package']['path'])
    profile = read(project / source['profile']['path'])
    profile['nativePackageSha256'] = source['package']['sha256']
    write(project / source['profile']['path'], profile)
    source['profile']['sha256'] = sha(project / source['profile']['path'])
    production = read(project / source['productionReview']['path'])
    production['decision'] = 'pending'
    production['subjectSha256'] = '0' * 64
    write(project / source['productionReview']['path'], production)
    source['productionReview']['sha256'] = sha(project / source['productionReview']['path'])
    write(project / 'action-v2.json', recipe)
    motion_path = project / 'source/motion-package.json'
    motion = read(motion_path)
    motion['engine'] = engine
    write(motion_path, motion)
    source_review = '.hephaera/.cache/tasks/action-v2-cli/source-review.json'
    write(project / source_review, {'schemaVersion': 1, 'kind': 'source-motion', 'decision': 'approved', 'human': True,
        'reviewer': 'synthetic-protocol-human', 'reason': 'SYNTHETIC source assertion on original engineering fixture; no authentic human or runtime acceptance',
        'rights': {'authorized': True, 'license': 'Original controlled Blender engineering fixture'},
        'motionPackageSha256': sha(motion_path), 'modelSha256': sha(project / motion['model']['path']), 'rigId': motion['rig']['id'],
        'clips': [{'id': clip['id'], 'motionSha256': clip['motionSha256']} for clip in motion['clips']]})

    def context():
        refs = []
        for folder in ('inputs', 'private', 'source'):
            for path in sorted((project / folder).rglob('*')):
                if path.is_file():
                    relative = path.relative_to(project).as_posix()
                    kind = 'rights' if path.name == 'rights.json' else 'review' if path.name in ('review.json', 'production-evidence.json') else 'input'
                    refs.append({'path': relative, 'sha256': sha(path), 'kind': kind})
        for path, kind in [('action-v2.json', 'input'), (source_review, 'review'), (host_fixture.POLICY, 'policy')]:
            refs.append({'path': path, 'sha256': sha(project / path), 'kind': kind})
        for path in sorted(project.glob('fixture-engine-*.json')):
            refs.append({'path': path.name, 'sha256': sha(path), 'kind': 'policy'})
        value = {'schemaVersion': 'bubo-host-context/v1', 'projectRoot': str(project), 'task': 'action-v2-cli',
            'roots': {'state': '.hephaera/.cache/tasks/action-v2-cli', 'staging': '.art-staging', 'tools': [], 'inputs': ['inputs', 'private', 'source'], 'outputs': ['assets/models']},
            'policy': {'pipeline': policy['pipeline'], 'styleSha256': style_hash, 'engine': engine, 'libraries': []},
            'references': refs, 'tools': [{'path': str(args.blender.resolve()), 'sha256': sha(args.blender), 'kind': 'fixture-tool'}],
            'budgets': {'timeoutSeconds': 600, 'maxLogBytes': 32 * 1024**2}, 'permissions': {'provisioning': False, 'promotion': False}}
        write(project / 'host-context.json', value)

    def run(operation, *tail, success=True):
        context()
        argv = ['dotnet', str(args.cli.resolve()), 'model-to-action', operation, '--api-version', '2', '--root', str(project), '--host-context', 'host-context.json', *map(str, tail)]
        started = time.perf_counter()
        process = subprocess.run(argv, cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=600)
        prefix = receipts / (f'{len(calls):03d}-' + operation)
        prefix.with_suffix('.stdout.json').write_bytes(process.stdout)
        prefix.with_suffix('.stderr.log').write_bytes(process.stderr)
        value = json.loads(process.stdout)
        calls.append({'argv': argv, 'exitCode': process.returncode, 'elapsedSeconds': time.perf_counter() - started,
            'stdoutSha256': sha(prefix.with_suffix('.stdout.json')), 'stderrSha256': sha(prefix.with_suffix('.stderr.log'))})
        write(receipts / 'commands.json', calls)
        if (process.returncode == 0) != success:
            raise AssertionError(f'{operation}: {value}\n{process.stderr.decode(errors="replace")[-3000:]}')
        return value

    subjects = run('inspect-effects', '--recipe', 'action-v2.json')['data']
    assert subjects['accepted'] is False and subjects['subjects'][0]['accepted'] is False
    production['decision'] = 'approved'
    production['subjectSha256'] = subjects['subjects'][0]['subjectSha256']
    write(project / source['productionReview']['path'], production)
    source['productionReview']['sha256'] = sha(project / source['productionReview']['path'])
    write(project / 'action-v2.json', recipe)
    run('init', '--asset', 'caster', '--version', 'v2', '--actor', 'original-fixture-author', '--recipe', 'action-v2.json', '--motion-package', 'source/motion-package.json', '--source-review', source_review, '--blender', args.blender.resolve())
    manifest = '.hephaera/.cache/tasks/action-v2-cli/model-to-action/caster/v2/manifest.json'
    for operation in ('inspect', 'compose', 'export', 'review', 'validate', 'status'):
        run(operation, '--manifest', manifest)
    state = read(project / manifest)
    exported = read(project / state['stages']['export']['artifacts']['package'])
    independent = read(project / state['stages']['export']['artifacts']['independentVerification'])
    review = read(project / state['stages']['review']['artifacts']['reviewReport'])
    assert exported['schemaVersion'] == 'hephaera-action-package/v2' and exported['effectSources']
    assert review['cueRendering'] == 'motion-and-anchors-only' and review['actualEngineFX'] == 'not-rendered'
    assert not state['approvals']
    run('promote', '--manifest', manifest, '--destination', 'assets/models/forbidden', success=False)
    assert not (project / 'assets/models/forbidden').exists()
    bound = project / exported['effectSources'][0]['resources'][0]['path']
    before = bound.read_bytes()
    try:
        bound.write_bytes(before + b'drift')
        run('status', '--manifest', manifest, success=False)
    finally:
        bound.write_bytes(before)
    run('validate', '--manifest', manifest)
    report = {'schemaVersion': 'bubo-action-v2-cli-fixture/v1', 'recordedUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
        'project': str(project), 'actualCompiledCli': str(args.cli.resolve()), 'cliSha256': sha(args.cli), 'blenderSha256': sha(args.blender),
        'actualBlender': True, 'freshProcessVerification': independent, 'calls': calls,
        'humanRuntimeAcceptance': False, 'actualEngineFX': 'not-rendered', 'assetPromotion': False,
        'notes': 'Original measured geometry; source/production review assertions are explicitly synthetic protocol fixtures.'}
    write(project / 'SUMMARY.json', report)
    print('PASS actual CLI/Blender v2 integration:', project / 'SUMMARY.json')


if __name__ == '__main__':
    main()
