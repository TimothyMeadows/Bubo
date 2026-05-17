using Bubo.LocalAgent.Abstractions;

namespace Bubo.LocalAgent.Cli;

public sealed record CommandLineOptions
{
    public string Command { get; init; } = "run";

    public required string WorkspacePath { get; init; }

    public required string InputPath { get; init; }

    public required string OutputPath { get; init; }

    public AgentMode Mode { get; init; } = AgentMode.Local;

    public bool ModeWasSpecified { get; init; }

    public string? ConfigPath { get; init; }

    public string? NativeBaseDirectory { get; init; }

    public bool NativeStrict { get; init; }

    public string NativeBackend { get; init; } = "cpu";

    public string? SandboxGpu { get; init; }
}
