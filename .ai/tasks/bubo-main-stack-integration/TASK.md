# Integrate Bubo goal stack onto main

## Goal

Integrate the completed stacked Bubo goal work from PRs #8 through #11 onto `main`, while preserving the exhaustive README and `.ai` context updates from PR #12.

## Scope

- Bring Docker sandbox runtime, llama.cpp native wrapper scaffold, inference providers, guarded tools, deterministic fixtures, CI workflows, examples, and docs from the completed stack into a branch based on `main`.
- Preserve the current `main` README and `.ai` memory/fragment/learning updates unless a newer stack artifact has explicit task evidence that should be retained.
- Validate the combined repository locally.
- Use installed host tools where useful: `gh`, CMake, Ninja, Docker Desktop, .NET SDK, and codex-cli.
- Raise a final integration PR with post-PR QA evidence.

## Assumptions

- PR #12 is merged to `main` and should remain the documentation/context baseline.
- PRs #8 through #11 are merged into stacked feature branches, not directly into `main`.
- The integration source branch is `origin/feature/bubo-agent-runtime-inference`, which contains the completed stack through PR #11.
- Native llama.cpp binaries may still be scaffolded rather than populated.

## Work Instructions

1. Branch from current `main`.
2. Integrate code and docs from the completed stack.
3. Prefer current `main` for README and `.ai` memory/learning/context when conflicts occur.
4. Keep task tracking current.
5. Run local validation and Docker validation now that Docker Desktop is installed.
6. Generate PR readiness with goal automation enabled.
7. Push/open a PR and post PR QA evidence.

## Verification

- `git diff --check`
- `dotnet restore Bubo.sln`
- `dotnet build Bubo.sln --configuration Release --no-restore`
- `dotnet test Bubo.sln --configuration Release --no-build`
- `powershell -ExecutionPolicy Bypass -File .\scripts\run-e2e-fixture.ps1 -Configuration Release`
- `dotnet pack` for packageable projects
- `docker version`
- `dotnet run --project src/LocalAgent.Cli -- sandbox test --workspace .`
- `codex --version`
- `gh --version`
- `cmake --version`
- `ninja --version`

## Review

- Integrated the completed stacked implementation branch onto current `main`, preserving the PR #12 README and `.ai` context baseline.
- Resolved README and goal-report conflicts without accepting older stack-side memory/task regressions.
- Wired deterministic `run_command`, `git_status`, and `git_diff` execution through the Docker sandbox runner; unit tests now inject fake sandbox runners rather than executing host commands.
- Fixed native package naming to `Bubo.LlamaCppSharp.Native`.
- Added Windows Docker discovery fallback and `.exe` preference so CLI sandbox commands can find Docker Desktop from non-refreshed shells.
- Local validation passed:
  - `dotnet restore Bubo.sln`
  - `dotnet build Bubo.sln --configuration Release --no-restore`
  - `dotnet test Bubo.sln --configuration Release --no-build --verbosity normal` passed 32 tests.
  - `dotnet pack` for `Bubo.LlamaCppSharp.Native`, `Bubo.LlamaCppSharp`, and `Bubo.LocalAgent.Cli`.
  - `dotnet run --no-build --configuration Release --project src/LocalAgent.Cli/LocalAgent.Cli.csproj -- doctor`
  - `dotnet run --no-build --configuration Release --project src/LocalAgent.Cli/LocalAgent.Cli.csproj -- models list`
  - `docker build --pull -f docker/bubo-sandbox/Dockerfile -t bubo-sandbox:local docker/bubo-sandbox`
  - Docker sandbox image smoke: `dotnet --version`, `git --version`, `gh --version`, `jq --version`
  - `dotnet run --no-build --configuration Release --project src/LocalAgent.Cli/LocalAgent.Cli.csproj -- sandbox test --workspace .`
  - `powershell -ExecutionPolicy Bypass -File .\scripts\run-e2e-fixture.ps1 -Configuration Release`
  - `dotnet format Bubo.sln --verify-no-changes`
  - `git diff --check` and `git diff --cached --check`
- Host tools available for this task:
  - Docker Desktop 4.73.0 / Docker Engine 29.4.3
  - GitHub CLI 2.92.0
  - CMake 4.3.2
  - Ninja 1.13.2
  - codex-cli 0.131.0-alpha.9

## Pull Request

https://github.com/TimothyMeadows/Bubo/pull/14

## Issue

https://github.com/TimothyMeadows/Bubo/issues/13
