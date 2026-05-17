# Subagent Plan: bubo-tool-hardening

## Capacity
- Requested: 5 developers
- Effective lanes: 5
- Reason: One main implementation lane owns writes, while four read-only review lanes can inspect architecture, security, docs, and QA without conflict.

## Rules
- Resolve each lane role with `./commands/resolve-role.sh` before delegation.
- Use explorer agents for read-only lanes.
- Keep the main agent responsible for implementation, integration, validation, PR creation, and post-PR QA.

## Lanes

### lane-1
- Role: computer-science/security-engineer
- Agent type: default
- Status: completed
- Scope: Main-agent lane. Implement workspace hardening and guarded patch tools.
- Write set: repository branch
- Dependencies: none
- Expected output: Implementation, tests, docs, PR, and QA evidence.
- Verification: Full local validation plus post-PR QA.

### lane-2
- Role: computer-science/software-architect
- Agent type: explorer
- Status: completed
- Scope: Review runtime/tool architecture for `patch_file`, `git_apply_patch`, and sandbox boundaries.
- Write set: none
- Dependencies: none
- Expected output: Integration risks and recommended implementation shape.
- Verification: Inspect runtime, abstractions, and tests.

### lane-3
- Role: computer-science/security-engineer
- Agent type: explorer
- Status: completed
- Scope: Review path traversal, symlink/reparse, command injection, and Git patch risks.
- Write set: none
- Dependencies: none
- Expected output: Security findings and required mitigations.
- Verification: Inspect guard/tool code and threat model docs.

### lane-4
- Role: computer-science/qa-engineer
- Agent type: explorer
- Status: completed
- Scope: Propose validation matrix and missing tests for workspace hardening and patch tools.
- Write set: none
- Dependencies: none
- Expected output: Test plan and residual risks.
- Verification: Inspect tests and fixture scripts.

### lane-5
- Role: computer-science/technical-writer
- Agent type: explorer
- Status: completed
- Scope: Review README/docs/examples that should change for patch tooling.
- Write set: none
- Dependencies: none
- Expected output: Documentation checklist.
- Verification: Inspect README, docs, and examples.

## Integration
- Main lane implements while lanes 2-5 inspect in parallel.
- Incorporate lane findings before final validation.
- PR will be stacked directly on current `main`.

## Results

- lane-2 architecture review: recommended `patch_file` as a native workspace tool, `git_apply_patch` as Docker-backed, registry registration, and result reporting updates. Integrated response: added `PatchFileTool`, `GitApplyPatchTool`, preflight scanning, registry entries, and `AgentRunner` file-change reporting.
- lane-3 security review: identified symlink/reparse escapes, `.git` metadata edits, sandbox mount trust, generic command risks, and patch preflight risks. Integrated response: workspace guard now rejects `.git` targets and symlink/reparse segments, file/search/list/write tools use hardened helpers, sandbox tools and Docker mount argument construction reject reparse mount roots, and `git_apply_patch` preflights dangerous paths/modes before Docker-backed `git apply`.
- lane-4 QA review: supplied coverage requirements for path hardening, patch tools, registry reachability, Docker-backed Git apply, and E2E fixtures. Integrated response: added unit tests for workspace guard hardening, patch tools, registry membership, runner reporting, and extended the scripted E2E fixture.
- lane-5 technical writing review: flagged docs still treating patch tools as planned and missing deterministic examples. Integrated response: updated README, security/config docs, examples README, and added `patch-file` plus `git-apply-patch` example inputs.
- lane-1 implementation result: PR #16 opened against `main`, post-PR QA posted, and GitHub Actions `dotnet` workflow run #6 passed.
