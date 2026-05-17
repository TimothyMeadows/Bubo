# Subagent Plan: bubo-inference-action-loop

## Capacity

- Requested: 3 developers
- Effective lanes: 3
- Reason: One main implementation lane owns code changes, while two read-only review lanes can inspect runtime/inference shape and validation/security risks without conflict.

## Lanes

### lane-1
- Role: computer-science/senior-developer
- Agent type: default
- Status: in_progress
- Scope: Main-agent lane. Implement inference-driven action proposal, CLI provider wiring, tests, docs, PR, and post-PR QA.
- Write set: repository branch
- Dependencies: none
- Expected output: Implementation and validated PR.
- Verification: Full local validation plus post-PR QA.

### lane-2
- Role: computer-science/software-architect
- Agent type: explorer
- Status: completed
- Scope: Review runtime and inference abstractions for the smallest safe action-generation loop.
- Write set: none
- Dependencies: none
- Expected output: Integration recommendations, likely file touch points, and risks.
- Verification: Inspect runtime, abstractions, CLI, and tests.

### lane-3
- Role: computer-science/security-engineer
- Agent type: explorer
- Status: completed
- Scope: Review model-output handling risks for inference-proposed tool actions.
- Write set: none
- Dependencies: none
- Expected output: Security constraints and test cases.
- Verification: Inspect tool parser, tool registry, workspace guard, sandbox usage, and docs.

## Integration

- Main lane implements while lanes 2-3 inspect in parallel.
- Incorporate lane findings before final validation.
- PR will target `main` because PR #16 has merged.

## Results

- lane-2 architecture review: endorsed the one-shot inference action loop, recommended workspace-aware `codex-cli`, richer prompt schemas, source-aware audit wording, provider failure handling, and tests for no-action/error/limit cases. Integrated response: CLI cloud mode now passes the requested workspace to `CodexCliOptions`, prompts include argument shapes and constraints, inference failures/exceptions write artifacts, action-source wording is accurate, and runtime tests cover success/no-action/invalid/throwing/limit cases.
- lane-3 security review: flagged generic `run_command` exposure to model output, model-controllable patch limits, unenforced command timeout, nested reparse traversal in list/search, and multiple-fence ambiguity. Integrated response: one-shot inference now uses a model-safe registry without `run_command`, runtime overrides patch/file-count limits from config, tool calls get `MaxCommandSeconds` cancellation, Docker process trees are killed on cancellation, list/search skip reparse points, and model output must contain at most one `bubo-actions` fence.
