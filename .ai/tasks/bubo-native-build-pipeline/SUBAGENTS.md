# Subagent Plan: Bubo Native Build Pipeline

## Capacity

- Requested team size: 5
- Effective parallel lanes: 5
- Main lane: implementation, integration, task ledger, validation, and final PR readiness
- Subagent lanes: 4 read-only review lanes, because the implementation touches shared scripts, CI, CLI, and docs where overlapping write access would create conflict risk.

## Lane 1: Main Integrator

- Role: `computer-science/backend-architect`
- Agent type: main
- Scope: implement scripts, CLI support, package validation, CI workflow, docs, task evidence, and final verification.
- Write set: repository-wide as needed for this task.
- Dependencies: none.
- Expected output: integrated changes ready for review.
- Verification: `dotnet build`, tests, script parse checks, package validation checks, and task evidence.

## Lane 2: Native Build Engineer

- Role: `computer-science/native-interop-engineer`
- Agent type: explorer
- Scope: review current native wrapper, RID naming, CMake assumptions, and local build-script requirements.
- Write set: none.
- Expected output: recommendations for the build script and native asset staging.
- Verification: cite inspected files and likely failure modes.

## Lane 3: CI And Release Engineer

- Role: `computer-science/devops-engineer`
- Agent type: explorer
- Scope: review GitHub Actions native workflow and packaging requirements.
- Write set: none.
- Expected output: workflow and artifact recommendations.
- Verification: cite inspected workflow/package files.

## Lane 4: QA Engineer

- Role: `computer-science/qa-engineer`
- Agent type: explorer
- Scope: identify targeted tests and validation commands for the native pipeline without requiring a full local native build.
- Write set: none.
- Expected output: test strategy and gaps.
- Verification: cite available test projects and scripts.

## Lane 5: Developer Experience Writer

- Role: `computer-science/technical-writer`
- Agent type: explorer
- Scope: review README and packaging docs for the commands developers need to build, test, and package native assets.
- Write set: none.
- Expected output: compact docs recommendations.
- Verification: cite inspected docs.

## Integration Order

1. Main lane creates task records and branch.
2. Read-only lanes inspect in parallel while implementation proceeds.
3. Main lane integrates useful findings into scripts, CI, docs, and validation.
4. Main lane records lane summaries in `VALIDATION.md`.

## Conflict Risks

- Low, because subagent lanes are read-only.
- Main lane owns all writes and final reconciliation.
