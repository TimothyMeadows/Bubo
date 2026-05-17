# Subagent Plan: bubo-opencaw-bootstrap

## Capacity

- Requested: 5
- Effective lanes: 5
- Reason: Main agent owns integration and four read-only explorer lanes provide architecture, security, implementation, and QA guidance without write conflicts.

## Rules

- Resolve each lane role with `./commands/resolve-role.sh` before delegation when available.
- Use `explorer` for read-only lanes.
- Keep the main agent responsible for implementation, integration, final verification, and user communication.

## Lanes

### lane-1

- Role: `computer-science/backend-architect`
- Agent type: explorer
- Status: completed
- Scope: Runtime/OpenCaw bootstrap architecture and startup command sequencing.
- Write set: none
- Dependencies: none
- Expected output: Placement and lifecycle recommendations for submodule update, bootstrap script execution, context loading, and input read.
- Verification: File references inspected.

### lane-2

- Role: `computer-science/security-engineer`
- Agent type: explorer
- Status: completed
- Scope: Config trust, path validation, script execution safety, and workspace `.ai` preservation.
- Write set: none
- Dependencies: none
- Expected output: Security constraints and tests.
- Verification: File references inspected.

### lane-3

- Role: `computer-science/senior-developer`
- Agent type: explorer
- Status: completed
- Scope: CLI/config/inference interface changes.
- Write set: none
- Dependencies: none
- Expected output: Minimal implementation map and tests.
- Verification: File references inspected.

### lane-4

- Role: `computer-science/qa-engineer`
- Agent type: explorer
- Status: completed
- Scope: Runtime/CLI/config/provider test plan and fake OpenCaw fixtures.
- Write set: none
- Dependencies: none
- Expected output: Verification plan with low-flake fixtures.
- Verification: File references inspected.

### lane-5

- Role: main agent
- Agent type: orchestrator/worker
- Status: completed
- Scope: Implement runtime/config/CLI/docs/tests and integrate lane findings.
- Write set: repository changes for issue #31.
- Dependencies: lane findings when available, but not blocking initial implementation.
- Expected output: Working implementation and validation evidence.
- Verification: Full validation plan.

## Integration

- Merge order: main implementation, then fold in lane findings before final verification.
- Conflict risks: Low because subagents are read-only.
- Final verification: Build, test, format, diff check.

## Results

- `lane-1`: Recommended runtime startup placement before `INPUT.md`, submodule update/bootstrap sequencing, and stable context loading into system prompt.
- `lane-2`: Recommended trust-boundary constraints for OpenCaw path, origin verification, config trust, script execution, and `.ai` preservation.
- `lane-3`: Recommended smallest CLI/config/inference wiring for `InferenceRequest.SystemPrompt` and Codex prompt composition.
- `lane-4`: Recommended fake OpenCaw fixtures, opt-out CLI E2E updates, and test coverage across runtime, config, parser, and provider prompt paths.
- `lane-5`: Implemented runtime, CLI/config, inference, docs, tests, and task/memory updates; validation passed with build, full tests, format, and diff check.
