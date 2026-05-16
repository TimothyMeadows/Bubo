using System.Runtime.InteropServices;

namespace Bubo.LlamaCppSharp.Native;

internal static partial class LlamaNativeMethods
{
    private const string LibraryName = "llama";

    [LibraryImport(LibraryName, EntryPoint = "llama_backend_init")]
    internal static partial void BackendInit();

    [LibraryImport(LibraryName, EntryPoint = "llama_backend_free")]
    internal static partial void BackendFree();

    [LibraryImport(LibraryName, EntryPoint = "llama_model_free")]
    internal static partial void ModelFree(nint model);

    [LibraryImport(LibraryName, EntryPoint = "llama_free")]
    internal static partial void ContextFree(nint context);

    [LibraryImport(LibraryName, EntryPoint = "llama_sampler_free")]
    internal static partial void SamplerFree(nint sampler);
}
