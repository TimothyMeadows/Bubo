"""Actual CPU-only public boundary checks; tiny synthetic data grants no model acceptance."""
from pathlib import Path
import argparse
import hashlib
import json
import shutil
import struct
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--cli', type=Path, required=True)
    cli = parser.parse_args().cli.resolve()
    temporary = ROOT / '.bubo/test-temp'
    temporary.mkdir(parents=True, exist_ok=True)
    checks = 0

    def check(condition, message):
        nonlocal checks
        assert condition, message
        checks += 1

    with tempfile.TemporaryDirectory(prefix='splat-cli-', dir=temporary) as working:
        project = Path(working) / 'project'
        inputs = project / 'inputs'
        inputs.mkdir(parents=True)
        policy = project / 'policy.json'
        policy.write_text('{"kind":"synthetic-CPU-only-policy"}\n', encoding='utf-8')

        def sha(path):
            return hashlib.sha256(path.read_bytes()).hexdigest()

        context = {
            'schemaVersion': 'bubo-host-context/v1', 'projectRoot': str(project), 'task': 'splat-contract',
            'roots': {'state': 'state', 'staging': 'staging', 'tools': ['tools'], 'inputs': ['inputs'], 'outputs': ['outputs']},
            'policy': {'pipeline': 'LOCAL', 'styleSha256': sha(policy), 'engine': {'effectiveEngine': 'THREEJS'}, 'libraries': []},
            'references': [{'path': 'policy.json', 'sha256': sha(policy), 'kind': 'policy'}],
            'tools': [], 'budgets': {'timeoutSeconds': 20, 'maxLogBytes': 1048576},
            'permissions': {'provisioning': False, 'promotion': False},
        }
        context_file = project / 'context.json'

        def save_context():
            context_file.write_text(json.dumps(context) + '\n', encoding='utf-8')

        save_context()
        common = ['--api-version', '2', '--root', str(project), '--host-context', str(context_file)]

        def snapshot():
            return {p.relative_to(project).as_posix(): sha(p) for p in project.rglob('*') if p.is_file()}

        def invoke(arguments, error=None, selected_cli=cli):
            before = snapshot()
            process = subprocess.run(['dotnet', str(selected_cli), *arguments], cwd=ROOT, capture_output=True, text=True, timeout=25)
            result = json.loads(process.stdout)
            check(snapshot() == before, 'Public command changed project files')
            check(not (project / 'state').exists() and not (project / 'staging').exists(), 'Created lifecycle state')
            if error is None:
                check(process.returncode == 0 and result.get('status') != 'failed', process.stdout + process.stderr)
            else:
                check(process.returncode != 0 and result.get('status') == 'failed', 'Expected JSON failure')
                check(error.lower() in process.stdout.lower(), 'Missing expected error: ' + error + ': ' + process.stdout)
            return result

        capabilities = invoke(['capabilities', '--api-version', '2'])
        check('image-to-splat' in capabilities['workflows'], 'Workflow not advertised')
        check(capabilities['splats']['operations'] == ['inspect', 'validate', 'setup', 'provision', 'generate', 'verify-package'], 'Wrong foundation operations')
        check(capabilities['splats']['generationAvailable'] and capabilities['splats']['provisioningExecutionAvailable'] and not any(capabilities['splats'][key] for key in ('nativeReadinessVerified', 'renderingAvailable', 'runtimeAcceptanceAvailable')), 'Implemented worker must not claim native qualification')
        setup = invoke(['image-to-splat', 'setup', *common])['data']
        check(setup['mode'] == 'dry-run' and setup['status'] == 'blocked' and not setup['gpuUsed'], 'Setup readiness overstated')
        check(not setup['filesWritten'] and not setup['licenseAcceptancePerformed'], 'Setup side effects advertised')
        check(len(setup['catalog']['weights']) == 5 and setup['catalog']['weightBytes'] == 3779190284, 'Model closure changed')
        check(len(setup['catalog']['environmentCandidates']['win-amd64']['artifacts']) == 16, 'Environment metadata incomplete')
        for operation, flags, error in [
            ('generate', [], 'Explicit --request required'),
            ('promote', [], 'Unsupported workflow/operation'),
            ('setup', ['--execute'], 'Explicit --request required'),
            ('inspect', ['--gpu', '0'], 'Unknown or inapplicable option'),
            ('setup', ['--load', 'true'], 'Unknown or inapplicable option'),
            ('setup', ['--manifest', 'anything'], 'Unknown or inapplicable option'),
        ]:
            invoke(['image-to-splat', operation, *common, *flags], error)

        names = 'x y z nx ny nz f_dc_0 f_dc_1 f_dc_2 opacity scale_0 scale_1 scale_2 rot_0 rot_1 rot_2 rot_3'.split()
        header = ('ply\nformat binary_little_endian 1.0\nelement vertex 1\n' + ''.join('property float ' + name + '\n' for name in names) + 'end_header\n').encode('ascii')
        model = inputs / 'object.ply'
        model.write_bytes(header + struct.pack('<17f', 1, 2, 3, 0, 0, 0, .1, .2, .3, 0, -1, -1, -1, 1, 0, 0, 0))
        budgets = dict(maxGaussians=131072, maxInputBytes=1048576, maxDecodedBytes=1048576, maxHeaderBytes=65536, timeoutSeconds=10, maxLogBytes=1048576, maxProcessMemoryBytes=134217728)
        request = dict(schemaVersion='bubo-splat-inspection/v1', styleSha256=sha(policy), input={'path': 'inputs/object.ply', 'sha256': sha(model)}, budgets=budgets)
        request_file = inputs / 'request.json'

        def save_request(value=None, text=None):
            request_file.write_text(text if text is not None else json.dumps(value or request), encoding='utf-8')
            context['references'] = context['references'][:1] + [{'path': 'inputs/request.json', 'sha256': sha(request_file), 'kind': 'input'}]
            save_context()

        save_request()
        result = invoke(['image-to-splat', 'inspect', *common, '--request', 'inputs/request.json'])['data']
        check(result['gaussianCount'] == 1 and result['sphericalHarmonicsDegree'] == 0 and result['decodedBytes'] == 68, 'Wrong Gaussian facts')
        check(result['minimum'] == result['maximum'] == [1, 2, 3] and result['technicalOnly'], 'Bounds or acceptance claim incorrect')
        context['policy']['pipeline'] = 'BLENDER'; save_context()
        invoke(['image-to-splat', 'inspect', *common, '--request', 'inputs/request.json'], 'explicit LOCAL')
        context['policy']['pipeline'] = 'LOCAL'; save_context()
        save_request({**request, 'unknown': True})
        invoke(['image-to-splat', 'inspect', *common, '--request', 'inputs/request.json'], 'could not be mapped')
        save_request({**request, 'input': {'path': 'inputs/object.ply'}})
        invoke(['image-to-splat', 'inspect', *common, '--request', 'inputs/request.json'], 'missing required')
        save_request({**request, 'input': None})
        invoke(['image-to-splat', 'inspect', *common, '--request', 'inputs/request.json'], 'Invalid splat inspection schema')
        save_request({**request, 'input': {**request['input'], 'Sha256': request['input']['sha256']}})
        invoke(['image-to-splat', 'inspect', *common, '--request', 'inputs/request.json'], 'could not be mapped')
        save_request({**request, 'budgets': {**budgets, 'unbounded': True}})
        invoke(['image-to-splat', 'inspect', *common, '--request', 'inputs/request.json'], 'could not be mapped')
        save_request(text=json.dumps(request)[:-1] + ',"schemaVersion":"duplicate"}')
        invoke(['image-to-splat', 'inspect', *common, '--request', 'inputs/request.json'], 'Duplicate JSON member')
        save_request({**request, 'input': {**request['input'], 'path': '../outside.ply'}})
        invoke(['image-to-splat', 'inspect', *common, '--request', 'inputs/request.json'], 'path')
        save_request({**request, 'input': {**request['input'], 'sha256': '0'*64}})
        invoke(['image-to-splat', 'inspect', *common, '--request', 'inputs/request.json'], 'hash differs')
        save_request({**request, 'budgets': {**budgets, 'maxInputBytes': 64}})
        invoke(['image-to-splat', 'inspect', *common, '--request', 'inputs/request.json'], 'byte bound')
        save_request({**request, 'styleSha256': '0'*64})
        invoke(['image-to-splat', 'inspect', *common, '--request', 'inputs/request.json'], 'STYLE identity')
        save_request()
        request_file.write_text('{}', encoding='utf-8')
        invoke(['image-to-splat', 'inspect', *common, '--request', 'inputs/request.json'], 'changed')

        image = inputs / 'source.png'
        image.write_bytes(b'synthetic undecoded image identity\n')
        generation_request = dict(schemaVersion='bubo-splat-request/v1', backend='triposplat', styleSha256=sha(policy), input={'path': 'inputs/source.png', 'sha256': sha(image)}, gaussianCounts=[32768, 131072], seed=42, budgets=budgets)
        save_request(generation_request)
        result = invoke(['image-to-splat', 'validate', *common, '--request', 'inputs/request.json'])['data']
        check(result['valid'] and not result['inputDecodingVerified'] and not result['generationAvailable'] and not result['gpuUsed'], 'Validation overstated execution')
        check(result['requestSha256'] == sha(request_file), 'Parsed request hash was not returned')
        save_request({**generation_request, 'gaussianCounts': [262144]})
        invoke(['image-to-splat', 'validate', *common, '--request', 'inputs/request.json'], 'Gaussian')
        artifact_payload = inputs / 'artifact.ply'
        artifact_payload.write_bytes(header.replace(b'element vertex 1\n', b'element vertex 32768\n') + model.read_bytes()[len(header):] * 32768)
        artifact = dict(schemaVersion='bubo-splat-artifact/v1', backend='triposplat', styleSha256=sha(policy), input=generation_request['input'],
                        payload={'path': 'inputs/artifact.ply', 'sha256': sha(artifact_payload)}, profile='triposplat-ply-sh0/v1', gaussianCount=32768,
                        coordinates='source-local', units='uncalibrated', scaleConfidence='unverified',
                        ancestry=dict(requestSha256='a'*64, sourceRevision='b'*40, modelRevision='c'*40, environmentSha256='d'*64,
                                      runtimeBuildSha256='e'*64, resourceCatalogSha256='f'*64, parentArtifactSha256=None),
                        budgets={**budgets, 'maxInputBytes': 8388608, 'maxDecodedBytes': 16777216})
        save_request(artifact)
        artifact_result = invoke(['image-to-splat', 'validate', *common, '--request', 'inputs/request.json'])['data']
        check(artifact_result['valid'] and artifact_result['technicalOnly'] and not artifact_result['ancestryVerified'], 'Artifact claims unverified construction ancestry')
        check(artifact_result['inspection']['gaussianCount'] == 32768 and artifact_result['requestSha256'] == sha(request_file), 'Artifact payload/descriptor identity mismatch')
        save_request({**artifact, 'gaussianCount': 131072})
        invoke(['image-to-splat', 'validate', *common, '--request', 'inputs/request.json'], 'differs from inspected')
        missing_parent = {k: v for k, v in artifact['ancestry'].items() if k != 'parentArtifactSha256'}
        save_request({**artifact, 'ancestry': missing_parent})
        invoke(['image-to-splat', 'validate', *common, '--request', 'inputs/request.json'], 'missing required')
        save_request({**artifact, 'units': 'metres'})
        invoke(['image-to-splat', 'validate', *common, '--request', 'inputs/request.json'], 'uncalibrated')
        setup_request = dict(schemaVersion='bubo-splat-provision/v1', task='splat-contract', installationRoot='tools/new-install', downloadRoot='tools/downloads',
                             bootstrapPython={'path': str(project / 'tools/bootstrap-python.exe'), 'sha256': 'a'*64},
                             runtimeSourceRoot='tools/existing-crt', noticeSourceRoot='tools/existing-notices', licenses=[{'path': 'inputs/licenses.json', 'sha256': 'b'*64}])
        save_request(setup_request)
        plan = invoke(['image-to-splat', 'provision', *common, '--request', 'inputs/request.json'])['data']
        check(plan['mode'] == 'dry-run' and not plan['gpuUsed'] and not plan['networkAccess'] and not plan['installation'], 'Provision dry-run performed setup')
        check(plan['downloadBytes'] > 7100000000 and plan['minimumFreeDiskBytes'] == 25769803776, 'Setup size/bounds omitted')
        invoke(['image-to-splat', 'provision', *common, '--request', 'inputs/request.json', '--execute'], 'Explicit provisioning permission')
        del context['roots']['outputs']
        save_context()
        plan_without_outputs = invoke(['image-to-splat', 'provision', *common, '--request', 'inputs/request.json'])['data']
        check(plan_without_outputs['mode'] == 'dry-run', 'Optional outputs absence broke dry-run')
        context['roots']['outputs'] = ['outputs']
        save_request({**setup_request, 'installationRoot': 'outputs/illegal-install'})
        invoke(['image-to-splat', 'provision', *common, '--request', 'inputs/request.json'], 'tool roots')
        registration = dict(schemaVersion='bubo-splat-installation-request/v1', task='splat-contract', receiptPath='state/install.json',
                            modelDirectory='tools/models', sourceDirectory='tools/source', environmentRoot='tools/environment', pythonBaseRoot='tools/base',
                            wheelhouseRoot='tools/wheels', pythonArchive='tools/downloads/python.tar.gz', noticeDirectory='tools/notices',
                            python={'path': 'tools/environment/Scripts/python.exe', 'sha256': 'a'*64}, licenses=[{'path': 'inputs/licenses.json', 'sha256': 'b'*64}],
                            componentReview={'path': 'inputs/component-review.json', 'sha256': 'c'*64})
        save_request(registration)
        registration_plan = invoke(['image-to-splat', 'setup', *common, '--request', 'inputs/request.json'])['data']
        check(registration_plan['mode'] == 'dry-run' and not registration_plan['installationVerified'] and not registration_plan['gpuUsed'], 'Registration dry-run overstated readiness')
        invoke(['image-to-splat', 'setup', *common, '--request', 'inputs/request.json', '--execute'], 'Explicit host provisioning permission')
        isolated = Path(working) / 'runtime'
        shutil.copytree(cli.parent, isolated)
        (isolated / 'resources/image-to-splat/catalog.json').write_bytes(b'{}')
        invoke(['capabilities', '--api-version', '2'], 'audited resource', isolated / cli.name)
    print('PASS:', checks, 'CPU-only Gaussian public CLI assertions; no native/model/GPU execution')


if __name__ == '__main__':
    main()
