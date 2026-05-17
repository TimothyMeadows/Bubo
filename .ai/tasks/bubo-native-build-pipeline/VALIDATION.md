# Validation Evidence

This file records validation for issue #27.

## Subagent Results

- Lane 2, native build engineer: recommended fixing PowerShell parse errors, commit-pinning native builds, staging sidecar `ggml` libraries, ignoring staged native binaries, and adding strict package-layout probing. Integrated.
- Lane 3, CI and release engineer: recommended script-driven CI, opt-in package validation, exact NuGet entry checks, CLI `--base-directory`, and raw/package artifact upload. Integrated.
- Lane 4, QA engineer: identified `$LASTEXITCODE:` PowerShell parse failures, proposed script parser gates, focused CLI/native tests, strict probe negative test, and package-validation negative test. Integrated.
- Lane 5, developer experience writer: recommended golden local commands, PowerShell 7 prerequisites, supported RID table, strict probe docs, and troubleshooting updates. Integrated.

## Local Validation

- `PowerShell script syntax OK`
- `git diff --check`
- `dotnet build Bubo.sln --configuration Release`
- `dotnet test tests/LocalAgent.Cli.Tests/LocalAgent.Cli.Tests.csproj --configuration Release --no-build --filter "FullyQualifiedName~CommandLineParserTests|FullyQualifiedName~NativeTestReportsMissingStrictBaseDirectoryAsset"`
- `dotnet test tests/LlamaCppSharp.Native.Tests/LlamaCppSharp.Native.Tests.csproj --configuration Release --no-build`
- `dotnet test Bubo.sln --configuration Release --no-build`
- `dotnet run --no-build --configuration Release --project src/LocalAgent.Cli/LocalAgent.Cli.csproj -- native test --base-directory src/LlamaCppSharp.Native --strict` returned the expected failure because no native asset is staged locally.
- `dotnet pack src/LlamaCppSharp.Native/LlamaCppSharp.Native.csproj --configuration Release --no-build --output artifacts/packages -p:RequireNativeAssetsForPack=true -p:RequiredNativeRid=linux-x64` returned the expected fail-fast validation error before package creation because no native asset is staged locally.
- `dotnet pack src/LlamaCppSharp.Native/LlamaCppSharp.Native.csproj --configuration Release --no-build --output artifacts/packages`
- `dotnet pack src/LlamaCppSharp/LlamaCppSharp.csproj --configuration Release --no-build --output artifacts/packages`
- `dotnet pack src/LocalAgent.Cli/LocalAgent.Cli.csproj --configuration Release --no-build --output artifacts/packages`
- `dotnet format Bubo.sln --verify-no-changes --no-restore`
- `bash .codex/commands/clean-context.sh --dry-run`

## Notes

- I did not run a full local llama.cpp native compilation in this environment. The new CI workflow and local `test-native-package.ps1 -BuildNative` path are designed to perform that heavier validation on supported RID runners.
