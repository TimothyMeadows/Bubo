using Bubo.LocalAgent.Abstractions;

namespace Bubo.LocalAgent.Cli;

public sealed record AgentConfigLoadResult
{
    public required AgentRunConfig Config { get; init; }

    public string? ConfigPath { get; init; }

    public bool WasLoaded { get; init; }
}
