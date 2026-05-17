# Bubo.LocalAgent.Cli

Command-line entrypoint for the Bubo local/cloud coding agent runtime.

The v1 CLI reads `INPUT.md`, writes `OUTPUT.md`, `agent-debug.jsonl`, and `agent-transcript.md`, and keeps file writes inside the configured workspace guard.

`bubo run` auto-loads `<workspace>/bubo.config.json` for mode, model profiles, and runtime limits. Use `--config <path>` when intentionally trusting sandbox policy such as network, GPU, image, and read-only model mount settings.
