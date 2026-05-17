using Bubo.LlamaCppSharp.Native;

namespace Bubo.LlamaCppSharp;

public static class LlamaRuntimeInfo
{
    public static string UpstreamRepository => NativeAssetInfo.UpstreamRepository;

    public static string ReleaseTag => NativeAssetInfo.ReleaseTag;

    public static string ReleaseCommit => NativeAssetInfo.ReleaseCommit;

    public static string ExpectedNativeLibraryPath(
        string baseDirectory,
        string backend = Native.LlamaNativeLibrary.CpuBackend)
    {
        return LlamaNativeLibrary.ExpectedRidAssetPath(baseDirectory, backend);
    }
}
