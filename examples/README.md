# Bubo Examples

Each example is a workspace-shaped folder with an `INPUT.md` file. Run one by pointing the CLI at the folder:

```bash
dotnet run --project src/LocalAgent.Cli -- run --workspace examples/file-edit --mode local
```

The examples use the deterministic `bubo-actions` block so they can run without a local model.

## Examples

- `no-op`: validates the v1 input/output contract without tool calls.
- `file-edit`: writes a bounded file inside the example workspace.
- `patch-file`: patches a bounded file with exact old/new text replacement.
- `git-apply-patch`: applies a guarded unified diff through Docker-backed `git apply`.
- `command-execution`: runs `dotnet --version` through the allowlisted command tool inside the Docker sandbox. Build `bubo-sandbox:local` before running this fixture.

Generated files such as `OUTPUT.md`, `agent-debug.jsonl`, and `agent-transcript.md` are intentionally not committed.
