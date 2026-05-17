namespace Bubo.LocalAgent.Abstractions;

public sealed record OpenCawOptions
{
    public string RepositoryUrl { get; init; } = "https://github.com/TimothyMeadows/OpenCaw";

    public string Path { get; init; } = ".opencaw";

    public string Ref { get; init; } = "main";

    public bool UpdateOnRun { get; init; }
}
