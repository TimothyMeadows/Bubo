using System.Text.RegularExpressions;
using static Bubo.FloorPlan.Domain.PlanGeometry;

namespace Bubo.FloorPlan.Domain;

/// <summary>Pure, bounded architectural consistency checks, not jurisdictional building-code approval.</summary>
public sealed class BuildingPlanValidator : IBuildingPlanValidator
{
    public ValidationReport Validate(BuildingPlan plan, CancellationToken cancellationToken = default)
    {
        var validation = new Validation(plan, cancellationToken);
        return validation.Run();
    }

    public void RequireValid(BuildingPlan plan, CancellationToken cancellationToken = default)
    {
        var report = Validate(plan, cancellationToken);
        if (!report.IsValid) throw new PlanValidationException(report);
    }

    private sealed class WorkLimitException : Exception;

    private sealed class Validation(BuildingPlan plan, CancellationToken token)
    {
        private readonly List<ValidationIssue> issues = [];
        private readonly HashSet<string> identities = new(StringComparer.Ordinal);
        private readonly List<(Polygon2 Polygon, string Path)> polygons = [];
        private readonly Dictionary<string, (StoreyPlan Storey, RoomPlan Room)> rooms = new(StringComparer.Ordinal);
        private readonly Dictionary<string, StoreyPlan> storeys = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> connectivity = new(StringComparer.Ordinal);
        private int storeyCount, roomCount, wallCount, openingCount, stairCount, roofCount, vertexCount;
        private long checks;
        private PlanGeometry geometry = null!;
        private double E => plan.Numerics.LinearTolerance;
        private double Feature => plan.Numerics.MinimumFeatureSize;

