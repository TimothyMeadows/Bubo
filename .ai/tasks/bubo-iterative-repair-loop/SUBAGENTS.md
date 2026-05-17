# Subagent Plan: bubo-iterative-repair-loop

## Capacity

- Requested: 6
- Effective lanes: 6
- Reason: one main implementation/integration lane plus five read-only specialist lanes. Runtime edits are centralized in the main lane to avoid conflicts in `AgentRunner`.

## Rules

- Resolve each lane role with `./commands/resolve-role.sh` before delegation.
- Use `explorer` for read-only lanes and `worker` for implementation lanes when Codex subagents are available.
- Worker lanes must declare disjoint write sets.
- Keep the main agent responsible for orchestration, critical-path blockers, integration, final verification, and user communication.

## Lanes

### lane-1

- Role: `computer-science/project-manager`
- Agent type: default
- Status: completed
- Scope: own implementation, integration, validation, PR creation, post-PR QA, and task ledger updates.
- Write set: runtime implementation, tests, docs as needed, and `.ai` task/goal artifacts.
- Dependencies: PR #20 branch state.
- Expected output: stacked PR with validated iterative repair loop.
- Verification: build, tests, format check, diff check, focused smoke, PR QA.

### lane-2

- Role: `computer-science/backend-architect`
- Agent type: explorer
- Status: completed
- Scope: review `AgentRunner` and prompt-builder seams for the smallest iterative repair-loop design.
- Write set: none
- Dependencies: none
- Expected output: design recommendations and coupling risks.
- Verification: cite files/classes inspected.

### lane-3

- Role: `computer-science/security-engineer`
- Agent type: explorer
- Status: completed
- Scope: review iterative model-output retry risks, tool-limit enforcement, and prompt-injection considerations.
- Write set: none
- Dependencies: none
- Expected output: security checklist and mitigations.
- Verification: cite relevant runtime checks.

### lane-4

- Role: `computer-science/qa-engineer`
- Agent type: explorer
- Status: completed
- Scope: identify tests for retry success, failure exhaustion, deterministic non-regression, and transcript/debug evidence.
- Write set: none
- Dependencies: none
- Expected output: prioritized test matrix.
- Verification: map tests to acceptance criteria.

### lane-5

- Role: `computer-science/technical-writer`
- Agent type: explorer
- Status: completed
- Scope: identify minimal docs/readme updates for iterative repair behavior without overstating autonomy.
- Write set: none
- Dependencies: none
- Expected output: doc notes and wording risks.
- Verification: cite docs inspected.

### lane-6

- Role: `computer-science/devops-automator`
- Agent type: explorer
- Status: completed
- Scope: review validation/CI implications and identify any package or workflow risks.
- Write set: none
- Dependencies: none
- Expected output: validation checklist and CI notes.
- Verification: cite relevant files.

## Integration

- Merge order: main agent integrates explorer recommendations after initial implementation design, then validates full solution.
- Conflict risks: medium because this branch is stacked on PR #20 and edits `AgentRunner`.
- Final verification: build, tests, format check, diff check, focused iterative-loop smoke, and PR QA.

## Results

### Result: lane-6 - completed - 2026-05-17T01:13:49Z
- Summary source: `../.ai/tasks/bubo-iterative-repair-loop/lane-results/lane-6-summary.txt`

```text

### Result: lane-3 - completed - 2026-05-17T01:13:49Z
- Summary source: `../.ai/tasks/bubo-iterative-repair-loop/lane-results/lane-3-summary.txt`

```text
DevOps automation review completed.

Findings:
- This task is runtime/test/doc only and does not require package, SDK, workflow, or project file changes.
- Validation should include focused runtime tests, full solution build/test, format, diff check, and package validation.
- PR #20 remains open, so this task should remain stacked on feature/bubo-config-loading.

Implementation impact:
- Full validation plan includes runtime focused tests and normal goal-flow checks.

Files changed by lane: None.
Security review completed.

Findings:
- Unknown model tools and oversized plans must not retry.
- maxToolCalls must be cumulative across inference-generated retries.
- Retry observations are untrusted input and need sanitization.
- Prior failed attempt side effects and issues should remain visible if a later retry succeeds.

Implementation impact:
- Unknown tools and cumulative tool-call limit failures stop immediately.
- Retry observations are sanitized, collapse control characters, replace markdown backticks, and are labeled untrusted in the prompt.
- Successful later attempts aggregate prior files, changes, and issues.

Files changed by lane: None.

### Result: lane-4 - completed - 2026-05-17T01:13:49Z
- Summary source: `../.ai/tasks/bubo-iterative-repair-loop/lane-results/lane-4-summary.txt`

```text
```
```

### Result: lane-5 - completed - 2026-05-17T01:13:49Z
- Summary source: `../.ai/tasks/bubo-iterative-repair-loop/lane-results/lane-5-summary.txt`

```text
QA review completed.

Findings:
- Highest-value tests cover retry success, max-iteration exhaustion, invalid output no-retry, unknown tool no-retry, no-action-after-failure, cumulative tool-call limits, and debug JSONL evidence.
- Explicit deterministic actions should remain non-inference.

Implementation impact:
- Runtime tests now cover the retry loop, maxIterations, non-retry cases, cumulative tool-call budget, no-actions-after-failure, debug event evidence, and deterministic non-regression.

Files changed by lane: None.

### Result: lane-2 - completed - 2026-05-17T01:13:49Z
- Summary source: `../.ai/tasks/bubo-iterative-repair-loop/lane-results/lane-2-summary.txt`

```text
```
Technical writing review completed.

Findings:
- Docs needed to replace stale one-shot inference wording with bounded generated-action repair wording.
- Wording should avoid overstating autonomy; separate planner/coder orchestration remains future work.

Implementation impact:
- README, configuration docs, security docs, and examples now describe bounded generated-action retries with MaxIterations.

Files changed by lane: None.
```
Backend architecture review completed.

Findings:
- Keep the loop inference-only so explicit bubo-actions remain deterministic and single-pass.
- Replace retryability string matching with a small internal execution outcome and typed stop reason.
- Treat oversized generated plans and unknown tools as non-retryable protocol/safety failures.
- Preserve failure state when a retry returns no actions.
- Include action-level observations in retry prompts.

Implementation impact:
- AgentRunner now uses ActionExecutionOutcome and ActionExecutionStopReason internally.
- ExecuteActionsAsync remains as a public-result wrapper for deterministic actions.
- The inference loop retries only ToolFailed outcomes.

Files changed by lane: None.
```

## Post-PR Lane Follow-Up

Lane 2 performed a final read-only diff audit after PR #22 opened and found one auditability edge case: max-iteration exhaustion returned the final failed attempt without aggregating prior failed-attempt side effects. The main lane fixed this before finalizing the task by composing the final failure through `AttachPriorAttemptEvidence` and adding `RunAsyncReportsPriorSideEffectsWhenInferenceIterationLimitIsReached`.
