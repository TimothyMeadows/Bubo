namespace Bubo.Vfx.Domain;

/// <summary>Declarative inspection data. This contract grants no authoring or runtime authority.</summary>
public sealed record VfxPackage
{
    public required string SchemaVersion { get; init; } = "bubo-vfx-package/v1";
    public required string Id { get; init; }
    public required string Version { get; init; }
    public required string StyleSha256 { get; init; }
    public required VfxEngineIdentity Engine { get; init; }
    public required VfxBudgets Budgets { get; init; }
    public required IReadOnlyList<VfxResource> Resources { get; init; }
    public required IReadOnlyList<VfxEffect> Effects { get; init; }
}

public sealed record VfxEngineIdentity
{
    public required string Id { get; init; }
    public required string ContractSha256 { get; init; }
    public required string DefinitionSha256 { get; init; }
    public required string RegistrySha256 { get; init; }
}

public sealed record VfxResource
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    /// <summary>Canonical forward-slash path relative to the package directory.</summary>
    public required string Path { get; init; }
    public required string Sha256 { get; init; }
    public required long Bytes { get; init; }
}

public sealed record VfxEffect
{
    public required string Id { get; init; }
    public required double DurationSeconds { get; init; }
    public required IReadOnlyList<VfxLayer> Layers { get; init; }
}

public sealed record VfxLayer
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required double StartSeconds { get; init; }
    public required double DurationSeconds { get; init; }
    public required IReadOnlyList<string> ResourceIds { get; init; }
    public required int MaxParticles { get; init; }
    public required int MaxVertices { get; init; }
    public required int MaxDrawCalls { get; init; }
    public required int MaxTrailPoints { get; init; }
}

public sealed record VfxBudgets
{
    public required long MaxPackageBytes { get; init; }
    public required long MaxDependencyBytes { get; init; }
    public required long MaxTotalDependencyBytes { get; init; }
    public required int MaxDependencies { get; init; }
    public required int MaxEffects { get; init; }
    public required int MaxLayers { get; init; }
    public required int MaxParticles { get; init; }
    public required int MaxVertices { get; init; }
    public required int MaxDrawCalls { get; init; }
    public required int MaxTrailPoints { get; init; }
    public required double MaxDurationSeconds { get; init; }
}

public sealed record ValidationIssue(string Code, string Path, string Message);
public sealed record ValidationReport(bool IsValid, IReadOnlyList<ValidationIssue> Issues);
