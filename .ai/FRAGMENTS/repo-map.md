# Repository Map

## Architecture Templates

- Canonical architecture contract: `ARCHITECTURE.md`
- Selected OpenCaw templates: `DOTNET`, `PLAYWRIGHT`, `DOTNET_ASPIRE`, `MARKDOWN`, `DOCKER`
- Docker expectations are governed by `./.codex/.architecture/DOCKER.md`.

## Bubo Solution Layout

- `src/LocalAgent.Abstractions`: shared agent, inference, sandbox, tool, model, run, and transcript contracts.
- `src/LocalAgent.Runtime`: workspace guarding, run orchestration, output/report artifact generation, deterministic file tools, Docker-backed Git/command tools, and tool dispatch.
- `src/LocalAgent.Cli`: CLI entrypoint for `bubo run`.
- `src/LocalAgent.Sandbox.Docker`: Docker sandbox argument construction, security defaults, mount validation, and runtime implementation surface.
- `src/LocalAgent.Inference.LlamaCpp`: local inference provider surface for direct llama.cpp interop.
- `src/LocalAgent.Inference.CodexCli`: cloud inference provider surface for `codex-cli`.
- `src/LlamaCppSharp.Native`: native llama.cpp package metadata and RID asset layout.
- `src/LlamaCppSharp`: managed llama.cpp wrapper surface.
- `tests/*`: focused unit/smoke coverage for contracts, runtime, CLI, sandbox, and native wrapper behavior.
