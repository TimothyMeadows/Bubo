"""Opt-in actual Windows native limits on owned synthetic export fixtures.

Run after lifecycle QA. Failed attempts remain in history; accepted source and
package bytes are never changed. Existing installations are used as-is.
"""
from pathlib import Path
import argparse
import hashlib
import json
import os
import re
import runpy
import subprocess
import tempfile
import time

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--cli', type=Path, default=ROOT/'artifacts/bubo/bubo.dll')
    parser.add_argument('--summary', required=True, type=Path)
    args = parser.parse_args()
    assert os.name == 'nt', 'Actual owned committed-memory enforcement requires Windows; no unsupported-platform success'
    owned = runpy.run_path(str(ROOT/'tests/test-floor-plan-export-lifecycle.py'))['owned_path']
    summary = owned(args.summary, ROOT/'.bubo/fpe')
    source = json.loads(summary.read_bytes()); assert source['status'] == 'passed'
    project = owned(source['project'], ROOT/'.bubo/fpc')
    cli = args.cli.resolve(strict=True)
    context_path = owned(project/'context.json', project)
    context = json.loads(context_path.read_bytes())
    parent = owned(ROOT/'.bubo/fpe-guards', ROOT/'.bubo', exists=False); parent.mkdir(parents=True, exist_ok=True)
    run = Path(tempfile.mkdtemp(prefix='r-', dir=parent))
    contexts = Path(tempfile.mkdtemp(prefix='native-guards-', dir=owned(project/'state', project)))
    records, checks = [], []
    sha = lambda path: hashlib.sha256(path.read_bytes()).hexdigest()

    def save(status):
        (run/'SUMMARY.json').write_text(json.dumps({'status': status, 'sourceSummary': str(summary), 'sourceSummarySha256': sha(summary),
            'records': records, 'checks': checks,
            'scope': 'Actual Windows public CLI/native Blender limit failures on synthetic engineering fixtures; no measured OS peak or human/runtime acceptance. Failed attempts retained.'}, indent=2)+'\n', encoding='utf-8')

    def check(value, label):
        assert value, label
        checks.append(label); print('PASS:', label, flush=True)

    def owned_blender():
        literal = str(project).replace("'", "''")
        command = "Get-CimInstance Win32_Process -Filter \"Name='blender.exe'\" | Where-Object { $_.CommandLine -and $_.CommandLine.Contains('"+literal+"') } | Select-Object ProcessId,CommandLine | ConvertTo-Json -Compress"
        result = subprocess.run(['powershell', '-NoProfile', '-Command', command], capture_output=True, text=True, encoding='utf-8',
                                timeout=20, check=True, creationflags=subprocess.CREATE_NO_WINDOW)
        value = json.loads(result.stdout) if result.stdout.strip() else []
        return value if isinstance(value, list) else [value]

    try:
        check(not owned_blender(), 'no fixture-owned Blender exists before serialized guard checks')
        cases = [('review-deadline', 'multi-storey', 'review', {'timeoutSeconds': 8}, ['cancel', 'timeout']),
                 ('export-memory', 'single-storey', 'export', {'maxPrivateBytes': 67_108_864}, ['exited', 'memory', 'job']),
                 ('export-log', 'angled-courtyard', 'export', {'maxLogBytes': 1024}, ['log'])]
        for name, building_name, operation, limits, expected_errors in cases:
            building = source['buildings'][building_name]
            manifest = owned(project/building['manifest'], project)
            state_path = owned(manifest.parent/'exports.json', project)
            original_state = json.loads(state_path.read_bytes())
            original_package = owned(building['package'], project)
            original_package_sha = sha(original_package)
            guard_context = json.loads(json.dumps(context)); guard_context['budgets'].update(limits)
            selected_context = contexts/(name+'.json')
            selected_context.write_text(json.dumps(guard_context, indent=2)+'\n', encoding='utf-8')
            arguments = ['dotnet', str(cli), 'floor-plan-to-model', operation, '--api-version', '2', '--root', str(project),
                         '--host-context', str(selected_context), '--manifest', building['manifest'],
                         '--blender', context['tools'][0]['path']]
            if operation == 'export':
                arguments += ['--author', 'synthetic-native-guard-fixture']
            out, err = run/(name+'.stdout'), run/(name+'.stderr')
            observed = []
            started = time.monotonic()
            record = {'case': name, 'arguments': arguments, 'context': str(selected_context), 'contextSha256': sha(selected_context),
                      'observedOwnedBlender': observed, 'stdout': str(out), 'stderr': str(err)}
            records.append(record); save('in-progress')
            with out.open('wb') as stdout, err.open('wb') as stderr:
                process = subprocess.Popen(arguments, cwd=ROOT, stdout=stdout, stderr=stderr, creationflags=subprocess.CREATE_NO_WINDOW)
                try:
                    while process.poll() is None and time.monotonic()-started < 45:
                        observed.extend(owned_blender())
                        time.sleep(.2)
                    code = process.wait(timeout=20)
                except BaseException as error:
                    record['observationOrWaitError'] = type(error).__name__+': '+str(error)
                    raise
                finally:
                    if process.poll() is None:
                        # Retire only this exact owned CLI, including observation failures.
                        # Windows closes its native owned-job handles on process exit.
                        record['forcedCliRetirement'] = True
                        process.terminate()
                        try:
                            process.wait(timeout=20)
                        except subprocess.TimeoutExpired:
                            process.kill(); process.wait(timeout=20)
                    record['exitCode'] = process.returncode
                    record['elapsedSeconds'] = time.monotonic()-started
                    save('in-progress')
            record['ownedBlenderImmediatelyAfterCli'] = owned_blender()
            check(not record['ownedBlenderImmediatelyAfterCli'], name+': owned Blender already retired at first post-CLI observation')
            result = json.loads(out.read_bytes())
            record['result'] = result; save('in-progress')
            check(code != 0 and result['workflow'] == 'floor-plan-to-model' and result['operation'] == operation
                  and result['status'] == 'failed' and any(text in result['error'].lower() for text in expected_errors), name+': actual selected failure family')
            state = json.loads(state_path.read_bytes()); key = 'views' if operation == 'review' else 'exports'
            check(len(state[key]) == len(original_state[key])+1 and state[key][-1]['status'] == 'failed', name+': latest failed native attempt retained')
            attempt = state[key][-1]; record['failedAttempt'] = attempt
            attempt_dir = owned(project/attempt['directory'], project)
            record['files'] = [{'path': str(p.relative_to(project)), 'bytes': p.stat().st_size, 'sha256': sha(p)} for p in attempt_dir.rglob('*') if p.is_file()]
            check(any(p['path'].endswith('job.json') for p in record['files']), name+': exact native job was saved before failure')
            native_log = owned(attempt_dir/('native.log' if operation == 'review' else 'produce/native.log'), project)
            native_text = native_log.read_text(encoding='utf-8')
            policy = 'resource-policy: windows-owned-job-aggregate-committed-memory maxPrivateBytes='+str(guard_context['budgets']['maxPrivateBytes'])+';'
            check(native_text.startswith(policy), name+': log proves child start and configured owned-job assignment')
            if name == 'export-memory':
                exit_match = re.search(r'Native process exited (-?\d+)', result['error'])
                record['memoryFailureEvidence'] = {'policy': policy, 'nativeExit': int(exit_match[1]) if exit_match else None,
                    'basis': 'Same previously successful executable/profile; selected64MiB owned-job policy and native failure. Resident/GPU/OS peak not measured.'}
                check((exit_match is not None and int(exit_match[1]) < 0) or 'MemoryError' in native_text,
                      name+': native Windows startup failure or explicit memory failure under selected cap')
            if name == 'export-log':
                check(native_log.stat().st_size <= guard_context['budgets']['maxLogBytes'], name+': retained log respects selected byte ceiling')
            if operation == 'review':
                check(bool(observed), name+': actual Blender child observed before cancellation')
            check(sha(original_package) == original_package_sha, name+': prior sealed package bytes unchanged')
            status_call = subprocess.run(['dotnet', str(cli), 'floor-plan-to-model', 'status', '--api-version', '2', '--root', str(project),
                '--host-context', str(context_path), '--manifest', building['manifest']], capture_output=True, text=True, encoding='utf-8', timeout=180, check=True)
            status = json.loads(status_call.stdout)['data']['export']; record['statusAfter'] = status
            check(not status['currentViewsValid'] and not status['humanModelAccepted'] and not status['runtimeAcceptance']
                  and status['currentExportValid'] == (operation == 'review'), name+': no earlier-success fallback or acceptance')
            for _ in range(5):
                remaining = owned_blender()
                if not remaining:
                    break
                time.sleep(.2)
            record['remainingOwnedBlender'] = remaining
            check(not remaining, name+': no owned Blender process survives retirement')
            save('in-progress')
        save('passed'); print(json.dumps({'status': 'passed', 'checks': len(checks), 'evidence': str(run/'SUMMARY.json')}), flush=True)
    except BaseException:
        save('failed'); raise


if __name__ == '__main__':
    main()
