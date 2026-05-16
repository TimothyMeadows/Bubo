# Bug Patterns

## Stacked PR Issue Closure Drift

Symptom:

- A PR body contains `Closes #N`, the PR is merged, but the task issue remains open.

Cause:

- The PR targeted a stacked feature branch instead of the repository default branch, so GitHub did not auto-close the linked issue.

Resolution:

- Fetch PR metadata to confirm the PR is actually merged.
- Close the linked issue manually with state reason `completed`.
- Remove the closed issue URL from `.ai/tasks/OPEN_ISSUES.md`.
