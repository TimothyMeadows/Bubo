using Bubo.LocalAgent.Abstractions;
using Bubo.LlamaCppSharp;

namespace Bubo.LocalAgent.Inference.LlamaCpp;

public sealed class LlamaCppInferenceProvider : IInferenceProvider
{
    public string Name => "llama.cpp";

    public Task<InferenceResponse> GenerateAsync(
        InferenceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = request.SystemPrompt;

        var probe = LlamaRuntimeAvailability.Probe();
        if (!probe.Success)
        {
            return Task.FromResult(new InferenceResponse
            {
                Text = $"Local llama.cpp inference is unavailable: {probe.Error}",
                Events = new[]
                {
                    new TranscriptEvent
                    {
                        Type = "llama.cpp.unavailable",
                        Message = probe.Error ?? "llama.cpp native library could not be loaded."
                    }
                }
            });
        }

        return Task.FromResult(new InferenceResponse
        {
            Text = "Local llama.cpp native library is available. Generation loop binding is pending model/context decode implementation.",
            Events = new[]
            {
                new TranscriptEvent
                {
                    Type = "llama.cpp.available",
                    Message = $"Loaded llama.cpp native library from {probe.ResolvedPath}."
                }
            }
        });
    }
}
