namespace Bubo.Splats.Domain;

/// <summary>A source identity supplied by the caller; resolving and hashing it are external operations.</summary>
public sealed record SplatSource
{
    public required string Path { get; init; }
    public required string Sha256 { get; init; }
}

/// <summary>Explicit admission limits. Process memory is not a GPU-memory or security-isolation guarantee.</summary>
public sealed record SplatBudgets
{
    public required int MaxGaussians { get; init; }
    public required long MaxInputBytes { get; init; }
    public required long MaxDecodedBytes { get; init; }
    public required int MaxHeaderBytes { get; init; }
    public required int TimeoutSeconds { get; init; }
    public required long MaxLogBytes { get; init; }
    public required long MaxProcessMemoryBytes { get; init; }
}

/// <summary>Technical request data; this record grants no installation, generation or human acceptance.</summary>
public sealed record SplatRequest
{
    public required string SchemaVersion { get; init; }
    public required string Backend { get; init; }
    public required string StyleSha256 { get; init; }
    public required SplatSource Input { get; init; }
    public required int[] GaussianCounts { get; init; }
    public required long Seed { get; init; }
    public required SplatBudgets Budgets { get; init; }
}

/// <summary>Declared construction identities; validation does not authenticate these observations or human review.</summary>
public sealed record SplatAncestry
{
    public required string RequestSha256 { get; init; }
    public required string SourceRevision { get; init; }
    public required string ModelRevision { get; init; }
    public required string EnvironmentSha256 { get; init; }
    public required string RuntimeBuildSha256 { get; init; }
    public required string ResourceCatalogSha256 { get; init; }
    public required string? ParentArtifactSha256 { get; init; }
}

/// <summary>An uncalibrated master Gaussian scene. Target transforms, collision and acceptance are separate contracts.</summary>
public sealed record SplatArtifact
{
    public required string SchemaVersion { get; init; }
    public required string Backend { get; init; }
    public required string StyleSha256 { get; init; }
    public required SplatSource Input { get; init; }
    public required SplatSource Payload { get; init; }
    public required string Profile { get; init; }
    public required int GaussianCount { get; init; }
    public required string Coordinates { get; init; }
    public required string Units { get; init; }
    public required string ScaleConfidence { get; init; }
    public required SplatAncestry Ancestry { get; init; }
    public required SplatBudgets Budgets { get; init; }
}
