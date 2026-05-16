# Bubo Examples

Each example is a workspace-shaped folder with an `INPUT.md` file. Run one by pointing the CLI at the folder:

```bash
dotnet run --project src/LocalAgent.Cli -- run --workspace examples/file-edit --mode local
```

The examples use the deterministic `bubo-actions` block so they can run without a local model.

## Examples

- `no-op`: validates the v1 input/output contract without tool calls.
- `file-edit`: writes a bounded file inside the example workspace.
- `command-execution`: runs `dotnet --version` through the allowlisted command tool.

Generated files such as `OUTPUT.md`, `agent-debug.jsonl`, and `agent-transcript.md` are intentionally not committed.
