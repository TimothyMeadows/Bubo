using System.Runtime.InteropServices;
using System.Reflection;

namespace Bubo.LlamaCppSharp.Native;

public static class LlamaNativeLibrary
{
    private static readonly object ResolverLock = new();

    private static nint resolvedNativeHandle;

    private static string? resolvedNativePath;

    static LlamaNativeLibrary()
    {
        NativeLibrary.SetDllImportResolver(typeof(LlamaNativeLibrary).Assembly, ResolveImport);
    }

    public const string CpuBackend = "cpu";

    public const string CudaBackend = "cuda";

    public const string MetalBackend = "metal";

    public const string VulkanBackend = "vulkan";

    public static string NativeLibraryFileName
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return "llama.dll";
            }

            if (OperatingSystem.IsMacOS())
            {
                return "libllama.dylib";
            }

            return "libllama.so";
        }
    }

    public static string RuntimeIdentifier
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                    ? "win-arm64"
                    : "win-x64";
            }

            if (OperatingSystem.IsMacOS())
            {
                return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                    ? "osx-arm64"
                    : "osx-x64";
            }

            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? "linux-arm64"
                : "linux-x64";
        }
    }

    public static string ExpectedRidAssetPath(string baseDirectory, string backend = CpuBackend)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        var normalizedBackend = NormalizeBackend(backend);
        var nativeDirectory = normalizedBackend == CpuBackend
            ? Path.Combine(baseDirectory, "runtimes", RuntimeIdentifier, "native")
            : Path.Combine(baseDirectory, "runtimes", RuntimeIdentifier, "native", normalizedBackend);

        return Path.Combine(
            nativeDirectory,
            NativeLibraryFileName);
    }

    public static NativeLibraryLoadResult TryLoadDefault(
        string? baseDirectory = null,
        bool allowFallbackByName = true,
        string backend = CpuBackend)
    {
        var normalizedBackend = NormalizeBackend(backend);
        var root = baseDirectory ?? AppContext.BaseDirectory;
        var ridPath = ExpectedRidAssetPath(root, normalizedBackend);
        PreloadSidecarLibraries(Path.GetDirectoryName(ridPath), NativeLibraryFileName);

        if (NativeLibrary.TryLoad(ridPath, out var handle))
        {
            var registerError = TryRegisterResolvedLibrary(handle, ridPath);
            if (registerError is not null)
            {
                NativeLibrary.Free(handle);
                return new NativeLibraryLoadResult
                {
                    Success = false,
                    Backend = normalizedBackend,
                    RuntimeIdentifier = RuntimeIdentifier,
                    ExpectedPath = ridPath,
                    Error = registerError
                };
            }

            return new NativeLibraryLoadResult
            {
                Success = true,
                Handle = handle,
                ResolvedPath = ridPath,
                Backend = normalizedBackend,
                RuntimeIdentifier = RuntimeIdentifier,
                ExpectedPath = ridPath
            };
        }

        if (allowFallbackByName && NativeLibrary.TryLoad(NativeLibraryFileName, out handle))
        {
            var registerError = TryRegisterResolvedLibrary(handle, NativeLibraryFileName);
            if (registerError is not null)
            {
                NativeLibrary.Free(handle);
                return new NativeLibraryLoadResult
                {
                    Success = false,
                    Backend = normalizedBackend,
                    RuntimeIdentifier = RuntimeIdentifier,
                    ExpectedPath = ridPath,
                    Error = registerError
                };
            }

            return new NativeLibraryLoadResult
            {
                Success = true,
                Handle = handle,
                ResolvedPath = NativeLibraryFileName,
                Backend = normalizedBackend,
                RuntimeIdentifier = RuntimeIdentifier,
                ExpectedPath = ridPath,
                FallbackUsed = true
            };
        }

        return new NativeLibraryLoadResult
        {
            Success = false,
            Backend = normalizedBackend,
            RuntimeIdentifier = RuntimeIdentifier,
            ExpectedPath = ridPath,
            Error = allowFallbackByName
                ? $"Unable to load llama.cpp native library for backend '{normalizedBackend}' from '{ridPath}' or by name '{NativeLibraryFileName}'."
                : $"Unable to load llama.cpp native library for backend '{normalizedBackend}' from '{ridPath}'."
        };
    }

    public static string NormalizeBackend(string? backend)
    {
        if (string.IsNullOrWhiteSpace(backend))
        {
            return CpuBackend;
        }

        var normalized = backend.Trim().ToLowerInvariant();
        return normalized switch
        {
            CpuBackend => CpuBackend,
            CudaBackend => CudaBackend,
            MetalBackend => MetalBackend,
            VulkanBackend => VulkanBackend,
            _ => throw new ArgumentException($"Unsupported llama.cpp native backend: {backend}", nameof(backend))
        };
    }

    private static nint ResolveImport(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, "llama", StringComparison.Ordinal) &&
            !string.Equals(libraryName, NativeLibraryFileName, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        lock (ResolverLock)
        {
            return resolvedNativeHandle;
        }
    }

    private static string? TryRegisterResolvedLibrary(nint handle, string path)
    {
        lock (ResolverLock)
        {
            if (resolvedNativeHandle == 0)
            {
                resolvedNativeHandle = handle;
                resolvedNativePath = path;
                return null;
            }

            if (string.Equals(resolvedNativePath, path, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return $"llama.cpp native library is already resolved to '{resolvedNativePath}'. Restart the process before switching native backends or paths.";
        }
    }

    private static void PreloadSidecarLibraries(string? nativeDirectory, string primaryLibraryName)
    {
        if (string.IsNullOrWhiteSpace(nativeDirectory) || !Directory.Exists(nativeDirectory))
        {
            return;
        }

        var candidates = Directory
            .EnumerateFiles(nativeDirectory)
            .Where(path => IsNativeLibraryCandidate(path, primaryLibraryName))
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (var pass = 0; pass < candidates.Count; pass++)
        {
            var loadedThisPass = false;
            for (var index = candidates.Count - 1; index >= 0; index--)
            {
                if (!NativeLibrary.TryLoad(candidates[index], out _))
                {
                    continue;
                }

                candidates.RemoveAt(index);
                loadedThisPass = true;
            }

            if (!loadedThisPass)
            {
                break;
            }
        }
    }

    private static bool IsNativeLibraryCandidate(string path, string primaryLibraryName)
    {
        var fileName = Path.GetFileName(path);
        if (string.Equals(fileName, primaryLibraryName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase);
        }

        if (OperatingSystem.IsMacOS())
        {
            return string.Equals(Path.GetExtension(path), ".dylib", StringComparison.OrdinalIgnoreCase);
        }

        return fileName.Contains(".so", StringComparison.OrdinalIgnoreCase);
    }
}
