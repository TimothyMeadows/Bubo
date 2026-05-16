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

- Confirm PR ordering and merge dependency notes are complete.

## Issue

https://github.com/TimothyMeadows/Bubo/issues/6
