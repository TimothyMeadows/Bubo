using Bubo.FloorPlan.Domain;

namespace Bubo.FloorPlan.Tests;

/// <summary>Explicitly authored synthetic v2 QA designs, not upgrades of approved v1 asset records.</summary>
public static class ConstructionPlanExamples
{
    public static BuildingPlan SingleStorey() => Ready(PlanExamples.SingleStorey(), false);
    public static BuildingPlan MultiStorey() => Ready(PlanExamples.MultiStorey(), false);
    public static BuildingPlan AngledCourtyard() => Ready(PlanExamples.AngledCourtyard(), true);
    public static BuildingPlan StackedThreeStorey() => Ready(PlanExamples.StackedThreeStorey(), false);

    private static BuildingPlan Ready(BuildingPlan source, bool courtyard)
    {
        var profile = new ConstructionProfile
        {
            RoofThickness = .18, CeilingThickness = 0,
            DoorLeafThickness = .04, DoorLeafGap = .025, DoorFrameWidth = .06, DoorFrameDepth = .025,
            DoorRevealDepth = .04, DoorOpenAngleDegrees = 90,
            WindowFrameWidth = .06, WindowFrameDepth = .025, GlassThickness = .012,
            RailingHeight = 1, RailThickness = .05, BalusterThickness = .03, BalusterGap = .12, RailingOffset = .02,
            GlassTransmission = .95, GlassIor = 1.45,
            CeilingMaterialId = "plaster", DoorMaterialId = "timber", FrameMaterialId = "frame-paint",
            GlassMaterialId = "clear-glass", RailingMaterialId = "rail-metal",
            MaxMeshes = 10_000, MaxConstructionVertices = 300_000, MaxArrangementCells = 20_000, MaxJobBytes = 16_777_216
        };
        var materials = new List<MaterialSpec>(source.Materials)
        {
            new() { Id = "plaster", Name = "Warm pale ceiling plaster", Color = "#ECE8DE", Roughness = .85, Metallic = 0 },
            new() { Id = "timber", Name = "Solid brown timber doors", Color = "#6D4D30", Roughness = .65, Metallic = 0 },
            new() { Id = "frame-paint", Name = "Pale painted frames", Color = "#DED6C8", Roughness = .55, Metallic = 0 },
            new() { Id = "clear-glass", Name = "Pale blue transmissive glazing", Color = "#C9E1EA", Roughness = .05, Metallic = 0 },
            new() { Id = "rail-metal", Name = "Dark metal rails", Color = "#56595B", Roughness = .4, Metallic = .65 }
        };
        var storeys = source.Storeys.Select(storey => storey with
        {
            Walls = storey.Walls.Select(wall => wall.Id.EndsWith("-wall-0-1", StringComparison.Ordinal) ? wall with
            {
                Openings = [.. wall.Openings, new WallOpening
                {
                    Id = storey.Id + "-window", Kind = OpeningKind.Window, Offset = 2, Width = 1.4,
                    Sill = .9, Head = 2.1, Pivot = null, Swing = null
                }]
            } : wall).ToList(),
            SlabOpenings = storey.SlabOpenings.Select(opening =>
            {
                var stair = source.Stairs.Single(s => s.UpperStoreyId == storey.Id && s.RequiredSlabOpeningId == opening.Id);
                var delta = PlanGeometry.Sub(stair.End, stair.Start);
                var normal = PlanGeometry.Mul(new Point2(-delta.Y, delta.X), .7 / PlanGeometry.Length(delta));
                return opening with { Boundary = new Polygon2 { Outer = [PlanGeometry.Sub(stair.Start, normal), PlanGeometry.Sub(stair.End, normal), PlanGeometry.Add(stair.End, normal), PlanGeometry.Add(stair.Start, normal)], Holes = [] } };
            }).ToList()
        }).ToList();
        return source with
        {
            SchemaVersion = "bubo-building-plan/v2", Name = "Construction-ready " + source.Name,
            Construction = profile, Materials = materials, Storeys = storeys,
            DetailProfiles = source.DetailProfiles.Select(detail => detail with { EaveOverhang = courtyard ? 0 : .2 }).ToList()
        };
    }
}
