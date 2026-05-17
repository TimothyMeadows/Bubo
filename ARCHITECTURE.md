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

Read `./.opencaw/.architecture/DOTNET.md` instructions
Read `./.opencaw/.architecture/PLAYWRIGHT.md` instructions
Read `./.opencaw/.architecture/DOTNET_ASPIRE.md` instructions
Read `./.opencaw/.architecture/MARKDOWN.md` instructions
Read `./.opencaw/.architecture/DOCKER.md` instructions

Add repository-specific architecture instructions below these read directives.

## Repository-Specific Notes

- Docker expectations are governed by `./.opencaw/.architecture/DOCKER.md`. Keep image construction, runtime configuration, and deployment concerns separate when adding or changing container assets.
- Bubo targets .NET 8 LTS. All production projects use `net8.0`; `global.json` permits newer installed SDKs only through roll-forward.
- The runtime is file-driven in v1: `INPUT.md` or inline Markdown in, Markdown report to stdout, with Bubo-owned review sidecars under `.ai/artifacts`.
- Runtime startup initializes OpenCaw from the workspace `.opencaw` submodule before reading `INPUT.md`; host project memory remains under `.ai` and must not be written into the OpenCaw baseline.
- All file and command tools must pass through `WorkspaceGuard` or an equivalent sandbox-root check before touching the filesystem.
- Docker is the required execution sandbox for agent-driven command execution. Network access must be explicit, and `none` is the default policy.
- Local inference uses direct llama.cpp interop through managed wrappers and RID native assets. Do not introduce a dependency on host-installed `llama-cli`, `llama-server`, Ollama, or a long-running llama service.
- Cloud inference is isolated behind the same inference abstractions and currently targets non-interactive `codex-cli`.
- `bubo-actions` fixtures are deterministic validation inputs; they are not a substitute for the future planner/coder model loop.
