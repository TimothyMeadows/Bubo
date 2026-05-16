# Gotchas

## Stacked PR Merge Semantics

- GitHub marks stacked PRs as merged when they are merged into their configured base branch, even if that base is another feature branch rather than `main`.
- `Closes #N` in a PR targeting a non-default stacked branch may not auto-close the linked issue. Verify issue state after merges and close completed task issues manually when needed.
- After stacked PR merges, inspect `git log --graph --all` or `git branch -r --contains <sha>` before assuming work is present on `main`.

## Current Main Branch Caveat

- After PRs #7-#11 were merged, only PR #7 was merged directly into `main`; PRs #8-#11 were merged into their stacked feature bases. Future implementation work should verify whether to integrate the final stack branch into `main` before building on the broader runtime features.
