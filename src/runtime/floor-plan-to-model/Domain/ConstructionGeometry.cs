using static Bubo.FloorPlan.Domain.PlanGeometry;
using static Bubo.FloorPlan.Domain.ConstructionGeometryPrimitives;

namespace Bubo.FloorPlan.Domain;

/// <summary>Dimension-driven, bounded analytic architecture. This is not an engine navigation service.</summary>
public sealed class ConstructionGeometryCompiler
{
    public ConstructionScene Compile(BuildingPlan plan, CancellationToken cancellationToken = default)
    {
        new ConstructionReadiness().RequireReady(plan, cancellationToken);
        return new Builder(plan, cancellationToken).Run();
    }

    private sealed record Solid(string Owner, string Storey, string Kind, List<Point2> Polygon, ConstructionPlane Lower, ConstructionPlane Upper, bool Dynamic)
    {
        public Point3 Minimum => new(Polygon.Min(p => p.X), Polygon.Min(p => p.Y), Polygon.Min(Lower.At));
        public Point3 Maximum => new(Polygon.Max(p => p.X), Polygon.Max(p => p.Y), Polygon.Max(Upper.At));
    }
    private sealed record Door(StoreyPlan Storey, WallPlan Wall, WallOpening Opening, string Anchor, Point2 Pivot, List<Point2> Polygon, double Lower, double Upper, double Angle);
    private sealed class Mesh(string id, string semantic, string storey, string kind, string material, string? anchor, bool dynamic)
    {
        public string Id => id;
        public List<Point3> Vertices { get; } = [];
        public List<int> Triangles { get; } = [];
        public List<string> Materials { get; } = [];
        public ConstructionMesh Result(string representation) => new()
        {
            Id = representation + "-" + id, SemanticId = semantic, StoreyId = storey, Kind = kind,
            Representation = representation, CollisionRole = representation == "visual" ? "none" : dynamic ? "dynamic" : "static",
            MaterialId = material, Vertices = [.. Vertices], Triangles = [.. Triangles], ParentAnchorId = anchor,
            TriangleMaterialIds = representation == "visual" ? [.. Materials] : null
        };
        public void Prism(List<Point2> polygon, ConstructionPlane lower, ConstructionPlane upper, string? bottom, ConstructionWork work)
        {
            var offset = Vertices.Count; var local = new List<Point3>(); var b = new int[polygon.Count]; var t = new int[polygon.Count];
            int Vertex(Point3 p)
            {
                for (var i = 0; i < local.Count; i++)
                {
                    work.Debit(); var q = local[i];
                    if (Math.Abs(p.X - q.X) <= work.E && Math.Abs(p.Y - q.Y) <= work.E && Math.Abs(p.Z - q.Z) <= work.E) return offset + i;
                }
                local.Add(p); return offset + local.Count - 1;
            }
            for (var i = 0; i < polygon.Count; i++)
            {
                if (upper.At(polygon[i]) < lower.At(polygon[i]) - work.E) throw new InvalidOperationException("Construction prism reverses its approved vertical interval.");
                b[i] = Vertex(XYZ(polygon[i], lower.At(polygon[i]))); t[i] = Vertex(XYZ(polygon[i], upper.At(polygon[i])));
            }
            Vertices.AddRange(local);
            void Triangle(int a, int c, int d, string m)
            {
                work.Debit(); if (a == c || a == d || c == d) return;
                var x = Vertices[a]; var y = Vertices[c]; var z = Vertices[d];
                var u = new Point3(y.X - x.X, y.Y - x.Y, y.Z - x.Z); var v = new Point3(z.X - x.X, z.Y - x.Y, z.Z - x.Z);
                var cross = new Point3(u.Y * v.Z - u.Z * v.Y, u.Z * v.X - u.X * v.Z, u.X * v.Y - u.Y * v.X);
                if (cross.X * cross.X + cross.Y * cross.Y + cross.Z * cross.Z <= Math.Pow(work.E, 4)) return;
                Triangles.AddRange([a, c, d]); Materials.Add(m);
            }
            for (var i = 1; i + 1 < polygon.Count; i++) { Triangle(b[0], b[i + 1], b[i], bottom ?? material); Triangle(t[0], t[i], t[i + 1], material); }
            for (var i = 0; i < polygon.Count; i++)
            {
                var j = (i + 1) % polygon.Count;
                Triangle(b[i], b[j], t[j], material); Triangle(b[i], t[j], t[i], material);
            }
        }
    }

    private sealed class Builder(BuildingPlan plan, CancellationToken token)
    {
        private readonly ConstructionProfile profile = plan.Construction!;
        private readonly ConstructionWork work = new(plan, token);
        private readonly Dictionary<string, Mesh> meshes = new(StringComparer.Ordinal);
        private readonly List<Solid> solids = [];
        private readonly List<ConstructionAnchor> anchors = [];
        private readonly List<Door> doors = [];
        private readonly Dictionary<List<Point2>, string> obstacleOwners = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<Point2, double> pointClearanceCache = [];
        private readonly Dictionary<(Point2 A, Point2 B), double> segmentClearanceCache = [];
        private readonly Dictionary<string, Point2> landingPoints = new(StringComparer.Ordinal);
        private PlanGeometry geometry = null!;
        private int vertices, triangles;
        private double E => work.E;
        private Mesh Get(string semantic, string storey, string kind, string material, string suffix = "", string? anchor = null, bool dynamic = false)
        {
            var key = kind + "-" + semantic + suffix;
            if (!meshes.TryGetValue(key, out var value))
            {
                if (2 * (meshes.Count + 1) > profile.MaxMeshes) throw new InvalidOperationException("Approved construction mesh budget exceeded.");
                meshes[key] = value = new(key, semantic, storey, kind, material, anchor, dynamic);
            }
            return value;
        }
        private void Append(Mesh target, string storey, string kind, List<Point2> polygon, ConstructionPlane lower, ConstructionPlane upper, bool dynamic = false, string? bottom = null)
        {
            work.Debit(); if (polygon.Count < 3) return;
            if (polygon.Max(p => upper.At(p) - lower.At(p)) <= E) return;
            var beforeV = target.Vertices.Count; var beforeT = target.Triangles.Count;
            target.Prism(polygon, lower, upper, bottom, work);
            foreach (var p in target.Vertices.Skip(beforeV))
            {
                work.Debit();
                if (!double.IsFinite(p.X) || !double.IsFinite(p.Y) || !double.IsFinite(p.Z) || Math.Abs(p.X) > plan.Numerics.MaxCoordinateMagnitude + E || Math.Abs(p.Y) > plan.Numerics.MaxCoordinateMagnitude + E || Math.Abs(p.Z) > plan.Numerics.MaxCoordinateMagnitude + E)
                    throw new InvalidOperationException("Derived construction coordinate exceeds the approved numerical magnitude: " + target.Id);
            }
            vertices += 2 * (target.Vertices.Count - beforeV); triangles += 2 * (target.Triangles.Count - beforeT) / 3;
            if (vertices > profile.MaxConstructionVertices || triangles > plan.Budgets.MaxGeneratedTriangles)
                throw new InvalidOperationException("Approved construction vertex/triangle budget exceeded; no mesh simplification is performed.");
            solids.Add(new(target.Id, storey, kind, polygon, lower, upper, dynamic));
        }
        private static bool BoundsOverlap(Solid a, Solid b, double e)
        {
            var x = a.Minimum; var y = a.Maximum; var u = b.Minimum; var v = b.Maximum;
            return Math.Min(y.X, v.X) - Math.Max(x.X, u.X) > e && Math.Min(y.Y, v.Y) - Math.Max(x.Y, u.Y) > e && Math.Min(y.Z, v.Z) - Math.Max(x.Z, u.Z) > e;
        }
        // Canonical assembly ownership subtracts only already-owned solid volume, preserving
        // the exact union at structural/trim contacts rather than leaving intersecting solids.
        private void Exclusive(Mesh target, string storey, string kind, List<Point2> polygon, ConstructionPlane lower, ConstructionPlane upper, string? bottom = null)
        {
            var proposed = new Solid(target.Id, storey, kind, polygon, lower, upper, false);
            var preserveMember = kind is "door-frame" or "window-frame" or "window-glass";
            var previous = new List<Solid>();
            foreach (var s in solids) { work.Debit(); if (!s.Dynamic && BoundsOverlap(proposed, s, E)) previous.Add(s); }
            if (previous.Count == 0) { Append(target, storey, kind, polygon, lower, upper, bottom: bottom); return; }
            var planes = new List<ConstructionPlane> { lower, upper }; planes.AddRange(previous.SelectMany(s => new[] { s.Lower, s.Upper }));
            var cuts = new List<ConstructionPlane>();
            for (var i = 0; i < planes.Count; i++) for (var j = i + 1; j < planes.Count; j++)
            { work.Debit(); cuts.Add(new(planes[i].X - planes[j].X, planes[i].Y - planes[j].Y, planes[i].C - planes[j].C)); }
            foreach (var cell in Partition(new[] { Polygon(polygon) }.Concat(previous.Select(s => Polygon(s.Polygon))), cuts, work))
            {
                var c = Centre(cell); if (geometry.InRing(c, polygon) < 0) continue;
                var occupied = previous.Where(s => geometry.InRing(c, s.Polygon) >= 0).ToList();
                var levels = new List<ConstructionPlane> { lower, upper }; levels.AddRange(occupied.SelectMany(s => new[] { s.Lower, s.Upper }));
                levels = levels.OrderBy(p => p.At(c)).ToList();
                for (var i = 1; i < levels.Count; i++)
                {
                    work.Debit(); var a = levels[i - 1]; var b = levels[i]; var z = (a.At(c) + b.At(c)) / 2;
                    if (b.At(c) - a.At(c) <= E || z < lower.At(c) || z > upper.At(c)) continue;
                    bool covered = false;
                    foreach (var s in occupied)
                    {
                        work.Debit(); if (z <= s.Lower.At(c) - E || z >= s.Upper.At(c) + E) continue;
                        if (preserveMember && s.Kind is not ("wall" or "gable-wall"))
                            throw new InvalidOperationException((kind == "window-glass" ? "Approved glazing" : "Approved opening frame") + " intersects structural geometry or another explicit assembly; its approved dimensions cannot be clipped: " + target.Id + " / " + s.Owner);
                        covered = true;
                        if (!preserveMember) break;
                    }
                    if (covered) continue;
                    Append(target, storey, kind, cell, a, b, bottom: bottom);
                }
            }
        }
        private void Anchor(string id, string semantic, string storey, string kind, Point3 position)
            => anchors.Add(new() { Id = id, SemanticId = semantic, StoreyId = storey, Kind = kind, Position = position, Axis = new(0, 0, 1), RotationRadians = 0 });

