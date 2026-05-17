namespace Bubo.LocalAgent.Abstractions;

public sealed record AgentRunResult
{
    public required bool Success { get; init; }

    public required string Summary { get; init; }

    public string ReportMarkdown { get; init; } = string.Empty;

    public IReadOnlyList<string> Plan { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ChangesMade { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> FilesChanged { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CommandsRun { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> TestResults { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> IssuesOrRisks { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> NextSteps { get; init; } = Array.Empty<string>();
}
