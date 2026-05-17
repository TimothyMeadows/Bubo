namespace Bubo.LocalAgent.Abstractions;

public sealed record InferenceRequest
{
    public required string Role { get; init; }

    public string? SystemPrompt { get; init; }

    public required string Prompt { get; init; }

    public required ModelProfile ModelProfile { get; init; }
}