        public ConstructionScene Run()
        {
            geometry = new(E, work.Debit);
            ValidateDetailedDimensions();
            foreach (var storey in plan.Storeys.OrderBy(s => s.Elevation).ThenBy(s => s.Id, StringComparer.Ordinal))
            {
                Anchor("floor-anchor-" + storey.Id, storey.Id, storey.Id, "floor-root", new(0, 0, storey.Elevation));
                Floor(storey); Walls(storey);
            }
            Roofs();
            foreach (var storey in plan.Storeys.OrderBy(s => s.Elevation).ThenBy(s => s.Id, StringComparer.Ordinal))
            { Openings(storey); Trim(storey, false); Trim(storey, true); Ceilings(storey); }
            foreach (var stair in plan.Stairs.OrderBy(s => s.Id, StringComparer.Ordinal)) Stairs(stair);
            foreach (var storey in plan.Storeys.OrderBy(s => s.Elevation).ThenBy(s => s.Id, StringComparer.Ordinal)) VoidRails(storey);
            Fascias();
            var clearance = Clearances();
            var result = meshes.Values.Where(m => m.Triangles.Count != 0).OrderBy(m => m.Id, StringComparer.Ordinal).SelectMany(m => new[] { m.Result("visual"), m.Result("collision") }).ToList();
            if (result.Count == 0) throw new InvalidOperationException("Construction produced no architecture.");
            var points = result.SelectMany(m => m.Vertices).ToList();
            return new()
            {
                SchemaVersion = "bubo-floor-plan-construction-scene/v1", PlanId = plan.Id, LinearTolerance = E,
                Materials = plan.Materials.OrderBy(m => m.Id, StringComparer.Ordinal).Select(m => new ConstructionMaterial { Id = m.Id, Name = m.Name, Color = m.Color, Roughness = m.Roughness, Metallic = m.Metallic, Transmission = m.Id == profile.GlassMaterialId ? profile.GlassTransmission : 0, Ior = m.Id == profile.GlassMaterialId ? profile.GlassIor : 1 }).ToList(),
                Meshes = result, Anchors = anchors, Clearance = clearance,
                Metrics = new() { MeshCount = result.Count, VertexCount = points.Count, TriangleCount = result.Sum(m => m.Triangles.Count / 3), VisualTriangleCount = result.Where(m => m.Representation == "visual").Sum(m => m.Triangles.Count / 3), CollisionTriangleCount = result.Where(m => m.Representation == "collision").Sum(m => m.Triangles.Count / 3), GeometryChecks = work.Checks, Minimum = new(points.Min(v => v.X), points.Min(v => v.Y), points.Min(v => v.Z)), Maximum = new(points.Max(v => v.X), points.Max(v => v.Y), points.Max(v => v.Z)) }
            };
        }

        private void ValidateDetailedDimensions()
        {
            foreach (var storey in plan.Storeys) foreach (var wall in storey.Walls)
            {
                work.Debit(); var detail = plan.DetailProfiles.Single(d => d.Id == wall.DetailProfileId);
                if (detail.PlinthHeight > wall.Height + E || detail.CorniceHeight > wall.Height + E)
                    throw new InvalidOperationException("Approved wall detail height exceeds its supporting wall; construction cannot truncate or erase the declared profile: " + wall.Id);
                if (detail.PlinthProjection > E && detail.CorniceProjection > E && detail.PlinthHeight + detail.CorniceHeight > wall.Height + E)
                    throw new InvalidOperationException("Approved wall detail height bands overlap; construction cannot shorten either declared profile: " + wall.Id);
            }
        }

        private void Floor(StoreyPlan storey)
        {
            var landings = plan.Stairs.SelectMany(s => new[] { (Stair: s, Storey: s.LowerStoreyId, Id: s.Id + "-lower-landing", Boundary: s.LowerLanding), (Stair: s, Storey: s.UpperStoreyId, Id: s.Id + "-upper-landing", Boundary: s.UpperLanding) }).Where(s => s.Storey == storey.Id).OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
            foreach (var landing in landings) Anchor("landing-anchor-" + landing.Id, landing.Id, storey.Id, "landing", XYZ(LandingPoint(landing.Id, landing.Boundary), storey.Elevation));
            foreach (var cell in Partition(new[] { storey.Footprint }.Concat(storey.SlabOpenings.Select(v => v.Boundary)).Concat(landings.Select(l => l.Boundary)), [], work))
            {
                var c = Centre(cell); if (geometry.InPolygon(c, storey.Footprint) < 0 || storey.SlabOpenings.Any(v => geometry.InPolygon(c, v.Boundary) >= 0)) continue;
                var landing = landings.FirstOrDefault(l => geometry.InPolygon(c, l.Boundary) >= 0);
                var semantic = landing.Id ?? storey.Id; var material = landing.Stair?.MaterialId ?? storey.FloorMaterialId; var kind = landing.Id is null ? "floor-slab" : "landing";
                Append(Get(semantic, storey.Id, kind, material), storey.Id, kind, cell, ConstructionPlane.Flat(storey.Elevation - storey.SlabThickness), ConstructionPlane.Flat(storey.Elevation), bottom: profile.CeilingMaterialId);
            }
        }
        private List<Point2> WallPolygon(StoreyPlan storey, WallPlan wall, double extra = 0, Func<WallPlan, double>? neighbourProjection = null)
        {
            Point2 End(Point2 node, Point2 other, bool left)
            {
                var d = Unit(node, other); var n = Normal(d); var a = Add(node, Mul(n, (wall.Thickness / 2 + extra) * (left ? 1 : -1)));
                var neighbours = storey.Walls.Where(w => w.Id != wall.Id).SelectMany(w => new[] { (W: w, Node: w.Start, Other: w.End), (W: w, Node: w.End, Other: w.Start) }).Where(x => Distance(x.Node, node) <= E).Select(x => (x.W, D: Unit(x.Node, x.Other))).ToList();
                if (neighbours.Count == 0) return a;
                double Angle(Point2 direction)
                { var angle = Math.Atan2(Cross(d, direction), Dot(d, direction)); if (angle <= 0) angle += Math.PI * 2; return left ? angle : Math.PI * 2 - angle; }
                var neighbour = neighbours.OrderBy(x => Angle(x.D)).ThenBy(x => x.W.Id, StringComparer.Ordinal).First(); var neighbourExtra = neighbourProjection?.Invoke(neighbour.W) ?? extra; var b = Add(node, Mul(Normal(neighbour.D), (neighbour.W.Thickness / 2 + neighbourExtra) * (left ? -1 : 1))); var denominator = Cross(d, neighbour.D);
                if (Math.Abs(denominator) <= E) return a;
                var result = Add(a, Mul(d, Cross(Sub(b, a), neighbour.D) / denominator));
                if (Distance(result, node) > plan.Numerics.MaxCoordinateMagnitude) throw new InvalidOperationException("Wall miter exceeds approved coordinate magnitude.");
                return result;
            }
            var result = new List<Point2> { End(wall.Start, wall.End, false), End(wall.End, wall.Start, true), End(wall.End, wall.Start, false), End(wall.Start, wall.End, true) };
            for (var i = result.Count - 1; i >= 0 && result.Count > 1; i--)
                if (Distance(result[i], result[(i + 1) % result.Count]) <= E) result.RemoveAt(i);
            if (result.Count < 3 || Math.Abs(Area(result)) <= E * E) throw new InvalidOperationException("Wall miter consumes its approved segment: " + wall.Id);
            for (var i = 0; i < result.Count; i++) for (var j = i + 1; j < result.Count; j++)
            {
                if (j == i + 1 || (i == 0 && j == result.Count - 1)) continue;
                if (geometry.Intersects(result[i], result[(i + 1) % result.Count], result[j], result[(j + 1) % result.Count]))
                    throw new InvalidOperationException("Wall miter self-intersects; change the explicit dimensions/profile: " + wall.Id);
            }
            if (Area(result) < 0) result.Reverse();
            return result;
        }
        private IEnumerable<ConstructionPlane> OpeningCuts(WallPlan wall)
        {
            var d = Unit(wall.Start, wall.End);
            foreach (var o in wall.Openings) foreach (var offset in new[] { o.Offset, o.Offset + o.Width })
                yield return new(d.X, d.Y, -Dot(d, wall.Start) - offset);
        }
        private bool WallSolid(StoreyPlan storey, WallPlan wall, Point2 p, double z)
        {
            if (z < storey.Elevation - E || z > storey.Elevation + wall.Height + E) return false;
            var t = Dot(Sub(p, wall.Start), Unit(wall.Start, wall.End));
            foreach (var o in wall.Openings)
            {
                work.Debit(); if (t > o.Offset - E && t < o.Offset + o.Width + E && z > storey.Elevation + o.Sill - E && z < storey.Elevation + o.Head + E) return false;
            }
            return true;
        }
        private void Walls(StoreyPlan storey)
        {
            var walls = storey.Walls.OrderBy(w => w.Id, StringComparer.Ordinal).ToList(); var polygons = walls.ToDictionary(w => w.Id, w => Polygon(WallPolygon(storey, w)), StringComparer.Ordinal);
            var levels = walls.SelectMany(w => new[] { storey.Elevation, storey.Elevation + w.Height }.Concat(w.Openings.SelectMany(o => new[] { storey.Elevation + o.Sill, storey.Elevation + o.Head }))).Distinct().Order().ToArray();
            foreach (var cell in Partition(polygons.Values, walls.SelectMany(OpeningCuts), work))
            {
                var c = Centre(cell); var included = walls.Where(w => geometry.InPolygon(c, polygons[w.Id]) >= 0).ToList(); if (included.Count == 0) continue;
                for (var i = 1; i < levels.Length; i++)
                {
                    work.Debit(); if (levels[i] - levels[i - 1] <= E) continue; var z = (levels[i] + levels[i - 1]) / 2;
                    var wall = included.FirstOrDefault(w => WallSolid(storey, w, c, z)); if (wall is null) continue;
                    Exclusive(Get(wall.Id, storey.Id, "wall", wall.MaterialId), storey.Id, "wall", cell, ConstructionPlane.Flat(levels[i - 1]), ConstructionPlane.Flat(levels[i]));
                }
            }
        }

