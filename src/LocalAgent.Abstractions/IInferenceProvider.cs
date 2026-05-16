namespace Bubo.LocalAgent.Abstractions;

public interface IInferenceProvider
{
    string Name { get; }

    Task<InferenceResponse> GenerateAsync(
        InferenceRequest request,
        CancellationToken cancellationToken);
}
