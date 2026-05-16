namespace Bubo.LocalAgent.Runtime;

internal sealed record AgentInputAction
{
    public required string Tool { get; init; }

    public IReadOnlyDictionary<string, string> Arguments { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
