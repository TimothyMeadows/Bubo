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
2. [x] Bubo Docker sandbox runtime (`../.ai/tasks/bubo-docker-sandbox/TASK.md`)
3. [x] Bubo llama.cpp native wrapper (`../.ai/tasks/bubo-llamacpp-native-wrapper/TASK.md`)
4. [x] Bubo agent runtime and inference providers (`../.ai/tasks/bubo-agent-runtime-inference/TASK.md`)
5. [x] Bubo end-to-end hardening and packaging (`../.ai/tasks/bubo-e2e-hardening-packaging/TASK.md`)

## Current Task

bubo-e2e-hardening-packaging

## Branch Chain
- base-structure | base: `main` | head: `chore/opencaw-base-structure` | PR: https://github.com/TimothyMeadows/Bubo/pull/1 | depends on: none | status: merged
- bubo-foundation-contracts | base: `main` | head: `feature/bubo-foundation-contracts` | PR: https://github.com/TimothyMeadows/Bubo/pull/7 | depends on: base-structure
- bubo-docker-sandbox | base: `feature/bubo-foundation-contracts` | head: `feature/bubo-docker-sandbox` | PR: https://github.com/TimothyMeadows/Bubo/pull/8 | depends on: bubo-foundation-contracts
- bubo-llamacpp-native-wrapper | base: `feature/bubo-docker-sandbox` | head: `feature/bubo-llamacpp-native-wrapper` | PR: https://github.com/TimothyMeadows/Bubo/pull/9 | depends on: bubo-docker-sandbox
- bubo-agent-runtime-inference | base: `feature/bubo-llamacpp-native-wrapper` | head: `feature/bubo-agent-runtime-inference` | PR: https://github.com/TimothyMeadows/Bubo/pull/10 | depends on: bubo-llamacpp-native-wrapper
- bubo-e2e-hardening-packaging | base: `feature/bubo-agent-runtime-inference` | head: `feature/bubo-e2e-hardening-packaging` | PR: pending | depends on: bubo-agent-runtime-inference

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
- Bubo Docker sandbox runtime: https://github.com/TimothyMeadows/Bubo/pull/8
- Bubo llama.cpp native wrapper: https://github.com/TimothyMeadows/Bubo/pull/9
- Bubo agent runtime and inference providers: https://github.com/TimothyMeadows/Bubo/pull/10
- Bubo end-to-end hardening and packaging: pending

## QA Evidence

- Base structure PR #1 QA comment posted before this goal started.
- bubo-foundation-contracts local validation passed: `dotnet restore Bubo.sln`, `dotnet build Bubo.sln --no-restore`, `dotnet test Bubo.sln --no-build`, and CLI smoke run.
- bubo-foundation-contracts post-PR QA posted on PR #7.
- bubo-docker-sandbox local validation passed: `dotnet restore Bubo.sln`, `dotnet build Bubo.sln --no-restore`, `dotnet test Bubo.sln --no-build`, and `bubo sandbox test` no-Docker failure path.
- bubo-docker-sandbox post-PR QA posted on PR #8.
- bubo-llamacpp-native-wrapper local validation passed: pinned llama.cpp `b9189`, `dotnet build Bubo.sln --no-restore`, `dotnet test Bubo.sln --no-build`, and `dotnet pack src/LlamaCppSharp.Native/LlamaCppSharp.Native.csproj -c Debug --no-build`.
- bubo-llamacpp-native-wrapper post-PR QA posted on PR #9.
- bubo-agent-runtime-inference local validation passed: `dotnet restore Bubo.sln`, `dotnet build Bubo.sln --no-restore`, `dotnet test Bubo.sln --no-build`, and codex-cli `0.131.0-alpha.9` non-interactive help check.
- bubo-agent-runtime-inference post-PR QA posted on PR #10.
- bubo-e2e-hardening-packaging local validation passed: `dotnet restore Bubo.sln`, `dotnet build Bubo.sln --configuration Release --no-restore`, `dotnet test Bubo.sln --configuration Release --no-build`, scripted E2E fixture, package validation for three packages, doctor/models CLI checks, `dotnet format Bubo.sln --verify-no-changes`, and `git diff --check`.

## Goal Completion Report
- Generate with `./commands/create-goal-completion-report.sh "bubo-local-cloud-agent"`.
- Include PR links in dependency order, branch base/head notes, post-PR QA evidence, and merge-conflict risk notes.
- Use this report for human approval after goal completion; do not merge automatically.

## Review Notes

- Later PRs should remain stacked until PR #1 is merged, or be retargeted after the base structure lands.
