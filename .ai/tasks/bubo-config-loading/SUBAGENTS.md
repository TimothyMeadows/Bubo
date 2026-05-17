# Subagent Plan: bubo-config-loading

## Capacity

- Requested: 6
- Effective lanes: 6
- Reason: one main implementation/integration lane plus five read-only specialist lanes. Implementation edits stay with the main agent to avoid conflicting writes in shared CLI/runtime files.

## Rules

- Resolve each lane role with `./commands/resolve-role.sh` before delegation.
- Use `explorer` for read-only lanes and `worker` for implementation lanes when Codex subagents are available.
- Worker lanes must declare disjoint write sets.
- Keep the main agent responsible for orchestration, critical-path blockers, integration, final verification, and user communication.

## Lanes

### lane-1

- Role: `computer-science/project-manager`
- Agent type: default
- Status: completed
- Scope: own implementation, integration, validation, PR creation, post-PR QA, and task ledger updates.
- Write set: all task implementation files, docs, tests, and `.ai` task/goal artifacts.
- Dependencies: none
- Expected output: merged implementation ready for PR with validation evidence.
- Verification: build, tests, format check, diff check, CLI smoke, PR QA.

### lane-2

- Role: `computer-science/backend-architect`
- Agent type: explorer
- Status: completed
- Scope: review current CLI, config, and runtime construction seams for the smallest robust config-loading design.
- Write set: none
- Dependencies: none
- Expected output: recommended design, risky coupling points, and suggested tests.
- Verification: cite relevant files/classes inspected.

### lane-3

- Role: `computer-science/security-engineer`
- Agent type: explorer
- Status: completed
- Scope: review config trust boundaries, sandbox option merging, enum parsing, and path handling risks.
- Write set: none
- Dependencies: none
- Expected output: security checklist for config loading and validation.
- Verification: cite mitigations and remaining risks.

### lane-4

- Role: `computer-science/qa-engineer`
- Agent type: explorer
- Status: completed
- Scope: identify regression and E2E tests for config discovery, CLI override precedence, invalid config, and runtime limits.
- Write set: none
- Dependencies: none
- Expected output: test matrix and high-value assertions.
- Verification: map tests to acceptance criteria.

### lane-5

- Role: `computer-science/technical-writer`
- Agent type: explorer
- Status: completed
- Scope: inspect README/config docs and recommend concise guided examples for `bubo.config.json`, local mode, and cloud mode.
- Write set: none
- Dependencies: none
- Expected output: doc update outline and wording risks.
- Verification: cite existing doc locations.

### lane-6

- Role: `computer-science/devops-automator`
- Agent type: explorer
- Status: completed
- Scope: inspect packaging/CI/sandbox command implications of config loading.
- Write set: none
- Dependencies: none
- Expected output: validation commands, packaging concerns, and CI risk notes.
- Verification: cite relevant project/CI files.

## Integration

- Merge order: main agent integrates explorer recommendations after initial implementation design, then validates full solution.
- Conflict risks: low because only the main agent writes files.
- Final verification: `dotnet build`, `dotnet test`, `dotnet format --verify-no-changes`, `git diff --check`, and a config-driven CLI smoke/E2E run.

## Results

### Result: lane-2 - completed - 2026-05-17T00:53:59Z
- Summary source: `../.ai/tasks/bubo-config-loading/lane-results/lane-2-summary.txt`

```text

### Result: lane-3 - completed - 2026-05-17T00:53:59Z
- Summary source: `../.ai/tasks/bubo-config-loading/lane-results/lane-3-summary.txt`

```text
Security review completed.

Findings:
- Repo-local workspace config must be treated as untrusted repository content.
- Workspace-default config should not be able to enable network, GPU, arbitrary images, host model mounts, or disabled Docker hardening.
- Configured runtime limits should not raise built-in safety ceilings or disable command timeouts.
- Config paths and host paths should reject symlink or reparse-point traversal risks.

Implementation impact:
- Workspace-default bubo.config.json can configurBackend architecture review completed.

Findings:
- Keep config loading CLI-local and pass one effective AgentRunConfig into AgentRunner.
- Use precedence: CLI-safe defaults, workspace bubo.config.json, explicit --config, explicit CLI flags.
- Keep --mode override aligned with AgentRunConfig.Mode before constructing the runner.
- Do not honor configured workspace/input/output/cache/container working directory host paths.

Implementation impact:
- Program now passes an effective config with Mode set to the ee mode, model profiles, and limits, but not sandbox policy.
- Explicit --config is required for trusted sandbox policy.
- Limits are bounded, maxCommandSeconds must be positive, Docker hardening cannot be disabled, and config path reparse points are rejected.

Files changed by lane: None.
ffective CLI/config mode.
- AgentConfigLoader rejects unsupported host mount path overrides.

Files changed by lane: None.
```
```

### Result: lane-5 - completed - 2026-05-17T00:53:59Z
- Summary source: `../.ai/tasks/bubo-config-loading/lane-results/lane-5-summary.txt`

```text

### Result: lane-4 - completed - 2026-05-17T00:53:59Z
- Summary source: `../.ai/tasks/bubo-config-loading/lane-results/lane-4-summary.txt`

```text

### Result: lane-6 - completed - 2026-05-17T00:53:59Z
- Summary source: `../.ai/tasks/bubo-config-loading/lane-results/lane-6-summary.txt`

```text
Technical writing review completed.

Findings:
- Docs needed explicit config discovery, precedence, safe workspace config behavior, trusted sandbox examples, and cloud mode examples.
- Avoid claiming codex-cli provider-specific model config until implemented.
- Keep network names in lower-case or kebab-case.

Implementation impact:
- README, docs/configuration.md, docs/security.md, examples/README.md, and CLI package README now document config loading and the trust boundary.
- examples/bubo.config.json is workspace-safe.
- examples/bubo.trusted.config.json shows explicit trusted sandbox policy.

Files changed by lane: None.
```
QA review completed.

Findings:
- Highest-value coverage is config discovery, explicit --config, mode precedence, invalid config, runtime limit enforcement, and documented example parsing.
- Program E2E tests should prove workspace-default config affects deterministic bubo-actions runs.
- Runtime tests should prove configured coder profile reaches inference.

Implementation impact:
- Added AgentConfigLoader unit tests for defaults, explicit config, invalid values, safety rejection, unknown JSON members, timeout disable prevention, and examples.
- Added Program.Main E2E tests for workspace config discovery, mode precedence, config-selected mode, and invalid config.
- Added runtime test for configured coder profile propagation.

Files changed by lane: None.
DevOps automation review completed.

Findings:
- New config loader files must be tracked before PR creation.
- No new NuGet package is needed because System.Text.Json is sufficient.
- CI does not run Docker smoke locally, so local validation should include build, test, format, diff check, package, and note Docker availability.
- examples should use bubo-sandbox:local instead of latest.

Implementation impact:
- Package validation was run for LocalAgent.Cli.
- Docker was checked and is not installed on this host, so live sandbox smoke is recorded as blocked.
- Trusted example config uses bubo-sandbox:local.

Files changed by lane: None.
```
```
