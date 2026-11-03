namespace Bubo.FloorPlan.Domain;

/// <summary>Explicit construction dimensions and material roles included in whole-design approval, never runtime defaults.</summary>
public sealed record ConstructionProfile
{
    public required double RoofThickness { get; init; }
    /// <summary>Zero shares structural slab/roof undersides; positive panels extend upward from the declared clear ceiling.</summary>
    public required double CeilingThickness { get; init; }
    public required double DoorLeafThickness { get; init; }
    public required double DoorLeafGap { get; init; }
    /// <summary>Frame widths extend outside nominal clear openings rather than reducing approved aperture dimensions.</summary>
    public required double DoorFrameWidth { get; init; }
    public required double DoorFrameDepth { get; init; }
    public required double DoorRevealDepth { get; init; }
    public required double DoorOpenAngleDegrees { get; init; }
    public required double WindowFrameWidth { get; init; }
    public required double WindowFrameDepth { get; init; }
    public required double GlassThickness { get; init; }
    public required double RailingHeight { get; init; }
    public required double RailThickness { get; init; }
    public required double BalusterThickness { get; init; }
    /// <summary>Maximum clear baluster gap; bounded endpoint/remainder distribution may produce smaller gaps.</summary>
    public required double BalusterGap { get; init; }
    /// <summary>Distance from nominal stair clear edge to the railing's inner face, outside the flight.</summary>
    public required double RailingOffset { get; init; }
    public required double GlassTransmission { get; init; }
    public required double GlassIor { get; init; }
    public required string CeilingMaterialId { get; init; }
    public required string DoorMaterialId { get; init; }
    public required string FrameMaterialId { get; init; }
    public required string GlassMaterialId { get; init; }
    public required string RailingMaterialId { get; init; }
    public required int MaxMeshes { get; init; }
    public required int MaxConstructionVertices { get; init; }
    public required int MaxArrangementCells { get; init; }
    public required long MaxJobBytes { get; init; }
}

/// <summary>Pure compiler output. All positions share the approved XY metre/Z-up datum.</summary>
public sealed record ConstructionScene
{
    public required string SchemaVersion { get; init; }
    public required string PlanId { get; init; }
    public required double LinearTolerance { get; init; }
    public required List<ConstructionMaterial> Materials { get; init; }
    public required List<ConstructionMesh> Meshes { get; init; }
    public required List<ConstructionAnchor> Anchors { get; init; }
    public required ConstructionMetrics Metrics { get; init; }
    public required ConstructionClearanceEvidence Clearance { get; init; }
}

public sealed record ConstructionMaterial
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Color { get; init; }
    public required double Roughness { get; init; }
    public required double Metallic { get; init; }
    public required double Transmission { get; init; }
    public required double Ior { get; init; }
}

/// <summary>Indexed triangle solids; several nonoverlapping closed shells may share a semantic mesh. Vertices are world metres.</summary>
public sealed record ConstructionMesh
{
    public required string Id { get; init; }
    public required string SemanticId { get; init; }
    public required string StoreyId { get; init; }
    public required string Kind { get; init; }
    public required string Representation { get; init; }
    public required string CollisionRole { get; init; }
    public required string MaterialId { get; init; }
    public required List<Point3> Vertices { get; init; }
    public required List<int> Triangles { get; init; }
    public required string? ParentAnchorId { get; init; }
    /// <summary>If supplied, exactly one declared material identity per triangle; otherwise MaterialId applies throughout.</summary>
    public List<string>? TriangleMaterialIds { get; init; }
}

/// <summary>Semantic transform anchor. Parenting preserves mesh world positions at the initial rotation.</summary>
public sealed record ConstructionAnchor
{
    public required string Id { get; init; }
    public required string SemanticId { get; init; }
    public required string StoreyId { get; init; }
    public required string Kind { get; init; }
    public required Point3 Position { get; init; }
    public required Point3 Axis { get; init; }
    public required double RotationRadians { get; init; }
}

public sealed record ConstructionMetrics
{
    public required int MeshCount { get; init; }
    public required int VertexCount { get; init; }
    public required int TriangleCount { get; init; }
    public required int VisualTriangleCount { get; init; }
    public required int CollisionTriangleCount { get; init; }
    /// <summary>Construction-stage work only; prerequisite validation/freshness stages have their own finite source cap and share the outer deadline/cancellation.</summary>
    public required long GeometryChecks { get; init; }
    public required Point3 Minimum { get; init; }
    public required Point3 Maximum { get; init; }
}

/// <summary>Measured construction geometry only, not an engine navigation/collision certificate.</summary>
public sealed record ConstructionClearanceEvidence
{
    public required List<ConstructionRouteEvidence> Routes { get; init; }
    public required List<ConstructionDoorSweepEvidence> DoorSweeps { get; init; }
    public required double MinimumRouteClearance { get; init; }
}

/// <summary>Route margin is a conservative centerline separation minus the full player radius, measured with final-open door poses.</summary>
public sealed record ConstructionRouteEvidence
{
    public required string RoomId { get; init; }
    public required string FromId { get; init; }
    public required string ToId { get; init; }
    public required List<Point3> Points { get; init; }
    public required double Radius { get; init; }
    public required double Height { get; init; }
    public required double MinimumClearance { get; init; }
}

/// <summary>Signed full leaf-body sweep; margin includes the conservative envelope error, never a centerline-only distance.</summary>
public sealed record ConstructionDoorSweepEvidence
{
    public required string OpeningId { get; init; }
    public required string AnchorId { get; init; }
    public required double AngleDegrees { get; init; }
    public required double MinimumClearance { get; init; }
    public required Point3 BoundsMinimum { get; init; }
    public required Point3 BoundsMaximum { get; init; }
}
