using Bubo.LocalAgent.Abstractions;

namespace Bubo.LocalAgent.Inference.LlamaCpp;

public sealed class LlamaCppInferenceProvider : IInferenceProvider
{
    public string Name => "llama.cpp";

    public Task<InferenceResponse> GenerateAsync(
        InferenceRequest request,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException(
            "Local llama.cpp inference is implemented in a later goal task.");
    }
}
