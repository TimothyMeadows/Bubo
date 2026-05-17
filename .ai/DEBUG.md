# Debug History

- Windows CUDA native builds: Visual Studio developer shells can set `Platform=x64`, which makes `dotnet run --no-build` look under `bin/x64/Release`. `scripts/test-native-package.ps1` clears the process `Platform` variable for `dotnet` calls. Ninja single-config builds must pass `CMAKE_BUILD_TYPE=Release` or they produce debug MSVC-runtime dependencies. CUDA Toolkit 13.2 places `cublas64_13.dll` under `bin/x64`, so CUDA runtime smoke needs both CUDA `bin` and `bin/x64` on `PATH` when using `-CudaToolkitRoot`.
