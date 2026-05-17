using System.Runtime.InteropServices;
using Bubo.LlamaCppSharp;
using Bubo.LlamaCppSharp.Native;

namespace Bubo.LlamaCppSharp.Native.Tests;

public sealed class NativeAssetInfoTests
{
    [Fact]
    public void NativeAssetInfoPinsLlamaCppRelease()
    {
        Assert.Equal("https://github.com/ggml-org/llama.cpp", NativeAssetInfo.UpstreamRepository);
        Assert.Equal("b9189", NativeAssetInfo.ReleaseTag);
        Assert.Equal("64b38b561b987679c4e1c6231f93860d3eec2638", NativeAssetInfo.ReleaseCommit);
        Assert.Equal(NativeAssetInfo.ReleaseTag, LlamaRuntimeInfo.ReleaseTag);
    }

    [Fact]
    public void ExpectedRidAssetPathUsesRuntimeNativeLayout()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "bubo-native-test");

        var path = LlamaNativeLibrary.ExpectedRidAssetPath(baseDirectory);

        Assert.Contains(Path.Combine("runtimes", LlamaNativeLibrary.RuntimeIdentifier, "native"), path);
        Assert.EndsWith(LlamaNativeLibrary.NativeLibraryFileName, path);
    }

    [Fact]
    public void ExpectedRidAssetPathUsesBackendNativeLayoutForGpuBackends()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "bubo-native-test");

        var path = LlamaNativeLibrary.ExpectedRidAssetPath(baseDirectory, LlamaNativeLibrary.CudaBackend);

        Assert.Contains(Path.Combine("runtimes", LlamaNativeLibrary.RuntimeIdentifier, "native", "cuda"), path);
        Assert.EndsWith(LlamaNativeLibrary.NativeLibraryFileName, path);
    }

    [Fact]
    public void TryLoadDefaultReturnsStructuredResultWhenNativeAssetIsMissing()
    {
        var baseDirectory = Path.Combine(
            Path.GetTempPath(),
            "bubo-native-test",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDirectory);

        var result = LlamaRuntimeAvailability.Probe(baseDirectory);

        if (result.Success)
        {
            NativeLibrary.Free(result.Handle);
            Assert.NotNull(result.ResolvedPath);
        }
        else
        {
            Assert.NotNull(result.Error);
            Assert.Contains("Unable to load", result.Error);
        }
    }

    [Fact]
    public void StrictProbeDoesNotFallbackToAmbientLibraryName()
    {
        var baseDirectory = Path.Combine(
            Path.GetTempPath(),
            "bubo-native-test",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDirectory);

        var result = LlamaRuntimeAvailability.Probe(
            baseDirectory,
            allowFallbackByName: false,
            backend: LlamaNativeLibrary.CudaBackend);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains(LlamaNativeLibrary.ExpectedRidAssetPath(baseDirectory, LlamaNativeLibrary.CudaBackend), result.Error);
        Assert.Contains("backend 'cuda'", result.Error);
        Assert.Equal(LlamaNativeLibrary.CudaBackend, result.Backend);
        Assert.Equal(LlamaNativeLibrary.RuntimeIdentifier, result.RuntimeIdentifier);
        Assert.Equal(
            LlamaNativeLibrary.ExpectedRidAssetPath(baseDirectory, LlamaNativeLibrary.CudaBackend),
            result.ExpectedPath);
        Assert.DoesNotContain("or by name", result.Error);
    }

    [Fact]
    public void NormalizeBackendRejectsUnsupportedBackend()
    {
        var exception = Assert.Throws<ArgumentException>(() => LlamaNativeLibrary.NormalizeBackend("quantum"));

        Assert.Contains("Unsupported", exception.Message);
    }
}
