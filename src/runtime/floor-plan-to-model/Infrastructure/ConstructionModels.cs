using Bubo.FloorPlan.Domain;
namespace Bubo.FloorPlan.Infrastructure;

public sealed record ConstructionIdentities(string PlanSha256, string DesignFingerprint, string RuntimeSha256);
public sealed record ConstructionLimits(int MaxTriangles, int MaxVertices, int MaxObjects, long MaxOutputBytes, long MaxJobBytes);
public sealed record ConstructionBlenderJob(string SchemaVersion, string Operation, string WorkspaceRoot, string OutputDirectory,
    ConstructionScene Scene, ConstructionIdentities Identities, SortedDictionary<string, string> Bundle, ConstructionLimits Limits);
public sealed record ConstructionBlendFile(string Path, string Sha256, long Bytes);
public sealed record ConstructionCounts(int Meshes, int Anchors, int Materials, int Vertices, int Triangles, int VisualTriangles, int CollisionTriangles);
public sealed record ConstructionMeshReport
{
    public required string Id { get; init; }
    public required string SemanticId { get; init; }
    public required string StoreyId { get; init; }
    public required string Kind { get; init; }
    public required string Representation { get; init; }
    public required string CollisionRole { get; init; }
    public required string MaterialId { get; init; }
    public required string? ParentAnchorId { get; init; }
    public required int VertexCount { get; init; }
    public required int TriangleCount { get; init; }
    public required Point3 Minimum { get; init; }
    public required Point3 Maximum { get; init; }
    public required double MaxWorldCoordinateError { get; init; }
    public required string GeometrySha256 { get; init; }
    public required string SourceGeometrySha256 { get; init; }
    public required string IndexSha256 { get; init; }
    public required string MaterialAssignmentsSha256 { get; init; }
    public required Dictionary<string, int> MaterialTriangleCounts { get; init; }
    public required int ComponentCount { get; init; }
    public required double MinimumComponentVolume { get; init; }
    public required bool Manifold { get; init; }
}
public sealed record ConstructionReport
{
    public required string SchemaVersion { get; init; }
    public required string JobSha256 { get; init; }
    public required Dictionary<string, string> Bundle { get; init; }
    public required ConstructionIdentities Identities { get; init; }
    public required string BlenderVersion { get; init; }
    public required ConstructionBlendFile Blend { get; init; }
    public required ConstructionCounts Counts { get; init; }
    public required Point3 Minimum { get; init; }
    public required Point3 Maximum { get; init; }
    public required double MaxWorldCoordinateError { get; init; }
    public required List<ConstructionMeshReport> Meshes { get; init; }
    public required List<ConstructionAnchor> Anchors { get; init; }
    public required List<ConstructionMaterial> Materials { get; init; }
    public required bool IndependentReimport { get; init; }
    public required bool HumanModelAccepted { get; init; }
    public required bool RuntimeAccepted { get; init; }
}
public sealed record ConstructionAttempt
{
    public required string Directory { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required string PlanSha256 { get; init; }
    public required string DesignFingerprint { get; init; }
    public required string ApprovalSha256 { get; init; }
    public required string ToolPath { get; init; }
    public required string ToolSha256 { get; init; }
    public required SortedDictionary<string, string> Bundle { get; init; }
    public string Status { get; set; } = "running";
    public string? Error { get; set; }
    public string? JobSha256 { get; set; }
    public string? ReportSha256 { get; set; }
    public string? BlendSha256 { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
public sealed record ConstructionState
{
    public string SchemaVersion { get; init; } = "bubo-floor-plan-construction/v1";
    public required string Task { get; init; }
    public required string Asset { get; init; }
    public required string Version { get; init; }
    public required string DesignManifest { get; init; }
    public required string RuntimeSha256 { get; init; }
    public List<ConstructionAttempt> Attempts { get; init; } = [];
}
