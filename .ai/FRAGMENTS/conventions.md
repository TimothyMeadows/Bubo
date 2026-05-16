# Conventions

## Bubo Documentation

- Keep README feature claims aligned with the current checkout. Use explicit status language such as Available, Scaffolded, and Planned when stacked goal work has not landed on `main`.
- Document Bubo's output contract as `INPUT.md` -> `OUTPUT.md`, `agent-debug.jsonl`, and `agent-transcript.md`.
- Do not claim transcripts expose hidden chain-of-thought; describe them as observable event logs and concise reasoning summaries only.

## Bubo Runtime

- Keep agent execution capabilities behind contracts and guarded tools rather than direct model-controlled file or process access.
- File and command tools must enforce workspace-root boundaries before reading, writing, patching, or running commands.
- Do not add dependencies on host-installed llama.cpp executables; local inference should flow through managed interop and packaged native assets.
