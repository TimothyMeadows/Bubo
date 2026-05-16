namespace Bubo.LocalAgent.Abstractions;

public sealed record AgentRunResult
{
    public required bool Success { get; init; }

    public required string Summary { get; init; }

    public IReadOnlyList<string> FilesChanged { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CommandsRun { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> IssuesOrRisks { get; init; } = Array.Empty<string>();
}
