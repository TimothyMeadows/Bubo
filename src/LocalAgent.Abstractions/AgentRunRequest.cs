namespace Bubo.LocalAgent.Abstractions;

public sealed record AgentRunRequest
{
    public required string WorkspacePath { get; init; }

    public required string InputPath { get; init; }

    public required string OutputPath { get; init; }

    public AgentMode Mode { get; init; } = AgentMode.Local;
}
