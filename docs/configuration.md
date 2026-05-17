# Configuration

Bubo loads JSON configuration for `bubo run` without requiring code changes.

## Discovery

`bubo run` uses this precedence order:

1. CLI-safe defaults.
2. `<workspace>/bubo.config.json` when the file exists.
3. `--config <path>` when supplied. Relative paths resolve from the current shell directory.
4. Explicit CLI flags. Today `--mode` overrides config mode.

No-config behavior preserves the previous CLI posture: Docker is used when available, network is `none`, GPU is not requested, and no host model directory is mounted by default.

## Trust Boundary

Workspace `bubo.config.json` is repository content, so Bubo treats it as untrusted. It can configure mode, model profiles, and runtime limits. It cannot configure sandbox policy.

Sandbox policy requires an explicit `--config` path. That explicit flag is the user trust signal for settings such as:

- Docker image.
- Network mode.
- GPU exposure.
- Read-only model mount.
- Memory, CPU, and PID limits.

Bubo never accepts config overrides for workspace, input, output, cache, or container working-directory host paths. Those are derived from the guarded workspace.

## Workspace Default Example

```json
{
  "mode": "local",
  "models": {
    "planner": {
      "family": "Qwen3 14B Instruct or equivalent GGUF",
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
      "family": "Qwen2.5-Coder 14B Instruct, Qwen3-Coder mid-size, or equivalent GGUF",
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
    "maxIterations": 8,
    "maxToolCalls": 40,
    "maxCommandSeconds": 300,
    "maxPatchBytes": 131072,
    "maxFilesChanged": 15,
    "maxTokensPerStep": 8192
  }
}
```

Run with default discovery:

```bash
bubo run --workspace ./repo
```

`threads: 0` means the runtime uses `Environment.ProcessorCount`.

Runtime limits are safety ceilings. Config may lower the built-in defaults but cannot raise them. `maxCommandSeconds` must be positive so config cannot disable timeouts.

## Trusted Sandbox Example

Pass trusted sandbox policy explicitly:

```bash
bubo run --workspace ./repo --config ./bubo.trusted.config.json
```

```json
{
  "sandbox": {
    "image": "bubo-sandbox:local",
    "network": "package-restore",
    "gpu": "nvidia",
    "modelsPath": "C:/Models/Bubo",
    "memory": "16g",
    "cpus": 4,
    "pidsLimit": 256
  }
}
```

Security hardening booleans cannot be disabled through config, and `sandbox.useDocker` cannot be set to `false`.

## Cloud Mode Example

```json
{
  "mode": "cloud"
}
```

Cloud mode delegates inference to `codex-cli`. Keep `codex` on `PATH`; provider-specific model/profile flags are not yet part of this config schema.

An explicit CLI mode wins:

```bash
bubo run --workspace ./repo --mode local
```

If `bubo.config.json` says `cloud`, the command above still runs local.

The first inference-driven runtime slice uses the `coder` model profile for one-shot action proposal when `INPUT.md` does not contain a deterministic `bubo-actions` fence. Future planner/coder orchestration will use both profiles across multiple steps. One-shot inference uses a model-safe tool registry and does not expose generic `run_command`.

Patch and file-change limits are used by deterministic tools:

- `maxPatchBytes` bounds `patch_file` old/new payloads and `git_apply_patch` unified diff payloads.
- `maxFilesChanged` bounds the number of file paths accepted by `git_apply_patch` preflight scanning.
- `maxToolCalls` bounds parsed deterministic or inference-proposed action plans before any tool executes.
- `maxCommandSeconds` bounds individual tool invocations and kills timed-out sandboxed child process trees.
