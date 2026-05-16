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
    "image": "bubo-sandbox:latest",
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
