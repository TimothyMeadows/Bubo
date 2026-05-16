namespace Bubo.LocalAgent.Abstractions;

public sealed record SandboxOptions
{
    public bool UseDocker { get; init; } = true;

    public string WorkspacePath { get; init; } = "/workspace";

    public string InputPath { get; init; } = "/input";

    public string OutputPath { get; init; } = "/output";

    public string? ModelsPath { get; init; } = "/models";

    public string CachePath { get; init; } = "/cache";

    public NetworkPolicy Network { get; init; } = NetworkPolicy.None;

    public string? Gpu { get; init; } = "nvidia";

    public string? Memory { get; init; } = "16g";

    public double? Cpus { get; init; }
}
