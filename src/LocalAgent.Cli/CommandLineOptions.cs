using Bubo.LocalAgent.Abstractions;

namespace Bubo.LocalAgent.Cli;

public sealed record CommandLineOptions
{
    public required string WorkspacePath { get; init; }

    public required string InputPath { get; init; }

    public required string OutputPath { get; init; }

    public AgentMode Mode { get; init; } = AgentMode.Local;
}
