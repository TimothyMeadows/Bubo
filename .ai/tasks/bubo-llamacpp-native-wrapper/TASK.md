# Bubo llama.cpp native wrapper

## Goal

Add the first safe managed wrapper surface for `ggml-org/llama.cpp`, including pinned source metadata, native asset layout, P/Invoke declarations, SafeHandle wrappers, and smoke-test scaffolding.

## Scope

- Pin the latest stable llama.cpp release/tag available at implementation time.
- Record exact tag and commit in native build metadata.
- Add runtime native asset layout for `win-x64`, `linux-x64`, and `osx-arm64`.
- Add safe managed handles and low-level bindings for model/context/sampler lifecycle.
- Add native smoke tests that skip when native assets or model files are absent.

## Assumptions

- The wrapper binds to upstream `include/llama.h`.
- API/ABI drift is expected and must be guarded by pinning and smoke tests.

## Work Instructions

1. Build on the Docker sandbox branch after its PR QA passes.
2. Do not depend on `llama-server`, `llama-cli`, Ollama, or host-installed binaries.
3. Keep raw native handles out of public APIs.

## Verification

- `dotnet build Bubo.sln --no-restore`
- `dotnet test Bubo.sln --no-build`
- Native smoke test loads the native library when assets are present.

## Review

- Confirm package layout matches NuGet RID asset conventions.

## Issue

https://github.com/TimothyMeadows/Bubo/issues/4
