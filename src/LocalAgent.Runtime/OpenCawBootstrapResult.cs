using Bubo.LocalAgent.Abstractions;

namespace Bubo.LocalAgent.Runtime;

public sealed record OpenCawBootstrapResult
{
    public bool Success { get; init; } = true;

    public string SystemPrompt { get; init; } = string.Empty;

    public string? Error { get; init; }

    public IReadOnlyList<TranscriptEvent> Events { get; init; } = Array.Empty<TranscriptEvent>();
}
