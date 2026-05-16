# Bubo end-to-end hardening and packaging

## Goal

Add end-to-end fixture validation, CI, documentation, packaging scaffolding, and final security checklist for the Bubo v1 goal.

## Scope

- Add fixture repos/tasks for local no-op, file edit, and command execution flows.
- Add GitHub Actions for .NET 8 restore/build/test and package validation.
- Add native/package build workflow design for RID assets.
- Document local setup, Docker requirements, NVIDIA Container Toolkit, and codex-cli cloud mode.
- Generate final goal completion report after all task PRs pass QA.

## Assumptions

- Full native packages may be scaffolded before all platform assets are available.
- CI should fail fast on .NET build/test issues and skip optional GPU-only tests unless runner support exists.

## Work Instructions

1. Build on the agent runtime branch after its PR QA passes.
2. Keep examples safe and free of real credentials.
3. Document unsupported platforms explicitly.

## Verification

- `dotnet restore Bubo.sln`
- `dotnet build Bubo.sln --no-restore`
- `dotnet test Bubo.sln --no-build`
- Run end-to-end fixture command.
- Generate `GOAL_REPORT.md`.

## Review

- Added deterministic `bubo-actions` fixtures for no-op, file edit, and command execution flows.
- Added guarded `run_command` support with a small executable allowlist and no shell expansion.
- Added CLI E2E coverage that exercises `bubo run` through `Program.Main`.
- Added GitHub Actions workflow for .NET 8 restore/build/test/package validation.
- Added manual native asset workflow scaffold for `win-x64`, `linux-x64`, and `osx-arm64` llama.cpp shared libraries.
- Added README, security, packaging, configuration, examples, and scripted E2E documentation.
- Added .NET tool packaging metadata for `Bubo.LocalAgent.Cli` and managed wrapper package metadata for `Bubo.LlamaCppSharp`.
- Validation passed:
  - `dotnet restore Bubo.sln`
  - `dotnet build Bubo.sln --configuration Release --no-restore`
  - `dotnet test Bubo.sln --configuration Release --no-build` passed: 29 tests across 5 assemblies.
  - `powershell -ExecutionPolicy Bypass -File .\scripts\run-e2e-fixture.ps1 -Configuration Release`
  - `dotnet pack` for `LlamaCppSharp.Native`, `Bubo.LlamaCppSharp`, and `Bubo.LocalAgent.Cli`.
  - `dotnet run --no-build --configuration Release --project src/LocalAgent.Cli/LocalAgent.Cli.csproj -- doctor`
  - `dotnet run --no-build --configuration Release --project src/LocalAgent.Cli/LocalAgent.Cli.csproj -- models list`
  - `dotnet format Bubo.sln --verify-no-changes`
  - `git diff --check`

## Issue

https://github.com/TimothyMeadows/Bubo/issues/6
