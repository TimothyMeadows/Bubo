# ARCHITECTURE.md

This file is the canonical architecture contract for this repository.

Generated from OpenCaw architecture templates:
- DOTNET
- PLAYWRIGHT
- DOTNET_ASPIRE
- MARKDOWN
- DOCKER

---

This document intentionally stays concise by referencing selected templates.

## Read Template Instructions

Read `./.codex/.architecture/DOTNET.md` instructions
Read `./.codex/.architecture/PLAYWRIGHT.md` instructions
Read `./.codex/.architecture/DOTNET_ASPIRE.md` instructions
Read `./.codex/.architecture/MARKDOWN.md` instructions
Read `./.codex/.architecture/DOCKER.md` instructions

Add repository-specific architecture instructions below these read directives.

## Repository-Specific Notes

- Docker expectations are governed by `./.codex/.architecture/DOCKER.md`. Keep image construction, runtime configuration, and deployment concerns separate when adding or changing container assets.
- Bubo targets .NET 8 LTS. All production projects use `net8.0`; `global.json` permits newer installed SDKs only through roll-forward.
- The runtime is file-driven in v1: `INPUT.md` in, `OUTPUT.md`, `agent-debug.jsonl`, and `agent-transcript.md` out.
- All file and command tools must pass through `WorkspaceGuard` or an equivalent sandbox-root check before touching the filesystem.
- Docker is the required execution sandbox for agent-driven command execution. Network access must be explicit, and `none` is the default policy.
- Local inference uses direct llama.cpp interop through managed wrappers and RID native assets. Do not introduce a dependency on host-installed `llama-cli`, `llama-server`, Ollama, or a long-running llama service.
- Cloud inference is isolated behind the same inference abstractions and currently targets non-interactive `codex-cli`.
- `bubo-actions` fixtures are deterministic validation inputs; they are not a substitute for the future planner/coder model loop.
