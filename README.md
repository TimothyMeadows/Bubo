# Bubo

Bubo is a .NET 8 LTS local/cloud coding-agent runtime. The project is building toward a local-first agent that can inspect repositories, edit files, run commands, and produce auditable Markdown output inside a Docker sandbox.

The name comes from the mythical robotic owl created by Hephaestus.

## Current V1 Surface

- `INPUT.md` is the task input.
- `OUTPUT.md` is the user-facing result.
- `agent-debug.jsonl` records structured observable events.
- `agent-transcript.md` records a readable event transcript and does not expose hidden chain-of-thought.
- Deterministic `bubo-actions` fixtures can run guarded file writes and allowlisted commands without a model.
- Local llama.cpp and cloud codex-cli inference providers share abstractions, with native llama.cpp assets scaffolded for pinned packaging.

## Quick Start

```bash
dotnet restore Bubo.sln
dotnet build Bubo.sln --no-restore
dotnet test Bubo.sln --no-build
```

Run a fixture:

```bash
dotnet run --project src/LocalAgent.Cli -- run --workspace examples/file-edit --mode local
```

That writes `examples/file-edit/OUTPUT.md`, `agent-debug.jsonl`, and `agent-transcript.md`. The file-edit fixture also writes `examples/file-edit/generated/result.txt`.

Run the scripted end-to-end fixture:

```powershell
pwsh ./scripts/run-e2e-fixture.ps1
```

Utility commands:

```bash
dotnet run --project src/LocalAgent.Cli -- doctor
dotnet run --project src/LocalAgent.Cli -- models list
dotnet run --project src/LocalAgent.Cli -- sandbox test --workspace .
dotnet run --project src/LocalAgent.Cli -- native test
```

## Fixture Actions

`INPUT.md` may include a fenced `bubo-actions` JSON array:

````text
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
    "tool": "run_command",
    "arguments": {
      "executable": "dotnet",
      "arguments": ["--version"]
    }
  }
]
```
````

The current command tool avoids shell expansion and allows only `dotnet`, `git`, and `gh`.

## Docker Sandbox

The sandbox image is defined at `docker/bubo-sandbox/Dockerfile`. It includes the .NET SDK, `git`, GitHub CLI `gh`, `openssh-client`, `curl`, `jq`, and CA certificates.

The Docker run builder defaults to:

- no network access,
- dropped Linux capabilities,
- `no-new-privileges`,
- read-only root filesystem where practical,
- read-only input/model mounts,
- writable workspace/output/cache mounts.

NVIDIA GPU mode uses Docker `--gpus all` and requires NVIDIA Container Toolkit on the host. CPU fallback remains supported when GPU access is unavailable.

## Cloud Mode

Cloud mode is scaffolded through `codex-cli`. The current spike targets `codex exec` with stdin prompt input, `--output-last-message`, `--json`, `--cd`, `--sandbox read-only`, and `--ask-for-approval never`.

The local environment used for this scaffold reported `codex-cli 0.131.0-alpha.9`, so command flags should be rechecked before relying on a newer CLI release.

## Native llama.cpp

The native wrapper path is pinned to upstream `ggml-org/llama.cpp` release `b9189`, commit `64b38b561b987679c4e1c6231f93860d3eec2638`.

Bubo packages native assets under:

```text
runtimes/
  win-x64/native/llama.dll
  linux-x64/native/libllama.so
  osx-arm64/native/libllama.dylib
```

The wrapper is designed for direct P/Invoke and `SafeHandle` ownership rather than host-installed `llama-cli`, `llama-server`, or Ollama.

## More

- Architecture: `ARCHITECTURE.md`
- Security model: `docs/security.md`
- Packaging: `docs/packaging.md`
- Configuration: `docs/configuration.md`
- Examples: `examples/README.md`