        private void Openings(StoreyPlan storey)
        {
            foreach (var wall in storey.Walls.OrderBy(w => w.Id, StringComparer.Ordinal)) foreach (var opening in wall.Openings.OrderBy(o => o.Id, StringComparer.Ordinal))
            {
                if (opening.Kind == OpeningKind.Passage) continue;
                var d = Unit(wall.Start, wall.End); var n = Normal(d); var fw = opening.Kind == OpeningKind.Door ? profile.DoorFrameWidth : profile.WindowFrameWidth; var depth = opening.Kind == OpeningKind.Door ? profile.DoorFrameDepth : profile.WindowFrameDepth;
                var frame = Get(opening.Id, storey.Id, opening.Kind == OpeningKind.Door ? "door-frame" : "window-frame", profile.FrameMaterialId);
                var bars = new List<(double A, double B, double Low, double High)> { (opening.Offset - fw, opening.Offset, opening.Sill, opening.Head), (opening.Offset + opening.Width, opening.Offset + opening.Width + fw, opening.Sill, opening.Head), (opening.Offset - fw, opening.Offset + opening.Width + fw, opening.Head, opening.Head + fw) };
                if (opening.Kind == OpeningKind.Window) bars.Add((opening.Offset - fw, opening.Offset + opening.Width + fw, opening.Sill - fw, opening.Sill));
                foreach (var side in new[] { -1, 1 }) foreach (var bar in bars)
                {
                    var low = side > 0 ? wall.Thickness / 2 : -wall.Thickness / 2 - depth; var high = side > 0 ? wall.Thickness / 2 + depth : -wall.Thickness / 2;
                    Exclusive(frame, storey.Id, opening.Kind == OpeningKind.Door ? "door-frame" : "window-frame", Rectangle(wall.Start, d, bar.A, bar.B, high, low), ConstructionPlane.Flat(storey.Elevation + bar.Low), ConstructionPlane.Flat(storey.Elevation + bar.High));
                }
                if (opening.Kind == OpeningKind.Window)
                    Exclusive(Get(opening.Id, storey.Id, "window-glass", profile.GlassMaterialId), storey.Id, "window-glass", Rectangle(wall.Start, d, opening.Offset, opening.Offset + opening.Width, profile.GlassThickness / 2, -profile.GlassThickness / 2), ConstructionPlane.Flat(storey.Elevation + opening.Sill), ConstructionPlane.Flat(storey.Elevation + opening.Head));
                else
                {
                    var a = opening.Offset + profile.DoorLeafGap; var b = opening.Offset + opening.Width - profile.DoorLeafGap; var low = -wall.Thickness / 2 + profile.DoorRevealDepth; var high = low + profile.DoorLeafThickness;
                    var pivot = Add(Add(wall.Start, Mul(d, opening.Pivot == DoorPivot.Start ? a : b)), Mul(n, (low + high) / 2)); var anchor = "door-anchor-" + opening.Id;
                    Anchor(anchor, opening.Id, storey.Id, "door-pivot", XYZ(pivot, storey.Elevation + opening.Sill));
                    var polygon = Rectangle(wall.Start, d, a, b, high, low); var lower = storey.Elevation + opening.Sill + profile.DoorLeafGap; var upper = storey.Elevation + opening.Head - profile.DoorLeafGap;
                    Append(Get(opening.Id, storey.Id, "door-leaf", profile.DoorMaterialId, anchor: anchor, dynamic: true), storey.Id, "door-leaf", polygon, ConstructionPlane.Flat(lower), ConstructionPlane.Flat(upper), true);
                    var angle = profile.DoorOpenAngleDegrees * Math.PI / 180 * (opening.Swing == DoorSwing.Left ? 1 : -1) * (opening.Pivot == DoorPivot.Start ? 1 : -1);
                    doors.Add(new(storey, wall, opening, anchor, pivot, polygon, lower, upper, angle));
                }
            }
        }
        private void Trim(StoreyPlan storey, bool cornice)
        {
            foreach (var wall in storey.Walls.OrderBy(w => w.Id, StringComparer.Ordinal))
            {
                var detail = plan.DetailProfiles.Single(d => d.Id == wall.DetailProfileId); var height = cornice ? detail.CorniceHeight : detail.PlinthHeight; var projection = cornice ? detail.CorniceProjection : detail.PlinthProjection;
                if (height <= E || projection <= E) continue;
                var inside = Polygon(WallPolygon(storey, wall)); var lower = storey.Elevation + (cornice ? wall.Height - height : 0); var upper = lower + height;
                (double Low, double High, double Projection) Band(WallPlan w)
                {
                    var p = plan.DetailProfiles.Single(d => d.Id == w.DetailProfileId); var h = cornice ? p.CorniceHeight : p.PlinthHeight; var z = storey.Elevation + (cornice ? w.Height - h : 0);
                    return (z, z + h, cornice ? p.CorniceProjection : p.PlinthProjection);
                }
                var levels = new[] { lower, upper }.Concat(storey.Walls.SelectMany(w => new[] { Band(w).Low, Band(w).High }.Concat(w.Openings.SelectMany(o => new[] { storey.Elevation + o.Sill, storey.Elevation + o.Head })))).Where(z => z >= lower && z <= upper).Distinct().Order().ToArray();
                for (var i = 1; i < levels.Length; i++)
                {
                    var middle = (levels[i] + levels[i - 1]) / 2; if (levels[i] - levels[i - 1] <= E) continue;
                    double NeighbourProjection(WallPlan w)
                    { var band = Band(w); return middle > band.Low - E && middle < band.High + E ? band.Projection : 0; }
                    var outer = Polygon(WallPolygon(storey, wall, projection, NeighbourProjection));
                    foreach (var cell in Partition([outer, inside], OpeningCuts(wall), work))
                    {
                        var c = Centre(cell); if (geometry.InPolygon(c, outer) < 0 || geometry.InPolygon(c, inside) > 0 || !WallSolid(storey, wall, c, middle)) continue;
                            Exclusive(Get(wall.Id, storey.Id, cornice ? "cornice" : "plinth", wall.MaterialId), storey.Id, cornice ? "cornice" : "plinth", cell, ConstructionPlane.Flat(levels[i - 1]), ConstructionPlane.Flat(levels[i]));
                    }
                }
            }
        }
        private StoreyPlan RoofStorey(RoofPlane roof) => plan.Storeys.Where(s => s.Elevation + s.Height <= roof.Vertices.Min(v => v.Z) + E).OrderByDescending(s => s.Elevation).ThenBy(s => s.Id, StringComparer.Ordinal).First();
        private void Roofs()
        {
            foreach (var roof in plan.Roofs.OrderBy(r => r.Id, StringComparer.Ordinal))
            {
                var storey = RoofStorey(roof); var polygon = Polygon(roof.Vertices.Select(XY).ToList()); var plane = Plane(roof.Vertices);
                foreach (var cell in Partition([polygon], [], work))
                    if (geometry.InPolygon(Centre(cell), polygon) >= 0)
                        Exclusive(Get(roof.Id, storey.Id, "roof", roof.MaterialId), storey.Id, "roof", cell, plane, plane.Shift(profile.RoofThickness), profile.CeilingMaterialId);
                foreach (var wall in storey.Walls.OrderBy(w => w.Id, StringComparer.Ordinal))
                {
                    var shape = Polygon(WallPolygon(storey, wall)); var low = ConstructionPlane.Flat(storey.Elevation + wall.Height); var cut = new ConstructionPlane(plane.X, plane.Y, plane.C - low.C);
                    foreach (var cell in Partition([polygon, shape], [cut], work))
                        if (geometry.InPolygon(Centre(cell), polygon) >= 0 && geometry.InPolygon(Centre(cell), shape) >= 0 && plane.At(Centre(cell)) - low.C > E)
                            Exclusive(Get(wall.Id, storey.Id, "gable-wall", wall.MaterialId), storey.Id, "gable-wall", cell, low, plane);
                }
            }
        }
        private void Ceilings(StoreyPlan storey)
        {
            Anchor("ceiling-anchor-" + storey.Id, storey.Id, storey.Id, profile.CeilingThickness == 0 ? "shared-ceiling" : "ceiling-panel", new(0, 0, storey.Elevation + storey.Height));
            if (profile.CeilingThickness == 0) return;
            var holes = plan.Stairs.Where(s => s.LowerStoreyId == storey.Id).Select(s => plan.Storeys.Single(u => u.Id == s.UpperStoreyId).SlabOpenings.Single(o => o.Id == s.RequiredSlabOpeningId).Boundary).ToList();
            var lower = ConstructionPlane.Flat(storey.Elevation + storey.Height); var upper = lower.Shift(profile.CeilingThickness);
            foreach (var cell in Partition(new[] { storey.Footprint }.Concat(holes), [], work))
                if (geometry.InPolygon(Centre(cell), storey.Footprint) >= 0 && !holes.Any(h => geometry.InPolygon(Centre(cell), h) >= 0))
                    CeilingPanel(storey, cell, lower, upper);
        }
        // A positive approved panel must fit in the available vertical gap. Ordinary
        // assembly subtraction may remove wall-edge columns, but must never erase or
        // thin a panel to conceal an intersection with a roof or another floor slab.
        private void CeilingPanel(StoreyPlan storey, List<Point2> polygon, ConstructionPlane lower, ConstructionPlane upper)
        {
            var target = Get(storey.Id, storey.Id, "ceiling-panel", profile.CeilingMaterialId);
            var proposed = new Solid(target.Id, storey.Id, "ceiling-panel", polygon, lower, upper, false);
            var previous = new List<Solid>();
            foreach (var solid in solids) { work.Debit(); if (!solid.Dynamic && BoundsOverlap(proposed, solid, E)) previous.Add(solid); }
            if (previous.Count == 0) { Append(target, storey.Id, "ceiling-panel", polygon, lower, upper); return; }
            var cuts = new List<ConstructionPlane>();
            foreach (var solid in previous) foreach (var a in new[] { lower, upper }) foreach (var b in new[] { solid.Lower, solid.Upper })
            { work.Debit(); cuts.Add(new(a.X - b.X, a.Y - b.Y, a.C - b.C)); }
            foreach (var cell in Partition(new[] { Polygon(polygon) }.Concat(previous.Select(s => Polygon(s.Polygon))), cuts, work))
            {
                var centre = Centre(cell); if (geometry.InRing(centre, polygon) < 0) continue;
                var blocked = false;
                foreach (var solid in previous)
                {
                    work.Debit(); if (geometry.InRing(centre, solid.Polygon) < 0) continue;
                    var overlap = false;
                    foreach (var point in cell)
                    {
                        work.Debit();
                        if (Math.Min(upper.At(point), solid.Upper.At(point)) - Math.Max(lower.At(point), solid.Lower.At(point)) > E) { overlap = true; break; }
                    }
                    if (!overlap) continue;
                    if (solid.Kind is "roof" or "floor-slab" or "landing" or "ceiling-panel")
                        throw new InvalidOperationException("Approved ceiling panel intersects structural geometry; provide an actual full-thickness gap: " + storey.Id + " / " + solid.Owner);
                    blocked = true;
                }
                if (!blocked) Append(target, storey.Id, "ceiling-panel", cell, lower, upper);
            }
        }

