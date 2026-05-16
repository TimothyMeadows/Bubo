namespace Bubo.LocalAgent.Inference.CodexCli;

public sealed record CodexCliOptions
{
    public string Executable { get; init; } = "codex";

    public string WorkingDirectory { get; init; } = Environment.CurrentDirectory;

    public string? Model { get; init; }

    public string? Profile { get; init; }

    public bool JsonEvents { get; init; } = true;

    public bool Ephemeral { get; init; } = true;
}
