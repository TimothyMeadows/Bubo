# Subagent Plan: bubo-main-stack-integration

## Capacity
- Requested: 5 developers
- Effective lanes: 5
- Reason: The work has one critical integration lane owned by the main agent and four parallel review/validation lanes with no overlapping write ownership.

## Rules
- Resolve each lane role with `./commands/resolve-role.sh` before delegation.
- Use `explorer` for read-only lanes and `worker` for implementation lanes when Codex subagents are available.
- Worker lanes must declare disjoint write sets.
- Keep the main agent responsible for orchestration, critical-path blockers, integration, final verification, and user communication.

## Lanes

### lane-1
- Role: computer-science/git-workflow-master
- Agent type: default
- Status: in_progress
- Scope: Main-agent lane. Integrate `origin/feature/bubo-agent-runtime-inference` onto `main`, resolve conflicts, preserve README and `.ai` context from PR #12, and prepare PR.
- Write set: repository integration branch
- Dependencies: none
- Expected output: Integrated branch and final PR.
- Verification: Full local validation, PR creation, and post-PR QA.

### lane-2
- Role: computer-science/software-architect
- Agent type: explorer
- Status: completed
- Scope: Review stack diff for architectural integration risks and missing files.
- Write set: none
- Dependencies: none
- Expected output: Summary of integration risks, files that must land, and any conflicts requiring attention.
- Verification: Compare `main..origin/feature/bubo-agent-runtime-inference` and report findings.

### lane-3
- Role: computer-science/technical-writer
- Agent type: explorer
- Status: completed
- Scope: Review README and `.ai` context preservation risks during integration.
- Write set: none
- Dependencies: none
- Expected output: Summary of documentation/context files that should be preserved from `main` or updated from stack.
- Verification: Compare README and `.ai` diffs and report findings.

### lane-4
- Role: computer-science/devops-automator
- Agent type: explorer
- Status: completed
- Scope: Review Docker, CI, package, and host-tool validation requirements.
- Write set: none
- Dependencies: none
- Expected output: Validation checklist for Docker, CMake/Ninja, GH CLI, package workflows, and CI files.
- Verification: Inspect Dockerfile, workflows, scripts, and available host tools.

### lane-5
- Role: computer-science/qa-engineer
- Agent type: explorer
- Status: completed
- Scope: Review test coverage and propose the final validation matrix for the integrated branch.
- Write set: none
- Dependencies: none
- Expected output: Test and smoke validation checklist with residual risks.
- Verification: Inspect test projects and fixture scripts.

## Integration
- Merge order: lane-1 integrates locally while lanes 2-5 run read-only in parallel, then lane-1 incorporates findings.
- Conflict risks: README and `.ai` files have divergent history between PR #12 and the stacked branch; prefer `main` unless stack has missing goal-report/task facts.
- Final verification: Run full local validation, generate PR readiness with `--goal`, open PR, and post PR QA.

## Results

- lane-2 software architecture review: confirmed the stack contains the required implementation files, warned that command execution was still host-backed, identified native llama.cpp packaging as scaffolded rather than release-ready, and recommended preserving current `main` README and `.ai` context. Integrated response: `run_command`, `git_status`, and `git_diff` now require a sandbox runner, and the README/goal files are being resolved from the PR #12 documentation baseline.
- lane-3 technical writing review: recommended keeping the expanded PR #12 README, current memory/fragments/learnings, open issue #13 only, and adding clear links to stack docs/examples. Integrated response: README keeps the exhaustive structure, updates current feature status, and links `docs/configuration.md`, `docs/security.md`, `docs/packaging.md`, and `examples/README.md`.
- lane-4 DevOps review: identified package naming drift, host tool PATH realities, Docker smoke validation needs, and native asset release limitations. Integrated response: native package ID/readme now use `Bubo.LlamaCppSharp.Native`, Docker discovery has a Windows fallback, and final validation includes Docker image build/smoke plus tool-version checks.
- lane-5 QA review: supplied the final validation matrix covering merge cleanliness, Release build/test, E2E fixture, packaging, Docker smoke, doctor/models, codex-cli, CMake, and Ninja checks. Integrated response: Release build and tests have passed; remaining checklist items will run after the merge index is clean.
