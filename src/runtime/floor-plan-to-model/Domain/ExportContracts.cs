using System.Text.Json.Serialization;

namespace Bubo.FloorPlan.Domain;

/// <summary>Portable, sealed payload. Its raw external hash is its identity; this inventory excludes package.json.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FloorPlanExportPackage
{
    public required string SchemaVersion { get; init; }
    public required string Task { get; init; }
    public required string Asset { get; init; }
    public required string Version { get; init; }
    public required ExportAncestry Ancestry { get; init; }
    public required ExportCoordinateSystem Coordinates { get; init; }
    public required NumericalPolicy Numerics { get; init; }
    public required List<ExportFileEntry> Files { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExportFileEntry(string Path, string Sha256, long Bytes);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExportAncestry
{
    public required string ModelAuthor { get; init; }
    public required string PlanSha256 { get; init; }
    public required string DesignFingerprint { get; init; }
    public required string ApprovalSha256 { get; init; }
    public required string RuntimeSha256 { get; init; }
    public required string CatalogSha256 { get; init; }
    public required string ConstructionAttempt { get; init; }
    public required string ConstructionJobSha256 { get; init; }
    public required string ConstructionReportSha256 { get; init; }
    public required string SourceSha256 { get; init; }
    public required SortedDictionary<string, string> Bundle { get; init; }
    public required ExportToolchain Toolchain { get; init; }
}

/// <summary>Portable tool identities only. Local executable/module roots belong to external native jobs.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExportToolchain
{
    public required string BlenderSha256 { get; init; }
    public required string BlenderVersion { get; init; }
    public required List<ExportFileEntry> GltfFiles { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExportCoordinateSystem(string Source, string Target, List<double> SourceToGltf);

/// <summary>Exact source dimensions plus explicit representation/node mapping. No observed-export or acceptance claims.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FloorPlanSemantics
{
    public required string SchemaVersion { get; init; }
    public required string PlanSha256 { get; init; }
    public required string ConstructionJobSha256 { get; init; }
    public required string SourceSha256 { get; init; }
    public required ExportCoordinateSystem Coordinates { get; init; }
    public required BuildingPlan Plan { get; init; }
    public required List<ConstructionMaterial> Materials { get; init; }
    public required List<ExportMeshIdentity> Meshes { get; init; }
    public required List<ConstructionAnchor> Anchors { get; init; }
    public required string DoorPoseConvention { get; init; }
    public required string ClearanceBasis { get; init; }
    public required ConstructionClearanceEvidence Clearance { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExportMeshIdentity
{
    public required string Id { get; init; }
    public required string NodeId { get; init; }
    public required string ParentNodeId { get; init; }
    public required string SemanticId { get; init; }
    public required string StoreyId { get; init; }
    public required string Kind { get; init; }
    public required string Representation { get; init; }
    public required string CollisionRole { get; init; }
    public required string MaterialId { get; init; }
    public required string? ParentAnchorId { get; init; }
    public required List<string>? TriangleMaterialIds { get; init; }
    public required int SourceVertexCount { get; init; }
    public required int SourceTriangleCount { get; init; }
    public required Point3 Minimum { get; init; }
    public required Point3 Maximum { get; init; }
}

/// <summary>External submitted decision, never included in or synthesized by the sealed payload.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FloorPlanModelReview
{
    public required string SchemaVersion { get; init; }
    public required string Kind { get; init; }
    public required string Decision { get; init; }
    public required string Reviewer { get; init; }
    public required string Role { get; init; }
    public required bool Human { get; init; }
    public required string Reason { get; init; }
    public required string PackageSha256 { get; init; }
    public required string VisualVerificationSha256 { get; init; }
    public required string CollisionVerificationSha256 { get; init; }
    public required string ViewsSha256 { get; init; }
    public required string? IndependentReviewSha256 { get; init; }
    public required List<ExportFileEntry> Artifacts { get; init; }
}

/// <summary>Caller-supplied current external identities, including exact complete reviewed artifacts.</summary>
public sealed record ModelReviewBinding
{
    public required string Kind { get; init; }
    public required string Author { get; init; }
    public required string PackageSha256 { get; init; }
    public required string VisualVerificationSha256 { get; init; }
    public required string CollisionVerificationSha256 { get; init; }
    public required string ViewsSha256 { get; init; }
    /// <summary>Only the current independently approved decision may be supplied for human admission.</summary>
    public required string? IndependentReviewSha256 { get; init; }
    public required List<ExportFileEntry> Artifacts { get; init; }
}
