# Project Memory

## Bubo Architecture Facts

- Bubo targets .NET 8 LTS and uses `global.json` roll-forward so newer local SDKs can build `net8.0`.
- The canonical v1 file contract is `INPUT.md` in and `OUTPUT.md`, `agent-debug.jsonl`, and `agent-transcript.md` out.
- Bubo's intended runtime separates contracts, orchestration, inference providers, Docker sandboxing, and CLI entrypoint across `LocalAgent.Abstractions`, `LocalAgent.Runtime`, `LocalAgent.Inference.*`, `LocalAgent.Sandbox.Docker`, and `LocalAgent.Cli`.
- Local inference is intended to use direct C# interop over pinned llama.cpp native assets; avoid introducing host-installed `llama-cli`, `llama-server`, Ollama, or daemon dependencies.
- Cloud inference is intended to use `codex-cli` behind the same inference abstraction; the locally observed CLI version during setup was `codex-cli 0.131.0-alpha.9`, so invocation flags should be rechecked before relying on newer versions.

## Repository State Notes

- As of the task-ledger cleanup after PRs #7-#11, `main` contains the foundation slice and documentation/task-ledger updates, while PRs #8-#11 were merged into stacked feature branches rather than directly into `main`.
- Goal-flow task issues #2 through #6 are closed as completed, and `.ai/tasks/OPEN_ISSUES.md` should remain empty until new open task issues are created.
