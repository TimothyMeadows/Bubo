# Validation: bubo-config-loading

## Summary

Configuration loading is implemented for `bubo run`.

## Commands

- `dotnet test tests/LocalAgent.Cli.Tests/LocalAgent.Cli.Tests.csproj --configuration Release --verbosity minimal`
- `dotnet test tests/LocalAgent.Runtime.Tests/LocalAgent.Runtime.Tests.csproj --configuration Release --verbosity minimal`
- `dotnet build Bubo.sln --configuration Release --no-restore`
- `dotnet test Bubo.sln --configuration Release --no-build --verbosity normal`
- `dotnet format Bubo.sln --verify-no-changes --no-restore`
- `git diff --check`
- Config-driven CLI smoke with workspace-default `bubo.config.json`
- `dotnet pack src/LocalAgent.Cli/LocalAgent.Cli.csproj --configuration Release --no-build --output artifacts/packages`

## Results

- CLI tests passed: 24 tests.
- Runtime tests passed: 39 tests.
- Full solution tests passed: 76 tests.
- Build, format, diff, config smoke, and CLI package validation passed.
- Docker live sandbox smoke is blocked because Docker is not installed on this host.
