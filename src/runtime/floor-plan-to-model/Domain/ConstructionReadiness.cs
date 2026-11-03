namespace Bubo.FloorPlan.Domain;

/// <summary>Construction admission and explicit detail consistency. Actual mesh routes/sweeps are compiler evidence, never inferred here.</summary>
public sealed class ConstructionReadiness
{
    public ValidationReport Validate(BuildingPlan plan, CancellationToken cancellationToken = default)
    {
        var report = new BuildingPlanValidator().Validate(plan, cancellationToken);
        if (!report.IsValid || plan.SchemaVersion == "bubo-building-plan/v2") return report;
        return new ValidationReport([new("construction-schema-required", "schemaVersion", "Construction requires an explicitly authored v2 plan/profile and a fresh exact whole-package approval; v1 remains design-only.")], report.Counts);
    }

    public void RequireReady(BuildingPlan plan, CancellationToken cancellationToken = default)
    {
        var report = Validate(plan, cancellationToken);
        if (!report.IsValid) throw new PlanValidationException(report);
    }

    internal static IReadOnlyList<ValidationIssue> ValidateProfile(BuildingPlan plan, CancellationToken token, Action debit)
    {
        var issues = new List<ValidationIssue>();
        if (plan.SchemaVersion != "bubo-building-plan/v2" || plan.Construction is not { } p) return issues;
        var tolerance = plan.Numerics.LinearTolerance;
        var feature = plan.Numerics.MinimumFeatureSize;
        void Error(string code, string name, string message) { token.ThrowIfCancellationRequested(); if (issues.Count < 128) issues.Add(new(code, name.StartsWith("$.", StringComparison.Ordinal) ? name[2..] : "construction." + name, message)); }
        void Dimension(double value, string name, bool zero = false)
        {
            if (!double.IsFinite(value) || value < (zero ? 0 : feature) || value > plan.Numerics.MaxCoordinateMagnitude || (zero && value > 0 && value < feature))
                Error("construction-dimension", name, "Explicit finite dimension must meet the source minimum feature and coordinate limits; only declared optional dimensions may be zero.");
        }
        foreach (var (value, name) in new[]
        {
            (p.RoofThickness, "roofThickness"), (p.DoorLeafThickness, "doorLeafThickness"), (p.DoorLeafGap, "doorLeafGap"),
            (p.DoorFrameWidth, "doorFrameWidth"), (p.DoorFrameDepth, "doorFrameDepth"), (p.WindowFrameWidth, "windowFrameWidth"),
            (p.WindowFrameDepth, "windowFrameDepth"), (p.GlassThickness, "glassThickness"), (p.RailingHeight, "railingHeight"),
            (p.RailThickness, "railThickness"), (p.BalusterThickness, "balusterThickness"), (p.BalusterGap, "balusterGap")
        }) Dimension(value, name);
        Dimension(p.CeilingThickness, "ceilingThickness", true); Dimension(p.DoorRevealDepth, "doorRevealDepth", true); Dimension(p.RailingOffset, "railingOffset", true);
        foreach (var detail in plan.DetailProfiles)
        {
            token.ThrowIfCancellationRequested();
            Dimension(detail.PlinthHeight, "$.detailProfiles." + detail.Id + ".plinthHeight", true);
            Dimension(detail.PlinthProjection, "$.detailProfiles." + detail.Id + ".plinthProjection", true);
            Dimension(detail.CorniceHeight, "$.detailProfiles." + detail.Id + ".corniceHeight", true);
            Dimension(detail.CorniceProjection, "$.detailProfiles." + detail.Id + ".corniceProjection", true);
            Dimension(detail.EaveOverhang, "$.detailProfiles." + detail.Id + ".eaveOverhang", true);
            Dimension(detail.FasciaDepth, "$.detailProfiles." + detail.Id + ".fasciaDepth", true);
        }
        foreach (var storey in plan.Storeys)
            Dimension(storey.SlabThickness, "$.storeys." + storey.Id + ".slabThickness");
        foreach (var stair in plan.Stairs)
        {
            token.ThrowIfCancellationRequested();
            Dimension(stair.StructureThickness, "$.stairs." + stair.Id + ".structureThickness");
            Dimension(stair.Width, "$.stairs." + stair.Id + ".width");
            Dimension(stair.RiserHeight, "$.stairs." + stair.Id + ".riserHeight");
            Dimension(stair.TreadDepth, "$.stairs." + stair.Id + ".treadDepth");
        }
        if (!double.IsFinite(p.DoorOpenAngleDegrees) || p.DoorOpenAngleDegrees < 90 || p.DoorOpenAngleDegrees > 180)
            Error("construction-door-angle", "doorOpenAngleDegrees", "Explicit final open angle must be in [90,180] degrees; the whole finite sweep still requires geometric verification.");
        if (!double.IsFinite(p.GlassTransmission) || p.GlassTransmission < 0 || p.GlassTransmission > 1)
            Error("construction-material", "glassTransmission", "Transmission must be explicitly finite in [0,1].");
        if (!double.IsFinite(p.GlassIor) || p.GlassIor < 1 || p.GlassIor > 3)
            Error("construction-material", "glassIor", "Glass IOR must be explicitly finite in [1,3].");
        void Budget(long value, long maximum, string name) { if (value <= 0 || value > maximum) Error("construction-budget", name, $"Finite positive construction budget must not exceed {maximum}."); }
        Budget(p.MaxMeshes, 10_000, "maxMeshes"); Budget(p.MaxConstructionVertices, 2_000_000, "maxConstructionVertices");
        Budget(p.MaxArrangementCells, 100_000, "maxArrangementCells"); Budget(p.MaxJobBytes, Math.Min(268_435_456, plan.Budgets.MaxExportBytes), "maxJobBytes");
        if (p.MaxJobBytes < 1024) Error("construction-budget", "maxJobBytes", "A construction job byte budget must be at least 1024 bytes.");
        var materials = plan.Materials.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var (id, name) in new[] { (p.CeilingMaterialId,"ceilingMaterialId"), (p.DoorMaterialId,"doorMaterialId"), (p.FrameMaterialId,"frameMaterialId"), (p.GlassMaterialId,"glassMaterialId"), (p.RailingMaterialId,"railingMaterialId") })
            if (id is null || !materials.Contains(id)) Error("construction-material-reference", name, "Role must explicitly reference a declared source material.");
        if (p.GlassTransmission > 0)
        {
            var opaque = new[] {p.CeilingMaterialId, p.DoorMaterialId, p.FrameMaterialId, p.RailingMaterialId}
                .Concat(plan.Storeys.Select(s => s.FloorMaterialId)).Concat(plan.Storeys.SelectMany(s => s.Walls).Select(w => w.MaterialId))
                .Concat(plan.Stairs.Select(s => s.MaterialId)).Concat(plan.Roofs.Select(r => r.MaterialId));
            if (opaque.Contains(p.GlassMaterialId, StringComparer.Ordinal)) Error("construction-material-role", "glassMaterialId", "Transmissive glass must not silently turn a structural, ceiling, door, frame or railing material into glass.");
        }
        if (p.DoorLeafGap < p.DoorLeafThickness / 2 + tolerance)
            Error("construction-door-gap", "doorLeafGap", "Leaf gap must be at least half its thickness plus linear tolerance so hinge rotation cannot enter the adjacent jamb.");
        if (p.BalusterThickness > p.RailThickness + tolerance) Error("construction-railing", "balusterThickness", "Balusters must fit the explicitly sized rail section.");
        if (plan.Stairs.Count > 0 && p.BalusterGap + tolerance >= 2 * plan.Player.Radius)
            Error("construction-railing", "balusterGap", "Gap must be smaller than the declared player diameter; this is geometry policy, not safety-code certification.");
        foreach (var storey in plan.Storeys)
        {
            token.ThrowIfCancellationRequested();
            if (p.RailingHeight + p.RailThickness > storey.Height + tolerance && plan.Stairs.Any(s => s.LowerStoreyId == storey.Id || s.UpperStoreyId == storey.Id))
                Error("construction-railing", "railingHeight", "Railing and top rail cannot exceed a connected storey's declared clear height.");
            foreach (var wall in storey.Walls)
            foreach (var opening in wall.Openings)
            {
                token.ThrowIfCancellationRequested();
                if (opening.Kind == OpeningKind.Door)
                {
                    if (p.DoorRevealDepth + p.DoorLeafThickness > wall.Thickness + tolerance)
                        Error("construction-door-depth", "doorRevealDepth", "Leaf and reveal must fit within the approved wall thickness; negative-face inset is explicit.");
                    if (opening.Width - 2 * p.DoorLeafGap < feature || opening.Head - opening.Sill - 2 * p.DoorLeafGap < feature)
                        Error("construction-door-leaf", "doorLeafGap", "The explicit gaps must leave a positive minimum-feature leaf width and height.");
                    if (opening.Width - p.DoorLeafGap - p.DoorLeafThickness / 2 + tolerance < 2 * plan.Player.Radius)
                        Error("construction-door-clearance", "doorLeafThickness", "The open leaf would reduce the declared aperture below player diameter; actual sweep and route checks remain required.");
                }
                if (opening.Kind == OpeningKind.Window && p.GlassThickness > wall.Thickness + tolerance)
                    Error("construction-window-depth", "glassThickness", "Glazing must fit within the approved wall thickness.");
            }
        }
        if (issues.Count == 0)
        {
            var geometry = new PlanGeometry(tolerance, debit);
            foreach (var stair in plan.Stairs)
            {
                token.ThrowIfCancellationRequested();
                var physical = geometry.Rectangle(stair.Start, stair.End, PhysicalStairWidth(stair, p));
                var lower = plan.Storeys.Single(s => s.Id == stair.LowerStoreyId);
                var upper = plan.Storeys.Single(s => s.Id == stair.UpperStoreyId);
                var opening = upper.SlabOpenings.Single(o => o.Id == stair.RequiredSlabOpeningId);
                if (!geometry.Contains(opening.Boundary, physical)) Error("construction-stair-void", "$.storeys." + upper.Id + ".slabOpenings." + opening.Id, "Approved upper slab void must contain the complete physical stair/support/railing envelope; construction never widens it automatically.");
                if (!geometry.Contains(lower.Rooms.Single(r => r.Id == stair.LowerRoomId).Boundary, physical)
                    || !geometry.Contains(upper.Rooms.Single(r => r.Id == stair.UpperRoomId).Boundary, physical))
                    Error("construction-stair-envelope", "$.stairs." + stair.Id, "Complete physical stair/support width must remain inside both connected clear rooms.");
            }
            ValidateEaves(plan, geometry, debit, Error, token);
        }
        return issues;
    }
    public static double PhysicalStairWidth(StairPlan stair, ConstructionProfile profile)
        => stair.Width + 2 * (profile.RailingOffset + Math.Max(profile.RailThickness, profile.BalusterThickness));

    // Roof edges are authored source geometry. Verify the piecewise-linear outward
    // envelope at every projected roof-vertex breakpoint; never expand the roof.
    private static void ValidateEaves(BuildingPlan plan, PlanGeometry geometry, Action debit, Action<string, string, string> error, CancellationToken token)
    {
        var tolerance = plan.Numerics.LinearTolerance;
        var roofs = plan.Roofs.Select(r => (Roof: r, Polygon: new Polygon2 { Outer = r.Vertices.Select(p => new Point2(p.X, p.Y)).ToList(), Holes = [] })).ToList();
        var details = plan.DetailProfiles.ToDictionary(d => d.Id, StringComparer.Ordinal);
        foreach (var storey in plan.Storeys)
        foreach (var wall in storey.Walls)
        {
            token.ThrowIfCancellationRequested();
            if ((wall.LeftRoomId is null) == (wall.RightRoomId is null)) continue;
            var outer = new Polygon2 { Outer = storey.Footprint.Outer, Holes = [] };
            if (!geometry.BoundaryContainsSegment(outer, wall.Start, wall.End)) continue; // Courtyard faces are not outward eaves.
            var direction = PlanGeometry.Sub(wall.End, wall.Start);
            var length = PlanGeometry.Length(direction);
            var along = PlanGeometry.Mul(direction, 1 / length);
            var normal = new Point2(-along.Y, along.X);
            if (wall.LeftRoomId is not null) normal = PlanGeometry.Mul(normal, -1);
            var breaks = new List<double> { 0, length };
            foreach (var roof in roofs)
            foreach (var vertex in roof.Polygon.Outer)
            {
                debit();
                var u = PlanGeometry.Dot(PlanGeometry.Sub(vertex, wall.Start), along);
                if (u > 0 && u < length) breaks.Add(u);
            }
            breaks.Sort();
            var samples = new List<double>(breaks);
            for (var i = 1; i < breaks.Count; i++) if (breaks[i] - breaks[i - 1] > tolerance) samples.Add((breaks[i] + breaks[i - 1]) / 2);
            foreach (var u in samples)
            {
                var origin = PlanGeometry.Add(wall.Start, PlanGeometry.Mul(along, u));
                var far = plan.Numerics.MaxCoordinateMagnitude * 4;
                var distances = new List<double> { 0, far };
                foreach (var roof in roofs)
                for (var i = 0; i < roof.Polygon.Outer.Count; i++)
                {
                    debit();
                    var a = roof.Polygon.Outer[i];
                    var edge = PlanGeometry.Sub(roof.Polygon.Outer[(i + 1) % roof.Polygon.Outer.Count], a);
                    var divisor = PlanGeometry.Cross(normal, edge);
                    if (Math.Abs(divisor) <= tolerance) continue;
                    var delta = PlanGeometry.Sub(a, origin);
                    var t = PlanGeometry.Cross(delta, edge) / divisor;
                    var v = PlanGeometry.Cross(delta, normal) / divisor;
                    if (t >= -tolerance && t <= far && v >= -tolerance && v <= 1 + tolerance) distances.Add(Math.Clamp(t, 0, far));
                }
                distances.Sort();
                double end = 0;
                var witness = origin;
                for (var i = 1; i < distances.Count; i++)
                {
                    if (distances[i] - distances[i - 1] <= tolerance) continue;
                    var midpoint = PlanGeometry.Add(origin, PlanGeometry.Mul(normal, (distances[i] + distances[i - 1]) / 2));
                    if (!roofs.Any(r => geometry.InPolygon(midpoint, r.Polygon) >= 0)) break;
                    end = distances[i]; witness = midpoint;
                }
                var contributors = roofs.Where(r => geometry.InPolygon(witness, r.Polygon) >= 0).ToList();
                if (contributors.Count == 0) { error("construction-eave-source", "$.roofs", "Cannot bind an exterior roof edge to its authored detail profile."); continue; }
                var measured = Math.Max(0, end - wall.Thickness / 2);
                if (contributors.Any(r => Math.Abs(details[r.Roof.DetailProfileId].EaveOverhang - measured) > tolerance))
                    error("construction-eave-dimension", "$.detailProfiles.eaveOverhang", "Roof source edge overhang from the physical outer wall face disagrees with its detail profile; author a coherent plan instead of expanding source vertices during construction.");
            }
        }
    }
}
