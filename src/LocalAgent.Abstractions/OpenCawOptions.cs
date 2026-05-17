namespace Bubo.LocalAgent.Abstractions;

public sealed record OpenCawOptions
{
    public bool Enabled { get; init; }

    public string RepositoryUrl { get; init; } = "https://github.com/TimothyMeadows/OpenCaw";

    public string Path { get; init; } = ".opencaw";

    public string Ref { get; init; } = "main";

    public bool UpdateOnRun { get; init; }

    public bool ExecuteBootstrap { get; init; } = true;
}
