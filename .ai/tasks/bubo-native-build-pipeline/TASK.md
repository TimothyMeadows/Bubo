# Add Full llama.cpp Native Build And Package Pipeline

## Issue

https://github.com/TimothyMeadows/Bubo/issues/27

## Goal

Add first-class support for building pinned llama.cpp native runtime assets locally and in CI, staging those assets into the `Bubo.LlamaCppSharp.Native` package layout, smoke-testing runtime loading from that layout, and verifying package contents.

## Assumptions

- Bubo targets .NET 8 LTS.
- Local inference must continue to use direct llama.cpp interop, not `llama-cli`, `llama-server`, Ollama, or a host daemon.
- The initial supported runtime identifiers remain `win-x64`, `linux-x64`, and `osx-arm64`.
- The current pinned llama.cpp reference remains `b9189` / commit `64b38b561b987679c4e1c6231f93860d3eec2638` unless validation proves it must change.
- GPU-specific native variants are out of scope for this slice; the pipeline should leave room for future CUDA, Metal, Vulkan, or ROCm variants.

## Ordered Tasks

1. Create a local native build script that clones or updates `ggml-org/llama.cpp`, checks out the pinned ref, builds shared libraries with CMake, and copies the resulting asset to `artifacts/native/<rid>/`.
2. Add staging support so a locally or CI-built native asset can be copied into `src/LlamaCppSharp.Native/runtimes/<rid>/native/`.
3. Add a package verification script that can build or reuse a staged native asset, run a runtime smoke probe, pack `Bubo.LlamaCppSharp.Native`, and verify the expected RID asset exists inside the `.nupkg`.
4. Add package-time validation switches so native package builds can fail fast when required RID assets are missing.
5. Extend `bubo native test` so it can probe a supplied base directory, including staged package assets.
6. Update the native GitHub Actions workflow to call the same scripts used by local developers and upload both native assets and verified packages.
7. Update developer documentation with prerequisites, local commands, CI behavior, and limitations.
8. Run build/test validation, record evidence, and mark the task complete when ready.

## Constraints

- All code edits must preserve the existing .NET solution layout.
- Generated native binaries must not be committed.
- Scripts must be usable from PowerShell on Windows, Linux, and macOS runners.
- Destructive cleanup must stay under the repository-owned artifact directories.
- Package validation must be opt-in so ordinary source-only development builds still work without native artifacts.

## Validation Plan

- Parse both PowerShell scripts without executing native builds.
- Run `dotnet build Bubo.sln -c Release`.
- Run the relevant unit test suite.
- Run `bubo native test` in a way that verifies CLI parsing and expected missing-library behavior without requiring a local native binary.
- Verify package validation fails clearly when required assets are absent.
- Review the generated workflow and package script paths for Windows/Linux/macOS path compatibility.

## Notes

- The user asked for implementation with a team size of 5; see `SUBAGENTS.md` for lane decomposition and review evidence.
- Implementation completed on branch `feature/bubo-native-build-pipeline`; see `VALIDATION.md` for local validation and subagent results.
