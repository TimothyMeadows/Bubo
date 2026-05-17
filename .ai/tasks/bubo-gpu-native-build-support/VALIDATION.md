# Validation Evidence

This file records validation for issue #29.

## Subagent Results

- Lane 2, CUDA native build engineer: recommended explicit CUDA architecture handling, CUDA 12.8+ validation for Blackwell/RTX 50xx, `-CudaCompiler` pass-through, sidecar preservation, backend-specific staging, and backend-aware CI/docs. Integrated.
- Lane 3, runtime loader engineer: recommended backend-specific strict probing, backend-aware package layout, parser tests, and a process-level native import resolver so `[LibraryImport("llama")]` binds to the selected backend path. Integrated.
- Lane 4, CI and container engineer: recommended CPU hosted lanes, opt-in CUDA self-hosted NVIDIA lanes, NVIDIA runner preflight with `nvidia-smi` and Docker GPU passthrough, explicit GPU config validation, and safer GPU defaults. Integrated.
- Lane 5, QA and docs engineer: recommended script parser checks, focused parser/native/sandbox tests, strict missing-CUDA negative checks, CUDA package-validation negative checks, and docs that distinguish CPU backend, CUDA backend, Docker GPU exposure, and model `gpuLayers`. Integrated.

## Local Validation

- PowerShell script syntax gate passed.
- `git diff --check` passed.
- `dotnet build Bubo.sln --configuration Release` passed.
- Focused CLI/native/sandbox/config tests passed:
  - `dotnet test tests/LocalAgent.Cli.Tests/LocalAgent.Cli.Tests.csproj --configuration Release --no-build --filter "FullyQualifiedName~CommandLineParserTests|FullyQualifiedName~SandboxCommandParserTests|FullyQualifiedName~NativeTestReportsMissingStrictBaseDirectoryAsset|FullyQualifiedName~AgentConfigLoaderTests"`
  - `dotnet test tests/LlamaCppSharp.Native.Tests/LlamaCppSharp.Native.Tests.csproj --configuration Release --no-build`
  - `dotnet test tests/LocalAgent.Sandbox.Docker.Tests/LocalAgent.Sandbox.Docker.Tests.csproj --configuration Release --no-build`
- Strict CUDA native probe without staged CUDA assets failed as expected:
  - `dotnet run --no-build --configuration Release --project src/LocalAgent.Cli/LocalAgent.Cli.csproj -- native test --base-directory src/LlamaCppSharp.Native --backend cuda --strict`
- Opt-in CUDA package validation without staged CUDA assets failed as expected before package creation:
  - `dotnet pack src/LlamaCppSharp.Native/LlamaCppSharp.Native.csproj --configuration Release --no-build --output artifacts/packages -p:RequireNativeAssetsForPack=true -p:RequiredNativeRid=linux-x64 -p:RequiredNativeBackend=cuda`
- `dotnet test Bubo.sln --configuration Release --no-build` passed.
- Source-only package checks passed for:
  - `Bubo.LlamaCppSharp.Native`
  - `Bubo.LlamaCppSharp`
  - `Bubo.LocalAgent.Cli`
- `dotnet format Bubo.sln --verify-no-changes --no-restore` passed.
- `bash .opencaw/commands/clean-context.sh --dry-run` passed.

## RTX 5080 Local CUDA Validation

- Installed/verified missing local tools for the hardware-backed validation path:
  - PowerShell 7.6.1.
  - Visual Studio Build Tools 2022 C++ workload.
  - CUDA Toolkit 13.2 (`nvcc` release 13.2).
  - CMake 4.3.2.
  - Docker Desktop CLI 29.4.3.
- Verified host GPU:
  - `nvidia-smi --query-gpu=name,driver_version,memory.total --format=csv,noheader`
  - Result: `NVIDIA GeForce RTX 5080 Laptop GPU, 596.36, 16303 MiB`.
- Verified Docker GPU passthrough:
  - `docker run --rm --gpus all ubuntu nvidia-smi`
  - Result: container saw the RTX 5080 with CUDA 13.2.
- Built and staged llama.cpp CUDA native assets for `win-x64` with CUDA architecture `120`:
  - `scripts/test-native-package.ps1 -Rid win-x64 -Backend cuda -CudaArchitectures "120" -CudaToolkitRoot "C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v13.2" -Generator Ninja -BuildNative -Clean`
  - Result: produced `llama.dll`, `ggml.dll`, `ggml-base.dll`, `ggml-cpu.dll`, and `ggml-cuda.dll` under `src/LlamaCppSharp.Native/runtimes/win-x64/native/cuda`.
- Verified strict CUDA native load and package contents:
  - `scripts/test-native-package.ps1 -Rid win-x64 -Backend cuda -CudaToolkitRoot "C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v13.2"`
  - Result: `bubo native test --backend cuda --strict` loaded `src/LlamaCppSharp.Native/runtimes/win-x64/native/cuda/llama.dll`, and the native NuGet package contained `runtimes/win-x64/native/cuda/llama.dll`.
- Verified one-shot incremental CUDA package path from the VC/Ninja environment:
  - `scripts/test-native-package.ps1 -Rid win-x64 -Backend cuda -CudaArchitectures "120" -CudaToolkitRoot "C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v13.2" -Generator Ninja -BuildNative`
  - Result: no native rebuild was needed, strict native load passed, and package verification passed.
- Verified Bubo Docker sandbox GPU check:
  - `dotnet run --no-build --configuration Release --project src/LocalAgent.Cli/LocalAgent.Cli.csproj -- sandbox test --workspace . --gpu nvidia`
  - Result: sandbox printed `NVIDIA GeForce RTX 5080 Laptop GPU, 596.36`.

## Notes

- Full CUDA native compilation and runtime smoke have now been run locally on an RTX 5080 Laptop GPU with CUDA Toolkit 13.2. The workflow still exposes an opt-in CUDA lane for a self-hosted NVIDIA runner and performs runner preflight before building.
