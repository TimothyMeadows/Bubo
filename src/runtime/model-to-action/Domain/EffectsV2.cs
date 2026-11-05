using System.Text.Json.Serialization;

namespace Hephaera.ModelAction.Domain;

/// <summary>Exact local bytes. References grant no rights, execution or presentation authority.</summary>
public sealed record ActionFileReference
{
    public required string Path { get; init; }
    public required string Sha256 { get; init; }
}

public sealed record ActionSizedFileReference
{
    public required string Path { get; init; }
    public required string Sha256 { get; init; }
    public required long Bytes { get; init; }
}

public sealed record ActionEffectProfileReference
{
    public required string SchemaVersion { get; init; }
    public required string Path { get; init; }
    public required string Sha256 { get; init; }
}

public sealed record ActionEffectSource
{
    public required string Id { get; init; }
    public required ActionFileReference Package { get; init; }
    public required string EffectId { get; init; }
    public required ActionEffectProfileReference Profile { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ActionFileReference? Preparation { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ActionFileReference? PreparationContext { get; init; }
    public required IReadOnlyList<ActionEffectCorrespondence> Correspondences { get; init; }
    public required ActionFileReference ProductionReview { get; init; }
    public required ActionFileReference Rights { get; init; }
}

public sealed record ActionEffectCorrespondence
{
    public required string ResourceId { get; init; }
    public required string ResourceSha256 { get; init; }
    public required string Kind { get; init; }
    public required string DecodedSha256 { get; init; }
    public required ActionSizedFileReference Report { get; init; }
}

public sealed record ActionEffectResource
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string Path { get; init; }
    public required string Sha256 { get; init; }
    public required long Bytes { get; init; }
}

/// <summary>Private source closure entries stay in local durable state.</summary>
public sealed record ActionEffectFile
{
    public required string Role { get; init; }
    public required string SourceId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResourceId { get; init; }
    public required string Path { get; init; }
    public required long Bytes { get; init; }
    public required string Sha256 { get; init; }
}

public sealed record ActionEffectSourceInventory
{
    public required string SourceClosureSha256 { get; init; }
    public required IReadOnlyList<ActionEffectFile> Files { get; init; }
}

/// <summary>Portable runtime closure contains no raw rights, review, capture or preparation-source payloads.</summary>
public sealed record ActionRuntimeEffectSource
{
    public required string Id { get; init; }
    public required string EffectId { get; init; }
    public required ActionFileReference Package { get; init; }
    public required ActionEffectProfileReference Profile { get; init; }
    public required IReadOnlyList<ActionEffectResource> Resources { get; init; }
    public required IReadOnlyList<ActionEffectCorrespondence> Correspondences { get; init; }
    public required ActionEffectRuntimeAncestry Ancestry { get; init; }
}

public sealed record ActionEffectRuntimeAncestry
{
    public required string SourceClosureSha256 { get; init; }
    public required string ProductionReviewSha256 { get; init; }
    public required string RightsSha256 { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NativePreparationSha256 { get; init; }
}

/// <summary>The fixed data profile describes rendering inputs; Bubo does not render them.</summary>
public sealed record ActionEffectProfile
{
    public required string SchemaVersion { get; init; }
    public required string NativePackageSha256 { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PreparationSha256 { get; init; }
    public required ActionEffectProfileBudgets Budgets { get; init; }
    public required IReadOnlyList<ActionEffectProfileLayer> Layers { get; init; }
}

public sealed record ActionEffectProfileBudgets
{
    public required int MaxSystems { get; init; }
    public required int MaxParticles { get; init; }
    public required int MaxTrailPoints { get; init; }
    public required int MaxVertices { get; init; }
    public required int MaxIndices { get; init; }
    public required int MaxDrawCalls { get; init; }
    public required long MaxTextureBytes { get; init; }
    public required long MaxBufferBytes { get; init; }
    public required double MaxDurationSeconds { get; init; }
    public required double MaxUpdateSeconds { get; init; }
}

public sealed record ActionEffectProfileLayer
{
    public required string EffectId { get; init; }
    public required string LayerId { get; init; }
    public required string Family { get; init; }
    public required ActionEffectEmission Emission { get; init; }
    public required double Size { get; init; }
    public required double Speed { get; init; }
    public required IReadOnlyList<double> Color { get; init; }
    public required string Space { get; init; }
    public required double MaxTailSeconds { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResourceId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ActionEffectGeometry? Geometry { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ActionEffectTrailProfile? Trail { get; init; }
}

public sealed record ActionEffectEmission
{
    public required string Kind { get; init; }
    public required int Capacity { get; init; }
    public required int Count { get; init; }
    public required double Rate { get; init; }
    public required double LifetimeSeconds { get; init; }
    public required double DurationSeconds { get; init; }
}

public sealed record ActionEffectGeometry
{
    public required string Kind { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? InnerRadius { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? OuterRadius { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Segments { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResourceId { get; init; }
}

public sealed record ActionEffectTrailProfile
{
    public required int HistoryPoints { get; init; }
}

public sealed record ActionEffectTransform
{
    public required string Units { get; init; }
    public required string Up { get; init; }
    public required string Forward { get; init; }
    public required string Handedness { get; init; }
    public required IReadOnlyList<double> Position { get; init; }
    public required IReadOnlyList<double> Orientation { get; init; }
    public required double Scale { get; init; }
    public required string ParentScale { get; init; }
}

public sealed record ActionEffectLifetime
{
    public required double TailSeconds { get; init; }
    public required string Pause { get; init; }
    public required string TailAttachment { get; init; }
    public required string Cancel { get; init; }
}

public sealed record ActionEffectOverride
{
    public required string Layer { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? SizeMultiplier { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<double>? ColorMultiplier { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? EmissionMultiplier { get; init; }
}

public sealed record ActionEffectTrailPolicy
{
    public required string Reset { get; init; }
    public required int MaxSamples { get; init; }
    public required double DistanceMeters { get; init; }
}

/// <summary>Assertions are checked for complete current identities, not authenticated as human decisions.</summary>
public sealed record ActionEffectRights
{
    public required string SchemaVersion { get; init; }
    public required bool Authorized { get; init; }
    public required string License { get; init; }
    public required string Owner { get; init; }
    public required string Reason { get; init; }
    public required IReadOnlyList<ActionSizedFileReference> Inputs { get; init; }
}

public sealed record ActionEffectProductionReview
{
    public required string SchemaVersion { get; init; }
    public required string Decision { get; init; }
    public required bool Independent { get; init; }
    public required string Reviewer { get; init; }
    public required string Author { get; init; }
    public required string SubjectSha256 { get; init; }
    public required string Reason { get; init; }
    public required IReadOnlyList<ActionSizedFileReference> Evidence { get; init; }
}

public sealed record ActionEffectCorrespondenceReport
{
    public required string SchemaVersion { get; init; }
    public required string ResourceId { get; init; }
    public required string ResourceSha256 { get; init; }
    public required string Kind { get; init; }
    public required string DecodedSha256 { get; init; }
    public required bool Independent { get; init; }
    public required ActionEffectResourceMeasurements Measurements { get; init; }
    public required string SourceEvidenceSha256 { get; init; }
}

public sealed record ActionEffectResourceMeasurements
{
    public required long Bytes { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Width { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Height { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Vertices { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Indices { get; init; }
}
