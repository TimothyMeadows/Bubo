using Bubo.LlamaCppSharp.Native;

namespace Bubo.LlamaCppSharp;

public static class LlamaRuntimeAvailability
{
    public static NativeLibraryLoadResult Probe(string? baseDirectory = null)
    {
        return LlamaNativeLibrary.TryLoadDefault(baseDirectory);
    }
}
