# Bubo foundation contracts and CLI no-op flow

## Goal

Create the first buildable .NET 8 slice for Bubo: solution scaffold, shared contracts, runtime no-op file contract, CLI entrypoint, and focused tests.

## Scope

- Add `global.json`, solution, project structure, and central build/package metadata.
- Add core abstractions for inference, tools, sandboxing, configuration, results, and transcripts.
- Add a no-op runtime that reads `INPUT.md` and writes `OUTPUT.md`, `agent-debug.jsonl`, and `agent-transcript.md`.
- Add CLI parsing for `bubo run`.
- Add unit tests for defaults, workspace path guarding, runtime output, and CLI parsing.

## Assumptions

- Target framework is `net8.0`.
- Local SDK may be newer than .NET 8 as long as the target framework remains .NET 8 and CI uses .NET 8.
- Docker, llama.cpp, and codex-cli execution are future task scopes.

## Work Instructions

1. Create the solution and projects listed in the goal plan.
2. Keep contracts in `LocalAgent.Abstractions`.
3. Keep no-op orchestration in `LocalAgent.Runtime`.
4. Keep CLI behavior thin and deterministic in `LocalAgent.Cli`.
5. Do not add external runtime dependencies unless needed for tests.

## Verification

- `dotnet restore Bubo.sln`
- `dotnet build Bubo.sln --no-restore`
- `dotnet test Bubo.sln --no-build`
- Run the CLI against a sample `INPUT.md` and confirm all output artifacts exist.

## Review

- Confirmed no model, Docker, network, or git mutation behavior is introduced in this first slice.
- Validation passed:
  - `dotnet restore Bubo.sln`
  - `dotnet build Bubo.sln --no-restore`
  - `dotnet test Bubo.sln --no-build`
  - CLI smoke run wrote `OUTPUT.md`, `agent-debug.jsonl`, and `agent-transcript.md`.

## Issue

https://github.com/TimothyMeadows/Bubo/issues/2
