# Security Model

Bubo treats repository content, model output, and generated tool requests as untrusted.

## Default Posture

- Docker sandbox execution defaults to `--network none`.
- The sandbox run builder drops Linux capabilities, enables `no-new-privileges`, and uses a read-only root filesystem where practical.
- Input and model mounts are read-only.
- Workspace, output, and cache mounts are the only writable mounts.
- File tools canonicalize paths through `WorkspaceGuard` and reject traversal outside the workspace.
- Command execution avoids shell expansion and starts from a small executable allowlist.
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

## Known V1 Limits

- The deterministic `bubo-actions` fixture format is intended for validation and controlled automation.
- Model-driven planning/coding is scaffolded behind inference abstractions but not yet a fully autonomous loop.
- Native llama.cpp package publishing requires populated native assets and per-RID smoke tests before release.
