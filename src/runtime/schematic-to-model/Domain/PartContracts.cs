namespace Bubo.Schematic.Domain;

/// <summary>Independent definition prototypes only; this job does not instantiate or rig an assembly.</summary>
public sealed record PartBuildJob
{
    public required string SchemaVersion { get; init; }
    public required string Operation { get; init; }
    public required string ProjectRoot { get; init; }
    public required string Output { get; init; }
    public required AssemblyDesign Design { get; init; }
    public required Dictionary<string, string> PartIdentities { get; init; }
    public required PartBuildIdentity Identity { get; init; }
    public required SortedDictionary<string, string> Bundle { get; init; }
    public required PartBuildLimits Limits { get; init; }
}
public sealed record PartBuildIdentity(string DesignSha256, string DesignFingerprint, string RuntimeBuildSha256, string ToolSha256);
public sealed record PartBuildLimits(int MaxVertices, int MaxTriangles, long MaxOutputBytes, int MaxJobBytes, int MaxFiles);
public sealed record PartArtifact
{
    public required string Id { get; init; }
    public required string DefinitionSha256 { get; init; }
    public required string MaterialId { get; init; }
    public required string BlendFile { get; init; }
    public required string BlendSha256 { get; init; }
    public required string GeometryFile { get; init; }
    public required string GeometrySha256 { get; init; }
    public required int VertexCount { get; init; }
    public required int TriangleCount { get; init; }
    public required Vec3 Minimum { get; init; }
    public required Vec3 Maximum { get; init; }
    public required double Volume { get; init; }
    public required double SurfaceArea { get; init; }
    public required int Components { get; init; }
    public required bool Manifold { get; init; }
    public required Vec3 MaterialColorLinear { get; init; }
    public required double MaterialRoughness { get; init; }
    public required double MaterialMetallic { get; init; }
}
public sealed record PartsReport
{
    public required string SchemaVersion { get; init; }
    public required string JobSha256 { get; init; }
    public required PartBuildIdentity Identity { get; init; }
    public required string BlenderVersion { get; init; }
    public required List<PartArtifact> Parts { get; init; }
    public required List<PartInstance> Instances { get; init; }
    public required long WeightedTriangles { get; init; }
    public required long TotalBytes { get; init; }
}
public sealed record PartReadinessReport(IReadOnlyList<ValidationIssue> Issues)
{
    public bool IsReady => Issues.Count == 0;
}
