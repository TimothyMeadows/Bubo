# Task: Add Bubo Iterative Inference Repair Loop

## Issue

https://github.com/TimothyMeadows/Bubo/issues/21

## Pull Request

https://github.com/TimothyMeadows/Bubo/pull/22

## Flow

- Type: goal
- Goal: Bubo Local/Cloud Coding Agent
- Branch: `feature/bubo-iterative-repair-loop`
- Base: `feature/bubo-config-loading`
- Depends on: PR #20 / `bubo-config-loading`
- Team size requested: 6

## Goal

Expand Bubo's one-shot inference action proposal into a bounded iterative loop that can feed observable tool results back into the inference provider and retry guarded actions within configured limits.

## Scope

- Use `AgentLimits.MaxIterations` for inference-generated action loops.
- Keep deterministic user-authored `bubo-actions` behavior unchanged.
- Keep all model-generated actions behind `ParseSingleFence`, model-safe tools, workspace guard, configured tool limits, and command timeout limits.
- Feed concise observable failure/success summaries into retry prompts.
- Stop on success, no actions, invalid model output, provider failure, unknown tool, or max iteration exhaustion.
- Preserve `OUTPUT.md`, `agent-debug.jsonl`, and `agent-transcript.md`.
- Add tests for retry after failed generated action, max iteration exhaustion, deterministic action non-regression, and transcript evidence.

## Out Of Scope

- Fully separate planner and coder model calls.
- Repository-wide file context planning.
- Enabling generic model-generated `run_command`.
- Direct llama.cpp decode/sampling completion.

## Acceptance Criteria

- A model-generated failed action can be followed by a second model response that succeeds within `maxIterations`.
- If every generated action attempt fails, Bubo stops after the configured iteration limit and reports the failure.
- Explicit `INPUT.md` `bubo-actions` still execute exactly once without invoking inference.
- Runtime transcripts record inference/tool iteration attempts and observations.
- Local validation passes: build, tests, format check, diff check, and a focused iterative-loop smoke test.

## Notes

- This task is stacked on PR #20 to reuse config loading and `MaxIterations` wiring before that PR merges.
- Docker is installed on this host, so live Docker sandbox smoke is now included in final validation.

## Validation

- `dotnet test tests/LocalAgent.Runtime.Tests/LocalAgent.Runtime.Tests.csproj --configuration Release --verbosity minimal` passed with 44 tests.
- `dotnet build Bubo.sln --configuration Release --no-restore` passed.
- Focused iterative-loop filter passed with 4 tests.
- Added a side-effect auditability regression test after lane-2 review found that max-iteration exhaustion could under-report prior partial writes.
- `dotnet test tests/LocalAgent.Runtime.Tests/LocalAgent.Runtime.Tests.csproj --configuration Release --filter "FullyQualifiedName~RunAsyncReportsPriorSideEffectsWhenInferenceIterationLimitIsReached|FullyQualifiedName~RunAsyncStopsAfterInferenceIterationLimit" --verbosity normal` passed with 2 tests.
- `dotnet build Bubo.sln --configuration Release --no-restore` passed after the auditability fix.
- `dotnet test Bubo.sln --configuration Release --no-build --verbosity minimal` passed with 82 tests.
- `dotnet test Bubo.sln --configuration Release --no-build --verbosity normal` passed with 81 tests before the auditability fix and the focused side-effect test was added.
- `dotnet format Bubo.sln --verify-no-changes --no-restore` passed after formatting one whitespace issue.
- `git diff --check` passed with line-ending normalization warnings only.
- `dotnet test Bubo.sln --configuration Release --no-build --verbosity minimal` passed with 82 tests after the auditability fix.
- Package validation passed for `LlamaCppSharp.Native`, `LlamaCppSharp`, and `LocalAgent.Cli`.
- `docker build --pull -f docker/bubo-sandbox/Dockerfile -t bubo-sandbox:local docker/bubo-sandbox` passed.
- `dotnet run --no-build --configuration Release --project src/LocalAgent.Cli/LocalAgent.Cli.csproj -- sandbox test --workspace .` passed and reported `git`, `gh`, and .NET from inside the sandbox container.

## Post-PR QA

- PR #22 was opened against `feature/bubo-config-loading`.
- GitHub Actions `dotnet` workflow run #16 passed for initial head `b29bfd4732ede5ffc7c0cc9d5a372583d2fda98b`.
- Post-PR QA evidence was posted to PR #22 and mirrored to issue #21 before the auditability follow-up.
- A lane-2 auditability risk was resolved before finalizing the task: max-iteration exhaustion now aggregates prior failed-attempt evidence, including partial side effects.
- Final local validation after the follow-up fix passed: build, full tests with 82 tests, format check, diff check, and package validation.
- Docker live sandbox smoke passed after Docker installation: the sandbox image built successfully and `bubo sandbox test` reported `git`, `gh`, and .NET from inside the container.