        private ValidationReport Report() => new(issues.AsReadOnly(), new(storeyCount, roomCount, wallCount, openingCount, stairCount, roofCount, vertexCount, checks));
        private void Error(string code, string path, string message)
        {
            token.ThrowIfCancellationRequested();
            if (issues.Count >= 255) throw new WorkLimitException();
            issues.Add(new(code, path, message));
        }
        private void Debit()
        {
            token.ThrowIfCancellationRequested();
            if (++checks > plan.Budgets.MaxGeometryChecks) throw new WorkLimitException();
        }
        private void Text(string? value, string path, int max = 256)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Any(char.IsControl)) Error("invalid-text", path, $"Required nonblank text, at most {max} characters and no controls.");
        }
        private void Id(string? id, string path)
        {
            if (id is null || !Regex.IsMatch(id, "\\A[A-Za-z][A-Za-z0-9_.-]{0,95}\\z", RegexOptions.CultureInvariant)) Error("invalid-id", path, "Identity must start with an ASCII letter and contain at most 96 ASCII letters, digits, underscores, dots or dashes.");
            else if (!identities.Add(id)) Error("duplicate-id", path, "Every identity is globally unique within the building.");
        }
        private bool Finite(double value, string path)
        {
            if (double.IsFinite(value)) return true;
            Error("nonfinite", path, "All numeric values must be finite.");
            return false;
        }
        private void Positive(double value, string path, bool zero = false)
        {
            if (Finite(value, path) && ((zero ? value < 0 : value <= 0) || value > plan.Numerics.MaxCoordinateMagnitude)) Error("invalid-dimension", path, "Dimension must have the declared sign and not exceed maximum coordinate magnitude.");
        }
        private bool Collection<T>(List<T>? list, string path, int hardLimit)
        {
            if (list is null) { Error("missing-collection", path, "Collection must be explicitly supplied."); return false; }
            if (list.Count > hardLimit) { Error("hard-limit", path, $"Input collection exceeds runtime hard cap {hardLimit}."); return false; }
            if (list.Any(x => x is null)) { Error("missing-object", path, "Collection contains null."); return false; }
            return true;
        }
        private void Point(Point2 p, string path)
        {
            vertexCount++;
            if (vertexCount > plan.Budgets.MaxVertices) throw new WorkLimitException();
            if (Finite(p.X, path + ".x") && Math.Abs(p.X) > plan.Numerics.MaxCoordinateMagnitude) Error("coordinate-range", path, "Coordinate exceeds declared magnitude.");
            if (Finite(p.Y, path + ".y") && Math.Abs(p.Y) > plan.Numerics.MaxCoordinateMagnitude) Error("coordinate-range", path, "Coordinate exceeds declared magnitude.");
        }
        private void Polygon(Polygon2? polygon, string path)
        {
            if (polygon is null) { Error("missing-polygon", path, "Polygon is required."); return; }
            if (!Collection(polygon.Outer, path + ".outer", 100_000) || !Collection(polygon.Holes, path + ".holes", 4096)) return;
            void Ring(List<Point2> ring, string location)
            {
                if (ring.Count < 3) Error("invalid-ring", location, "A closed polygon needs at least three distinct vertices with implicit closing edge.");
                foreach (var point in ring) Point(point, location);
            }
            Ring(polygon.Outer, path + ".outer");
            for (var i = 0; i < polygon.Holes.Count; i++)
                if (Collection(polygon.Holes[i], path + $".holes[{i}]", 100_000)) Ring(polygon.Holes[i], path + $".holes[{i}]");
            polygons.Add((polygon, path));
        }
        private void Reference(string? id, ISet<string> available, string path)
        {
            if (id is null || !available.Contains(id)) Error("unknown-reference", path, "Reference must identify a declared object of the required kind.");
        }

        public ValidationReport Run()
        {
            token.ThrowIfCancellationRequested();
            if (plan is null) { Error("missing-plan", "$", "Building plan is required."); return Report(); }
            try
            {
                Basics();
                if (issues.Count != 0) return Report();
                Structure();
                if (issues.Count != 0) return Report();
                geometry = new PlanGeometry(E, Debit);
                foreach (var (polygon, path) in polygons) ValidatePolygon(polygon, path);
                if (issues.Count != 0) return Report();
                References();
                if (issues.Count != 0) return Report();
                ArchitecturalGeometry();
                if (issues.Count == 0)
                    foreach (var issue in ConstructionReadiness.ValidateProfile(plan, token, Debit)) Error(issue.Code, issue.Path, issue.Message);
            }
            catch (Exception error) when (error is WorkLimitException or GeometryCapacityException)
            {
                if (issues.Count < 256) issues.Add(new("validation-budget-exhausted", "$", "Finite geometry/issue budget exhausted; plan has not passed validation."));
            }
            return Report();
        }

        private void Basics()
        {
            if (plan.SchemaVersion is not ("bubo-building-plan/v1" or "bubo-building-plan/v2")) Error("schema-version", "schemaVersion", "Expected bubo-building-plan/v1 or bubo-building-plan/v2.");
            if (plan.SchemaVersion == "bubo-building-plan/v1" && plan.Construction is not null) Error("construction-version", "construction", "v1 is immutable design-only; author v2 explicitly instead of adding construction data to a v1 record.");
            if (plan.SchemaVersion == "bubo-building-plan/v2" && plan.Construction is null) Error("construction-required", "construction", "v2 requires an explicit construction profile included in complete design approval.");
            if (plan.CoordinateSystem != "xy-metres-z-up") Error("coordinate-system", "coordinateSystem", "Only common XY metres, absolute Z-up elevations are supported.");
            Id(plan.Id, "id"); Text(plan.Name, "name"); Text(plan.Purpose, "purpose", 2048); Text(plan.ArchitecturalStyle, "architecturalStyle", 2048);
            if (plan.StyleSha256 is null || !Regex.IsMatch(plan.StyleSha256, "\\A[a-f0-9]{64}\\z", RegexOptions.CultureInvariant)) Error("invalid-style-hash", "styleSha256", "Exact lower-case SHA-256 style identity is required.");
            if (plan.Numerics is null || plan.Player is null || plan.Budgets is null) { Error("missing-policy", "$", "Numerics, player envelope and budgets are required."); return; }
            var n = plan.Numerics;
            if (!double.IsFinite(n.LinearTolerance) || n.LinearTolerance < 1e-9 || n.LinearTolerance > .01) Error("numerical-policy", "numerics.linearTolerance", "Tolerance must be finite in [1e-9, 0.01] metres.");
            if (!double.IsFinite(n.AngularToleranceDegrees) || n.AngularToleranceDegrees < .001 || n.AngularToleranceDegrees > 1) Error("numerical-policy", "numerics.angularToleranceDegrees", "Angular tolerance must be finite in [0.001, 1] degrees.");
            if (!double.IsFinite(n.MaxCoordinateMagnitude) || n.MaxCoordinateMagnitude <= 0 || n.MaxCoordinateMagnitude > 1_000_000) Error("numerical-policy", "numerics.maxCoordinateMagnitude", "Coordinate magnitude must be finite in (0, 1000000].");
            if (!double.IsFinite(n.MinimumFeatureSize) || n.MinimumFeatureSize < n.LinearTolerance * 10 || n.MinimumFeatureSize > 10) Error("numerical-policy", "numerics.minimumFeatureSize", "Minimum feature must be finite, at least ten linear tolerances and at most 10 metres.");
            Positive(plan.Player.Radius, "player.radius"); Positive(plan.Player.Height, "player.height"); Positive(plan.Player.MaxStepHeight, "player.maxStepHeight"); Positive(plan.Player.MinLandingDepth, "player.minLandingDepth");
            if (plan.Player.Radius < n.MinimumFeatureSize || plan.Player.Height < n.MinimumFeatureSize || plan.Player.MinLandingDepth < n.MinimumFeatureSize) Error("player-feature-size", "player", "Player radius/height and landing depth must meet declared minimum feature size.");
            var b = plan.Budgets;
            void Bound(long value, long maximum, string name) { if (value <= 0 || value > maximum) Error("invalid-budget", "budgets." + name, $"Budget must be in [1, {maximum}]."); }
            Bound(b.MaxStoreys, 64, "maxStoreys"); Bound(b.MaxRooms, 4096, "maxRooms"); Bound(b.MaxWalls, 16384, "maxWalls"); Bound(b.MaxOpenings, 16384, "maxOpenings");
            Bound(b.MaxStairs, 1024, "maxStairs"); Bound(b.MaxRoofPlanes, 4096, "maxRoofPlanes"); Bound(b.MaxVertices, 100_000, "maxVertices"); Bound(b.MaxGeometryChecks, 5_000_000, "maxGeometryChecks");
            Bound(b.MaxPreviewBytes, 67_108_864, "maxPreviewBytes"); Bound(b.MaxExportBytes, 1_073_741_824, "maxExportBytes"); Bound(b.MaxGeneratedTriangles, plan.SchemaVersion == "bubo-building-plan/v2" ? 4_000_000 : 10_000_000, "maxGeneratedTriangles");
        }

        private void Structure()
        {
            if (!Collection(plan.Materials, "materials", 4096) || !Collection(plan.DetailProfiles, "detailProfiles", 4096)
                || !Collection(plan.Storeys, "storeys", 64) || !Collection(plan.Stairs, "stairs", 1024) || !Collection(plan.Roofs, "roofs", 4096)) return;
            storeyCount = plan.Storeys.Count; stairCount = plan.Stairs.Count; roofCount = plan.Roofs.Count;
            if (storeyCount == 0 || roofCount == 0 || plan.Materials.Count == 0 || plan.DetailProfiles.Count == 0) Error("incomplete-building", "$", "At least one storey, roof plane, material and detail profile are required.");
            foreach (var material in plan.Materials)
            {
                Id(material.Id, "materials.id"); Text(material.Name, "materials.name");
                if (material.Color is null || !Regex.IsMatch(material.Color, "\\A#[a-fA-F0-9]{6}\\z", RegexOptions.CultureInvariant)) Error("invalid-color", "materials.color", "Use an explicit #RRGGBB material color.");
                foreach (var (value, name) in new[] { (material.Roughness, "roughness"), (material.Metallic, "metallic") })
                    if (!double.IsFinite(value) || value < 0 || value > 1) Error("invalid-material", "materials." + name, "Material scalar must be finite in [0,1].");
            }
            foreach (var detail in plan.DetailProfiles)
            {
                Id(detail.Id, "detailProfiles.id"); Text(detail.Name, "detailProfiles.name");
                foreach (var value in new[] { detail.PlinthHeight, detail.PlinthProjection, detail.CorniceHeight, detail.CorniceProjection, detail.EaveOverhang, detail.FasciaDepth }) Positive(value, "detailProfiles.dimensions", true);
            }
            foreach (var s in plan.Storeys)
            {
                token.ThrowIfCancellationRequested();
                Id(s.Id, "storeys.id"); Text(s.Name, "storeys.name"); Finite(s.Elevation, "storeys.elevation");
                if (Math.Abs(s.Elevation) > plan.Numerics.MaxCoordinateMagnitude) Error("coordinate-range", "storeys.elevation", "Elevation exceeds declared coordinate magnitude.");
                Positive(s.Height, "storeys.height"); Positive(s.SlabThickness, "storeys.slabThickness");
                if (s.SlabThickness >= s.Height || s.Height + E < plan.Player.Height) Error("vertical-clearance", s.Id, "Storey must exceed its slab thickness and contain the player envelope.");
                Polygon(s.Footprint, s.Id + ".footprint");
                if (!Collection(s.Rooms, s.Id + ".rooms", 4096) || !Collection(s.Walls, s.Id + ".walls", 16384) || !Collection(s.SlabOpenings, s.Id + ".slabOpenings", 4096)) continue;
                roomCount += s.Rooms.Count; wallCount += s.Walls.Count;
                if (roomCount > plan.Budgets.MaxRooms || wallCount > plan.Budgets.MaxWalls) { Error("input-budget-exceeded", s.Id, "Aggregate room/wall count exceeds declared budget."); throw new WorkLimitException(); }
                if (s.Rooms.Count == 0 || s.Walls.Count == 0) Error("incomplete-storey", s.Id, "A storey requires explicit empty rooms and walls.");
                foreach (var r in s.Rooms) { Id(r.Id, s.Id + ".rooms.id"); Text(r.Name, r.Id + ".name"); Polygon(r.Boundary, r.Id + ".boundary"); }
                foreach (var w in s.Walls)
                {
                    Id(w.Id, s.Id + ".walls.id"); Point(w.Start, w.Id + ".start"); Point(w.End, w.Id + ".end");
                    Positive(w.Thickness, w.Id + ".thickness"); Positive(w.Height, w.Id + ".height");
                    if (Distance(w.Start, w.End) < Feature || w.Thickness < Feature || w.Height < Feature || w.Height > s.Height + E) Error("wall-dimensions", w.Id, "Wall length/thickness/height must meet minimum feature; height cannot exceed storey height.");
                    if (!Collection(w.Openings, w.Id + ".openings", 16384)) continue;
                    openingCount += w.Openings.Count;
                    if (openingCount > plan.Budgets.MaxOpenings) { Error("input-budget-exceeded", w.Id, "Aggregate opening count exceeds declared budget."); throw new WorkLimitException(); }
                    foreach (var o in w.Openings)
                    {
                        Id(o.Id, w.Id + ".openings.id");
                        if (!Enum.IsDefined(o.Kind) || (o.Pivot.HasValue && !Enum.IsDefined(o.Pivot.Value)) || (o.Swing.HasValue && !Enum.IsDefined(o.Swing.Value))) Error("unknown-opening-kind", o.Id, "Opening/pivot/swing enumeration is unknown.");
                        Positive(o.Offset, o.Id + ".offset", true); Positive(o.Width, o.Id + ".width"); Positive(o.Sill, o.Id + ".sill", true); Positive(o.Head, o.Id + ".head");
                        if (o.Width < Feature || o.Head - o.Sill < Feature || o.Offset + o.Width > Distance(w.Start, w.End) + E || o.Head > w.Height + E) Error("opening-dimensions", o.Id, "Opening must fit the wall and retain positive width and height.");
                        if ((o.Kind == OpeningKind.Door) != (o.Pivot.HasValue && o.Swing.HasValue) || (o.Kind != OpeningKind.Door && (o.Pivot.HasValue || o.Swing.HasValue))) Error("door-pivot", o.Id, "Doors require an explicit pivot and swing; other openings have neither.");
                        if (o.Kind is OpeningKind.Door or OpeningKind.Passage && (o.Width + E < plan.Player.Radius * 2 || o.Head - o.Sill + E < plan.Player.Height || o.Sill > plan.Player.MaxStepHeight + E)) Error("opening-clearance", o.Id, "Door/passage must admit the declared player envelope and step height.");
                    }
                }
                foreach (var opening in s.SlabOpenings) { Id(opening.Id, s.Id + ".slabOpenings.id"); Polygon(opening.Boundary, opening.Id + ".boundary"); }
            }
            foreach (var stair in plan.Stairs)
            {
                Id(stair.Id, "stairs.id"); Point(stair.Start, stair.Id + ".start"); Point(stair.End, stair.Id + ".end");
                Positive(stair.Width, stair.Id + ".width"); Positive(stair.RiserHeight, stair.Id + ".riserHeight"); Positive(stair.TreadDepth, stair.Id + ".treadDepth"); Positive(stair.Headroom, stair.Id + ".headroom");
                Positive(stair.StructureThickness, stair.Id + ".structureThickness");
                if (stair.StructureThickness < Feature || stair.StructureThickness > 1) Error("stair-structure-thickness", stair.Id, "Vertical waist-slab thickness must meet minimum feature size and cannot exceed 1 metre.");
                if (stair.RiserCount < 2 || stair.RiserCount > 4096 || stair.Width + E < 2 * plan.Player.Radius || stair.RiserHeight > plan.Player.MaxStepHeight + E || stair.TreadDepth < Feature || stair.Headroom + E < plan.Player.Height || Distance(stair.Start, stair.End) < Feature) Error("stair-dimensions", stair.Id, "Straight stairs need 2..4096 risers, positive run/treads and declared player width, step and headroom clearance.");
                Polygon(stair.LowerLanding, stair.Id + ".lowerLanding"); Polygon(stair.UpperLanding, stair.Id + ".upperLanding");
            }
            foreach (var roof in plan.Roofs)
            {
                Id(roof.Id, "roofs.id");
                if (!Collection(roof.Vertices, roof.Id + ".vertices", 100_000)) continue;
                if (roof.Vertices.Count < 3) Error("invalid-roof", roof.Id, "Roof plane requires at least three vertices.");
                foreach (var p in roof.Vertices)
                {
                    Point(new(p.X, p.Y), roof.Id + ".vertices");
                    if (Finite(p.Z, roof.Id + ".vertices.z") && Math.Abs(p.Z) > plan.Numerics.MaxCoordinateMagnitude) Error("coordinate-range", roof.Id, "Roof elevation exceeds declared magnitude.");
                }
            }
            var b = plan.Budgets;
            if (storeyCount > b.MaxStoreys || roomCount > b.MaxRooms || wallCount > b.MaxWalls || openingCount > b.MaxOpenings || stairCount > b.MaxStairs || roofCount > b.MaxRoofPlanes || vertexCount > b.MaxVertices) Error("input-budget-exceeded", "budgets", "Plan exceeds at least one declared entity/vertex budget.");
        }

        private void ValidatePolygon(Polygon2 polygon, string path)
        {
            void Ring(IReadOnlyList<Point2> ring, bool hole, string location)
            {
                var area = Area(ring);
                if ((hole ? area >= -Feature * Feature : area <= Feature * Feature)) Error("ring-winding-area", location, "Outer must be counterclockwise, holes clockwise, with area above minimum-feature squared.");
                for (var i = 0; i < ring.Count; i++)
                {
                    var next = (i + 1) % ring.Count;
                    if (Distance(ring[i], ring[next]) < Feature) Error("short-edge", location, "Repeated vertices and edges below declared minimum feature are invalid.");
                    for (var j = i + 1; j < ring.Count; j++)
                    {
                        var jnext = (j + 1) % ring.Count;
                        if (j == next || jnext == i) continue;
                        if (geometry.Intersects(ring[i], ring[next], ring[j], ring[jnext])) Error("self-intersection", location, "Ring edges cross, touch or overlap outside their adjacent vertices.");
                    }
                    var previous = ring[(i + ring.Count - 1) % ring.Count];
                    if (geometry.CollinearOverlap(previous, ring[i], ring[i], ring[next])) Error("self-intersection", location, "Adjacent edges must not backtrack over one another.");
                }
            }
            Ring(polygon.Outer, false, path + ".outer");
            for (var i = 0; i < polygon.Holes.Count; i++)
            {
                var hole = polygon.Holes[i];
                Ring(hole, true, path + $".holes[{i}]");
                if (hole.Any(p => geometry.InRing(p, polygon.Outer) != 1)) Error("hole-outside", path, "Holes must lie strictly inside their outer ring.");
                foreach (var outer in new[] { polygon.Outer }.Concat(polygon.Holes.Take(i)))
                {
                    for (var a = 0; a < hole.Count; a++)
                    for (var b = 0; b < outer.Count; b++)
                        if (geometry.Intersects(hole[a], hole[(a + 1) % hole.Count], outer[b], outer[(b + 1) % outer.Count])) Error("hole-intersection", path, "Holes must not touch or cross outer/other boundaries.");
                }
                for (var j = 0; j < i; j++)
                    if (geometry.InRing(hole[0], polygon.Holes[j]) >= 0 || geometry.InRing(polygon.Holes[j][0], hole) >= 0) Error("nested-holes", path, "Holes cannot overlap or nest.");
            }
        }

        private void References()
        {
            var materials = plan.Materials.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
            var details = plan.DetailProfiles.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var s in plan.Storeys)
            {
                storeys.Add(s.Id, s);
                Reference(s.FloorMaterialId, materials, s.Id + ".floorMaterialId");
                var localRooms = s.Rooms.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var r in s.Rooms) { rooms.Add(r.Id, (s, r)); connectivity.Add(r.Id, new(StringComparer.Ordinal)); }
                foreach (var w in s.Walls)
                {
                    Reference(w.MaterialId, materials, w.Id + ".materialId"); Reference(w.DetailProfileId, details, w.Id + ".detailProfileId");
                    if (w.LeftRoomId is not null) Reference(w.LeftRoomId, localRooms, w.Id + ".leftRoomId");
                    if (w.RightRoomId is not null) Reference(w.RightRoomId, localRooms, w.Id + ".rightRoomId");
                    if (w.LeftRoomId == w.RightRoomId) Error("wall-room-sides", w.Id, "A wall must reference distinct adjacent rooms, with at most one exterior null side.");
                }
            }
            if (!rooms.ContainsKey(plan.EntryRoomId ?? "")) Error("unknown-reference", "entryRoomId", "Entry room must identify a declared room.");
            foreach (var stair in plan.Stairs)
            {
                if (!storeys.ContainsKey(stair.LowerStoreyId ?? "") || !storeys.ContainsKey(stair.UpperStoreyId ?? "")) Error("unknown-reference", stair.Id, "Stair storeys must be declared.");
                if (!rooms.ContainsKey(stair.LowerRoomId ?? "") || !rooms.ContainsKey(stair.UpperRoomId ?? "")) Error("unknown-reference", stair.Id, "Stair landing rooms must be declared.");
                Reference(stair.MaterialId, materials, stair.Id + ".materialId");
            }
            foreach (var roof in plan.Roofs) { Reference(roof.MaterialId, materials, roof.Id + ".materialId"); Reference(roof.DetailProfileId, details, roof.Id + ".detailProfileId"); }
        }

        private void ArchitecturalGeometry()
        {
            bool exteriorEntry = false;
            foreach (var s in plan.Storeys)
            {
                foreach (var other in plan.Storeys.Where(other => string.CompareOrdinal(other.Id, s.Id) < 0))
                {
                    Debit();
                    var separation = Math.Abs(other.Elevation - s.Elevation);
                    if (separation < Feature || (s.Elevation > other.Elevation ? s.Elevation - s.SlabThickness < other.Elevation + other.Height - E : other.Elevation - other.SlabThickness < s.Elevation + s.Height - E)) Error("storey-elevation-overlap", s.Id, "Storey volumes/slabs must not overlap vertically; elevations are distinct finished floors.");
                }
                foreach (var r in s.Rooms)
                {
                    if (!geometry.Contains(s.Footprint, r.Boundary)) Error("room-outside-floor", r.Id, "Clear room boundary must fit floor material and avoid courtyards.");
                    foreach (var other in s.Rooms.Where(other => string.CompareOrdinal(other.Id, r.Id) < 0))
                        if (geometry.Overlap(r.Boundary, other.Boundary)) Error("room-overlap", r.Id, "Clear room interiors cannot overlap.");
                    RoomEnclosure(s, r);
                    foreach (var wall in s.Walls)
                        if (geometry.Overlap(r.Boundary, geometry.Rectangle(wall.Start, wall.End, wall.Thickness))) Error("wall-in-clear-room", r.Id, "Physical wall material must not intrude into a declared clear room interior.");
                }
                foreach (var slab in s.SlabOpenings)
                {
                    if (!geometry.Contains(s.Footprint, slab.Boundary)) Error("slab-opening-outside", slab.Id, "Slab opening must fit the floor footprint.");
                    foreach (var other in s.SlabOpenings.Where(other => string.CompareOrdinal(other.Id, slab.Id) < 0))
                        if (geometry.Overlap(slab.Boundary, other.Boundary)) Error("slab-opening-overlap", slab.Id, "Slab openings cannot overlap.");
                }
                foreach (var w in s.Walls)
                {
                    if (!geometry.ContainsSegment(s.Footprint, w.Start, w.End)) Error("wall-outside-floor", w.Id, "Wall centre line must remain within floor footprint boundary.");
                    foreach (var other in s.Walls.Where(other => string.CompareOrdinal(other.Id, w.Id) < 0))
                    {
                        if (geometry.CollinearOverlap(w.Start, w.End, other.Start, other.End)) Error("duplicate-shared-wall", w.Id, "Collinear wall spans overlap: author every physical/shared wall once.");
                        else if (geometry.Intersects(w.Start, w.End, other.Start, other.End, true)) Error("wall-crossing", w.Id, "Wall intersections require explicit junction endpoints.");
                        else
                        {
                            var commonEndpoint = Distance(w.Start, other.Start) <= E || Distance(w.Start, other.End) <= E || Distance(w.End, other.Start) <= E || Distance(w.End, other.End) <= E;
                            if (!commonEndpoint)
                            {
                                if (geometry.Intersects(w.Start, w.End, other.Start, other.End)) Error("wall-junction-not-split", w.Id, "T junctions must split every participating centre line at a common endpoint.");
                                else if (geometry.Overlap(geometry.Rectangle(w.Start, w.End, w.Thickness), geometry.Rectangle(other.Start, other.End, other.Thickness))) Error("wall-volume-overlap", w.Id, "Distinct physical wall strips cannot overlap away from a common authored endpoint.");
                            }
                        }
                    }
                    var direction = Mul(Sub(w.End, w.Start), 1 / Distance(w.Start, w.End));
                    var normal = new Point2(-direction.Y, direction.X);
                    foreach (var o in w.Openings)
                    {
                        var before = issues.Count;
                        foreach (var other in w.Openings.Where(other => string.CompareOrdinal(other.Id, o.Id) < 0))
                        {
                            Debit();
                            if (Math.Min(o.Offset + o.Width, other.Offset + other.Width) - Math.Max(o.Offset, other.Offset) > E && Math.Min(o.Head, other.Head) - Math.Max(o.Sill, other.Sill) > E) Error("opening-overlap", o.Id, "Wall opening volumes cannot overlap.");
                        }
                        foreach (var (roomId, sign) in new[] { (w.LeftRoomId, 1), (w.RightRoomId, -1) })
                        {
                            if (roomId is null) continue;
                            var boundary = rooms[roomId].Room.Boundary;
                            var faceA = Add(Add(w.Start, Mul(direction, o.Offset)), Mul(normal, sign * w.Thickness / 2));
                            var faceB = Add(faceA, Mul(direction, o.Width));
                            if (!geometry.BoundaryContainsSegment(boundary, faceA, faceB)) Error("opening-room-alignment", o.Id, "Entire opening width must lie on the clear boundary of each declared adjacent room.");
                            if (o.Kind is OpeningKind.Door or OpeningKind.Passage)
                            {
                                var centre = Mul(Add(faceA, faceB), .5);
                                var approach = geometry.Rectangle(centre, Add(centre, Mul(normal, sign * plan.Player.Radius * 2)), o.Width);
                                if (!geometry.Contains(boundary, approach)) Error("opening-approach-clearance", o.Id, "Door/passage needs full-width, one-player-diameter clear approach inside each room.");
                                if (s.SlabOpenings.Any(slab => geometry.Overlap(slab.Boundary, approach))) Error("opening-approach-over-void", o.Id, "Door/passage approach must stand on floor slab, not an opening.");
                            }
                        }
                        if (o.Kind is OpeningKind.Door or OpeningKind.Passage && issues.Count == before)
                        {
                            if (w.LeftRoomId is not null && w.RightRoomId is not null) Connect(w.LeftRoomId, w.RightRoomId);
                            else if ((w.LeftRoomId ?? w.RightRoomId) == plan.EntryRoomId)
                            {
                                var mid = Add(w.Start, Mul(direction, o.Offset + o.Width / 2));
                                var outward = Mul(normal, w.LeftRoomId is null ? 1 : -1);
                                if (geometry.InPolygon(Add(mid, Mul(outward, w.Thickness / 2 + E * 4)), s.Footprint) < 0) exteriorEntry = true;
                                else Error("exterior-door-alignment", o.Id, "Entry opening's exterior side must reach outside the floor footprint or into its courtyard.");
                            }
                        }
                    }
                }
            }
            foreach (var stair in plan.Stairs) Stair(stair);
            for (var i = 0; i < plan.Stairs.Count; i++)
            for (var j = 0; j < i; j++)
            {
                StairPair(plan.Stairs[i], plan.Stairs[j]);
                StairLandingsAgainstFlight(plan.Stairs[i], plan.Stairs[j]);
                StairLandingsAgainstFlight(plan.Stairs[j], plan.Stairs[i]);
            }
            var beforeRoofs = issues.Count;
            foreach (var roof in plan.Roofs) Roof(roof);
            if (issues.Count == beforeRoofs) RoofSystem();
            if (!exteriorEntry) Error("missing-exterior-entry", "entryRoomId", "Entry room requires a real exterior door/passage with player clearance.");
            var reached = new HashSet<string>(StringComparer.Ordinal) { plan.EntryRoomId };
            var queue = new Queue<string>(); queue.Enqueue(plan.EntryRoomId);
            while (queue.TryDequeue(out var current))
                foreach (var neighbour in connectivity[current]) { Debit(); if (reached.Add(neighbour)) queue.Enqueue(neighbour); }
            foreach (var room in rooms.Values.Where(r => r.Room.RequiresAccess))
                if (!reached.Contains(room.Room.Id)) Error("disconnected-room", room.Room.Id, "Required room is not connected to the exterior entry through actual admissible doors/passages/stairs.");
        }

        private void RoomEnclosure(StoreyPlan storey, RoomPlan room)
        {
            foreach (var ring in geometry.Rings(room.Boundary))
            for (var i = 0; i < ring.Count; i++)
            {
                var a = ring[i]; var b = ring[(i + 1) % ring.Count]; var ab = Sub(b, a); var length = Length(ab);
                var coverage = new List<(double Start, double End)>();
                foreach (var wall in storey.Walls)
                {
                    Debit();
                    var sign = wall.LeftRoomId == room.Id ? 1 : wall.RightRoomId == room.Id ? -1 : 0;
                    if (sign == 0) continue;
                    var (faceA, faceB) = WallFace(storey, wall, room.Id);
                    if (Math.Abs(Cross(ab, Sub(faceA, a))) > E * length || Math.Abs(Cross(ab, Sub(faceB, a))) > E * length) continue;
                    var start = Dot(Sub(faceA, a), ab) / length; var end = Dot(Sub(faceB, a), ab) / length;
                    coverage.Add((Math.Min(start, end), Math.Max(start, end)));
                }
                double until = 0;
                foreach (var span in coverage.OrderBy(c => c.Start)) { if (span.Start > until + E) break; until = Math.Max(until, span.End); }
                if (until < length - E) Error("unenclosed-room", room.Id, "Every clear room boundary edge must be covered by an assigned wall face, including courtyard boundaries.");
            }
        }

        private (Point2 Start, Point2 End) WallFace(StoreyPlan storey, WallPlan wall, string roomId)
        {
            var direction = Sub(wall.End, wall.Start);
            var side = wall.LeftRoomId == roomId ? 1 : -1;
            var normal = Mul(new Point2(-direction.Y, direction.X), side * wall.Thickness / (2 * Length(direction)));
            var start = Add(wall.Start, normal); var end = Add(wall.End, normal);
            foreach (var neighbour in storey.Walls)
            {
                Debit();
                if (neighbour.Id == wall.Id || (neighbour.LeftRoomId != roomId && neighbour.RightRoomId != roomId)) continue;
                var atStart = Distance(wall.Start, neighbour.Start) <= E || Distance(wall.Start, neighbour.End) <= E;
                var atEnd = Distance(wall.End, neighbour.Start) <= E || Distance(wall.End, neighbour.End) <= E;
                if (!atStart && !atEnd) continue;
                var otherDirection = Sub(neighbour.End, neighbour.Start);
                var denominator = Cross(direction, otherDirection);
                if (Math.Abs(denominator) <= E * Math.Max(Length(direction), Length(otherDirection))) continue;
                var otherSide = neighbour.LeftRoomId == roomId ? 1 : -1;
                var otherNormal = Mul(new Point2(-otherDirection.Y, otherDirection.X), otherSide * neighbour.Thickness / (2 * Length(otherDirection)));
                var otherPoint = Add(neighbour.Start, otherNormal);
                var t = Cross(Sub(otherPoint, Add(wall.Start, normal)), otherDirection) / denominator;
                var intersection = Add(Add(wall.Start, normal), Mul(direction, t));
                if (atStart) start = intersection;
                if (atEnd) end = intersection;
            }
            return (start, end);
        }

        private void Connect(string a, string b) { connectivity[a].Add(b); connectivity[b].Add(a); }

        private void Stair(StairPlan stair)
        {
            var before = issues.Count;
            var lower = storeys[stair.LowerStoreyId]; var upper = storeys[stair.UpperStoreyId];
            var rise = upper.Elevation - lower.Elevation;
            if (rise <= 0 || Math.Abs(rise - stair.RiserCount * stair.RiserHeight) > E || Math.Abs(Distance(stair.Start, stair.End) - (stair.RiserCount - 1) * stair.TreadDepth) > E) Error("stair-rise-run", stair.Id, "Straight-flight rise and run must exactly match storey elevations, riser count and tread depth within tolerance.");
            if (rooms[stair.LowerRoomId].Storey.Id != lower.Id || rooms[stair.UpperRoomId].Storey.Id != upper.Id) Error("stair-room-storey", stair.Id, "Landing rooms must belong to the declared respective storeys.");
            var footprint = geometry.Rectangle(stair.Start, stair.End, stair.Width);
            if (geometry.Overlap(footprint, stair.LowerLanding) || geometry.Overlap(footprint, stair.UpperLanding)) Error("stair-landing-over-flight", stair.Id, "Usable landings must remain outside their own flight interior; only shared endpoint edges may touch.");
            var direction = Mul(Sub(stair.End, stair.Start), 1 / Distance(stair.Start, stair.End));
            var depth = Math.Max(plan.Player.MinLandingDepth, plan.Player.Radius * 2);
            var lowerRequired = geometry.Rectangle(Sub(stair.Start, Mul(direction, depth)), stair.Start, stair.Width);
            var upperRequired = geometry.Rectangle(stair.End, Add(stair.End, Mul(direction, depth)), stair.Width);
            if (stair.LowerLanding.Holes.Count != 0 || stair.UpperLanding.Holes.Count != 0 || !geometry.Contains(stair.LowerLanding, lowerRequired) || !geometry.Contains(stair.UpperLanding, upperRequired)) Error("stair-landing-clearance", stair.Id, "Landings must contain the full stair width and player landing depth immediately outside each flight endpoint.");
            if (!geometry.Contains(rooms[stair.LowerRoomId].Room.Boundary, stair.LowerLanding) || !geometry.Contains(rooms[stair.UpperRoomId].Room.Boundary, stair.UpperLanding)) Error("stair-landing-room", stair.Id, "Each landing must fit its declared clear room.");
            if (!geometry.Contains(rooms[stair.LowerRoomId].Room.Boundary, footprint) || !geometry.Contains(rooms[stair.UpperRoomId].Room.Boundary, footprint)) Error("stair-flight-room", stair.Id, "Entire straight flight must remain in its declared clear stairwell rooms.");
            if (!geometry.Contains(lower.Footprint, footprint) || !geometry.Contains(upper.Footprint, footprint)) Error("stair-outside-floor", stair.Id, "Flight footprint must fit both storey footprints.");
            var slab = upper.SlabOpenings.SingleOrDefault(o => o.Id == stair.RequiredSlabOpeningId);
            if (slab is null || slab.Boundary.Holes.Count != 0 || !geometry.Contains(slab.Boundary, footprint)) Error("stair-slab-alignment", stair.Id, "Declared upper-storey slab opening must contain the entire straight-flight footprint; no hidden slab intersection is allowed.");
            if (slab is not null && geometry.Overlap(slab.Boundary, stair.UpperLanding)) Error("stair-landing-over-void", stair.Id, "Upper landing must stand on slab, outside the required flight void.");
            if (lower.SlabOpenings.Any(o => geometry.Overlap(o.Boundary, stair.LowerLanding)) || upper.SlabOpenings.Any(o => o.Id != stair.RequiredSlabOpeningId && geometry.Overlap(o.Boundary, stair.UpperLanding))) Error("stair-landing-over-void", stair.Id, "Both landings must stand on slab, clear of every slab opening.");
            foreach (var intermediate in plan.Storeys.Where(s => s.Elevation > lower.Elevation + E && s.Elevation < upper.Elevation - E)) Error("unsupported-stair-storey-span", stair.Id, "A baseline straight flight joins consecutive storeys; split flights at intermediate landings.");
            foreach (var wall in lower.Walls.Concat(upper.Walls))
                if (geometry.Overlap(footprint, geometry.Rectangle(wall.Start, wall.End, wall.Thickness))) Error("stair-wall-collision", stair.Id, "Stair flight intersects a physical wall; use an explicitly authored clear stairwell.");
            // Full-flight slab cut is conservative: no slab can reduce the declared headroom.
            if (stair.Headroom > lower.Height + E || stair.Headroom > upper.Height + E) Error("stair-headroom", stair.Id, "Declared headroom exceeds a connected storey's actual clear height.");
            if (issues.Count == before) Connect(stair.LowerRoomId, stair.UpperRoomId);
        }

        private void Roof(RoofPlane roof)
        {
            var points = roof.Vertices.Select(p => new Point2(p.X, p.Y)).ToList();
            var polygon = new Polygon2 { Outer = points, Holes = [] };
            ValidatePolygon(polygon, roof.Id + ".vertices.xy");
            var origin = roof.Vertices[0];
            Point3 normal = default;
            for (var i = 1; i + 1 < roof.Vertices.Count; i++)
            {
                Debit();
                var a = roof.Vertices[i]; var b = roof.Vertices[i + 1];
                normal = new((a.Y - origin.Y) * (b.Z - origin.Z) - (a.Z - origin.Z) * (b.Y - origin.Y), (a.Z - origin.Z) * (b.X - origin.X) - (a.X - origin.X) * (b.Z - origin.Z), (a.X - origin.X) * (b.Y - origin.Y) - (a.Y - origin.Y) * (b.X - origin.X));
                if (normal.Z > Feature * Feature) break;
            }
            var length = Math.Sqrt(normal.X * normal.X + normal.Y * normal.Y + normal.Z * normal.Z);
            if (length <= Feature * Feature || normal.Z / length <= Math.Sin(plan.Numerics.AngularToleranceDegrees * Math.PI / 180)) { Error("unsupported-roof-plane", roof.Id, "Roof must be a nonvertical upward-wound planar surface."); return; }
            foreach (var p in roof.Vertices)
                if (Math.Abs((p.X - origin.X) * normal.X + (p.Y - origin.Y) * normal.Y + (p.Z - origin.Z) * normal.Z) / length > E) Error("nonplanar-roof", roof.Id, "Every roof vertex must lie in one authored plane.");
            var covered = plan.Storeys.Where(s => geometry.Overlap(polygon, s.Footprint)).ToList();
            if (covered.Count == 0) Error("roof-detached", roof.Id, "Roof projection must cover some building footprint.");
            else if (roof.Vertices.Any(p => p.Z < covered.Max(s => s.Elevation + s.Height) - E)) Error("roof-wall-clearance", roof.Id, "Baseline roof plane must not descend below any wall/storey top under its projection.");
        }

        private void StairPair(StairPlan a, StairPlan b)
        {
            Debit();
            var aFootprint = geometry.Rectangle(a.Start, a.End, a.Width);
            var bFootprint = geometry.Rectangle(b.Start, b.End, b.Width);
            if (!geometry.Overlap(aFootprint, bFootprint)) return;
            var aDirection = Mul(Sub(a.End, a.Start), 1 / Distance(a.Start, a.End));
            var bDirection = Mul(Sub(b.End, b.Start), 1 / Distance(b.Start, b.End));
            var aFloor = storeys[a.LowerStoreyId].Elevation;
            var bFloor = storeys[b.LowerStoreyId].Elevation;
            bool insufficientHeadroom = false;
            // A tread is a constant walking top with a linear waist-slab soffit beneath it.
            // Closed tread cells include riser boundaries/nosings conservatively.
            for (var i = 0; i < a.RiserCount - 1; i++)
            {
                Debit();
                var aStart = Add(a.Start, Mul(aDirection, i * a.TreadDepth));
                var aCell = geometry.Rectangle(aStart, Add(aStart, Mul(aDirection, a.TreadDepth)), a.Width);
                var aTop = aFloor + (i + 1) * a.RiserHeight;
                for (var j = 0; j < b.RiserCount - 1; j++)
                {
                    Debit();
                    var bStart = Add(b.Start, Mul(bDirection, j * b.TreadDepth));
                    var bCell = geometry.Rectangle(bStart, Add(bStart, Mul(bDirection, b.TreadDepth)), b.Width);
                    var intersection = geometry.ConvexIntersection(aCell.Outer, bCell.Outer);
                    if (intersection.Count < 3 || Math.Abs(Area(intersection)) <= E * E) continue;
                    var bTop = bFloor + (j + 1) * b.RiserHeight;
                    if (Math.Abs(aTop - bTop) <= E)
                    {
                        Error("stair-flight-overlap", a.Id, $"Flight solid intersects {b.Id}; coincident/reversed flights cannot share one physical stair volume."); return;
                    }
                    var upper = aTop > bTop ? a : b;
                    var upperDirection = aTop > bTop ? aDirection : bDirection;
                    var upperFloor = aTop > bTop ? aFloor : bFloor;
                    var lowerTop = Math.Min(aTop, bTop);
                    var requiredHeadroom = aTop > bTop ? b.Headroom : a.Headroom;
                    var minimumSoffit = intersection.Min(p => upperFloor + Dot(Sub(p, upper.Start), upperDirection) / upper.TreadDepth * upper.RiserHeight - upper.StructureThickness);
                    var actualGap = minimumSoffit - lowerTop;
                    if (actualGap <= E)
                    {
                        Error("stair-flight-overlap", a.Id, $"Flight solid intersects {b.Id}; discrete tread/riser tops and authored waist-slab soffits overlap."); return;
                    }
                    if (actualGap + E < requiredHeadroom) insufficientHeadroom = true;
                }
            }
            if (insufficientHeadroom) Error("stair-flight-headroom", a.Id, $"Vertically separated flight {b.Id} leaves less than the lower flight's declared headroom beneath the upper waist slab.");
        }

        private void StairLandingsAgainstFlight(StairPlan owner, StairPlan flight)
        {
            Debit();
            var footprint = geometry.Rectangle(flight.Start, flight.End, flight.Width);
            var direction = Mul(Sub(flight.End, flight.Start), 1 / Distance(flight.Start, flight.End));
            var flightFloor = storeys[flight.LowerStoreyId].Elevation;
            foreach (var (landing, storeyId, location) in new[] { (owner.LowerLanding, owner.LowerStoreyId, "lowerLanding"), (owner.UpperLanding, owner.UpperStoreyId, "upperLanding") })
            {
                if (!geometry.Overlap(landing, footprint)) continue;
                var landingStorey = storeys[storeyId];
                var landingTop = landingStorey.Elevation;
                var landingBottom = landingTop - landingStorey.SlabThickness;
                bool insufficientHeadroom = false;
                bool collision = false;
                for (var i = 0; i < flight.RiserCount - 1; i++)
                {
                    Debit();
                    var start = Add(flight.Start, Mul(direction, i * flight.TreadDepth));
                    var cell = geometry.Rectangle(start, Add(start, Mul(direction, flight.TreadDepth)), flight.Width);
                    if (!geometry.Overlap(landing, cell)) continue;
                    var flightTop = flightFloor + (i + 1) * flight.RiserHeight;
                    double actualGap, requiredHeadroom;
                    if (flightTop < landingTop - E)
                    {
                        actualGap = landingBottom - flightTop;
                        requiredHeadroom = flight.Headroom;
                    }
                    else
                    {
                        var witnesses = geometry.IntersectionVertices(landing, cell);
                        if (witnesses.Count == 0) throw new GeometryCapacityException();
                        var minimumSoffit = witnesses.Min(p => flightFloor + Dot(Sub(p, flight.Start), direction) / flight.TreadDepth * flight.RiserHeight - flight.StructureThickness);
                        actualGap = minimumSoffit - landingTop;
                        requiredHeadroom = owner.Headroom;
                    }
                    if (actualGap <= E) { collision = true; break; }
                    if (actualGap + E < requiredHeadroom) insufficientHeadroom = true;
                }
                if (collision) Error("stair-landing-flight-overlap", owner.Id + "." + location, $"Landing slab intersects flight {flight.Id}; authored flat slab and discrete flight solids cannot share a physical volume.");
                else if (insufficientHeadroom) Error("stair-landing-flight-headroom", owner.Id + "." + location, $"Landing and flight {flight.Id} leave less than the lower occupant's declared headroom beneath the higher structure.");
            }
        }

        private void RoofSystem()
        {
            var projections = plan.Roofs.Select(r => new Polygon2 { Outer = r.Vertices.Select(p => new Point2(p.X, p.Y)).ToList(), Holes = [] }).ToList();
            for (var i = 0; i < projections.Count; i++)
            {
                for (var j = 0; j < i; j++)
                    if (geometry.Overlap(projections[i], projections[j])) Error("roof-projection-overlap", plan.Roofs[i].Id, "Baseline roof facets may share edges, but must not overlap in XY; stacked roof volumes need a future explicit profile.");
                foreach (var courtyard in plan.Storeys.SelectMany(s => s.Footprint.Holes))
                {
                    var voidPolygon = new Polygon2 { Outer = courtyard.AsEnumerable().Reverse().ToList(), Holes = [] };
                    if (geometry.Overlap(projections[i], voidPolygon)) Error("roof-covers-courtyard", plan.Roofs[i].Id, "Declared open courtyard interiors must remain uncovered by roof facets.");
                }
            }
            if (!geometry.Covers(plan.Storeys.Select(s => s.Footprint).ToList(), projections)) Error("incomplete-roof-coverage", "roofs", "Union of authored roof projections must cover all building floor material, excluding open courtyards; exact straight-edge arrangement checks use the finite geometry budget.");
        }
    }
}
