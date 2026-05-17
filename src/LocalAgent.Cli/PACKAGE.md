# Bubo.LocalAgent.Cli

Command-line entrypoint for the Bubo local/cloud coding agent runtime.

The v1 CLI reads a Markdown input file or inline Markdown prompt text, writes the run report to stdout, writes review sidecars under `.ai/artifacts`, and keeps code file writes inside the configured folder guard.

`bubo run --folder <path>` auto-loads `<folder>/bubo.config.json` for mode, model profiles, and runtime limits. Use `--config <path>` when intentionally trusting sandbox policy such as network, GPU, image, and read-only model mount settings.
