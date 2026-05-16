namespace Bubo.LocalAgent.Abstractions;

public sealed record ModelProfile
{
    public string Role { get; init; } = string.Empty;

    public string? Path { get; init; }

    public int ContextSize { get; init; } = 32_768;

    public double Temperature { get; init; } = 0.2;

    public double TopP { get; init; } = 0.9;

    public double RepeatPenalty { get; init; } = 1.05;

    public int MaxTokens { get; init; } = 4_096;

    public string GpuLayers { get; init; } = "auto";

    public int Threads { get; init; } = Environment.ProcessorCount;
}
