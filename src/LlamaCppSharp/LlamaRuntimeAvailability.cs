using Bubo.LlamaCppSharp.Native;

namespace Bubo.LlamaCppSharp;

public static class LlamaRuntimeAvailability
{
    public static NativeLibraryLoadResult Probe(
        string? baseDirectory = null,
        bool allowFallbackByName = true)
    {
        return LlamaNativeLibrary.TryLoadDefault(baseDirectory, allowFallbackByName);
    }
}
