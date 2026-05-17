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
6. [x] Integrate Bubo goal stack onto main (`../.ai/tasks/bubo-main-stack-integration/TASK.md`)
7. [x] Harden Bubo workspace tools and patch flow (`../.ai/tasks/bubo-tool-hardening/TASK.md`)
8. [x] Add Bubo inference-driven action loop (`../.ai/tasks/bubo-inference-action-loop/TASK.md`)
9. [x] Add Bubo configuration loading (`../.ai/tasks/bubo-config-loading/TASK.md`)
10. [x] Add Bubo iterative inference repair loop (`../.ai/tasks/bubo-iterative-repair-loop/TASK.md`)

## Current Task

Goal implementation is complete through stacked task #21. PR #20 and PR #22 remain open for human review and merge approval.

## Branch Chain
- base-structure | base: `main` | head: `chore/opencaw-base-structure` | PR: https://github.com/TimothyMeadows/Bubo/pull/1 | depends on: none | status: merged
- bubo-foundation-contracts | base: `main` | head: `feature/bubo-foundation-contracts` | PR: https://github.com/TimothyMeadows/Bubo/pull/7 | depends on: base-structure | status: merged
- bubo-docker-sandbox | base: `feature/bubo-foundation-contracts` | head: `feature/bubo-docker-sandbox` | PR: https://github.com/TimothyMeadows/Bubo/pull/8 | depends on: bubo-foundation-contracts | status: merged
- bubo-llamacpp-native-wrapper | base: `feature/bubo-docker-sandbox` | head: `feature/bubo-llamacpp-native-wrapper` | PR: https://github.com/TimothyMeadows/Bubo/pull/9 | depends on: bubo-docker-sandbox | status: merged
- bubo-agent-runtime-inference | base: `feature/bubo-llamacpp-native-wrapper` | head: `feature/bubo-agent-runtime-inference` | PR: https://github.com/TimothyMeadows/Bubo/pull/10 | depends on: bubo-llamacpp-native-wrapper | status: merged
- bubo-e2e-hardening-packaging | base: `feature/bubo-agent-runtime-inference` | head: `feature/bubo-e2e-hardening-packaging` | PR: https://github.com/TimothyMeadows/Bubo/pull/11 | depends on: bubo-agent-runtime-inference | status: merged
- bubo-main-stack-integration | base: `main` | head: `feature/bubo-main-stack-integration` | PR: https://github.com/TimothyMeadows/Bubo/pull/14 | depends on: bubo-e2e-hardening-packaging | status: post_pr_qa_passed
- bubo-tool-hardening | base: `main` | head: `feature/bubo-tool-hardening` | PR: https://github.com/TimothyMeadows/Bubo/pull/16 | depends on: bubo-main-stack-integration | status: post_pr_qa_passed
- bubo-inference-action-loop | base: `main` | head: `feature/bubo-inference-action-loop` | PR: https://github.com/TimothyMeadows/Bubo/pull/18 | depends on: bubo-tool-hardening | status: post_pr_qa_passed
- bubo-config-loading | base: `main` | head: `feature/bubo-config-loading` | PR: https://github.com/TimothyMeadows/Bubo/pull/20 | depends on: bubo-inference-action-loop | status: post_pr_qa_passed
- bubo-iterative-repair-loop | base: `feature/bubo-config-loading` | head: `feature/bubo-iterative-repair-loop` | PR: https://github.com/TimothyMeadows/Bubo/pull/22 | depends on: bubo-config-loading | status: post_pr_qa_passed

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
- Bubo end-to-end hardening and packaging: https://github.com/TimothyMeadows/Bubo/pull/11
- Integrate Bubo goal stack onto main: https://github.com/TimothyMeadows/Bubo/pull/14
- Harden Bubo workspace tools and patch flow: https://github.com/TimothyMeadows/Bubo/pull/16
- Add Bubo inference-driven action loop: https://github.com/TimothyMeadows/Bubo/pull/18
- Add Bubo configuration loading: https://github.com/TimothyMeadows/Bubo/pull/20
- Add Bubo iterative inference repair loop: https://github.com/TimothyMeadows/Bubo/pull/22

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
- bubo-e2e-hardening-packaging post-PR QA posted on PR #11.
- bubo-main-stack-integration local validation passed: `dotnet restore Bubo.sln`, `dotnet build Bubo.sln --configuration Release --no-restore`, `dotnet test Bubo.sln --configuration Release --no-build --verbosity normal` with 32 passing tests, package validation for three packages, doctor/models CLI checks, Docker image build and sandbox smoke, scripted E2E fixture, `dotnet format Bubo.sln --verify-no-changes`, and `git diff --check`.
- bubo-main-stack-integration post-PR QA posted on PR #14.
- bubo-tool-hardening local validation passed: `dotnet build Bubo.sln --configuration Release --no-restore`, `dotnet test Bubo.sln --configuration Release --no-build --verbosity normal` with 44 passing tests, scripted E2E fixture, live `git_apply_patch` fixture, `dotnet format Bubo.sln --verify-no-changes`, and `git diff --check`.
- bubo-tool-hardening post-PR QA posted on PR #16; local QA passed and the latest GitHub Actions `dotnet` workflow passed.
- bubo-inference-action-loop local validation passed: `dotnet build Bubo.sln --configuration Release --no-restore`, `dotnet test Bubo.sln --configuration Release --no-build --verbosity normal` with 60 passing tests, scripted E2E fixture, `dotnet format Bubo.sln --verify-no-changes`, and `git diff --check`.
- bubo-inference-action-loop post-PR QA posted on PR #18; local QA passed and the GitHub Actions `dotnet` workflow passed.
- bubo-config-loading local validation passed: `dotnet build Bubo.sln --configuration Release --no-restore`, `dotnet test Bubo.sln --configuration Release --no-build --verbosity normal` with 76 passing tests, `dotnet format Bubo.sln --verify-no-changes --no-restore`, `git diff --check`, config-driven CLI smoke, and `dotnet pack src/LocalAgent.Cli/LocalAgent.Cli.csproj --configuration Release --no-build --output artifacts/packages`. Docker live sandbox smoke is blocked locally because Docker is not installed on this host.
- bubo-config-loading post-PR QA posted on PR #20; local QA passed and the GitHub Actions `dotnet` workflow passed.
- bubo-iterative-repair-loop local validation passed: `dotnet build Bubo.sln --configuration Release --no-restore`, focused runtime tests with 44 passing tests, focused iterative-loop filter with 4 passing tests, side-effect auditability focused regression with 2 passing tests, `dotnet test Bubo.sln --configuration Release --no-build --verbosity minimal` with 82 passing tests after the auditability fix, `dotnet format Bubo.sln --verify-no-changes --no-restore`, `git diff --check`, and package validation for `LlamaCppSharp.Native`, `LlamaCppSharp`, and `LocalAgent.Cli`. Docker live sandbox smoke is blocked locally because Docker is not installed on this host.
- bubo-iterative-repair-loop post-PR QA posted on PR #22 and mirrored to issue #21; initial GitHub Actions `dotnet` workflow run #16 passed for head `b29bfd4732ede5ffc7c0cc9d5a372583d2fda98b`, and a follow-up auditability fix was validated locally before final goal reporting.

## Goal Completion Report
- Generated at `.ai/goals/bubo-local-cloud-agent/GOAL_REPORT.md`.
- Include PR links in dependency order, branch base/head notes, post-PR QA evidence, and merge-conflict risk notes.
- Use this report for human approval after goal completion; do not merge automatically.

## Review Notes

- PRs #7 through #11 are closed and merged. Issues #2 through #6 are closed as completed.
- PRs #8 through #11 were merged into their stacked base branches, and PR #14 integrates the completed stack back onto `main`.
- Task #15 hardens workspace/patch tools after PR #14 merged; issue #15 is linked to PR #16 and should close on merge.
- Task #17 connects inference providers to guarded action generation after PR #16 merged; PR #18 merged and issue #17 is closed as completed.
- Task #19 adds external configuration loading after PR #18 merged; issue #19 is linked to PR #20.
- Task #21 adds an iterative inference repair loop stacked on PR #20 because it uses the config/limit wiring from that branch; issue #21 is linked to PR #22.
- Human merge order for the remaining open stack is PR #20 first, then PR #22 after confirming checks/base freshness.
