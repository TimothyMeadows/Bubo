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

    public static NativeLibraryLoadResult TryLoadDefault(string? baseDirectory = null)
    {
        var root = baseDirectory ?? AppContext.BaseDirectory;
        var ridPath = ExpectedRidAssetPath(root);

        if (NativeLibrary.TryLoad(ridPath, out var handle))
        {
            return new NativeLibraryLoadResult
            {
                Success = true,
                Handle = handle,
                ResolvedPath = ridPath
            };
        }

        if (NativeLibrary.TryLoad(NativeLibraryFileName, out handle))
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
            Error = $"Unable to load llama.cpp native library from '{ridPath}' or by name '{NativeLibraryFileName}'."
        };
    }
}
