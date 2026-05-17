# Security Model

Bubo treats repository content, model output, and generated tool requests as untrusted.

## Default Posture

- Docker sandbox execution defaults to `--network none`.
- The sandbox run builder drops Linux capabilities, enables `no-new-privileges`, and uses a read-only root filesystem where practical.
- Input and model mounts are read-only.
- Workspace, output, and cache mounts are the only writable mounts.
- File tools canonicalize paths through `WorkspaceGuard` and reject traversal, `.git` metadata edits, and symlink or reparse-point escape paths.
- `patch_file` is a bounded exact old/new text replacement and fails when the old text is absent, ambiguous, oversized, or unsafe to target.
- Agent-driven command execution runs through the Docker sandbox runner, avoids shell expansion, and starts from a small executable allowlist.
- `git_apply_patch` preflights unified diff paths before invoking Docker-backed `git apply --check` and `git apply`.
- Inference output is not executed directly. Bubo only parses fenced `bubo-actions` JSON arrays, rejects invalid or oversized action plans, rejects unknown tools, and routes accepted actions through a model-safe guarded tool registry.
- Inference-generated repair retries exclude generic `run_command`; deterministic/user-authored action fixtures can still use it for guarded validation commands.
- Runtime config limits override action-supplied patch and file-count limits, so model output cannot raise its own safety ceilings.
- Workspace-default `bubo.config.json` is treated as repository content and cannot set sandbox policy. Explicit `--config` is required before Bubo accepts network, GPU, Docker image, or model mount settings from config.
- OpenCaw bootstrap policy is trusted configuration. The CLI default uses `.opencaw`, verifies it is a Git checkout with the expected OpenCaw origin, and only then runs the scaffold script when host `.ai` files are missing.
- Sandboxed command execution is bounded by `MaxCommandSeconds`; timed-out child process trees are killed.
- `gh` and Git operations are available but Bubo does not auto-push or auto-create PRs unless the surrounding goal-flow explicitly asks for it.

## GPU Notes

NVIDIA GPU access uses Docker `--gpus all` and requires NVIDIA Container Toolkit on the host. Exposing GPU devices expands the container attack surface, so CPU mode remains the safer default when local inference performance is not required.

Future AMD/ROCm, Vulkan, and Metal support should be added as explicit profiles with their own native packages and sandbox notes.

## Network Modes

- `none`: no network access.
- `package-restore`: bridge networking for dependency restore windows.
- `research`: bridge networking for controlled research/download workflows.
- `full`: bridge networking only when explicitly requested.

Research should be mediated outside the sandbox when possible instead of giving arbitrary network access to a generated command stream.

## OpenCaw Bootstrap

OpenCaw initialization happens before `INPUT.md` is read so the model starts with the baseline instructions and host project memory. That bootstrap can update a Git submodule and execute OpenCaw scaffold shell scripts, so Bubo treats the OpenCaw path/ref/repository settings as trusted policy.

Mitigations:

- OpenCaw path must be a direct workspace child such as `.opencaw`; nested paths, absolute paths, `.git`, `.ai`, path traversal, and reparse points are rejected.
- The checkout must be a Git repository or submodule.
- The checkout `origin` must match the configured OpenCaw repository URL.
- Existing `.ai` memory, rules, debug notes, fragments, learnings, and task ledgers are host-owned and retained.
- Workspace-default `bubo.config.json` cannot redirect OpenCaw bootstrap. Use explicit `--config` or CLI flags when deliberately changing bootstrap behavior.

## Known V1 Limits

- The deterministic `bubo-actions` fixture format is intended for validation and controlled automation.
- `run_command`, `git_status`, `git_diff`, and `git_apply_patch` require a Docker sandbox runner in normal CLI execution; unit tests inject fake runners for deterministic coverage.
- Model-driven action repair is a bounded guarded path. Separate planner/coder autonomy is still roadmap work.
- Debug logs and transcripts may contain untrusted model or tool output, with event payload truncation for readability.
- Native llama.cpp package publishing requires populated native assets and per-RID smoke tests before release.
