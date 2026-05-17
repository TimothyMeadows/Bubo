# Bubo.LlamaCppSharp.Native

Native runtime asset package for Bubo's direct `llama.cpp` integration.

This package pins `ggml-org/llama.cpp` release `b9189` at commit `64b38b561b987679c4e1c6231f93860d3eec2638`.

CPU native binaries are expected under NuGet RID asset paths such as `runtimes/win-x64/native/llama.dll`, `runtimes/linux-x64/native/libllama.so`, and `runtimes/osx-arm64/native/libllama.dylib`.

GPU backend builds are staged under backend-specific subdirectories such as `runtimes/linux-x64/native/cuda/libllama.so`. Builds may also include sidecar `ggml` dynamic libraries in the same backend directory.
