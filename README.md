# Bubo

Bubo is a local-first .NET 8 coding-agent runtime designed to inspect repositories, plan changes, edit files, run commands, use Git, and produce auditable Markdown output while keeping code execution and file writes inside a Docker-based sandbox.

The name comes from Bubo, the mythical robotic owl created by Hephaestus.

## Table Of Contents

1. [Project Status](#project-status)
2. [What Bubo Is](#what-bubo-is)
3. [Design Goals](#design-goals)
4. [Non-Goals](#non-goals)
5. [Feature Overview](#feature-overview)
6. [Architecture At A Glance](#architecture-at-a-glance)
7. [Repository Layout](#repository-layout)
8. [Core Concepts](#core-concepts)
9. [Input And Output Contract](#input-and-output-contract)
10. [CLI Overview](#cli-overview)
11. [Guided Example 1: Run The File Contract](#guided-example-1-run-the-file-contract)
12. [Guided Example 2: Read The Audit Artifacts](#guided-example-2-read-the-audit-artifacts)
13. [Guided Example 3: Local Model Configuration](#guided-example-3-local-model-configuration)
14. [Guided Example 4: Docker Sandbox Shape](#guided-example-4-docker-sandbox-shape)
15. [Guided Example 5: Cloud Mode Through codex-cli](#guided-example-5-cloud-mode-through-codex-cli)
16. [Guided Example 6: Deterministic Tool Fixture](#guided-example-6-deterministic-tool-fixture)
17. [Guided Example 7: Native llama.cpp Asset Packaging](#guided-example-7-native-llamacpp-asset-packaging)
18. [Inference Modes](#inference-modes)
19. [Model Profiles](#model-profiles)
20. [Tool System](#tool-system)
21. [Docker Sandbox](#docker-sandbox)
22. [Network Policy](#network-policy)
23. [Git And GitHub Support](#git-and-github-support)
24. [Security Model](#security-model)
25. [Auditing And Logs](#auditing-and-logs)
26. [Build, Test, And Package](#build-test-and-package)
27. [Troubleshooting](#troubleshooting)
28. [Roadmap](#roadmap)
29. [Glossary](#glossary)

## Project Status

Bubo is being built in incremental goal-flow slices. This integration branch brings the completed v1 scaffold onto the documentation baseline: a .NET 8 solution, shared contracts, CLI commands, deterministic action execution, one-shot inference action proposal, Docker sandbox command construction, direct llama.cpp native interop scaffolding, cloud inference through `codex-cli`, guarded tools, package scaffolding, and end-to-end fixtures.

The remaining limits are deliberate: multi-iteration autonomous planner/coder model orchestration is still roadmap work, local llama.cpp generation requires populated native assets plus fuller decode/sampling bindings, and cloud mode depends on stable non-interactive `codex-cli` behavior.

Status language used in this README:

| Status | Meaning |
| --- | --- |
| Available | Present in the current checkout and expected to work. |
| Scaffolded | Type, project, or package shape exists, but runtime behavior is intentionally limited. |
| Planned | Part of the v1 architecture but not fully wired in the current checkout. |

## What Bubo Is

Bubo is a pure C# agent runtime for software development tasks. It is intended to run from a repository workspace, read a task from `INPUT.md`, reason over the codebase, apply bounded edits, run validation commands, and write a human-readable result to `OUTPUT.md`.

The long-term runtime has two inference modes:

- Local mode uses direct C# interop over `llama.cpp` and GGUF models.
- Cloud mode delegates inference to `codex-cli` while preserving the same agent abstractions and output contract.

Bubo treats model output as untrusted. File writes, command execution, Git operations, and future network access are mediated by runtime tools and sandbox policy rather than handed directly to the model.

## Design Goals

- Target .NET 8 LTS.
- Keep the agent runtime pure C# where practical.
- Use direct llama.cpp interop instead of shelling out to `llama-cli`, `llama-server`, Ollama, or a host daemon.
- Make local mode first-class for private repositories and offline workflows.
- Provide cloud mode through `codex-cli` for stronger hosted inference when explicitly selected.
- Keep the user-facing workflow file-driven and auditable.
- Keep all generated code changes inside the mounted workspace.
- Make Docker the execution boundary for command execution.
- Disable network access by default.
- Use structured logs and transcripts so agent behavior can be reviewed after the run.
- Keep hidden reasoning hidden. Bubo records summaries, observations, commands, and tool decisions, not private chain-of-thought.

## Non-Goals

- Bubo is not a general desktop automation system.
- Bubo is not a replacement for Docker security hardening or host isolation.
- Bubo does not automatically push, open PRs, or publish packages by default.
- Bubo does not mount host secrets by default.
- Bubo does not claim local model quality is equivalent to hosted frontier models.
- Bubo does not expose raw hidden chain-of-thought in output artifacts.

## Feature Overview

| Feature | Status | Notes |
| --- | --- | --- |
| .NET 8 solution and project layout | Available | Core projects are present under `src/` and tests under `tests/`. |
| CLI `run` command | Available | Reads `INPUT.md` and writes output artifacts. |
| File contract | Available | `INPUT.md`, `OUTPUT.md`, `agent-debug.jsonl`, `agent-transcript.md`. |
| Workspace guard | Available | Canonicalizes paths and rejects traversal outside the workspace. |
| Agent contracts | Available | Interfaces for inference, tools, sandboxing, model profiles, and transcripts. |
| No-op runner | Available | Proves the file contract without editing source files. |
| Docker sandbox runner | Available | Builds deterministic `docker run` invocations and exposes `bubo sandbox test`. |
| llama.cpp native wrapper | Scaffolded | Native library probing, safe handles, pinned upstream metadata, and RID asset layout are present. |
| Local inference provider | Scaffolded | Uses the shared inference abstraction and reports native runtime availability. |
| codex-cli provider | Scaffolded | Builds non-interactive `codex exec` invocations through the shared inference abstraction. |
| Inference action proposal | Available | If `INPUT.md` has no deterministic action fence, the configured provider can propose a fenced `bubo-actions` JSON array. |
| Guarded file/search/Git tools | Available | Deterministic action execution routes through workspace-guarded tools. |
| Deterministic tool fixtures | Available | `bubo-actions` fixtures validate file writes and command execution without a model. |
| Package workflows | Available | CI and package metadata cover managed, native, and CLI package outputs. |

## Architecture At A Glance

```text
INPUT.md
   |
   v
LocalAgent.Cli
   |
   v
LocalAgent.Runtime
   |
   +--> Planner/coder orchestration
   +--> Context and transcript management
   +--> Tool dispatch
   |
   +--> LocalAgent.Inference.LlamaCpp
   |       |
   |       v
   |     LlamaCppSharp -> LlamaCppSharp.Native -> llama.cpp shared library
   |
   +--> LocalAgent.Inference.CodexCli
   |       |
   |       v
   |     codex-cli child process
   |
   +--> LocalAgent.Sandbox.Docker
           |
           v
         Docker container with /workspace, /input, /output, /models, /cache
```

The runtime is intentionally layered:

- `LocalAgent.Abstractions` defines contracts.
- `LocalAgent.Runtime` coordinates the agent loop and artifacts.
- Inference providers implement model backends.
- Sandbox providers implement safe command execution.
- Tools expose bounded capabilities to the agent.

## Repository Layout

```text
src/
  LlamaCppSharp.Native/
    Native llama.cpp package metadata and native asset layout.

  LlamaCppSharp/
    Managed llama.cpp wrapper surface.

  LocalAgent.Abstractions/
    Shared contracts for agents, inference, tools, sandboxing, config, and transcripts.

  LocalAgent.Runtime/
    Agent orchestration, workspace guarding, output generation, and future tool dispatch.

  LocalAgent.Inference.LlamaCpp/
    Local inference provider using the managed llama.cpp wrapper.

  LocalAgent.Inference.CodexCli/
    Cloud inference provider using codex-cli.

  LocalAgent.Sandbox.Docker/
    Docker sandbox runtime.

  LocalAgent.Cli/
    Command-line entrypoint.

tests/
  Unit and smoke tests for contracts, CLI behavior, runtime behavior, native wrapper behavior, and sandbox command construction.
```

Additional guides:

- [Configuration](docs/configuration.md)
- [Security Model](docs/security.md)
- [Packaging](docs/packaging.md)
- [Examples](examples/README.md)

## Core Concepts

### Workspace

The workspace is the only writable repository area Bubo is allowed to mutate. Every file tool must resolve paths through a workspace guard before reading or writing.

### Input

`INPUT.md` is the task instruction file. It is deliberately plain Markdown so users can review, edit, and archive tasks without special tooling.

### Output

`OUTPUT.md` is the final user-facing report. It should be safe to read, attach to an issue, or include in a PR summary.

### Debug Log

`agent-debug.jsonl` is structured event output. It is intended for debugging, not polished user communication.

### Transcript

`agent-transcript.md` is a readable log of observable events. It explicitly does not include hidden chain-of-thought.

### Mode

Bubo supports two conceptual modes:

- `local`: use local GGUF models through llama.cpp interop.
- `cloud`: use `codex-cli` as the inference backend.

The CLI accepts both mode names. Runs first execute deterministic `bubo-actions` when the input provides them. If no action fence exists, the configured local or cloud inference provider gets a single prompt asking for a fenced `bubo-actions` JSON array, and any returned actions still execute through the guarded tool registry. Full multi-iteration planner/coder orchestration remains roadmap work.

## Input And Output Contract

### Input File

```text
INPUT.md
```

Example:

```markdown
# Task

Update the README to explain how Bubo works.
```

### Output Files

```text
OUTPUT.md
agent-debug.jsonl
agent-transcript.md
```

### OUTPUT.md Shape

Bubo writes a stable report skeleton:

```markdown
# Result

## Summary

## Plan

## Changes Made

## Files Changed

## Commands Run

## Test Results

## Issues / Risks

## Next Steps
```

The runner fills these sections with no-op validation details or deterministic tool and command results.

## CLI Overview

Current available command:

```bash
dotnet run --project src/LocalAgent.Cli -- run \
  --workspace <path> \
  --input <path-to-INPUT.md> \
  --output <path-to-OUTPUT.md> \
  --mode <local|cloud>
```

Defaults:

```text
--workspace current directory
--input <workspace>/INPUT.md
--output <workspace>/OUTPUT.md
--mode local
```

Utility commands:

```bash
bubo doctor
bubo models list
bubo sandbox test --workspace <path>
bubo native test
```

These utility commands are intended to validate host prerequisites, model profiles, Docker availability, and native llama.cpp loading.

## Guided Example 1: Run The File Contract

This example works with the no-action path of the current runtime.

1. Create a temporary workspace:

```powershell
$workspace = Join-Path $env:TEMP "bubo-readme-demo"
New-Item -ItemType Directory -Force -Path $workspace | Out-Null
```

2. Create `INPUT.md`:

```powershell
@'
# Task

Say hello from Bubo and prove the output contract works.
'@ | Set-Content -Path (Join-Path $workspace "INPUT.md") -Encoding UTF8
```

3. Run Bubo:

```powershell
dotnet run --project .\src\LocalAgent.Cli -- run --workspace $workspace --mode local
```

4. Inspect the generated files:

```powershell
Get-ChildItem $workspace
Get-Content (Join-Path $workspace "OUTPUT.md")
Get-Content (Join-Path $workspace "agent-transcript.md")
Get-Content (Join-Path $workspace "agent-debug.jsonl")
```

Expected behavior:

- `OUTPUT.md` exists.
- `agent-debug.jsonl` exists.
- `agent-transcript.md` exists.
- No repository files are modified by the no-action runner.

## Guided Example 2: Read The Audit Artifacts

After a run, read the artifacts in this order:

1. `OUTPUT.md`: start here for the human result.
2. `agent-transcript.md`: inspect observable runtime events.
3. `agent-debug.jsonl`: inspect structured event data.

Example `agent-transcript.md` excerpt:

```markdown
# Agent Transcript

This transcript records observable events and does not include hidden chain-of-thought.

## run.started

Bubo run started.

## input.read

Read task input.

## run.completed

Bubo run completed successfully.
```

Example `agent-debug.jsonl` event:

```json
{"timestamp":"2026-05-16T00:00:00+00:00","type":"input.read","message":"Read task input.","data":{"path":"INPUT.md","characters":"71"}}
```

Use the Markdown report for communication and the JSONL stream for diagnostics.

## Guided Example 3: Local Model Configuration

Local mode is designed for GGUF models that fit the target machine. The starting target is 16 GB GPU memory.

Recommended planner profile:

```json
{
  "role": "planner",
  "family": "Qwen3 14B Instruct or equivalent",
  "path": "/models/planner.gguf",
  "contextSize": 32768,
  "temperature": 0.2,
  "topP": 0.9,
  "repeatPenalty": 1.05,
  "maxTokens": 4096,
  "gpuLayers": "auto",
  "threads": 0
}
```

Recommended coder profile:

```json
{
  "role": "coder",
  "family": "Qwen2.5-Coder 14B Instruct, Qwen3-Coder mid-size, or equivalent",
  "path": "/models/coder.gguf",
  "contextSize": 32768,
  "temperature": 0.1,
  "topP": 0.95,
  "repeatPenalty": 1.05,
  "maxTokens": 8192,
  "gpuLayers": "auto",
  "threads": 0
}
```

Guidance:

- Use `Q4_K_M` for broad compatibility.
- Use `Q5_K_M` when VRAM headroom allows.
- Start with `contextSize` 16384 if 32768 is unstable.
- Use deterministic seeds for repeatable debugging.
- Keep model mounts read-only inside Docker.

## Guided Example 4: Docker Sandbox Shape

Bubo's command execution boundary is Docker. The intended container layout is:

```text
/workspace  writable mounted repository or task workspace
/input      read-only task input area
/output     writable output area
/models     read-only model mount
/cache      writable cache area
```

Default security posture:

```text
--network none
--cap-drop ALL
--security-opt no-new-privileges
--read-only where practical
--pids-limit <configured>
--memory <configured>
--cpus <configured>
```

NVIDIA GPU mode adds:

```text
--gpus all
```

Host prerequisites for GPU mode:

- Docker Engine or Docker Desktop.
- NVIDIA GPU driver.
- NVIDIA Container Toolkit.
- A llama.cpp native build with the right GPU backend enabled.

CPU fallback should remain available when GPU support is missing.

## Guided Example 5: Cloud Mode Through codex-cli

Cloud mode uses `codex-cli` as a child process rather than changing the rest of the agent runtime.

Conceptual command:

```bash
bubo run \
  --workspace ./repo \
  --input ./repo/INPUT.md \
  --output ./repo/OUTPUT.md \
  --mode cloud
```

Provider responsibilities:

- Detect `codex` on `PATH`.
- Build a non-interactive prompt from the same runtime context.
- Capture stdout and stderr.
- Normalize the result into the same `OUTPUT.md` contract.
- Avoid passing secrets unless explicitly configured.
- Keep the same planner/coder abstraction used by local mode.

Important implementation note:

`codex-cli` flags can drift. Bubo should keep a spike or doctor check that confirms stable non-interactive invocation before relying on a new CLI version.

## Guided Example 6: Deterministic Tool Fixture

The runtime includes deterministic fixtures to validate tool execution without a model. A fixture is an `INPUT.md` with an explicit tool block.

Example:

````markdown
# Task

Write a generated note and verify the .NET SDK version.

```bubo-actions
[
  {
    "tool": "write_file",
    "arguments": {
      "path": "generated/result.txt",
      "content": "Hello from Bubo.\n"
    }
  },
  {
    "tool": "patch_file",
    "arguments": {
      "path": "generated/result.txt",
      "old": "Hello from Bubo.",
      "new": "Patched by Bubo."
    }
  },
  {
    "tool": "run_command",
    "arguments": {
      "executable": "dotnet",
      "arguments": ["--version"]
    }
  }
]
```
````

Guardrails:

- `write_file` can only write inside the workspace.
- `patch_file` applies one exact old/new text replacement and fails if the old text is absent or ambiguous.
- `run_command` avoids shell expansion.
- `run_command`, `git_status`, `git_diff`, and `git_apply_patch` execute through the Docker sandbox runner.
- Executables are allowlisted.
- Results are recorded in `OUTPUT.md`, `agent-debug.jsonl`, and `agent-transcript.md`.

This fixture format is for validation and controlled automation. It is not a replacement for the planner/coder model loop.
When the fixture fence is absent, CLI runs may ask the selected inference provider to propose the same fenced JSON shape. Model output is still treated as untrusted: prose is ignored, invalid action JSON fails safely, and unknown or unsafe tools are rejected before execution.

Command fixtures require the Docker sandbox image:

```bash
docker build -t bubo-sandbox:local docker/bubo-sandbox
```

## Guided Example 7: Native llama.cpp Asset Packaging

Bubo owns its llama.cpp native dependency through RID assets.

Expected package layout:

```text
runtimes/
  win-x64/native/llama.dll
  linux-x64/native/libllama.so
  osx-arm64/native/libllama.dylib
```

Pinned upstream source:

```text
Repository: https://github.com/ggml-org/llama.cpp
Release: b9189
Commit: 64b38b561b987679c4e1c6231f93860d3eec2638
```

Validation flow:

```bash
dotnet pack src/LlamaCppSharp.Native/LlamaCppSharp.Native.csproj --configuration Release
dotnet test tests/LlamaCppSharp.Native.Tests/LlamaCppSharp.Native.Tests.csproj --configuration Release
```

Publishing rule:

Do not claim support for a RID until the native asset exists, loads successfully, and passes a smoke test on that platform.

## Inference Modes

### Local Mode

Local mode routes inference through:

```text
LocalAgent.Inference.LlamaCpp
  -> LlamaCppSharp
  -> LlamaCppSharp.Native
  -> llama.cpp shared library
```

Local mode is intended for:

- private repositories,
- offline work,
- reproducible model profiles,
- local model experimentation,
- environments where cloud inference is not allowed.

### Cloud Mode

Cloud mode routes inference through:

```text
LocalAgent.Inference.CodexCli
  -> codex-cli child process
```

Cloud mode is intended for:

- stronger hosted model reasoning,
- cases where local hardware is insufficient,
- workflows already approved for cloud inference.

Both modes should feed the same runtime contracts and write the same output artifacts.

## Model Profiles

Model profiles make defaults replaceable without code changes.

Full conceptual config:

```json
{
  "mode": "local",
  "models": {
    "planner": {
      "path": "/models/planner.gguf",
      "contextSize": 32768,
      "temperature": 0.2,
      "topP": 0.9,
      "repeatPenalty": 1.05,
      "maxTokens": 4096,
      "gpuLayers": "auto"
    },
    "coder": {
      "path": "/models/coder.gguf",
      "contextSize": 32768,
      "temperature": 0.1,
      "topP": 0.95,
      "repeatPenalty": 1.05,
      "maxTokens": 8192,
      "gpuLayers": "auto"
    }
  }
}
```

Planner responsibilities:

- Understand the user task.
- Inspect repository summaries.
- Decide which files and tools are relevant.
- Produce an execution plan.
- Avoid unnecessary edits.
- Produce concise reasoning summaries rather than hidden chain-of-thought.

Coder responsibilities:

- Generate patches.
- Explain edits.
- Run build and test commands through guarded tools.
- Fix compile or test errors within loop limits.
- Keep output and writes inside the workspace.

## Tool System

All agent actions should flow through tools.

Conceptual tool interface:

```csharp
public interface IAgentTool
{
    string Name { get; }
    string Description { get; }
    Task<ToolResult> InvokeAsync(ToolRequest request, CancellationToken cancellationToken);
}
```

Current default registry:

| Tool | Purpose |
| --- | --- |
| `read_file` | Read a UTF-8 file inside the workspace. |
| `write_file` | Write a UTF-8 file inside the workspace. |
| `patch_file` | Apply a bounded old/new text replacement inside the workspace. |
| `list_files` | Enumerate files under the workspace. |
| `search_text` | Search text under the workspace. |
| `run_command` | Run allowlisted commands without shell expansion. |
| `git_status` | Inspect Git status. |
| `git_diff` | Inspect Git diffs. |
| `git_apply_patch` | Apply guarded unified diffs through Docker-backed `git apply`. |

Inference-proposed actions use a smaller model-safe registry. It excludes generic `run_command`; build and test command execution remains available to deterministic/user-authored `bubo-actions` and future approval-gated loops, but not to the first one-shot model proposal path.

Planned next tools:

| Tool | Purpose |
| --- | --- |
| `git_commit_optional` | Commit only when explicitly configured. |

Tool safety rules:

- Resolve every path against the workspace root.
- Reject path traversal, `.git` metadata edits, and symlink/reparse escape paths.
- Bound file sizes, patch sizes, file counts, tool-call counts, and command runtime.
- Enforce configured limits in the runtime instead of trusting action-supplied limit values.
- Avoid shell string composition.
- Capture stdout, stderr, and exit code.
- Redact secrets before model-visible output when practical.
- Stop when loop limits are reached.

## Docker Sandbox

The sandbox is the boundary for build and test command execution.

Required image capabilities:

- .NET SDK.
- `git`.
- GitHub CLI `gh`.
- `openssh-client`.
- `ca-certificates`.
- `curl`.
- `jq`.

Default mounts:

| Host path | Container path | Access |
| --- | --- | --- |
| workspace | `/workspace` | writable |
| input | `/input` | read-only |
| output | `/output` | writable |
| models | `/models` | read-only |
| cache | `/cache` | writable |

Security flags:

```text
--cap-drop ALL
--security-opt no-new-privileges
--network none
--read-only where practical
--pids-limit <limit>
--memory <limit>
--cpus <limit>
```

GPU tradeoff:

GPU access is useful for local inference, but exposing GPU devices to containers increases the attack surface. Keep GPU mode explicit.

## Network Policy

Bubo uses explicit network modes:

| Mode | Description |
| --- | --- |
| `none` | No network access. Default. |
| `package-restore` | Temporary network access for dependency restore workflows. |
| `research` | Controlled research or download workflows. |
| `full` | Full network access only when explicitly requested. |

Recommended practice:

- Keep normal coding runs on `none`.
- Use `package-restore` only around package manager commands.
- Prefer host-mediated research over arbitrary sandbox network access.
- Never mount secrets by default.

## Git And GitHub Support

Bubo is designed to support Git workflows without surprising remote mutations.

Supported operations and guarded defaults:

- Read current branch.
- Read `git status`.
- Inspect diffs.
- Create a working branch when configured.
- Apply patches.
- Run tests.
- Optionally commit changes.
- Optionally use `gh` for PR creation only when explicitly configured.

Default rule:

Bubo must not push, create PRs, or merge changes unless the user or configuration explicitly asks for that behavior.

## Security Model

Bubo assumes model output is untrusted.

Threats addressed by the design:

- Path traversal.
- Prompt injection from repository files.
- Command injection.
- Secret exfiltration.
- Network exfiltration.
- Docker escape risk.
- GPU device exposure.
- Malicious package install scripts.
- Unsafe Git hooks.
- Accidental edits outside the workspace.

Mitigations:

- Workspace-root canonicalization for file operations.
- Docker sandbox for command execution.
- Network disabled by default.
- No host secret mounts by default.
- Read-only model and input mounts.
- Command allowlist or approval mode.
- One-shot inference uses a model-safe registry that excludes generic command execution.
- Resource limits.
- Structured logs.
- Dry-run support where practical.
- Explicit approval gates for destructive operations.
- No auto-push in v1.

## Auditing And Logs

Bubo creates three main artifact types:

| Artifact | Audience | Purpose |
| --- | --- | --- |
| `OUTPUT.md` | User/reviewer | Final result and summary. |
| `agent-transcript.md` | Developer/reviewer | Human-readable observable event transcript. |
| `agent-debug.jsonl` | Developer/debugging | Structured event records. |

The transcript may include:

- tool decisions,
- commands run,
- observations,
- summaries,
- errors,
- file paths,
- exit codes.

The transcript must not claim to expose hidden chain-of-thought.

## Build, Test, And Package

Restore:

```bash
dotnet restore Bubo.sln
```

Build:

```bash
dotnet build Bubo.sln --no-restore
```

Test:

```bash
dotnet test Bubo.sln --no-build
```

Release build:

```bash
dotnet build Bubo.sln --configuration Release --no-restore
dotnet test Bubo.sln --configuration Release --no-build
```

Package examples:

```bash
dotnet pack src/LlamaCppSharp.Native/LlamaCppSharp.Native.csproj --configuration Release
dotnet pack src/LlamaCppSharp/LlamaCppSharp.csproj --configuration Release
dotnet pack src/LocalAgent.Cli/LocalAgent.Cli.csproj --configuration Release
```

## Troubleshooting

### `INPUT.md` Is Missing

Error shape:

```text
Input file does not exist.
```

Fix:

```bash
echo "# Task" > INPUT.md
dotnet run --project src/LocalAgent.Cli -- run --workspace .
```

### Output Was Written Somewhere Unexpected

Use explicit paths:

```bash
dotnet run --project src/LocalAgent.Cli -- run \
  --workspace ./my-workspace \
  --input ./my-workspace/INPUT.md \
  --output ./my-workspace/OUTPUT.md
```

### Docker Is Not Available

Check Docker:

```bash
docker version
```

If Docker is unavailable, local command execution through the sandbox cannot run. The file contract can still be validated.

### llama.cpp Native Library Is Missing

Expected native names:

```text
Windows: llama.dll
Linux:   libllama.so
macOS:   libllama.dylib
```

Fix:

- Build or install the matching RID asset.
- Place it under `runtimes/<rid>/native/`.
- Re-run the native smoke test when available.

### codex-cli Is Not Found

Check:

```bash
codex --version
```

Fix:

- Install or expose `codex` on `PATH`.
- Re-run the cloud provider doctor/spike command when available.

## Roadmap

Short-term:

- Populate and smoke-test llama.cpp native assets for supported RIDs.
- Expand P/Invoke bindings for tokenization, decode, logits, sampling, and streaming generation.
- Add optional guarded commit tooling.
- Expand one-shot inference action proposal into multi-iteration planner/coder orchestration over inference providers and guarded tools.
- Add richer config loading for model profiles, sandbox policy, and loop limits.
- Harden command approval policy and secret redaction.
- Keep `codex-cli` non-interactive invocation checks current as CLI flags evolve.

Medium-term:

- Add native smoke tests per RID.
- Add model metadata inspection.
- Add embeddings support if practical.
- Add package restore network windows.
- Add command approval policy.
- Add structured redaction for secrets.
- Add richer prompt templates and context management.

Long-term:

- Add AMD/ROCm or Vulkan support if hardware and llama.cpp support justify it.
- Add richer language images for non-.NET repositories.
- Add policy-driven PR creation.
- Add multi-repository workflows.
- Add model benchmarking and profile recommendations.

## Glossary

| Term | Meaning |
| --- | --- |
| Agent | Runtime component that reads a task, inspects context, chooses tools, and produces output. |
| GGUF | Model file format commonly used by llama.cpp. |
| llama.cpp | Native inference engine used by Bubo local mode. |
| Planner | Model role responsible for understanding the task and selecting work. |
| Coder | Model role responsible for generating and refining edits. |
| Sandbox | Docker execution boundary for commands and tools. |
| Workspace | The mounted repository or task directory Bubo can read and write. |
| Transcript | Auditable record of observable events, not hidden reasoning. |
| Tool | A bounded runtime capability exposed to the agent. |
| RID | .NET runtime identifier such as `win-x64`, `linux-x64`, or `osx-arm64`. |
