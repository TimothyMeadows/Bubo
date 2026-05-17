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
- `dotnet pack src/LlamaCppSharp.Native/LlamaCppSharp.Native.csproj --configuration Release --no-build --output artifacts/packages`
- `dotnet pack src/LlamaCppSharp/LlamaCppSharp.csproj --configuration Release --no-build --output artifacts/packages`
- `dotnet pack src/LocalAgent.Cli/LocalAgent.Cli.csproj --configuration Release --no-build --output artifacts/packages`

## Results

- Focused runtime tests passed: 44 tests after implementation.
- Focused iterative-loop filter passed: 4 tests.
- Full solution tests passed: 81 tests.
- Build, format, diff, and package validation passed.
- `git diff --check` reported line-ending normalization warnings only.
- Docker live sandbox smoke remains blocked locally because Docker is not installed on this host.
