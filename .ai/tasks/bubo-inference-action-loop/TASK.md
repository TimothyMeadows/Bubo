# Add Bubo inference-driven action loop

## Goal

Connect Bubo's local/cloud inference providers to the runtime so tasks without an explicit `bubo-actions` fence can still produce guarded tool actions and auditable artifacts.

## Scope

- Preserve deterministic `bubo-actions` execution as the first-priority path.
- When no deterministic actions are present and an inference provider is configured, build a safe action-generation prompt from `INPUT.md` and the available tool registry.
- Parse only fenced `bubo-actions` JSON from model output.
- Execute model-proposed actions through the existing guarded tools, workspace guard, and Docker sandbox.
- Record provider events, prompt/response metadata, parsing failures, and no-action cases in debug/transcript artifacts.
- Wire CLI `--mode local|cloud` to local llama.cpp or cloud codex-cli providers through the same runtime abstraction.
- Add tests and docs for inference-proposed actions without requiring real model execution.

## Assumptions

- This slice is not a full autonomous multi-iteration planner/coder loop.
- Model output remains untrusted and must only affect the workspace through parsed, allowlisted tools.
- Local llama.cpp generation may still return an unavailable/no-action response until native decode/sampling bindings and assets are populated.
- Cloud mode should use the existing `codex-cli` provider shape without adding secrets or host mounts.

## Work Instructions

1. Keep implementation small and reviewable.
2. Add unit tests with fake inference providers before or alongside runtime changes.
3. Avoid exposing hidden reasoning in output/transcripts.
4. Keep all command/file execution behind existing tools.
5. Update README/security docs where status wording changes.
6. Validate with Release build/test, fixture smoke, format, and diff checks.

## Verification

- `dotnet build Bubo.sln --configuration Release --no-restore`
- `dotnet test Bubo.sln --configuration Release --no-build --verbosity normal`
- `powershell -ExecutionPolicy Bypass -File .\scripts\run-e2e-fixture.ps1 -Configuration Release`
- `dotnet format Bubo.sln --verify-no-changes`
- `git diff --check`

## Review

- Added one-shot inference action proposal when `INPUT.md` has no deterministic `bubo-actions` fence.
- Kept deterministic `bubo-actions` as the priority path.
- Wired CLI local/cloud modes to `LlamaCppInferenceProvider` and `CodexCliInferenceProvider`.
- Scoped cloud `codex-cli` execution to the requested workspace.
- Added `InferenceResponse.Success` so provider failures can stop safely while still writing artifacts.
- Added a model-safe registry for inference-proposed actions that excludes generic `run_command`.
- Enforced runtime patch/file-count limits instead of trusting model-supplied values.
- Added per-tool timeout cancellation through `MaxCommandSeconds` and kill-on-cancel for Docker child process trees.
- Hardened recursive list/search enumeration to skip symlink/reparse paths.
- Rejected multiple model-output `bubo-actions` fences.
- Updated README, security, configuration, and examples docs for the one-shot inference path.
- Validation passed locally:
  - `dotnet build Bubo.sln --configuration Release --no-restore`
  - `dotnet test Bubo.sln --configuration Release --no-build --verbosity normal` passed 60 tests.
  - `powershell -ExecutionPolicy Bypass -File .\scripts\run-e2e-fixture.ps1 -Configuration Release`
  - `dotnet format Bubo.sln --verify-no-changes`
  - `git diff --check`
- PR opened: https://github.com/TimothyMeadows/Bubo/pull/18
- Post-PR QA posted on PR #18 and mirrored to issue #17.
- GitHub Actions `dotnet` workflow passed.

## Issue

https://github.com/TimothyMeadows/Bubo/issues/17
