# Task: Add Bubo Configuration Loading

## Issue

https://github.com/TimothyMeadows/Bubo/issues/19

## Flow

- Type: goal
- Goal: Bubo Local/Cloud Coding Agent
- Branch: `feature/bubo-config-loading`
- Base: `main`
- Team size requested: 6

## Goal

Add external configuration loading so Bubo can configure local/cloud mode, planner/coder model profiles, sandbox policy, and runtime loop limits without code changes.

## Scope

- Add `--config <path>` support for `bubo run`.
- Load `<workspace>/bubo.config.json` by default when it exists.
- Preserve current no-config behavior, including no implicit GPU/model host mount from the CLI path.
- Let explicit CLI options remain deterministic; `--mode` overrides config mode when present.
- Merge config safely into `AgentRunConfig` using defaults for omitted values.
- Support model profiles under the existing documented `models.planner` and `models.coder` shape.
- Support sandbox and limits values used by the existing runtime.
- Update README/config docs/examples.
- Add unit and E2E tests for config parsing and runtime application.

## Out Of Scope

- Multi-turn planner/coder loops.
- New inference provider protocols.
- Auto-push or auto-merge.
- Installing or requiring host llama.cpp, Ollama, or llama CLI binaries.

## Acceptance Criteria

- `bubo run --config ./bubo.config.json` loads mode, model profiles, sandbox options, and limits.
- `bubo run` loads `<workspace>/bubo.config.json` automatically when present.
- Explicit `--mode` wins over config mode.
- Invalid config JSON or invalid enum values fail with a clear CLI error.
- Configured `limits.maxToolCalls` affects deterministic `bubo-actions` runs.
- No-config CLI behavior remains backward compatible.
- Documentation includes guided config examples for local and cloud mode.
- Local validation passes: build, tests, format check, diff check, and a config-driven CLI smoke/E2E run.

## Notes

- Issue #17 was closed after PR #18 merged; `.ai/tasks/OPEN_ISSUES.md` was synced to only track issue #19.
- Role resolution was run from `.opencaw` because the role resolver expects the OpenCaw mount as its working directory.
- Security review concluded that workspace-default config must be treated as untrusted repository content. Bubo now requires explicit `--config` before accepting sandbox policy such as network, GPU, Docker image, or model mount settings.
- Docker is not installed on this host, so live Docker sandbox smoke is blocked locally. Docker command behavior remains covered by unit tests.

## Validation

- `dotnet test tests/LocalAgent.Cli.Tests/LocalAgent.Cli.Tests.csproj --configuration Release --verbosity minimal` passed with 24 tests.
- `dotnet test tests/LocalAgent.Runtime.Tests/LocalAgent.Runtime.Tests.csproj --configuration Release --verbosity minimal` passed with 39 tests.
- `dotnet build Bubo.sln --configuration Release --no-restore` passed.
- `dotnet test Bubo.sln --configuration Release --no-build --verbosity normal` passed with 76 tests.
- `dotnet format Bubo.sln --verify-no-changes --no-restore` passed.
- `git diff --check` passed; Git reported line-ending normalization warnings only.
- Config-driven CLI smoke passed with workspace-default `bubo.config.json` and deterministic `write_file` action.
- `dotnet pack src/LocalAgent.Cli/LocalAgent.Cli.csproj --configuration Release --no-build --output artifacts/packages` passed.

## Post-PR QA

- PR: https://github.com/TimothyMeadows/Bubo/pull/20
- QA comment posted on PR #20.
- GitHub Actions `dotnet` workflow run #14 passed for head `221e565483e665b8e179d3944d4d2a2fe26f2c83`.
