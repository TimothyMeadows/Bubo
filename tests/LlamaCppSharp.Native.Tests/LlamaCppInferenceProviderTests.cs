using Bubo.LocalAgent.Abstractions;
using Bubo.LocalAgent.Inference.LlamaCpp;

namespace Bubo.LlamaCppSharp.Native.Tests;

public sealed class LlamaCppInferenceProviderTests
{
    [Fact]
    public async Task GenerateAsyncReturnsStructuredUnavailableResponseWhenNativeLibraryIsMissing()
    {
        var provider = new LlamaCppInferenceProvider();

        var response = await provider.GenerateAsync(
            new InferenceRequest
            {
                Role = "planner",
                Prompt = "hello",
                ModelProfile = new ModelProfile { Role = "planner" }
            },
            CancellationToken.None);

        Assert.Contains("llama.cpp", response.Text);
        Assert.NotEmpty(response.Events);
    }
}
