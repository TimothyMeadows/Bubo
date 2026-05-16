# Bubo Local/Cloud Coding Agent

## Flow
- Type: goal
- Automation: enabled
- PR readiness confirmation: automatic
- Post-PR QA: required before next task
- Auto-merge: disabled
- Merge approval: human only

## Outcome

Implement Bubo as a .NET 8 LTS coding-agent runtime with a Docker sandbox, local llama.cpp inference path, cloud codex-cli inference path, task/file contracts, and auditable Markdown/JSONL outputs.

## Success Criteria

- Bubo has a buildable .NET 8 solution and CLI.
- `INPUT.md` can drive a run that writes `OUTPUT.md`, `agent-debug.jsonl`, and `agent-transcript.md`.
- Docker sandbox execution is available and defaults to no network.
- llama.cpp native interop is represented by a pinned source/package plan and safe managed wrapper surface.
- Local and cloud inference providers share the same abstraction.
- End-to-end fixture validation proves writes stay inside the workspace.
- Each task PR has post-PR QA evidence before the next task advances.

## Constraints

- Base structure PR #1 is merged; subsequent task PRs target `main` unless they depend on an unmerged goal task branch.
- Target framework is `net8.0`.
- Do not auto-merge, approve, or enable auto-merge.
- Do not auto-push or create PRs except through goal-flow task completion.
- Treat model output as untrusted; all file and command operations must pass through guarded tools.

## Task Queue
1. [x] Bubo foundation contracts and CLI no-op flow (`../.ai/tasks/bubo-foundation-contracts/TASK.md`)
2. [ ] Bubo Docker sandbox runtime (`../.ai/tasks/bubo-docker-sandbox/TASK.md`)
3. [ ] Bubo llama.cpp native wrapper (`../.ai/tasks/bubo-llamacpp-native-wrapper/TASK.md`)
4. [ ] Bubo agent runtime and inference providers (`../.ai/tasks/bubo-agent-runtime-inference/TASK.md`)
5. [ ] Bubo end-to-end hardening and packaging (`../.ai/tasks/bubo-e2e-hardening-packaging/TASK.md`)

## Current Task

bubo-foundation-contracts

## Branch Chain
- base-structure | base: `main` | head: `chore/opencaw-base-structure` | PR: https://github.com/TimothyMeadows/Bubo/pull/1 | depends on: none | status: merged
- bubo-foundation-contracts | base: `main` | head: `feature/bubo-foundation-contracts` | PR: https://github.com/TimothyMeadows/Bubo/pull/7 | depends on: base-structure

## Automation Rules
- Complete one task at a time unless the project-manager lane plan explicitly marks safe parallel work.
- After each task completes local validation, generate PR readiness with `./commands/pr-readiness-check.sh --goal`.
- Automatically push/open a PR for the completed task without asking for human PR readiness confirmation.
- Run post-PR QA immediately after the PR is available.
- Do not advance to the next task until post-PR QA is complete.
- Never merge, auto-merge, approve, or enable auto-merge for goal PRs.
- If a future task depends on a previous task or has likely merge-conflict risk, base the future task branch on the previous task branch or PR head and record that dependency in `Branch Chain`.
- When all goal tasks have completed post-PR QA, generate `GOAL_REPORT.md` with `./commands/create-goal-completion-report.sh "bubo-local-cloud-agent"` before asking for human PR approval.
- Stop goal automation on validation failure, PR creation failure, post-PR QA failure, merge conflict, unresolved role ambiguity, or any required product/security decision outside this goal plan.

## PRs

- Base structure: https://github.com/TimothyMeadows/Bubo/pull/1
- Bubo foundation contracts and CLI no-op flow: https://github.com/TimothyMeadows/Bubo/pull/7

## QA Evidence

- Base structure PR #1 QA comment posted before this goal started.
- bubo-foundation-contracts local validation passed: `dotnet restore Bubo.sln`, `dotnet build Bubo.sln --no-restore`, `dotnet test Bubo.sln --no-build`, and CLI smoke run.

## Goal Completion Report
- Generate with `./commands/create-goal-completion-report.sh "bubo-local-cloud-agent"`.
- Include PR links in dependency order, branch base/head notes, post-PR QA evidence, and merge-conflict risk notes.
- Use this report for human approval after goal completion; do not merge automatically.

## Review Notes

- Later PRs should remain stacked until PR #1 is merged, or be retargeted after the base structure lands.
