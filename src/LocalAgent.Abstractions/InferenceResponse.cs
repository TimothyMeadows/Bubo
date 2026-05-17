namespace Bubo.LocalAgent.Abstractions;

public sealed record InferenceResponse
{
    public bool Success { get; init; } = true;

    public required string Text { get; init; }

    public IReadOnlyList<TranscriptEvent> Events { get; init; } = Array.Empty<TranscriptEvent>();
}
