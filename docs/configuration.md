# Configuration

Bubo's v1 runtime exposes model and sandbox defaults in code and documents the intended JSON shape for the next config-loader slice.

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
  "sandbox": {
    "network": "none",
    "gpu": "nvidia",
    "image": "bubo-sandbox:local",
    "readOnlyRootFilesystem": true,
    "dropAllCapabilities": true,
    "noNewPrivileges": true
  },
  "limits": {
    "maxIterations": 8,
    "maxToolCalls": 80,
    "maxCommandSeconds": 600,
    "maxPatchBytes": 262144,
    "maxFilesChanged": 25,
    "maxTokensPerStep": 8192
  }
}
```

`threads: 0` means the runtime should use `Environment.ProcessorCount` when a persisted config loader is added.

The first inference-driven runtime slice uses the `coder` model profile for one-shot action proposal when `INPUT.md` does not contain a deterministic `bubo-actions` fence. Future planner/coder orchestration will use both profiles across multiple steps. One-shot inference uses a model-safe tool registry and does not expose generic `run_command`.

Patch and file-change limits are used by deterministic tools:

- `maxPatchBytes` bounds `patch_file` old/new payloads and `git_apply_patch` unified diff payloads.
- `maxFilesChanged` bounds the number of file paths accepted by `git_apply_patch` preflight scanning.
- `maxToolCalls` bounds parsed deterministic or inference-proposed action plans before any tool executes.
- `maxCommandSeconds` bounds individual tool invocations and kills timed-out sandboxed child process trees.
