using Bubo.LlamaCppSharp.Native;

namespace Bubo.LlamaCppSharp;

public static class LlamaRuntimeAvailability
{
    public static NativeLibraryLoadResult Probe(
        string? baseDirectory = null,
        bool allowFallbackByName = true,
        string backend = LlamaNativeLibrary.CpuBackend)
    {
        return LlamaNativeLibrary.TryLoadDefault(baseDirectory, allowFallbackByName, backend);
    }
}
