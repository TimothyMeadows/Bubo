namespace Bubo.LocalAgent.Abstractions;

public sealed record InferenceChunk
{
    public required string Text { get; init; }

    public bool IsFinal { get; init; }
}
