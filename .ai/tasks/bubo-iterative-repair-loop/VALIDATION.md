# Validation: bubo-iterative-repair-loop

## Summary

Bubo now has a bounded inference-generated action repair loop for inputs without deterministic `bubo-actions`.

## Commands

- `dotnet test tests/LocalAgent.Runtime.Tests/LocalAgent.Runtime.Tests.csproj --configuration Release --verbosity minimal`
- `dotnet build Bubo.sln --configuration Release --no-restore`
- `dotnet test tests/LocalAgent.Runtime.Tests/LocalAgent.Runtime.Tests.csproj --configuration Release --no-build --filter "FullyQualifiedName~RunAsyncRetriesInferenceGeneratedActionsAfterToolFailure|FullyQualifiedName~RunAsyncStopsAfterInferenceIterationLimit|FullyQualifiedName~RunAsyncEnforcesCumulativeInferenceToolCallLimit|FullyQualifiedName~RunAsyncReportsFailureWhenRetryReturnsNoActionsAfterFailure" --verbosity normal`
- `dotnet test Bubo.sln --configuration Release --no-build --verbosity normal`
- `dotnet format Bubo.sln --verify-no-changes --no-restore`
- `git diff --check`
- `dotnet test Bubo.sln --configuration Release --no-build --verbosity minimal`
- `dotnet test tests/LocalAgent.Runtime.Tests/LocalAgent.Runtime.Tests.csproj --configuration Release --filter "FullyQualifiedName~RunAsyncReportsPriorSideEffectsWhenInferenceIterationLimitIsReached|FullyQualifiedName~RunAsyncStopsAfterInferenceIterationLimit" --verbosity normal`
- `dotnet build Bubo.sln --configuration Release --no-restore`
- `dotnet test Bubo.sln --configuration Release --no-build --verbosity minimal`
- `dotnet format Bubo.sln --verify-no-changes --no-restore`
- `git diff --check`
- `dotnet pack src/LlamaCppSharp.Native/LlamaCppSharp.Native.csproj --configuration Release --no-build --output artifacts/packages`
- `dotnet pack src/LlamaCppSharp/LlamaCppSharp.csproj --configuration Release --no-build --output artifacts/packages`
- `dotnet pack src/LocalAgent.Cli/LocalAgent.Cli.csproj --configuration Release --no-build --output artifacts/packages`
- `docker build --pull -f docker/bubo-sandbox/Dockerfile -t bubo-sandbox:local docker/bubo-sandbox`
- `dotnet run --no-build --configuration Release --project src/LocalAgent.Cli/LocalAgent.Cli.csproj -- sandbox test --workspace .`

## Results

- Focused runtime tests passed: 44 tests after implementation.
- Focused iterative-loop filter passed: 4 tests.
- Lane-2 auditability follow-up passed: max-iteration exhaustion now reports prior partial side effects, with a focused 2-test run.
- Full solution tests passed: 82 tests after the auditability fix.
- Build, format, diff, and package validation passed.
- `git diff --check` reported line-ending normalization warnings only.
- Docker live sandbox smoke passed after Docker installation. The image build completed and `bubo sandbox test` reported `git version 2.39.5`, `gh version 2.92.0`, and `.NET 8.0.421` from inside the container.
