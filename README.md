# Bubo

Bubo is a local-first .NET 8 coding-agent runtime. It reads a task from `INPUT.md`, works inside a repository workspace, runs guarded tools, and writes auditable results to Markdown and JSONL artifacts.

The name comes from Bubo, the mythical robotic owl created by Hephaestus.

## Table Of Contents

1. [Quick Start](#quick-start)
2. [What Bubo Does](#what-bubo-does)
3. [How A Run Works](#how-a-run-works)
4. [Input And Output Contract](#input-and-output-contract)
5. [CLI Usage](#cli-usage)
6. [Configuration](#configuration)
7. [Guided Examples](#guided-examples)
8. [Docker Sandbox](#docker-sandbox)
9. [Inference Modes](#inference-modes)
10. [Tools](#tools)
11. [Security Rules](#security-rules)
12. [Developer Onboarding](#developer-onboarding)
13. [Agent Onboarding](#agent-onboarding)
14. [Troubleshooting](#troubleshooting)
15. [Further Reading](#further-reading)

## Quick Start

Prerequisites:

- .NET 8 SDK.
- Docker Desktop or Docker Engine for sandboxed command execution.
- Optional: `codex-cli` for cloud mode.
- Optional: GGUF models and native llama.cpp assets for local model inference.

Build and test:

```powershell
dotnet restore Bubo.sln
dotnet build Bubo.sln --configuration Release --no-restore
dotnet test Bubo.sln --configuration Release --no-build
```

Build the sandbox image:

```powershell
docker build --pull -f docker/bubo-sandbox/Dockerfile -t bubo-sandbox:local docker/bubo-sandbox
```

Check the sandbox:

```powershell
dotnet run --no-build --configuration Release --project src/LocalAgent.Cli/LocalAgent.Cli.csproj -- sandbox test --workspace .
```

Run Bubo against a workspace:

```powershell
dotnet run --project src/LocalAgent.Cli/LocalAgent.Cli.csproj -- run --workspace ./repo
```

## What Bubo Does

Bubo is built for coding-agent workflows:

- Read user instructions from `INPUT.md`.
- Inspect and modify files inside one workspace.
- Execute deterministic user-provided tool actions.
- Ask a local or cloud inference provider for guarded tool actions when no deterministic action block is provided.
- Retry inference-generated repair actions within configured loop limits.
- Run build, test, Git, and command tools through bounded runtime APIs.
- Write `OUTPUT.md`, `agent-debug.jsonl`, and `agent-transcript.md`.
- Keep command execution inside a Docker sandbox.

Bubo treats model output as untrusted. The model proposes actions; the runtime validates and executes them.

## How A Run Works

```text
INPUT.md
   |
   v
LocalAgent.Cli
   |
   v
LocalAgent.Runtime
   |
   +--> deterministic bubo-actions, when present
   |
   +--> local or cloud inference provider, when no action fence is present
   |
   +--> guarded tools
   |
   +--> Docker sandbox for command execution
   |
   v
OUTPUT.md
agent-debug.jsonl
agent-transcript.md
```

Run behavior:

1. Load CLI options and optional configuration.
2. Validate workspace, input, and output paths.
3. Read `INPUT.md`.
4. Execute a deterministic `bubo-actions` block when present.
5. Otherwise ask the configured inference provider for fenced `bubo-actions` JSON.
6. Execute generated actions through the model-safe tool registry.
7. Feed concise tool observations into retries after retryable tool failures.
8. Stop on success, no actions, invalid action JSON, unknown tools, non-retryable safety failures, provider failure, or loop limits.
9. Write the output report, transcript, and debug log.

## Input And Output Contract

Input:

```text
INPUT.md
```

Output:

```text
OUTPUT.md
agent-debug.jsonl
agent-transcript.md
```

`OUTPUT.md` uses this stable shape:

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

`agent-transcript.md` records observable runtime events. It does not expose hidden chain-of-thought.

`agent-debug.jsonl` records structured events for debugging and audit.

## CLI Usage

Main command:

```bash
bubo run \
  --workspace ./repo \
  --input ./repo/INPUT.md \
  --output ./repo/OUTPUT.md \
  --mode local \
  --config ./bubo.config.json
```

When running from source:

```bash
dotnet run --project src/LocalAgent.Cli/LocalAgent.Cli.csproj -- run --workspace ./repo
```

Defaults:

```text
--workspace current directory
--input <workspace>/INPUT.md
--output <workspace>/OUTPUT.md
--mode local
--config <workspace>/bubo.config.json when present
```

Utility commands:

```bash
bubo doctor
bubo models list
bubo sandbox test --workspace ./repo
bubo native test
```

From source:

```bash
dotnet run --project src/LocalAgent.Cli/LocalAgent.Cli.csproj -- doctor
dotnet run --project src/LocalAgent.Cli/LocalAgent.Cli.csproj -- models list
dotnet run --project src/LocalAgent.Cli/LocalAgent.Cli.csproj -- sandbox test --workspace .
dotnet run --project src/LocalAgent.Cli/LocalAgent.Cli.csproj -- native test
```

## Configuration

Configuration precedence:

1. Built-in defaults.
2. Workspace `bubo.config.json`.
3. Explicit `--config`.
4. Explicit CLI flags.

`--mode` is a CLI override. If config says `cloud` and the command says `--mode local`, Bubo runs local.

Workspace-default config is treated as repository content. It can configure mode, model profiles, and safe runtime limits. It cannot set trusted sandbox policy such as network mode, GPU mode, Docker image, host model mounts, or hardening switches.

Use explicit `--config` when you deliberately trust sandbox settings:

```bash
bubo run --workspace ./repo --config ./bubo.trusted.config.json
```

Example:

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
      "gpuLayers": "auto",
      "threads": 0
    },
    "coder": {
      "path": "/models/coder.gguf",
      "contextSize": 32768,
      "temperature": 0.1,
      "topP": 0.95,
      "repeatPenalty": 1.05,
      "maxTokens": 8192,
      "gpuLayers": "auto",
      "threads": 0
    }
  },
  "limits": {
    "maxIterations": 3,
    "maxToolCalls": 40,
    "maxCommandSeconds": 300,
    "maxPatchBytes": 131072,
    "maxFilesChanged": 15
  }
}
```

Trusted sandbox policy example:

```json
{
  "sandbox": {
    "image": "bubo-sandbox:local",
    "network": "package-restore",
    "gpu": "nvidia",
    "modelsPath": "C:/Models/Bubo"
  }
}
```

## Guided Examples

### Example 1: No-Action File Contract

Create a demo workspace:

```powershell
$workspace = Join-Path $env:TEMP "bubo-readme-demo"
New-Item -ItemType Directory -Force -Path $workspace | Out-Null
@'
# Task

Say hello from Bubo and prove the output contract works.
'@ | Set-Content -Path (Join-Path $workspace "INPUT.md") -Encoding UTF8
```

Run:

```powershell
dotnet run --project .\src\LocalAgent.Cli -- run --workspace $workspace --mode local
```

Inspect:

```powershell
Get-ChildItem $workspace
Get-Content (Join-Path $workspace "OUTPUT.md")
Get-Content (Join-Path $workspace "agent-transcript.md")
Get-Content (Join-Path $workspace "agent-debug.jsonl")
```

### Example 2: Deterministic Tool Actions

Use deterministic actions when you want a fixture, smoke test, or controlled automation without model inference.

````markdown
# Task

Write and patch a generated note.

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
  }
]
```
````

Run:

```bash
bubo run --workspace ./repo
```

The deterministic action fence executes once. It does not invoke inference.

### Example 3: Docker-Backed Command Fixture

Build the sandbox image:

```bash
docker build --pull -f docker/bubo-sandbox/Dockerfile -t bubo-sandbox:local docker/bubo-sandbox
```

Use an action block that asks for a guarded command:

````markdown
# Task

Check the .NET SDK version.

```bubo-actions
[
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

`run_command` is routed through the Docker sandbox and avoids shell expansion.

### Example 4: Cloud Mode

Cloud mode uses `codex-cli` behind the same inference abstraction:

```bash
bubo run --workspace ./repo --mode cloud
```

Or:

```json
{
  "mode": "cloud"
}
```

Cloud mode should only receive secrets when they are explicitly mounted or configured. No host secrets are passed by default.

### Example 5: Local Model Profiles

Local mode is designed for GGUF models. A good starting point for 16 GB GPU memory is:

- Planner: Qwen3 14B Instruct or similar, `Q4_K_M` or `Q5_K_M`.
- Coder: Qwen2.5-Coder 14B Instruct, Qwen3-Coder mid-size, or similar, `Q4_K_M` or `Q5_K_M`.
- Context: `32768` if stable, otherwise `16384`.
- GPU layers: `auto` or as many as fit.

Keep model mounts read-only inside Docker.

## Docker Sandbox

The sandbox image includes:

- .NET SDK.
- `git`.
- GitHub CLI `gh`.
- `openssh-client`.
- `ca-certificates`.
- `curl`.
- `jq`.

Container layout:

```text
/workspace  writable workspace
/input      read-only task input
/output     writable output
/models     read-only model mount
/cache      writable cache
```

Default posture:

```text
--network none
--cap-drop ALL
--security-opt no-new-privileges
--read-only where practical
--pids-limit <configured>
--memory <configured>
--cpus <configured>
```

Network modes:

| Mode | Use |
| --- | --- |
| `none` | Normal coding runs. |
| `package-restore` | Dependency restore windows. |
| `research` | Controlled research or downloads. |
| `full` | Explicitly approved unrestricted access. |

GPU mode is explicit. NVIDIA mode uses `--gpus all` and requires host GPU drivers plus NVIDIA Container Toolkit.

## Inference Modes

Local mode:

```text
LocalAgent.Inference.LlamaCpp
  -> LlamaCppSharp
  -> LlamaCppSharp.Native
  -> llama.cpp shared library
```

Cloud mode:

```text
LocalAgent.Inference.CodexCli
  -> codex-cli child process
```

Both modes feed the same runtime contracts and write the same output artifacts.

Native llama.cpp assets use the standard RID layout:

```text
runtimes/
  win-x64/native/llama.dll
  linux-x64/native/libllama.so
  osx-arm64/native/libllama.dylib
```

Pinned upstream reference:

```text
Repository: https://github.com/ggml-org/llama.cpp
Release: b9189
Commit: 64b38b561b987679c4e1c6231f93860d3eec2638
```

## Tools

Default deterministic tools:

| Tool | Purpose |
| --- | --- |
| `read_file` | Read a UTF-8 file inside the workspace. |
| `write_file` | Write a UTF-8 file inside the workspace. |
| `patch_file` | Apply a bounded exact old/new replacement. |
| `list_files` | Enumerate workspace files. |
| `search_text` | Search workspace text. |
| `run_command` | Run allowlisted commands through Docker without shell expansion. |
| `git_status` | Inspect Git status. |
| `git_diff` | Inspect Git diffs. |
| `git_apply_patch` | Apply guarded unified diffs through Docker-backed `git apply`. |

Inference-generated actions use a smaller model-safe registry. Generic `run_command` is excluded from model-generated repair retries.

Tool safety rules:

- Resolve paths against the workspace root.
- Reject path traversal and workspace escapes.
- Bound tool-call count, file count, patch size, and command duration.
- Treat model output as data, not authority.
- Capture stdout, stderr, exit code, files changed, and issues.
- Stop at configured loop limits.

## Security Rules

Bubo assumes repository content and model output may be hostile.

Default rules:

- No host secrets mounted by default.
- Network disabled by default.
- Input and model mounts are read-only.
- All writable paths stay inside the workspace or configured output/cache mounts.
- Command execution goes through Docker.
- Tool arguments are validated before execution.
- Git hooks and remote mutations are not trusted by default.
- Bubo does not push, open PRs, merge, or publish unless explicitly configured or requested.
- Output artifacts include summaries, tool observations, and decisions, not hidden chain-of-thought.

Threats the design accounts for:

- Path traversal.
- Prompt injection.
- Command injection.
- Secret exfiltration.
- Network exfiltration.
- Docker escape risk.
- GPU device exposure.
- Malicious package install scripts.
- Accidental edits outside the workspace.

## Developer Onboarding

Repository layout:

```text
src/
  LlamaCppSharp.Native/          Native package metadata and RID asset layout.
  LlamaCppSharp/                 Managed llama.cpp wrapper.
  LocalAgent.Abstractions/       Shared contracts.
  LocalAgent.Runtime/            Agent loop, tools, workspace guard, output artifacts.
  LocalAgent.Inference.LlamaCpp/ Local inference provider.
  LocalAgent.Inference.CodexCli/ Cloud inference provider.
  LocalAgent.Sandbox.Docker/     Docker command runner.
  LocalAgent.Cli/                CLI entrypoint.

tests/
  Unit and smoke tests for contracts, runtime behavior, CLI behavior, native probing, and Docker command construction.
```

Common commands:

```bash
dotnet restore Bubo.sln
dotnet build Bubo.sln --configuration Release --no-restore
dotnet test Bubo.sln --configuration Release --no-build
dotnet format Bubo.sln --verify-no-changes --no-restore
git diff --check
```

Package checks:

```bash
dotnet pack src/LlamaCppSharp.Native/LlamaCppSharp.Native.csproj --configuration Release --no-build --output artifacts/packages
dotnet pack src/LlamaCppSharp/LlamaCppSharp.csproj --configuration Release --no-build --output artifacts/packages
dotnet pack src/LocalAgent.Cli/LocalAgent.Cli.csproj --configuration Release --no-build --output artifacts/packages
```

Docker checks:

```bash
docker build --pull -f docker/bubo-sandbox/Dockerfile -t bubo-sandbox:local docker/bubo-sandbox
dotnet run --no-build --configuration Release --project src/LocalAgent.Cli/LocalAgent.Cli.csproj -- sandbox test --workspace .
```

When adding a tool:

1. Implement `IAgentTool`.
2. Resolve all paths through `WorkspaceGuard`.
3. Add bounded input validation.
4. Record useful `ToolResult` output and errors.
5. Decide whether the tool belongs in the deterministic registry, the model-safe registry, or both.
6. Add tests for success, failure, traversal rejection, and limit enforcement.

When changing inference behavior:

1. Keep deterministic `bubo-actions` single-pass.
2. Keep model-generated actions behind fenced JSON parsing.
3. Treat observations fed back to the model as untrusted text.
4. Preserve side effects and failed-attempt evidence in output artifacts.
5. Cover retry success, retry exhaustion, invalid output, unknown tool, and limit exhaustion.

## Agent Onboarding

Use this section when Bubo itself, Codex, or another coding agent is working in this repository.

Before editing:

- Read `AGENTS.md`.
- Read `.codex/AGENTS.md` when present.
- Check `git status --short --branch`.
- Prefer the existing project style and tests.
- Keep unrelated local changes intact.

Good task prompts for Bubo:

```markdown
# Task

Make the smallest safe change that updates the CLI help text for sandbox test.

## Validation

- Run the CLI tests.
- Run `dotnet format`.
- Summarize files changed.
```

Good deterministic fixture prompts:

````markdown
# Task

Create a generated note.

```bubo-actions
[
  {
    "tool": "write_file",
    "arguments": {
      "path": "generated/note.txt",
      "content": "Generated by Bubo.\n"
    }
  }
]
```
````

Agent expectations:

- Do not assume network access.
- Do not assume secrets are mounted.
- Do not write outside the workspace.
- Do not run destructive Git operations without explicit approval.
- Prefer small patches and focused tests.
- Report commands run and validation results in `OUTPUT.md`.
- Keep hidden chain-of-thought out of artifacts.

## Troubleshooting

### `INPUT.md` Is Missing

Create one:

```bash
echo "# Task" > INPUT.md
bubo run --workspace .
```

### Docker Is Not On PATH

Check:

```bash
docker version
```

On Windows with Docker Desktop, the CLI is commonly installed at:

```text
C:\Program Files\Docker\Docker\resources\bin\docker.exe
```

Add that directory to `PATH` or call it by full path.

### Sandbox Test Fails

Rebuild the image:

```bash
docker build --pull -f docker/bubo-sandbox/Dockerfile -t bubo-sandbox:local docker/bubo-sandbox
```

Then rerun:

```bash
bubo sandbox test --workspace .
```

Expected output includes versions for `git`, `gh`, and .NET.

### llama.cpp Native Library Is Missing

Expected names:

```text
Windows: llama.dll
Linux:   libllama.so
macOS:   libllama.dylib
```

Place the asset under `runtimes/<rid>/native/`, then run:

```bash
bubo native test
```

### `codex` Is Not Found

Check:

```bash
codex --version
```

Then make sure `codex` is on `PATH` before running cloud mode.

## Further Reading

- [Architecture](ARCHITECTURE.md)
- [Configuration](docs/configuration.md)
- [Security Model](docs/security.md)
- [Packaging](docs/packaging.md)
- [Examples](examples/README.md)
