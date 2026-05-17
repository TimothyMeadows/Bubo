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

Pinned upstream:

- Repository: https://github.com/ggml-org/llama.cpp
- Release: `b9189`
- Commit: `64b38b561b987679c4e1c6231f93860d3eec2638`

## Native Build Prerequisites

Local native builds require:

- PowerShell 7 or newer (`pwsh`).
- Git.
- CMake.
- A platform compiler toolchain:
  - Windows: Visual Studio Build Tools with C++ support.
  - Linux: GCC or Clang plus normal build essentials.
  - macOS: Xcode command line tools.
- .NET 8 SDK for package and smoke-test validation.

The first supported runtime identifiers are `win-x64`, `linux-x64`, and `osx-arm64`.

| RID | Package Validation | Notes |
| --- | --- | --- |
| `win-x64` | Supported | Produces `llama.dll` plus sidecar `.dll` files when required. |
| `linux-x64` | Supported | Produces `libllama.so` plus sidecar `.so` files when required. |
| `osx-arm64` | Supported | Produces `libllama.dylib` plus sidecar `.dylib` files when required. |
| `win-arm64` | Experimental script input | Not part of v1 package validation. |
| `linux-arm64` | Experimental script input | Not part of v1 package validation. |
| `osx-x64` | Experimental script input | Not part of v1 package validation. |

## Build Native Assets Locally

Build and stage the current platform asset into the package layout:

```powershell
pwsh ./scripts/build-llama-native.ps1 -StageToPackage
```

Build a specific supported RID:

```powershell
pwsh ./scripts/build-llama-native.ps1 -Rid linux-x64 -StageToPackage -Clean
```

Outputs:

```text
artifacts/native/<rid>/<library>
artifacts/native/<rid>/llama-native-build.json
src/LlamaCppSharp.Native/runtimes/<rid>/native/<library>   # only with -StageToPackage
```

The build script clones or updates `ggml-org/llama.cpp` under `artifacts/src/llama.cpp`, checks out the requested ref, builds shared libraries with CMake, copies `llama` plus any sidecar dynamic libraries produced by the build, and writes a small build manifest.

## Smoke Test And Package Verification

After staging an asset:

```powershell
pwsh ./scripts/test-native-package.ps1 -Rid linux-x64
```

To build, stage, smoke test, pack, and verify in one command:

```powershell
pwsh ./scripts/test-native-package.ps1 -Rid linux-x64 -BuildNative
```

The verification script:

1. Confirms the staged asset exists.
2. Builds the .NET solution.
3. Runs `bubo native test --base-directory src/LlamaCppSharp.Native --strict`.
4. Packs `Bubo.LlamaCppSharp.Native` with required native asset validation enabled.
5. Opens the `.nupkg` and verifies `runtimes/<rid>/native/<library>` plus staged sidecar libraries are present.

## Local Validation

```bash
dotnet restore Bubo.sln
dotnet build Bubo.sln --configuration Release --no-restore
dotnet test Bubo.sln --configuration Release --no-build
dotnet pack src/LlamaCppSharp.Native/LlamaCppSharp.Native.csproj --configuration Release --no-build --output artifacts/packages
dotnet pack src/LlamaCppSharp/LlamaCppSharp.csproj --configuration Release --no-build --output artifacts/packages
dotnet pack src/LocalAgent.Cli/LocalAgent.Cli.csproj --configuration Release --no-build --output artifacts/packages
```

Native package validation is opt-in so source-only builds do not require native binaries:

```powershell
dotnet pack src/LlamaCppSharp.Native/LlamaCppSharp.Native.csproj `
  --configuration Release `
  --no-build `
  --output artifacts/packages `
  -p:RequireNativeAssetsForPack=true `
  -p:RequiredNativeRid=linux-x64
```

Omit `RequiredNativeRid` to require all supported RID assets.

The strict runtime smoke test probes only the staged package layout and does not fall back to an ambient library on `PATH`:

```powershell
dotnet run --no-build --configuration Release --project src/LocalAgent.Cli/LocalAgent.Cli.csproj -- native test --base-directory src/LlamaCppSharp.Native --strict
```

## CI Native Asset Workflow

`.github/workflows/native-assets.yml` is manually dispatched and runs the same script path used locally:

```text
scripts/test-native-package.ps1 -Rid <rid> -Ref <ref> -Configuration Release -BuildNative
```

Each matrix job builds one RID, smoke-tests the staged native library, packs the native package with RID validation enabled, and uploads:

- `llama-<rid>`: the built native asset and build manifest.
- `package-<rid>`: the verified native NuGet package for that RID.

## Unsupported Platforms

Only `win-x64`, `linux-x64`, and `osx-arm64` are scaffolded for v1. Additional RIDs need a native build, a smoke test, and package metadata before support is claimed.
