# Bubo agent runtime and inference providers

## Goal

Implement the first practical Bubo agent loop with interchangeable local llama.cpp and cloud codex-cli inference providers.

## Scope

- Implement planner/coder model profile loading.
- Implement local inference provider using `LlamaCppSharp`.
- Spike and implement codex-cli child-process provider.
- Implement guarded tools for file reads, patching, search, command execution, and git status/diff/apply.
- Add structured transcript and debug logging through the full loop.

## Assumptions

- The exact codex-cli non-interactive flags must be confirmed during the spike.
- Tool outputs are model-visible; secrets must be redacted before transcript/debug persistence.

## Work Instructions

1. Build on the llama.cpp wrapper branch after its PR QA passes.
2. Preserve the same planner/coder abstraction for local and cloud modes.
3. Keep path and command enforcement outside model control.

## Verification

- `dotnet build Bubo.sln --no-restore`
- `dotnet test Bubo.sln --no-build`
- Run a fixture agent task that reads files and writes a bounded patch.

## Review

- Confirm no hidden chain-of-thought is written to output artifacts.

## Issue

https://github.com/TimothyMeadows/Bubo/issues/5
