using Bubo.LocalAgent.Abstractions;

namespace Bubo.LocalAgent.Inference.CodexCli;

public sealed class CodexCliInferenceProvider : IInferenceProvider
{
    public string Name => "codex-cli";

    public Task<InferenceResponse> GenerateAsync(
        InferenceRequest request,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException(
            "codex-cli inference is implemented in a later goal task.");
    }
}
