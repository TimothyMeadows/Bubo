# Subagent Plan: bubo-agent-folder-workspace

## Capacity

- Requested: 5
- Effective lanes: 5
- Reason: Main agent owns integration and four explorer lanes cover runtime, CLI/config, sandbox/security, and QA/docs without write conflicts.

## Rules

- Subagents are read-only explorers for this task.
- Main agent owns implementation, integration, final verification, and user communication.
- Do not revert edits made by other lanes.

## Lanes

### lane-1

- Role: `computer-science/backend-architect`
- Agent type: explorer
- Status: completed
- Scope: Runtime path model and `AgentRunner` code-folder/input/output split.
- Write set: none
- Dependencies: none
- Expected output: Minimal runtime design and edge cases.
- Verification: File references inspected.

### lane-2

- Role: `computer-science/senior-developer`
- Agent type: explorer
- Status: completed
- Scope: CLI parser/config/program wiring for `--folder`, `--workspace`, input, and output.
- Write set: none
- Dependencies: none
- Expected output: Parser and Program change map.
- Verification: File references inspected.

### lane-3

- Role: `computer-science/security-engineer`
- Agent type: explorer
- Status: completed
- Scope: Sandbox mounts, path traversal, output containment, and symlink/reparse risks.
- Write set: none
- Dependencies: none
- Expected output: Security constraints and tests.
- Verification: File references inspected.

### lane-4

- Role: `computer-science/qa-engineer`
- Agent type: explorer
- Status: completed
- Scope: Unit/E2E/doc coverage for INPUT + FOLDER -> OUTPUT.
- Write set: none
- Dependencies: none
- Expected output: Focused test matrix and doc touchpoints.
- Verification: File references inspected.

### lane-5

- Role: main agent
- Agent type: orchestrator/worker
- Status: completed
- Scope: Implement runtime, CLI, docs, and tests.
- Write set: repository changes for issue #33.
- Dependencies: lane findings when available.
- Expected output: Working implementation and validation evidence.
- Verification: Build, test, format, diff check.

## Results

- lane-1 confirmed the runtime already had separate request fields but `AgentRunner` forced input/output through `WorkspaceGuard`; the final clarified design keeps input externally readable while output remains folder-contained.
- lane-2 mapped the CLI change: add `--folder`, preserve `--workspace`, detect conflicts, and keep config loading rooted in the effective folder.
- lane-3 identified the main security constraint: do not pass external output directories into sandbox tool mounts; final output artifacts are written inside the shared folder.
- lane-4 supplied the focused test and docs matrix for external input, sidecar placement, parser aliases, and path escape protection.
- lane-5 implemented and validated the feature with targeted runtime, CLI, E2E, and documentation changes.