        private void Stairs(StairPlan stair)
        {
            var lowerStorey = plan.Storeys.Single(s => s.Id == stair.LowerStoreyId); var z = lowerStorey.Elevation; var d = Unit(stair.Start, stair.End); var n = Normal(d); var run = Distance(stair.Start, stair.End); var width = stair.Width / 2 + profile.RailingOffset + Math.Max(profile.RailThickness, profile.BalusterThickness);
            var slope = stair.RiserHeight / stair.TreadDepth; var pitch = new ConstructionPlane(d.X * slope, d.Y * slope, z - Dot(d, stair.Start) * slope);
            for (var i = 0; i < stair.RiserCount - 1; i++)
                Exclusive(Get(stair.Id, stair.LowerStoreyId, "stair-flight", stair.MaterialId), stair.LowerStoreyId, "stair-flight", Rectangle(stair.Start, d, i * stair.TreadDepth, (i + 1) * stair.TreadDepth, width, -width), pitch.Shift(-stair.StructureThickness), ConstructionPlane.Flat(z + (i + 1) * stair.RiserHeight));
            foreach (var side in new[] { -1, 1 })
            {
                var centre = side * (stair.Width / 2 + profile.RailingOffset + Math.Max(profile.RailThickness, profile.BalusterThickness) / 2); var rail = pitch.Shift(stair.RiserHeight + profile.RailingHeight);
                Exclusive(Get(stair.Id, stair.LowerStoreyId, "stair-rail", profile.RailingMaterialId, "-" + (side < 0 ? "right" : "left")), stair.LowerStoreyId, "stair-rail", Rectangle(stair.Start, d, 0, run, centre + profile.RailThickness / 2, centre - profile.RailThickness / 2), rail.Shift(-profile.RailThickness), rail);
                var count = Math.Max(2, (int)Math.Ceiling((run - profile.BalusterThickness) / (profile.BalusterGap + profile.BalusterThickness)) + 1); var step = (run - profile.BalusterThickness) / (count - 1);
                if (count > profile.MaxMeshes) throw new InvalidOperationException("Railing post count exceeds approved construction mesh bound.");
                for (var j = 0; j < count; j++)
                {
                    var along = profile.BalusterThickness / 2 + j * step; var a = Math.Max(0, along - profile.BalusterThickness / 2); var b = Math.Min(run, along + profile.BalusterThickness / 2);
                    for (var i = 0; i < stair.RiserCount - 1; i++)
                    {
                        var start = Math.Max(a, i * stair.TreadDepth); var end = Math.Min(b, (i + 1) * stair.TreadDepth); if (end - start <= E) continue;
                        Exclusive(Get(stair.Id, stair.LowerStoreyId, "stair-balusters", profile.RailingMaterialId, "-" + (side < 0 ? "right" : "left")), stair.LowerStoreyId, "stair-balusters", Rectangle(stair.Start, d, start, end, centre + profile.BalusterThickness / 2, centre - profile.BalusterThickness / 2), ConstructionPlane.Flat(z + (i + 1) * stair.RiserHeight), rail.Shift(-profile.RailThickness));
                    }
                }
            }
        }
        private void VoidRails(StoreyPlan storey)
        {
            foreach (var opening in storey.SlabOpenings.OrderBy(o => o.Id, StringComparer.Ordinal))
            foreach (var ring in new[] { opening.Boundary.Outer }.Concat(opening.Boundary.Holes))
            {
                Point2 OffsetJoin(int i, double offset)
                {
                    work.Debit(); var node = ring[i]; var previous = Unit(ring[(i + ring.Count - 1) % ring.Count], node); var next = Unit(node, ring[(i + 1) % ring.Count]);
                    var a = Add(node, Mul(Normal(previous), offset)); var b = Add(node, Mul(Normal(next), offset)); var denominator = Cross(previous, next);
                    if (Math.Abs(denominator) < 1e-14)
                    {
                        if (Dot(previous, next) > 0 && Distance(a, b) <= E) return Mul(Add(a, b), .5);
                        throw new InvalidOperationException("Void-railing offset joint cannot be certified at the declared precision.");
                    }
                    var result = Add(a, Mul(previous, Cross(Sub(b, a), next) / denominator));
                    if (!double.IsFinite(result.X) || !double.IsFinite(result.Y) || Math.Abs(result.X) > plan.Numerics.MaxCoordinateMagnitude + E || Math.Abs(result.Y) > plan.Numerics.MaxCoordinateMagnitude + E)
                        throw new InvalidOperationException("Derived construction coordinate exceeds the approved numerical magnitude at void-railing joint.");
                    return result;
                }
                List<Point2> OffsetRing(double offset)
                {
                    var result = Enumerable.Range(0, ring.Count).Select(i => OffsetJoin(i, offset)).ToList();
                    if (Math.Sign(Area(result)) != Math.Sign(Area(ring)) || Math.Abs(Area(result)) <= E * E) throw new InvalidOperationException("Approved void-railing offset consumes its source ring.");
                    for (var i = 0; i < result.Count; i++) for (var j = i + 1; j < result.Count; j++)
                    {
                        if (j == i + 1 || (i == 0 && j == result.Count - 1)) continue;
                        if (geometry.Intersects(result[i], result[(i + 1) % result.Count], result[j], result[(j + 1) % result.Count])) throw new InvalidOperationException("Approved void-railing offset self-intersects; no unsafe joint is emitted.");
                    }
                    return result;
                }
                var inner = OffsetRing(-profile.RailingOffset); var outer = OffsetRing(-profile.RailingOffset - profile.RailThickness); var middle = OffsetRing(-profile.RailingOffset - profile.RailThickness / 2);
                for (var edge = 0; edge < ring.Count; edge++)
                {
                    var a = ring[edge]; var b = ring[(edge + 1) % ring.Count]; var d = Unit(a, b); var offset = -profile.RailingOffset - profile.RailThickness / 2;
                    List<Point2> quad = [inner[edge], inner[(edge + 1) % ring.Count], outer[(edge + 1) % ring.Count], outer[edge]]; if (Area(quad) < 0) quad.Reverse();
                    var begin = Dot(Sub(middle[edge], a), d); var end = Dot(Sub(middle[(edge + 1) % ring.Count], a), d);
                    if (end - begin <= E || Dot(Sub(inner[(edge + 1) % ring.Count], inner[edge]), d) <= E || Dot(Sub(outer[(edge + 1) % ring.Count], outer[edge]), d) <= E)
                        throw new InvalidOperationException("Void-railing miter consumes an edge; change the explicit design/profile.");
                    for (var i = 0; i < quad.Count; i++)
                        if (Cross(Sub(quad[(i + 1) % quad.Count], quad[i]), Sub(quad[(i + 2) % quad.Count], quad[(i + 1) % quad.Count])) < -E)
                            throw new InvalidOperationException("Void-railing miter is not a valid convex edge section.");
                    var ranges = new List<(double A, double B)> { (double.NegativeInfinity, double.PositiveInfinity) };
                    foreach (var stair in plan.Stairs)
                    {
                        Point2? endpoint = stair.LowerStoreyId == storey.Id ? stair.Start : stair.UpperStoreyId == storey.Id ? stair.End : null; if (endpoint is null) continue;
                        var direction = Unit(stair.Start, stair.End); var normal = Normal(direction); var half = stair.Width / 2 + profile.RailingOffset + Math.Max(profile.RailThickness, profile.BalusterThickness);
                        var u = Add(endpoint.Value, Mul(normal, -half)); var v = Add(endpoint.Value, Mul(normal, half));
                        if (Math.Abs(Cross(d, Sub(u, a))) > E || Math.Abs(Cross(d, Sub(v, a))) > E) continue;
                        var x = Dot(Sub(u, a), d); var y = Dot(Sub(v, a), d); var low = Math.Min(x, y); var high = Math.Max(x, y); var next = new List<(double A, double B)>();
                        foreach (var range in ranges) { if (low > range.A + E) next.Add((range.A, Math.Min(range.B, low))); if (high < range.B - E) next.Add((Math.Max(range.A, high), range.B)); }
                        ranges = next.Where(r => r.B - r.A > E).ToList();
                    }
                    foreach (var range in ranges)
                    {
                        var cell = quad;
                        if (double.IsFinite(range.A)) cell = Clip(cell, new(d.X, d.Y, -Dot(d, a) - range.A), true, work);
                        if (double.IsFinite(range.B) && cell.Count != 0) cell = Clip(cell, new(d.X, d.Y, -Dot(d, a) - range.B), false, work);
                        if (cell.Count == 0) continue;
                        var mesh = Get(opening.Id, storey.Id, "void-railing", profile.RailingMaterialId);
                        Exclusive(mesh, storey.Id, "void-railing", cell, ConstructionPlane.Flat(storey.Elevation + profile.RailingHeight - profile.RailThickness), ConstructionPlane.Flat(storey.Elevation + profile.RailingHeight));
                        var first = Math.Max(begin, range.A) + profile.BalusterThickness / 2; var last = Math.Min(end, range.B) - profile.BalusterThickness / 2; if (last < first) continue;
                        var count = Math.Max(1, (int)Math.Ceiling((last - first) / (profile.BalusterGap + profile.BalusterThickness)) + 1);
                        if (count > profile.MaxArrangementCells) throw new InvalidOperationException("Void-railing post budget exceeded.");
                        for (var i = 0; i < count; i++)
                        {
                            var t = count == 1 ? (first + last) / 2 : first + i * (last - first) / (count - 1);
                            var post = Rectangle(a, d, t - profile.BalusterThickness / 2, t + profile.BalusterThickness / 2, offset + profile.BalusterThickness / 2, offset - profile.BalusterThickness / 2);
                            foreach (var boundary in Edges(cell)) { post = Clip(post, Line(boundary.A, boundary.B), true, work); if (post.Count == 0) break; }
                            if (post.Count != 0) Exclusive(mesh, storey.Id, "void-railing", post, ConstructionPlane.Flat(storey.Elevation), ConstructionPlane.Flat(storey.Elevation + profile.RailingHeight - profile.RailThickness));
                        }
                    }
                }
            }
        }
        private void Fascias()
        {
            foreach (var roof in plan.Roofs.OrderBy(r => r.Id, StringComparer.Ordinal))
            {
                var detail = plan.DetailProfiles.Single(d => d.Id == roof.DetailProfileId); if (detail.FasciaDepth <= E) continue; var plane = Plane(roof.Vertices); var storey = RoofStorey(roof); var shape = Polygon(roof.Vertices.Select(XY).ToList());
                foreach (var (a, b) in ExposedRoofEdges(roof))
                {
                    var band = Polygon(Rectangle(a, Unit(a, b), 0, Distance(a, b), profile.RoofThickness, 0));
                    foreach (var cell in Partition([shape, band], [], work))
                        if (geometry.InPolygon(Centre(cell), shape) >= 0 && geometry.InPolygon(Centre(cell), band) >= 0)
                            Exclusive(Get(roof.Id, storey.Id, "roof-fascia", roof.MaterialId), storey.Id, "roof-fascia", cell, plane.Shift(-detail.FasciaDepth), plane);
                }
            }
        }
        private IEnumerable<(Point2 A, Point2 B)> ExposedRoofEdges(RoofPlane roof)
        {
            var plane = Plane(roof.Vertices);
            foreach (var (a, b) in Edges(roof.Vertices.Select(XY).ToList()))
            {
                var direction = Unit(a, b); var length = Distance(a, b); var ranges = new List<(double A, double B)> { (0, length) };
                foreach (var other in plan.Roofs.Where(r => r.Id != roof.Id))
                {
                    var otherPlane = Plane(other.Vertices);
                    foreach (var (c, d) in Edges(other.Vertices.Select(XY).ToList()))
                    {
                        work.Debit(); if (Math.Abs(Cross(direction, Sub(c, a))) > E || Math.Abs(Cross(direction, Sub(d, a))) > E) continue;
                        var u = Dot(Sub(c, a), direction); var v = Dot(Sub(d, a), direction); var lo = Math.Max(0, Math.Min(u, v)); var hi = Math.Min(length, Math.Max(u, v));
                        if (hi - lo <= E) continue;
                        var first = Add(a, Mul(direction, lo)); var last = Add(a, Mul(direction, hi));
                        if (Math.Abs(plane.At(first) - otherPlane.At(first)) > E || Math.Abs(plane.At(last) - otherPlane.At(last)) > E) continue;
                        var next = new List<(double A, double B)>();
                        foreach (var range in ranges)
                        {
                            if (lo > range.A + E) next.Add((range.A, Math.Min(lo, range.B)));
                            if (hi < range.B - E) next.Add((Math.Max(hi, range.A), range.B));
                        }
                        ranges = next.Where(x => x.B - x.A > E).ToList();
                    }
                }
                foreach (var range in ranges) yield return (Add(a, Mul(direction, range.A)), Add(a, Mul(direction, range.B)));
            }
        }

