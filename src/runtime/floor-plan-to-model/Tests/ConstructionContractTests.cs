using System.Text.Json;
using System.Text.Json.Nodes;
using Bubo.FloorPlan.Domain;
using Bubo.FloorPlan.Infrastructure;

namespace Bubo.FloorPlan.Tests;

public static class ConstructionContractTests
{
    public static void Run()
    {
        Check.Case("construction: v1 remains design-only without serialization identity changes", () =>
        {
            var plan = PlanExamples.SingleStorey();
            new BuildingPlanValidator().RequireValid(plan);
            Check.True(!JsonNode.Parse(JsonSerializer.SerializeToUtf8Bytes(plan, StrictPlanJson.Options))!.AsObject().ContainsKey("construction"));
            var rejected = new ConstructionReadiness().Validate(plan);
            Check.True(!rejected.IsValid && rejected.Issues.Any(i => i.Code == "construction-schema-required"));
        });
        Check.Case("construction: explicit null construction key is still an unknown v1 input field", () =>
        {
            var node = JsonSerializer.SerializeToNode(PlanExamples.SingleStorey(), StrictPlanJson.Options)!.AsObject();
            node["construction"] = null;
            Check.Throws<JsonException>(() => StrictPlanJson.Parse<BuildingPlan>(JsonSerializer.SerializeToUtf8Bytes(node)));
        });
        Check.Case("construction: v2 requires explicit detail source and v1 rejects profile injection", () =>
        {
            var v2 = ConstructionPlanExamples.SingleStorey();
            Check.True(new BuildingPlanValidator().Validate(v2 with { Construction = null }).Issues.Any(i => i.Code == "construction-required"));
            Check.True(new BuildingPlanValidator().Validate(v2 with { SchemaVersion = "bubo-building-plan/v1" }).Issues.Any(i => i.Code == "construction-version"));
        });
        Check.Case("construction: all four explicit v2 source designs validate and strictly roundtrip", () =>
        {
            foreach (var plan in new[] { ConstructionPlanExamples.SingleStorey(), ConstructionPlanExamples.MultiStorey(), ConstructionPlanExamples.AngledCourtyard(), ConstructionPlanExamples.StackedThreeStorey() })
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(plan, StrictPlanJson.Options);
                new ConstructionReadiness().RequireReady(StrictPlanJson.Parse<BuildingPlan>(bytes));
            }
        });
        Check.Case("construction: missing required detail dimension is not defaulted", () =>
        {
            var node = JsonSerializer.SerializeToNode(ConstructionPlanExamples.SingleStorey(), StrictPlanJson.Options)!.AsObject();
            node["construction"]!.AsObject().Remove("roofThickness");
            Check.Throws<JsonException>(() => StrictPlanJson.Parse<BuildingPlan>(JsonSerializer.SerializeToUtf8Bytes(node)));
        });
        Check.Case("construction: nonfinite dimensions and unsupported finite budgets fail", () =>
        {
            var plan = ConstructionPlanExamples.SingleStorey(); var profile = plan.Construction!;
            foreach (var invalid in new[] { profile with { RoofThickness = double.NaN }, profile with { BalusterGap = -1 }, profile with { GlassIor = double.PositiveInfinity }, profile with { MaxMeshes = 10_001 }, profile with { MaxConstructionVertices = 2_000_001 }, profile with { MaxArrangementCells = 0 }, profile with { MaxJobBytes = 0 } })
                Check.True(!new ConstructionReadiness().Validate(plan with { Construction = invalid }).IsValid);
        });
        Check.Case("construction: explicit material roles cannot be missing or turn structure into glass", () =>
        {
            var plan = ConstructionPlanExamples.SingleStorey(); var profile = plan.Construction!;
            Check.True(new ConstructionReadiness().Validate(plan with { Construction = profile with { DoorMaterialId = "missing" } }).Issues.Any(i => i.Code == "construction-material-reference"));
            Check.True(new ConstructionReadiness().Validate(plan with { Construction = profile with { GlassMaterialId = "masonry" } }).Issues.Any(i => i.Code == "construction-material-role"));
        });
        Check.Case("construction: hinge gap, wall reveal and final door angle are explicit and bounded", () =>
        {
            var plan = ConstructionPlanExamples.SingleStorey(); var profile = plan.Construction!;
            Check.True(new ConstructionReadiness().Validate(plan with { Construction = profile with { DoorLeafGap = .001 } }).Issues.Any(i => i.Code == "construction-door-gap"));
            Check.True(new ConstructionReadiness().Validate(plan with { Construction = profile with { DoorRevealDepth = .19 } }).Issues.Any(i => i.Code == "construction-door-depth"));
            Check.True(new ConstructionReadiness().Validate(plan with { Construction = profile with { DoorOpenAngleDegrees = 89 } }).Issues.Any(i => i.Code == "construction-door-angle"));
        });
        Check.Case("construction: open leaf cannot silently remove player-width clearance", () =>
        {
            var plan = ConstructionPlanExamples.SingleStorey(); var storey = plan.Storeys[0];
            var walls = storey.Walls.Select(w => w with { Openings = w.Openings.Select(o => o.Kind == OpeningKind.Door ? o with { Width = .62 } : o).ToList() }).ToList();
            var result = new ConstructionReadiness().Validate(plan with { Storeys = [storey with { Walls = walls }] });
            Check.True(result.Issues.Any(i => i.Code == "construction-door-clearance"));
        });
        Check.Case("construction: stair supports and outside rails require the authored wider slab opening", () =>
        {
            var plan = ConstructionPlanExamples.MultiStorey(); var upper = plan.Storeys[1];
            var narrow = upper.SlabOpenings[0] with { Boundary = PlanExamples.Rect(3, 2.4, 6.5, 3.6) };
            var result = new ConstructionReadiness().Validate(plan with { Storeys = [plan.Storeys[0], upper with { SlabOpenings = [narrow] }] });
            Check.True(result.Issues.Any(i => i.Code == "construction-stair-void"));
        });
        Check.Case("construction: source eave dimensions cannot expand or contradict roof vertices", () =>
        {
            var plan = ConstructionPlanExamples.SingleStorey();
            var result = new ConstructionReadiness().Validate(plan with { DetailProfiles = plan.DetailProfiles.Select(d => d with { EaveOverhang = .3 }).ToList() });
            Check.True(result.Issues.Any(i => i.Code == "construction-eave-dimension"));
        });
        Check.Case("construction: glazing thickness cannot exceed approved wall depth", () =>
        {
            var plan = ConstructionPlanExamples.SingleStorey();
            Check.True(new ConstructionReadiness().Validate(plan with { Construction = plan.Construction! with { GlassThickness = .3 } }).Issues.Any(i => i.Code == "construction-window-depth"));
        });
        Check.Case("construction: admission honors already requested cancellation", () =>
        {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            Check.Throws<OperationCanceledException>(() => new ConstructionReadiness().RequireReady(ConstructionPlanExamples.SingleStorey(), cancellation.Token));
        });
        Check.Case("construction: mandatory slabs and positive source details cannot disappear below minimum feature", () =>
        {
            var plan = ConstructionPlanExamples.SingleStorey();
            var tiny = plan.Numerics.LinearTolerance / 10;
            var slab = plan with { Storeys = [plan.Storeys[0] with { SlabThickness = tiny }] };
            Check.True(new ConstructionReadiness().Validate(slab).Issues.Any(i => i.Code == "construction-dimension" && i.Path.EndsWith(".slabThickness", StringComparison.Ordinal)));
            Check.Throws<PlanValidationException>(() => new ConstructionGeometryCompiler().Compile(slab));
            foreach (var detail in new[] { plan.DetailProfiles[0] with { PlinthHeight = tiny }, plan.DetailProfiles[0] with { PlinthProjection = tiny }, plan.DetailProfiles[0] with { CorniceHeight = tiny }, plan.DetailProfiles[0] with { CorniceProjection = tiny }, plan.DetailProfiles[0] with { FasciaDepth = tiny } })
                Check.True(new ConstructionReadiness().Validate(plan with { DetailProfiles = [detail] }).Issues.Any(i => i.Code == "construction-dimension"));
            var noOptionalBands = plan with { DetailProfiles = plan.DetailProfiles.Select(d => d with { PlinthHeight = 0, PlinthProjection = 0, CorniceHeight = 0, CorniceProjection = 0, FasciaDepth = 0 }).ToList() };
            new ConstructionReadiness().RequireReady(noOptionalBands);
            Check.True(new ConstructionGeometryCompiler().Compile(noOptionalBands).Meshes.All(m => m.Kind is not ("plinth" or "cornice" or "roof-fascia")));
        });
    }
}
