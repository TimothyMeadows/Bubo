namespace Bubo.LocalAgent.Abstractions;

public sealed record AgentLimits
{
    public int MaxIterations { get; init; } = 8;

    public int MaxToolCalls { get; init; } = 80;

    public int MaxCommandSeconds { get; init; } = 600;

    public int MaxPatchBytes { get; init; } = 262_144;

    public int MaxFilesChanged { get; init; } = 25;

    public int MaxTokensPerStep { get; init; } = 8_192;
}
