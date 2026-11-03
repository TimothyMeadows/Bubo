namespace Bubo.FloorPlan.Domain;

/// <summary>One complete, empty architectural building. XY metres, common datum, positive Z up.</summary>
public sealed record BuildingPlan
{
    public required string SchemaVersion { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Purpose { get; init; }
    public required string ArchitecturalStyle { get; init; }
    public required string StyleSha256 { get; init; }
    public required string CoordinateSystem { get; init; }
    public required NumericalPolicy Numerics { get; init; }
    public required PlayerEnvelope Player { get; init; }
    public required PlanBudgets Budgets { get; init; }
    public required string EntryRoomId { get; init; }
    public required List<MaterialSpec> Materials { get; init; }
    public required List<DetailProfile> DetailProfiles { get; init; }
    public required List<StoreyPlan> Storeys { get; init; }
    public required List<StairPlan> Stairs { get; init; }
    public required List<RoofPlane> Roofs { get; init; }
}

public readonly record struct Point2(double X, double Y);
public readonly record struct Point3(double X, double Y, double Z);

/// <summary>Implicit closing edge; first vertex must not be repeated. Outer CCW, holes CW.</summary>
public sealed record Polygon2
{
    public required List<Point2> Outer { get; init; }
    public required List<List<Point2>> Holes { get; init; }
}

public sealed record NumericalPolicy
{
    public required double LinearTolerance { get; init; }
    public required double AngularToleranceDegrees { get; init; }
    public required double MaxCoordinateMagnitude { get; init; }
    public required double MinimumFeatureSize { get; init; }
}

/// <summary>Declared runtime clearance envelope; these are not building-code certifications.</summary>
public sealed record PlayerEnvelope
{
    public required double Radius { get; init; }
    public required double Height { get; init; }
    public required double MaxStepHeight { get; init; }
    public required double MinLandingDepth { get; init; }
}

public sealed record PlanBudgets
{
    public required int MaxStoreys { get; init; }
    public required int MaxRooms { get; init; }
    public required int MaxWalls { get; init; }
    public required int MaxOpenings { get; init; }
    public required int MaxStairs { get; init; }
    public required int MaxRoofPlanes { get; init; }
    public required int MaxVertices { get; init; }
    public required long MaxGeometryChecks { get; init; }
    public required long MaxPreviewBytes { get; init; }
    public required long MaxExportBytes { get; init; }
    public required int MaxGeneratedTriangles { get; init; }
}

public sealed record MaterialSpec
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Color { get; init; }
    public required double Roughness { get; init; }
    public required double Metallic { get; init; }
}

/// <summary>Authored facade/roof section dimensions; no generated or external runtime mesh.</summary>
public sealed record DetailProfile
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required double PlinthHeight { get; init; }
    public required double PlinthProjection { get; init; }
    public required double CorniceHeight { get; init; }
    public required double CorniceProjection { get; init; }
    public required double EaveOverhang { get; init; }
    public required double FasciaDepth { get; init; }
}

public sealed record StoreyPlan
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required double Elevation { get; init; }
    public required double Height { get; init; }
    public required double SlabThickness { get; init; }
    public required string FloorMaterialId { get; init; }
    public required Polygon2 Footprint { get; init; }
    public required List<RoomPlan> Rooms { get; init; }
    public required List<WallPlan> Walls { get; init; }
    public required List<SlabOpening> SlabOpenings { get; init; }
}

public sealed record RoomPlan
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required Polygon2 Boundary { get; init; }
    public required bool RequiresAccess { get; init; }
}

/// <summary>One wall centre line per physical wall; room polygons describe clear space.</summary>
public sealed record WallPlan
{
    public required string Id { get; init; }
    public required Point2 Start { get; init; }
    public required Point2 End { get; init; }
    public required double Thickness { get; init; }
    public required double Height { get; init; }
    public required string MaterialId { get; init; }
    public required string DetailProfileId { get; init; }
    public required string? LeftRoomId { get; init; }
    public required string? RightRoomId { get; init; }
    public required List<WallOpening> Openings { get; init; }
}

public enum OpeningKind { Door, Window, Passage }
public enum DoorPivot { Start, End }
public enum DoorSwing { Left, Right }

/// <summary>Offset measured from wall Start. Sill/head are relative to the storey elevation.</summary>
public sealed record WallOpening
{
    public required string Id { get; init; }
    public required OpeningKind Kind { get; init; }
    public required double Offset { get; init; }
    public required double Width { get; init; }
    public required double Sill { get; init; }
    public required double Head { get; init; }
    public required DoorPivot? Pivot { get; init; }
    public required DoorSwing? Swing { get; init; }
}

public sealed record SlabOpening
{
    public required string Id { get; init; }
    public required Polygon2 Boundary { get; init; }
}

/// <summary>Straight single flight only. Start/End are lower/upper tread edges, run=(risers-1)*tread.</summary>
public sealed record StairPlan
{
    public required string Id { get; init; }
    public required string LowerStoreyId { get; init; }
    public required string UpperStoreyId { get; init; }
    public required string LowerRoomId { get; init; }
    public required string UpperRoomId { get; init; }
    public required Point2 Start { get; init; }
    public required Point2 End { get; init; }
    public required double Width { get; init; }
    public required int RiserCount { get; init; }
    public required double RiserHeight { get; init; }
    public required double TreadDepth { get; init; }
    public required double Headroom { get; init; }
    /// <summary>Vertical waist-slab thickness below pitch Z=lowerFloor+(run/treadDepth)*riserHeight.</summary>
    public required double StructureThickness { get; init; }
    public required Polygon2 LowerLanding { get; init; }
    public required Polygon2 UpperLanding { get; init; }
    public required string RequiredSlabOpeningId { get; init; }
    public required string MaterialId { get; init; }
}

/// <summary>One planar polygon, upward winding; common XY datum, absolute Z elevations.</summary>
public sealed record RoofPlane
{
    public required string Id { get; init; }
    public required List<Point3> Vertices { get; init; }
    public required string MaterialId { get; init; }
    public required string DetailProfileId { get; init; }
}

public sealed record ValidationIssue(string Code, string Path, string Message);
public sealed record ValidationCounts(int Storeys, int Rooms, int Walls, int Openings, int Stairs, int RoofPlanes, int Vertices, long GeometryChecks);
public sealed record ValidationReport(IReadOnlyList<ValidationIssue> Issues, ValidationCounts Counts)
{
    public bool IsValid => Issues.Count == 0;
}

public interface IBuildingPlanValidator
{
    ValidationReport Validate(BuildingPlan plan, CancellationToken cancellationToken = default);
    void RequireValid(BuildingPlan plan, CancellationToken cancellationToken = default);
}

public sealed class PlanValidationException(ValidationReport report)
    : ArgumentException($"Building plan has {report.Issues.Count} validation issue(s).")
{
    public ValidationReport Report { get; } = report;
}
