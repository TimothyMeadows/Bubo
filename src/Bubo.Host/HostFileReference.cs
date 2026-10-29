namespace Bubo.Host;

/// <summary>An exact caller-declared file identity, not a review or rights grant.</summary>
public sealed record HostFileReference(string Path, string Sha256, string Kind);
