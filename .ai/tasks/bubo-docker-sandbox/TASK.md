# Bubo Docker sandbox runtime

## Goal

Implement Docker sandbox configuration and command execution for Bubo with safe mounts, network policies, resource limits, and initial sandbox tests.

## Scope

- Add Docker image/runtime definitions for `/workspace`, `/input`, `/output`, `/models`, and `/cache`.
- Implement Docker command construction and sandbox execution in `LocalAgent.Sandbox.Docker`.
- Default to no network and explicit resource limits.
- Include git, gh, .NET 8 SDK, openssh-client, ca-certificates, curl, and jq in the sandbox image.
- Add GPU option support for NVIDIA via `--gpus all` plus CPU fallback behavior.

## Assumptions

- Docker may not be installed on every developer host; tests that require Docker must skip cleanly when unavailable.
- No host secrets are mounted by default.

## Work Instructions

1. Build on the foundation branch.
2. Keep Docker command creation deterministic and testable.
3. Add network policies: `none`, `package-restore`, `research`, `full`.
4. Add sandbox test/doctor paths without requiring privileged host access.

## Verification

- `dotnet build Bubo.sln --no-restore`
- `dotnet test Bubo.sln --no-build`
- Docker available: run sandbox test command and verify mount/network options.

## Review

- Confirm generated Docker arguments do not mount more than the allowed directories.

## Issue

https://github.com/TimothyMeadows/Bubo/issues/3
