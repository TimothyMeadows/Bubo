using System.Runtime.InteropServices;

namespace Bubo.LlamaCppSharp.Native;

public static class LlamaNativeLibrary
{
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

    public static string ExpectedRidAssetPath(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        return Path.Combine(
            baseDirectory,
            "runtimes",
            RuntimeIdentifier,
            "native",
            NativeLibraryFileName);
    }

    public static NativeLibraryLoadResult TryLoadDefault(
        string? baseDirectory = null,
        bool allowFallbackByName = true)
    {
        var root = baseDirectory ?? AppContext.BaseDirectory;
        var ridPath = ExpectedRidAssetPath(root);
        PreloadSidecarLibraries(Path.GetDirectoryName(ridPath), NativeLibraryFileName);

        if (NativeLibrary.TryLoad(ridPath, out var handle))
        {
            return new NativeLibraryLoadResult
            {
                Success = true,
                Handle = handle,
                ResolvedPath = ridPath
            };
        }

        if (allowFallbackByName && NativeLibrary.TryLoad(NativeLibraryFileName, out handle))
        {
            return new NativeLibraryLoadResult
            {
                Success = true,
                Handle = handle,
                ResolvedPath = NativeLibraryFileName
            };
        }

        return new NativeLibraryLoadResult
        {
            Success = false,
            Error = allowFallbackByName
                ? $"Unable to load llama.cpp native library from '{ridPath}' or by name '{NativeLibraryFileName}'."
                : $"Unable to load llama.cpp native library from '{ridPath}'."
        };
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