        // Clearance routines below use the actual compiled closed cells. Floors/voids,
        // trim and fully-open door leaves all participate; no graph-only admission.
        private ConstructionClearanceEvidence Clearances() => MeasureClearances();

        private ConstructionClearanceEvidence MeasureClearances()
        {
            var sweeps = new List<ConstructionDoorSweepEvidence>();
            foreach (var door in doors)
            {
                var radius = door.Polygon.Max(p => Distance(p, door.Pivot));
                var delta = 2 * Math.Acos(Math.Clamp(1 - E / Math.Max(radius, E), -1, 1));
                var count = Math.Max(1, (int)Math.Ceiling(Math.Abs(door.Angle) / Math.Max(delta, 1e-12)));
                if (count > profile.MaxArrangementCells) throw new InvalidOperationException("Door sweep cannot be certified within the approved arrangement budget.");
                var candidates = new List<List<Point2>>();
                foreach (var solid in solids)
                {
                    work.Debit(); if (solid.Dynamic) continue;
                    var min = solid.Minimum; var max = solid.Maximum;
                    if (min.Z >= door.Upper - E || max.Z <= door.Lower + E || min.X > door.Pivot.X + radius || max.X < door.Pivot.X - radius || min.Y > door.Pivot.Y + radius || max.Y < door.Pivot.Y - radius) continue;
                    var clipped = Project(solid, door.Lower, door.Upper); if (clipped.Count != 0) candidates.Add(clipped);
                }
                var minimum = double.PositiveInfinity; var boundPoints = new List<Point2>();
                for (var i = 0; i < count; i++)
                {
                    var a = door.Angle * i / count; var b = door.Angle * (i + 1) / count;
                    var endpointVertices = door.Polygon.Select(p => Rotate(p, door.Pivot, a)).Concat(door.Polygon.Select(p => Rotate(p, door.Pivot, b))).ToList();
                    var envelope = Hull(endpointVertices, work); var error = radius * (1 - Math.Cos(Math.Abs(b - a) / 2));
                    boundPoints.AddRange(endpointVertices);
                    foreach (var obstacle in candidates)
                    {
                        work.Debit(); var bound = BoundsDistance(envelope, obstacle);
                        var clearance = (bound > error + E ? bound : PolygonDistance(envelope, obstacle, work)) - error;
                        minimum = Math.Min(minimum, clearance);
                        if (clearance < E) throw new InvalidOperationException("Door swept leaf intersects or cannot clear compiled static architecture: " + door.Opening.Id);
                    }
                }
                sweeps.Add(new() { OpeningId = door.Opening.Id, AnchorId = door.Anchor, AngleDegrees = door.Angle * 180 / Math.PI, MinimumClearance = double.IsFinite(minimum) ? minimum : 0, BoundsMinimum = new(boundPoints.Min(p => p.X) - E, boundPoints.Min(p => p.Y) - E, door.Lower), BoundsMaximum = new(boundPoints.Max(p => p.X) + E, boundPoints.Max(p => p.Y) + E, door.Upper) });
            }
            var routes = new List<ConstructionRouteEvidence>();
            foreach (var storey in plan.Storeys.OrderBy(s => s.Elevation).ThenBy(s => s.Id, StringComparer.Ordinal))
            foreach (var room in storey.Rooms.OrderBy(r => r.Id, StringComparer.Ordinal))
            {
                pointClearanceCache.Clear(); segmentClearanceCache.Clear();
                var obstacles = Obstacles(storey.Elevation, plan.Player.Height);
                foreach (var opening in storey.SlabOpenings)
                    foreach (var cell in Partition([opening.Boundary], [], work))
                        if (geometry.InPolygon(Centre(cell), opening.Boundary) >= 0) { obstacles.Add(cell); obstacleOwners[cell] = "void-" + opening.Id; }
                obstacles = obstacles.Where(p => BoundsDistance(room.Boundary.Outer, p) <= E && (geometry.InPolygon(Centre(p), room.Boundary) > 0 || geometry.Overlap(room.Boundary, Polygon(p)))).ToList();
                var portals = new List<(string Id, Point2 Point)>();
                foreach (var wall in storey.Walls)
                foreach (var opening in wall.Openings.Where(o => o.Kind != OpeningKind.Window))
                {
                    var side = wall.LeftRoomId == room.Id ? 1 : wall.RightRoomId == room.Id ? -1 : 0; if (side == 0) continue;
                    var d = Unit(wall.Start, wall.End); var detail = plan.DetailProfiles.Single(p => p.Id == wall.DetailProfileId);
                    var offset = wall.Thickness / 2 + plan.Player.Radius + Math.Max(Math.Max(detail.PlinthProjection, detail.CorniceProjection), Math.Max(profile.DoorFrameDepth, profile.WindowFrameDepth)) + E * 4;
                    portals.Add((opening.Id, Add(Add(wall.Start, Mul(d, opening.Offset + opening.Width / 2)), Mul(Normal(d), side * offset))));
                }
                foreach (var stair in plan.Stairs)
                {
                    if (stair.LowerStoreyId == storey.Id && stair.LowerRoomId == room.Id) portals.Add((stair.Id + "-lower-landing", LandingPoint(stair.Id + "-lower-landing", stair.LowerLanding)));
                    if (stair.UpperStoreyId == storey.Id && stair.UpperRoomId == room.Id) portals.Add((stair.Id + "-upper-landing", LandingPoint(stair.Id + "-upper-landing", stair.UpperLanding)));
                }
                portals = portals.OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
                if (portals.Count == 0 && !room.RequiresAccess) continue;
                if (portals.Count == 0) throw new InvalidOperationException("Required room has no measured geometric access portal: " + room.Id);
                var nodes = CandidateNodes(room.Boundary, obstacles, portals.Select(p => p.Point));
                foreach (var portal in portals)
                    if (PointClearance(room.Boundary, obstacles, portal.Point) < -E) throw new InvalidOperationException("Compiled detail blocks the required portal approach: " + portal.Id);
                if (nodes.Count == 0) throw new InvalidOperationException("Required room has no certified player-clearance interior: " + room.Id);
                var hub = nodes.OrderByDescending(p => PointClearance(room.Boundary, obstacles, p)).ThenBy(p => p.X).ThenBy(p => p.Y).First();
                var paths = new List<(string Id, List<Point2> Points, double Clearance)>();
                foreach (var portal in portals)
                {
                    var path = FindPath(room.Boundary, obstacles, hub, portal.Point, nodes); var clearance = PathClearance(room.Boundary, obstacles, path);
                    paths.Add((portal.Id, path, clearance));
                    routes.Add(new() { RoomId = room.Id, FromId = "room-hub-" + room.Id, ToId = portal.Id, Points = path.Select(p => XYZ(p, storey.Elevation)).ToList(), Radius = plan.Player.Radius, Height = plan.Player.Height, MinimumClearance = clearance });
                }
                for (var i = 0; i < paths.Count; i++) for (var j = i + 1; j < paths.Count; j++)
                {
                    work.Debit(); if (routes.Count >= profile.MaxArrangementCells) throw new InvalidOperationException("Portal route-evidence budget exceeded.");
                    var combined = paths[i].Points.AsEnumerable().Reverse().Concat(paths[j].Points.Skip(1)).ToList();
                    routes.Add(new() { RoomId = room.Id, FromId = paths[i].Id, ToId = paths[j].Id, Points = combined.Select(p => XYZ(p, storey.Elevation)).ToList(), Radius = plan.Player.Radius, Height = plan.Player.Height, MinimumClearance = Math.Min(paths[i].Clearance, paths[j].Clearance) });
                }
            }
            // Prove the local crossing itself as well as each room's interior route.
            // An exterior endpoint extends only beyond this wall's approved attached
            // eave/frame/trim extent, never to an unrelated global building boundary.
            foreach (var storey in plan.Storeys.OrderBy(s => s.Elevation).ThenBy(s => s.Id, StringComparer.Ordinal))
            foreach (var wall in storey.Walls.OrderBy(w => w.Id, StringComparer.Ordinal))
            foreach (var opening in wall.Openings.Where(o => o.Kind != OpeningKind.Window).OrderBy(o => o.Id, StringComparer.Ordinal))
            {
                work.Debit(); var detail = plan.DetailProfiles.Single(d => d.Id == wall.DetailProfileId); var direction = Unit(wall.Start, wall.End); var normal = Normal(direction);
                var attached = Math.Max(Math.Max(detail.PlinthProjection, detail.CorniceProjection), Math.Max(profile.DoorFrameDepth, profile.WindowFrameDepth));
                var centre = Add(wall.Start, Mul(direction, opening.Offset + opening.Width / 2));
                double Offset(bool exterior, Point2 outward)
                {
                    var extent = wall.Thickness / 2 + attached;
                    if (exterior) extent = Math.Max(extent, ConnectedRoofExtent(centre, outward, wall.Thickness / 2));
                    return extent + plan.Player.Radius + E * 4;
                }
                var a = Add(centre, Mul(normal, Offset(wall.LeftRoomId is null, normal))); var b = Sub(centre, Mul(normal, Offset(wall.RightRoomId is null, Mul(normal, -1))));
                var foot = storey.Elevation + opening.Sill; var clearance = MeasureBodyClearance(a, b, foot, plan.Player.Height, "opening approach: " + opening.Id);
                var points = new List<Point3> { XYZ(a, storey.Elevation) };
                if (opening.Sill > E)
                {
                    clearance = Math.Min(clearance, MeasureBodyClearance(a, a, storey.Elevation, plan.Player.Height + opening.Sill, "opening approach lift: " + opening.Id));
                    clearance = Math.Min(clearance, MeasureBodyClearance(b, b, storey.Elevation, plan.Player.Height + opening.Sill, "opening approach lift: " + opening.Id));
                }
                points.Add(XYZ(a, foot)); points.Add(XYZ(b, foot)); points.Add(XYZ(b, storey.Elevation));
                if (routes.Count >= profile.MaxArrangementCells) throw new InvalidOperationException("Portal route-evidence budget exceeded.");
                routes.Add(new()
                {
                    RoomId = wall.LeftRoomId ?? wall.RightRoomId!, FromId = wall.LeftRoomId is null ? "exterior-approach-" + opening.Id : wall.LeftRoomId + "-approach-" + opening.Id,
                    ToId = wall.RightRoomId is null ? "exterior-approach-" + opening.Id : wall.RightRoomId + "-approach-" + opening.Id,
                    Points = points, Radius = plan.Player.Radius, Height = plan.Player.Height, MinimumClearance = double.IsFinite(clearance) ? clearance : 0
                });
            }
            foreach (var stair in plan.Stairs.OrderBy(s => s.Id, StringComparer.Ordinal))
            {
                var lower = plan.Storeys.Single(s => s.Id == stair.LowerStoreyId); var upper = plan.Storeys.Single(s => s.Id == stair.UpperStoreyId); var direction = Unit(stair.Start, stair.End); var run = Distance(stair.Start, stair.End); var radius = plan.Player.Radius;
                var ownFlight = "stair-flight-" + stair.Id;
                bool LandingSupport(Solid s, StoreyPlan floor, Polygon2 boundary)
                {
                    return s.Storey == floor.Id && (s.Kind is "landing" or "floor-slab") && Math.Abs(s.Upper.X) <= E && Math.Abs(s.Upper.Y) <= E && Math.Abs(s.Upper.C - floor.Elevation) <= E && geometry.InPolygon(Centre(s.Polygon), boundary) >= 0;
                }
                var support = solids.Where(s => !s.Dynamic && (s.Owner == ownFlight || LandingSupport(s, lower, stair.LowerLanding) || LandingSupport(s, upper, stair.UpperLanding))).ToList();
                double SupportHeight(Point2 p)
                {
                    var height = double.NegativeInfinity;
                    foreach (var s in support)
                    {
                        work.Debit(); if (BoundsDistance([p], s.Polygon) >= radius - E) continue;
                        var distance = geometry.InRing(p, s.Polygon) >= 0 ? 0 : Edges(s.Polygon).Min(e => PointSegmentDistance(p, e.A, e.B));
                        if (distance >= radius - E) continue;
                        if (Math.Abs(s.Upper.X) > E || Math.Abs(s.Upper.Y) > E) throw new InvalidOperationException("Declared stair support is not a measured horizontal tread or landing: " + stair.Id);
                        height = Math.Max(height, s.Upper.C);
                    }
                    if (!double.IsFinite(height)) throw new InvalidOperationException("Stair corridor has no actual declared tread/landing support: " + stair.Id);
                    return height;
                }
                var lowerPoint = LandingPoint(stair.Id + "-lower-landing", stair.LowerLanding); var upperPoint = LandingPoint(stair.Id + "-upper-landing", stair.UpperLanding); var points = new List<Point3> { XYZ(lowerPoint, lower.Elevation) }; var clearance = double.PositiveInfinity;
                void BodyClearance(Point2 a, Point2 b, double foot, double height, string context)
                {
                    clearance = Math.Min(clearance, MeasureBodyClearance(a, b, foot, height, "stair " + context + ": " + stair.Id, ownFlight));
                }
                void Corridor(Point2 a, Point2 b, double foot)
                {
                    BodyClearance(a, b, foot, plan.Player.Height, "player clearance");
                    points.Add(XYZ(a, foot)); points.Add(XYZ(b, foot));
                }
                // A finite-radius cylinder begins stepping when its leading edge reaches
                // the next declared tread. The standing body is conservatively measured
                // above the highest ACTUAL own tread/landing beneath that footprint.
                // Walls, trim, rails and unrelated floors can never become step support.
                var events = Enumerable.Range(0, stair.RiserCount).Select(i => i * stair.TreadDepth - radius).Append(run + radius).ToArray();
                var first = Add(stair.Start, Mul(direction, events[0]));
                void Approach(Polygon2 landing, Point2 a, Point2 b, double foot)
                {
                    pointClearanceCache.Clear(); segmentClearanceCache.Clear();
                    var obstacles = Obstacles(foot, plan.Player.Height, ownFlight);
                    var path = SegmentClearance(landing, obstacles, a, b) >= -E ? new List<Point2> { a, b } : FindPath(landing, obstacles, a, b, CandidateNodes(landing, obstacles, [a, b]));
                    for (var i = 1; i < path.Count; i++) Corridor(path[i - 1], path[i], foot);
                }
                Approach(stair.LowerLanding, lowerPoint, first, lower.Elevation);
                var previousHeight = lower.Elevation;
                for (var i = 1; i < events.Length; i++)
                {
                    var a = Add(stair.Start, Mul(direction, events[i - 1])); var b = Add(stair.Start, Mul(direction, events[i]));
                    var height = SupportHeight(Mul(Add(a, b), .5));
                    if (height < previousHeight - E || height - previousHeight > plan.Player.MaxStepHeight + E)
                        throw new InvalidOperationException("Actual declared stair-support rise exceeds the approved player maximum step: " + stair.Id);
                    if (height > previousHeight + E)
                    {
                        // At this fixed-XY event the actor rises onto the next admitted
                        // support. Standing endpoints alone leave a gap when the actor
                        // is shorter than the riser; certify their full swept-body union.
                        // Only the existing own-flight support exception applies: floor
                        // slabs, landings, walls, trim and rails remain measured obstacles.
                        BodyClearance(a, a, previousHeight, plan.Player.Height + height - previousHeight, "support lift");
                        points.Add(XYZ(a, previousHeight));
                    }
                    Corridor(a, b, height); previousHeight = height;
                }
                if (Math.Abs(previousHeight - upper.Elevation) > E) throw new InvalidOperationException("Actual stair support corridor does not meet the declared upper floor: " + stair.Id);
                var last = Add(stair.Start, Mul(direction, events[^1]));
                Approach(stair.UpperLanding, last, upperPoint, upper.Elevation);
                routes.Add(new() { RoomId = stair.LowerRoomId, FromId = stair.Id + "-lower-landing", ToId = stair.Id + "-upper-landing", Points = points, Radius = plan.Player.Radius, Height = plan.Player.Height, MinimumClearance = double.IsFinite(clearance) ? clearance : 0 });
            }
            return new() { Routes = routes, DoorSweeps = sweeps, MinimumRouteClearance = routes.Count == 0 ? 0 : routes.Min(r => r.MinimumClearance) };
        }

