# Packaging

Bubo targets .NET 8 LTS and keeps managed code separate from platform-specific llama.cpp assets.

## Packages

- `Bubo.LlamaCppSharp.Native`: native asset carrier package with RID layout under `runtimes/<rid>/native`.
- `Bubo.LlamaCppSharp`: managed llama.cpp wrapper and runtime availability helpers.
- `Bubo.LocalAgent.Cli`: .NET tool package that exposes the `bubo` command.

## Native Asset Layout

```text
runtimes/
  win-x64/native/llama.dll
  linux-x64/native/libllama.so
  osx-arm64/native/libllama.dylib
```

The native workflow is manual because the first version pins a known llama.cpp source ref and should only publish assets after smoke tests pass for each RID.

Pinned upstream:

- Repository: https://github.com/ggml-org/llama.cpp
- Release: `b9189`
- Commit: `64b38b561b987679c4e1c6231f93860d3eec2638`

## Local Validation

```bash
dotnet restore Bubo.sln
dotnet build Bubo.sln --configuration Release --no-restore
dotnet test Bubo.sln --configuration Release --no-build
dotnet pack src/LlamaCppSharp.Native/LlamaCppSharp.Native.csproj --configuration Release --no-build --output artifacts/packages
dotnet pack src/LlamaCppSharp/LlamaCppSharp.csproj --configuration Release --no-build --output artifacts/packages
dotnet pack src/LocalAgent.Cli/LocalAgent.Cli.csproj --configuration Release --no-build --output artifacts/packages
```

## Unsupported Platforms

Only `win-x64`, `linux-x64`, and `osx-arm64` are scaffolded for v1. Additional RIDs need a native build, a smoke test, and package metadata before support is claimed.
