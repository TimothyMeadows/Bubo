# Workflows

## Goal-Flow Cleanup After Stacked Merges

When a goal-flow stack is merged:

1. Fetch and inspect all remote refs with `git fetch --all --prune`.
2. Confirm each PR's `merged` state through GitHub metadata.
3. Confirm whether each merge landed on `main` or only on a stacked feature branch.
4. Update `.ai/tasks/TODO.md` to mark completed tasks.
5. Clear `.ai/tasks/OPEN_ISSUES.md` for closed task issues.
6. Update `.ai/goals/<goal>/GOAL.md` and `GOAL_REPORT.md` with merged status and any integration caveats.
7. Close task issues that did not auto-close because their PR targeted a non-default branch.

## PR Readiness With Explicit User Approval

- When the user explicitly asks to create a PR, that is sufficient approval to branch, commit, push, and open the PR for the requested changes.
- Still generate or maintain local validation evidence before the PR when practical, and run/post post-PR QA after the PR exists.

## Goal-Flow PR Readiness On Windows

- Run `./.codex/commands/pr-readiness-check.sh --goal` for the durable gate, but verify the actual commit scope with native `git status --short --branch` when WSL reports many unrelated line-ending changes.
- For Bubo tool hardening, include both unit validation and CLI fixture validation before opening the PR: Release build, Release tests, scripted E2E fixture, live Docker-backed `git_apply_patch` fixture, `dotnet format --verify-no-changes`, and `git diff --check`.