        // Follow only the connected source-roof projection attached to this wall.
        // Separate roof profiles and off-boundary aprons therefore use their actual
        // edge, while a gap to another courtyard wing terminates the local approach.
        private double ConnectedRoofExtent(Point2 origin, Point2 outward, double wallFace)
        {
            var intervals = new List<(double Start, double End)>();
            foreach (var roof in plan.Roofs)
            {
                var ring = roof.Vertices.Select(XY).ToList(); var cuts = new List<double> { 0 };
                foreach (var (a, b) in Edges(ring))
                {
                    work.Debit(); var edge = Sub(b, a); var delta = Sub(a, origin); var denominator = Cross(outward, edge);
                    if (Math.Abs(denominator) > E)
                    {
                        var t = Cross(delta, edge) / denominator; var u = Cross(delta, outward) / denominator;
                        if (t >= -E && u >= -E && u <= 1 + E) cuts.Add(Math.Max(0, t));
                    }
                    else if (Math.Abs(Cross(delta, outward)) <= E)
                    {
                        foreach (var point in new[] { a, b }) { var t = Dot(Sub(point, origin), outward); if (t >= -E) cuts.Add(Math.Max(0, t)); }
                    }
                }
                var sorted = cuts.Order().ToList(); var unique = new List<double>();
                foreach (var t in sorted) { work.Debit(); if (unique.Count == 0 || t - unique[^1] > E) unique.Add(t); }
                for (var i = 1; i < unique.Count; i++)
                {
                    work.Debit(); var a = unique[i - 1]; var b = unique[i];
                    if (b - a > E && geometry.InRing(Add(origin, Mul(outward, (a + b) / 2)), ring) >= 0) intervals.Add((a, b));
                }
            }
            var extent = wallFace;
            foreach (var interval in intervals.OrderBy(i => i.Start).ThenBy(i => i.End))
            {
                work.Debit(); if (interval.Start > extent + E) break;
                extent = Math.Max(extent, interval.End);
            }
            return extent;
        }

