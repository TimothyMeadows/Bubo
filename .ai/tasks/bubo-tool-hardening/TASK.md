# Harden Bubo workspace tools and patch flow

## Goal

Harden Bubo's workspace boundary and add guarded patch application tools so deterministic agent actions can make bounded edits without escaping the workspace.

## Scope

- Add `patch_file` support for bounded textual replacements inside the workspace.
- Add `git_apply_patch` support that applies unified diffs through the Docker sandbox.
- Strengthen workspace path handling for symlink or reparse-point escape attempts.
- Update docs and examples for the deterministic patch flow.
- Keep all command execution through Docker sandbox runners.
- Maintain goal-flow tracking and raise a task PR with post-PR QA.

## Assumptions

- Full autonomous planner/coder orchestration remains a later slice.
- Native llama.cpp runtime assets remain scaffolded until a native asset task populates and smoke-tests real binaries.
- `patch_file` can start with deterministic old/new text replacement rather than a complete unified-diff parser because `git_apply_patch` handles unified diffs.

## Work Instructions

1. Branch from current `main` after PR #14 is merged.
2. Add tests before or alongside each new tool behavior.
3. Keep direct host process execution out of agent-driven command tools.
4. Reject path traversal and symlink/reparse escapes for file tools.
5. Update `README.md`, `docs/security.md`, and examples as needed.
6. Validate with Release build/test, fixture smoke, format, and diff checks.

## Verification

- `dotnet restore Bubo.sln`
- `dotnet build Bubo.sln --configuration Release --no-restore`
- `dotnet test Bubo.sln --configuration Release --no-build --verbosity normal`
- `powershell -ExecutionPolicy Bypass -File .\scripts\run-e2e-fixture.ps1 -Configuration Release`
- `dotnet format Bubo.sln --verify-no-changes`
- `git diff --check`

## Review

- Added `patch_file` for bounded exact old/new text replacement.
- Added `git_apply_patch` with patch preflight and Docker-backed `git apply --check` / `git apply`.
- Registered patch tools in the default tool registry and updated `AgentRunner` file-change reporting.
- Hardened `WorkspaceGuard` to reject `.git` metadata paths and symlink/reparse-point target or parent segments for file tools.
- Hardened Docker mount argument construction to reject symlink/reparse-point mount sources.
- Normalized read/write/list/search/command working-directory tools onto the hardened guard helpers.
- Added tests for workspace hardening, patch tool success/failure behavior, registry reachability, sandbox-backed Git apply, and agent output reporting.
- Added deterministic patch examples and updated README/security/config docs.
- Validation passed locally:
  - `dotnet build Bubo.sln --configuration Release --no-restore`
  - `dotnet test Bubo.sln --configuration Release --no-build --verbosity normal` passed 44 tests.
  - `powershell -ExecutionPolicy Bypass -File .\scripts\run-e2e-fixture.ps1 -Configuration Release`
  - Live `git_apply_patch` fixture in a temporary Git workspace.
  - `dotnet format Bubo.sln --verify-no-changes`
  - `git diff --check`

## Issue

https://github.com/TimothyMeadows/Bubo/issues/15
