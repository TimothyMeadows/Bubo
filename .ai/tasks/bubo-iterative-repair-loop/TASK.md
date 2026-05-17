# Task: Add Bubo Iterative Inference Repair Loop

## Issue

https://github.com/TimothyMeadows/Bubo/issues/21

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
- Docker is not installed on this host, so live Docker sandbox smoke is blocked locally.

## Validation

- `dotnet test tests/LocalAgent.Runtime.Tests/LocalAgent.Runtime.Tests.csproj --configuration Release --verbosity minimal` passed with 44 tests.
- `dotnet build Bubo.sln --configuration Release --no-restore` passed.
- Focused iterative-loop filter passed with 4 tests.
- `dotnet test Bubo.sln --configuration Release --no-build --verbosity normal` passed with 81 tests.
- `dotnet format Bubo.sln --verify-no-changes --no-restore` passed after formatting one whitespace issue.
- `git diff --check` passed with line-ending normalization warnings only.
- `dotnet test Bubo.sln --configuration Release --no-build --verbosity minimal` passed with 81 tests after formatting.
- Package validation passed for `LlamaCppSharp.Native`, `LlamaCppSharp`, and `LocalAgent.Cli`.