        private double MeasureBodyClearance(Point2 a, Point2 b, double foot, double height, string context, string? excludedOwner = null)
        {
            var path = new List<Point2> { a, b }; var radius = plan.Player.Radius; var minimum = double.PositiveInfinity;
            void Check(List<Point2> polygon, string owner)
            {
                if (polygon.Count == 0) return;
                var bound = BoundsDistance(path, polygon);
                var distance = (bound >= radius ? bound : SegmentPolygonDistance(a, b, polygon)) - radius;
                minimum = Math.Min(minimum, distance);
                if (distance < -E) throw new InvalidOperationException($"Compiled solids violate declared {context}; obstacle={owner}; foot={foot:R}; height={height:R}; separation={distance:R}");
            }
            foreach (var solid in solids)
            {
                work.Debit(); if (solid.Dynamic || solid.Owner == excludedOwner || solid.Maximum.Z <= foot + E || solid.Minimum.Z >= foot + height - E) continue;
                // The unprojected XY bounds contain every vertically clipped point.
                // A distant bound therefore supplies a conservative safe margin and
                // cannot hide a blocker. Only nearby cells need exact Z projection.
                var bound = BoundsDistance(path, solid.Polygon);
                if (bound >= radius) { minimum = Math.Min(minimum, bound - radius); continue; }
                Check(Project(solid, foot + E * 2, foot + height - E * 2), solid.Owner);
            }
            foreach (var door in doors)
            {
                work.Debit(); if (door.Upper <= foot + E || door.Lower >= foot + height - E) continue;
                Check(door.Polygon.Select(p => Rotate(p, door.Pivot, door.Angle)).ToList(), "open-door-" + door.Opening.Id);
            }
            return minimum;
        }

