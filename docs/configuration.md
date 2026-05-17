# Configuration

Bubo loads JSON configuration for `bubo run` without requiring code changes.

## Discovery

`bubo run` uses this precedence order:

1. CLI-safe defaults.
2. `<folder>/bubo.config.json` when the file exists.
3. `--config <path>` when supplied. Relative paths resolve from the current shell directory.
4. Explicit CLI flags. Today `--mode` overrides config mode.

No-config behavior uses Docker when available, keeps network at `none`, avoids GPU and host model mounts by default, and enables OpenCaw bootstrap from the folder `.opencaw` submodule.

`--folder` is the shared code and artifact folder Bubo may inspect and modify. `--input` may be either a Markdown file path outside that folder or an inline Markdown prompt string. `--output` must stay under `<folder>/.ai/artifacts`. File tools, Git operations, OpenCaw context, and sandboxed commands still operate on their requested paths inside `--folder`.

## Trust Boundary

Folder `bubo.config.json` is repository content, so Bubo treats it as untrusted. It can configure mode, model profiles, and runtime limits. It cannot configure sandbox policy or OpenCaw bootstrap policy.

Sandbox policy requires an explicit `--config` path. That explicit flag is the user trust signal for settings such as:

- Docker image.
- Network mode.
- GPU exposure.
- Read-only model mount.
- Memory, CPU, and PID limits.

Bubo never accepts config overrides for workspace, input, output, cache, or container working-directory host paths. Tool and command mounts are derived from the guarded folder, and report artifacts are written by the host runtime under `<folder>/.ai/artifacts`.

OpenCaw policy also requires an explicit `--config` trust signal because it controls host-side submodule update and bootstrap script execution. Folder-default config cannot redirect the OpenCaw path, repository URL, or ref. OpenCaw loading and bootstrap execution cannot be disabled.

## Folder Default Example

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
bubo run --folder ./repo
```

`threads: 0` means the runtime uses `Environment.ProcessorCount`.

Runtime limits are safety ceilings. Config may lower the built-in defaults but cannot raise them. `maxCommandSeconds` must be positive so config cannot disable timeouts.

## Trusted Sandbox Example

Pass trusted sandbox policy explicitly:

```bash
bubo run --folder ./repo --config ./bubo.trusted.config.json
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

`sandbox.gpu` accepts `nvidia` or `none`. NVIDIA mode only exposes Docker GPU devices; model offload still also requires a CUDA-enabled native llama.cpp backend and model `gpuLayers` configuration.

## OpenCaw Example

CLI defaults:

```text
openCaw.repositoryUrl https://github.com/TimothyMeadows/OpenCaw
openCaw.path .opencaw
openCaw.ref main
openCaw.updateOnRun true
```

Common one-off flags:

```bash
bubo run --folder ./repo --opencaw-update false
bubo run --folder ./repo --opencaw-path .opencaw --opencaw-ref main
```

Trusted config can override OpenCaw policy:

```json
{
  "openCaw": {
    "repositoryUrl": "https://github.com/TimothyMeadows/OpenCaw",
    "path": ".opencaw",
    "ref": "main",
    "updateOnRun": true
  }
}
```

Bubo verifies the OpenCaw checkout is a Git checkout and that `origin` matches the configured repository before loading baseline instructions or running the scaffold script. There is no `openCaw.enabled` or `openCaw.executeBootstrap` setting because OpenCaw loading and bootstrap execution are always active.

## Cloud Mode Example

```json
{
  "mode": "cloud"
}
```

Cloud mode delegates inference to `codex-cli`. Keep `codex` on `PATH`; provider-specific model/profile flags are not yet part of this config schema.

An explicit CLI mode wins:

```bash
bubo run --folder ./repo --mode local
```

If `bubo.config.json` says `cloud`, the command above still runs local.

The inference-driven runtime uses the `coder` model profile for bounded generated-action retries when `INPUT.md` does not contain a deterministic `bubo-actions` fence. Future planner/coder orchestration will use both profiles across multiple steps. Inference-generated actions use a model-safe tool registry and do not expose generic `run_command`.

Patch and file-change limits are used by deterministic tools:

- `maxIterations` bounds inference-generated action repair attempts.
- `maxPatchBytes` bounds `patch_file` old/new payloads and `git_apply_patch` unified diff payloads.
- `maxFilesChanged` bounds the number of file paths accepted by `git_apply_patch` preflight scanning.
- `maxToolCalls` bounds parsed deterministic and inference-generated action plans before any tool executes.
- `maxCommandSeconds` bounds individual tool invocations and kills timed-out sandboxed child process trees.
