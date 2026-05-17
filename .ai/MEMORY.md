# Project Memory

## Bubo Architecture Facts

- Bubo targets .NET 8 LTS and uses `global.json` roll-forward so newer local SDKs can build `net8.0`.
- The canonical v1 file contract is `INPUT.md` in and `OUTPUT.md`, `agent-debug.jsonl`, and `agent-transcript.md` out.
- Bubo's intended runtime separates contracts, orchestration, inference providers, Docker sandboxing, and CLI entrypoint across `LocalAgent.Abstractions`, `LocalAgent.Runtime`, `LocalAgent.Inference.*`, `LocalAgent.Sandbox.Docker`, and `LocalAgent.Cli`.
- Local inference is intended to use direct C# interop over pinned llama.cpp native assets; avoid introducing host-installed `llama-cli`, `llama-server`, Ollama, or daemon dependencies.
- Cloud inference is intended to use `codex-cli` behind the same inference abstraction; the locally observed CLI version during setup was `codex-cli 0.131.0-alpha.9`, so invocation flags should be rechecked before relying on newer versions.
- The runtime has deterministic patch tools: `patch_file` for exact old/new text replacement and Docker-backed `git_apply_patch` for guarded unified diffs.
- Workspace tools treat model output as untrusted and reject path traversal, `.git` metadata targets, and symlink/reparse-point escape paths.
- When `INPUT.md` has no deterministic `bubo-actions` fence, Bubo can do one-shot inference action proposal. The model-safe registry intentionally excludes generic `run_command`; accepted model actions still go through guarded tools.
- CLI `bubo run` initializes OpenCaw from the workspace `.opencaw` submodule before reading `INPUT.md`; host project context stays in `.ai` and is fed to inference as system prompt context.

## Repository State Notes

- As of the task-ledger cleanup after PRs #7-#11, `main` contains the foundation slice and documentation/task-ledger updates, while PRs #8-#11 were merged into stacked feature branches rather than directly into `main`.
- Goal-flow task issues #2 through #6 are closed as completed, and `.ai/tasks/OPEN_ISSUES.md` should remain empty until new open task issues are created.
- After PR #14, the completed Bubo goal stack is integrated onto `main`; follow-up goal tasks should branch from `main` unless a new task explicitly depends on an unmerged feature branch.
