namespace Bubo.LocalAgent.Abstractions;

public interface IStreamingInferenceProvider : IInferenceProvider
{
    IAsyncEnumerable<InferenceChunk> StreamAsync(
        InferenceRequest request,
        CancellationToken cancellationToken);
}
