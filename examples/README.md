# Bubo Examples

Each example is a workspace-shaped folder with an `INPUT.md` file. Run one by pointing the CLI at the folder:

```bash
dotnet run --project src/LocalAgent.Cli -- run --workspace examples/file-edit --mode local
```

The examples use the deterministic `bubo-actions` block so they can run without a local model. When a run input omits `bubo-actions`, the CLI can ask the selected local/cloud inference provider for the same fenced JSON shape and retry after guarded tool failures within configured limits; keep examples deterministic unless you are intentionally testing provider behavior.

`examples/bubo.config.json` shows workspace-default configuration for mode, model profiles, and lowered runtime limits. Bubo auto-loads a file with that name from the selected workspace when it exists.

`examples/bubo.trusted.config.json` shows sandbox policy. Pass sandbox policy with `--config <path>` only when you explicitly trust the file:

```bash
dotnet run --project src/LocalAgent.Cli -- run --workspace examples/file-edit --config examples/bubo.trusted.config.json
```

## Examples

- `no-op`: validates the v1 input/output contract without tool calls.
- `file-edit`: writes a bounded file inside the example workspace.
- `patch-file`: patches a bounded file with exact old/new text replacement.
- `git-apply-patch`: applies a guarded unified diff through Docker-backed `git apply`.
- `command-execution`: runs `dotnet --version` through the allowlisted command tool inside the Docker sandbox. Build `bubo-sandbox:local` before running this fixture.

Generated files such as `OUTPUT.md`, `agent-debug.jsonl`, and `agent-transcript.md` are intentionally not committed.
