# Add GPU Backend Native Build Support

## Issue

https://github.com/TimothyMeadows/Bubo/issues/29

## Goal

Add backend-aware native build support for Bubo's direct llama.cpp runtime, preserving the current CPU path while adding first-class NVIDIA CUDA build support and clear extension points for Metal, Vulkan, and future ROCm/HIP variants.

## Assumptions

- CPU remains the default backend.
- NVIDIA CUDA is the first GPU backend to support for `linux-x64` and `win-x64`.
- RTX 50xx / Blackwell support requires CUDA Toolkit 12.8+ and compute capability 12.0 guidance.
- Metal support is primarily an `osx-arm64` package/build profile.
- Vulkan is an experimental cross-platform profile until validated on real runners.
- Full GPU runtime smoke tests require hardware-backed runners; local non-GPU validation should still verify scripts, parser behavior, package metadata, and strict missing-library behavior.

## Constraints

- Do not break the existing CPU native build commands.
- Do not commit generated native binaries.
- Keep backend-specific native outputs distinguishable in artifacts and package layout.
- Avoid claiming GPU runtime validation unless an actual GPU-backed command ran.
- Keep package validation opt-in for ordinary source-only development.

## Ordered Tasks

1. Add backend concepts to native scripts: `cpu`, `cuda`, `metal`, and `vulkan`.
2. Add backend-specific artifact and staging paths while keeping CPU compatible with existing RID/native layout.
3. Add CUDA build flags, CUDA architecture configuration, and CUDA Toolkit version documentation.
4. Add backend-aware package validation, including required primary native library and sidecar assets.
5. Extend CLI/native runtime probing with `--backend` and strict backend path resolution.
6. Extend sandbox test CLI to support NVIDIA GPU smoke validation.
7. Update CI workflow to run CPU lanes and expose CUDA lanes without pretending hosted runners have GPUs.
8. Update README and packaging docs with guided GPU build commands and RTX 50xx notes.
9. Run validation and record evidence.

## Validation Plan

- Parse PowerShell scripts.
- Run `git diff --check`.
- Run `dotnet build Bubo.sln --configuration Release`.
- Run focused CLI/native parser and smoke tests.
- Run full solution tests.
- Verify strict backend probe fails cleanly when no GPU native asset is staged.
- Verify CPU source-only pack still works.
- Verify opt-in CUDA package validation fails fast when assets are absent.
- Run `dotnet format Bubo.sln --verify-no-changes --no-restore`.

## Notes

- The user requested implementation with a team size of 5; see `SUBAGENTS.md`.
- Implementation completed on branch `feature/bubo-gpu-native-build-support`; see `VALIDATION.md` for local validation and subagent results.
