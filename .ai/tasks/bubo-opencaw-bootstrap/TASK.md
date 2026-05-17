# Add Native OpenCaw Bootstrap Support

## Issue

https://github.com/TimothyMeadows/Bubo/issues/31

## Goal

Add native OpenCaw support directly into Bubo so each enabled run updates the OpenCaw submodule, executes OpenCaw startup/bootstrap before reading `INPUT.md`, preserves the target workspace `.ai` memory/learnings, and injects OpenCaw plus project context into the agent system prompt.

## Assumptions

- OpenCaw is provided by the tracked `.opencaw` submodule.
- CLI defaults should enable OpenCaw and update the submodule each run.
- Existing project `.ai` content is durable host memory and must not be overwritten.
- OpenCaw bootstrap scripts run before `INPUT.md` is read.
- Bubo workspace guards and tool safety remain authoritative at execution time.

## Ordered Tasks

1. [x] Add OpenCaw runtime options and task tracking.
2. [x] Implement submodule update and bootstrap script execution.
3. [x] Load OpenCaw and host `.ai` context into a system prompt.
4. [x] Wire CLI/config and inference request/provider support.
5. [x] Add runtime, config, CLI, and provider tests.
6. [x] Update docs.
7. [x] Run validation and record evidence.

## Implementation Notes

- Added `OpenCawOptions` to the run config and CLI-safe defaults that enable OpenCaw, update-on-run, and bootstrap execution.
- Added runtime `OpenCawBootstrapper` that validates a direct-child `.opencaw` path, updates/verifies the OpenCaw Git checkout, runs the host scaffold script when required, preserves existing `.ai` files, and builds system prompt context before `INPUT.md`.
- Renamed the OpenCaw submodule mount to `.opencaw` so Bubo-owned startup and Codex CLI fallback can share the same baseline path.
- Added `InferenceRequest.SystemPrompt` and wired it through the runtime, Codex CLI provider prompt composition, and local provider request handling.
- Added explicit OpenCaw path/ref/update options. OpenCaw loading and bootstrap execution are mandatory and cannot be disabled.
- Updated README, configuration, security, examples, architecture notes, and durable `.ai` memory fragments.

## Validation Plan

- [x] `dotnet build Bubo.sln`
- [x] `dotnet test Bubo.sln`
- [x] `dotnet format Bubo.sln --verify-no-changes --no-restore`
- [x] `git diff --check`

## Validation Evidence

- `dotnet build Bubo.sln`: passed.
- `dotnet test Bubo.sln`: passed, 106 tests.
- `dotnet format Bubo.sln --verify-no-changes --no-restore`: passed.
- `git diff --check`: passed.