        private List<Point2> Project(Solid solid, double lower, double upper)
        {
            var polygon = Clip(solid.Polygon, new(solid.Upper.X, solid.Upper.Y, solid.Upper.C - lower), true, work);
            return polygon.Count == 0 ? [] : Clip(polygon, new(solid.Lower.X, solid.Lower.Y, solid.Lower.C - upper), false, work);
        }
        private Point2 LandingPoint(string id, Polygon2 polygon)
        {
            if (landingPoints.TryGetValue(id, out var saved)) return saved;
            pointClearanceCache.Clear(); segmentClearanceCache.Clear();
            var candidates = CandidateNodes(polygon, [], []);
            if (candidates.Count == 0) throw new InvalidOperationException("Landing has no certified player-clearance standing point: " + id);
            var point = candidates.OrderByDescending(p => PointClearance(polygon, [], p)).ThenBy(p => p.X).ThenBy(p => p.Y).First();
            pointClearanceCache.Clear(); segmentClearanceCache.Clear(); landingPoints[id] = point; return point;
        }
        private List<List<Point2>> Obstacles(double foot, double height, string? excludedOwner = null)
        {
            obstacleOwners.Clear(); var result = new List<List<Point2>>(); var identities = new HashSet<string>(StringComparer.Ordinal);
            void Add(List<Point2> polygon, string owner)
            {
                if (polygon.Count == 0) return;
                var key = string.Join(";", polygon.Select(p => $"{Math.Round(p.X / E)}:{Math.Round(p.Y / E)}"));
                if (identities.Add(key)) { result.Add(polygon); obstacleOwners[polygon] = owner; }
            }
            foreach (var solid in solids)
            {
                work.Debit(); if (solid.Dynamic || solid.Owner == excludedOwner) continue;
                if (solid.Maximum.Z <= foot + E || solid.Minimum.Z >= foot + height - E) continue;
                Add(Project(solid, foot + E * 2, foot + height - E * 2), solid.Owner);
            }
            foreach (var door in doors)
                if (door.Upper > foot + E && door.Lower < foot + height - E) Add(door.Polygon.Select(p => Rotate(p, door.Pivot, door.Angle)).ToList(), "open-door-" + door.Opening.Id);
            return result;
        }
        private static double BoundsDistance(IReadOnlyList<Point2> a, IReadOnlyList<Point2> b)
        {
            var dx = Math.Max(0, Math.Max(a.Min(p => p.X) - b.Max(p => p.X), b.Min(p => p.X) - a.Max(p => p.X)));
            var dy = Math.Max(0, Math.Max(a.Min(p => p.Y) - b.Max(p => p.Y), b.Min(p => p.Y) - a.Max(p => p.Y)));
            return Math.Sqrt(dx * dx + dy * dy);
        }
        private double SegmentPolygonDistance(Point2 a, Point2 b, List<Point2> obstacle)
        {
            if (geometry.InRing(a, obstacle) >= 0 || geometry.InRing(b, obstacle) >= 0) return 0;
            var min = double.PositiveInfinity;
            foreach (var (c, d) in Edges(obstacle)) min = Math.Min(min, SegmentDistance(a, b, c, d, work));
            return min;
        }
        private double PointClearance(Polygon2 room, List<List<Point2>> obstacles, Point2 p)
        {
            work.Debit(); if (pointClearanceCache.TryGetValue(p, out var cached)) return cached;
            if (geometry.InPolygon(p, room) < 0) return double.NegativeInfinity;
            var min = double.PositiveInfinity;
            foreach (var (a, b) in Edges(room)) { work.Debit(); min = Math.Min(min, PointSegmentDistance(p, a, b)); }
            foreach (var obstacle in obstacles)
            {
                work.Debit(); if (BoundsDistance([p], obstacle) >= min) continue;
                if (geometry.InRing(p, obstacle) >= 0) return -plan.Player.Radius;
                foreach (var (a, b) in Edges(obstacle)) { work.Debit(); min = Math.Min(min, PointSegmentDistance(p, a, b)); }
            }
            var result = min - plan.Player.Radius;
            if (pointClearanceCache.Count < profile.MaxArrangementCells) pointClearanceCache[p] = result;
            return result;
        }
        private double SegmentClearance(Polygon2 room, List<List<Point2>> obstacles, Point2 a, Point2 b)
        {
            work.Debit(); var key = a.X < b.X || (a.X == b.X && a.Y <= b.Y) ? (a, b) : (b, a);
            if (segmentClearanceCache.TryGetValue(key, out var cached)) return cached;
            if (!geometry.ContainsSegment(room, a, b)) return double.NegativeInfinity;
            var min = double.PositiveInfinity;
            foreach (var (c, d) in Edges(room)) min = Math.Min(min, SegmentDistance(a, b, c, d, work));
            foreach (var obstacle in obstacles) { work.Debit(); if (BoundsDistance([a, b], obstacle) < min) min = Math.Min(min, SegmentPolygonDistance(a, b, obstacle)); }
            var result = min - plan.Player.Radius;
            if (segmentClearanceCache.Count < profile.MaxArrangementCells) segmentClearanceCache[key] = result;
            return result;
        }
        private double PathClearance(Polygon2 room, List<List<Point2>> obstacles, List<Point2> points)
        {
            var min = double.PositiveInfinity;
            for (var i = 1; i < points.Count; i++) min = Math.Min(min, SegmentClearance(room, obstacles, points[i - 1], points[i]));
            if (!double.IsFinite(min) || min < -E) throw new InvalidOperationException("A construction path lacks measured geometric clearance.");
            return min;
        }
        private List<Point2> CandidateNodes(Polygon2 room, List<List<Point2>> obstacles, IEnumerable<Point2> portals)
        {
            var candidates = new List<Point2>(portals); var margin = plan.Player.Radius + E * 8;
            void Candidate(Point2 p)
            {
                work.Debit(); if (candidates.Count >= profile.MaxArrangementCells) throw new InvalidOperationException("Construction path-candidate budget exhausted."); candidates.Add(p);
            }
            void Ring(IReadOnlyList<Point2> ring, bool left)
            {
                for (var i = 0; i < ring.Count; i++)
                {
                    work.Debit(); var previous = ring[(i + ring.Count - 1) % ring.Count]; var p = ring[i]; var next = ring[(i + 1) % ring.Count];
                    var a = Mul(Normal(Unit(previous, p)), left ? 1 : -1); var b = Mul(Normal(Unit(p, next)), left ? 1 : -1); var bisector = Add(a, b); var length = Length(bisector);
                    if (length > E)
                    {
                        bisector = Mul(bisector, 1 / length); var divisor = Dot(bisector, a);
                        if (divisor > E) Candidate(Add(p, Mul(bisector, margin / divisor)));
                    }
                    for (var j = 0; j < 8; j++) Candidate(Add(p, new(margin * Math.Cos(j * Math.PI / 4), margin * Math.Sin(j * Math.PI / 4))));
                }
            }
            Ring(room.Outer, true); foreach (var hole in room.Holes) Ring(hole, true);
            // Hulls only propose roadmap nodes. Every admitted point/segment is still
            // checked against the exact original compiled cells; no obstacle is replaced.
            foreach (var group in obstacles.GroupBy(p => obstacleOwners.GetValueOrDefault(p, "unowned"))) Ring(Hull(group.SelectMany(p => p), work), false);
            var minx = room.Outer.Min(p => p.X); var maxx = room.Outer.Max(p => p.X); var miny = room.Outer.Min(p => p.Y); var maxy = room.Outer.Max(p => p.Y);
            for (var x = 1; x <= 3; x++) for (var y = 1; y <= 3; y++) Candidate(new(minx + (maxx - minx) * x / 4, miny + (maxy - miny) * y / 4));
            var result = new List<Point2>(); var seen = new HashSet<(long X, long Y)>();
            foreach (var p in candidates)
            {
                work.Debit(); if (result.Count >= profile.MaxArrangementCells) throw new InvalidOperationException("Construction path-node budget exhausted.");
                var key = ((long)Math.Round(p.X / E), (long)Math.Round(p.Y / E));
                if (seen.Add(key) && PointClearance(room, obstacles, p) >= -E) result.Add(p);
            }
            return result;
        }
        private List<Point2> FindPath(Polygon2 room, List<List<Point2>> obstacles, Point2 start, Point2 end, List<Point2> candidates)
        {
            if (SegmentClearance(room, obstacles, start, end) >= -E) return [start, end];
            var nodes = new List<Point2> { start, end }; nodes.AddRange(candidates.Where(p => Distance(p, start) > E && Distance(p, end) > E));
            var costs = Enumerable.Repeat(double.PositiveInfinity, nodes.Count).ToArray(); var parent = Enumerable.Repeat(-1, nodes.Count).ToArray(); var closed = new bool[nodes.Count]; var queue = new PriorityQueue<int, (double Cost, int Index)>(); costs[0] = 0; queue.Enqueue(0, (0, 0));
            while (queue.TryDequeue(out var current, out _))
            {
                work.Debit(); if (closed[current]) continue; closed[current] = true;
                if (current == 1)
                {
                    var path = new List<Point2>(); for (var i = current; i >= 0; i = parent[i]) path.Add(nodes[i]); path.Reverse(); return path;
                }
                for (var i = 0; i < nodes.Count; i++)
                {
                    work.Debit(); if (closed[i]) continue; var cost = costs[current] + Distance(nodes[current], nodes[i]);
                    if (cost >= costs[i] || SegmentClearance(room, obstacles, nodes[current], nodes[i]) < -E) continue;
                    costs[i] = cost; parent[i] = current; queue.Enqueue(i, (cost + Distance(nodes[i], end), i));
                }
            }
            throw new InvalidOperationException("No player-clearance path could be certified in the actual compiled room within the approved search/work bounds.");
        }
    }
}
