using System.Text.Json;
using Bubo.FloorPlan.Domain;
using Bubo.FloorPlan.Infrastructure;

namespace Bubo.FloorPlan.Tests;

public static class ConstructionGeometryTests
{
    public static void Run()
    {
        foreach (var (name, factory) in new (string, Func<BuildingPlan>)[] { ("single", ConstructionPlanExamples.SingleStorey), ("multi", ConstructionPlanExamples.MultiStorey), ("courtyard", ConstructionPlanExamples.AngledCourtyard), ("stacked", ConstructionPlanExamples.StackedThreeStorey) })
            Check.Case("construction geometry: closed measured architecture " + name, () =>
            {
                var plan = factory(); var scene = new ConstructionGeometryCompiler().Compile(plan);
                Check.True(scene.Meshes.Count == scene.Metrics.MeshCount && scene.Metrics.TriangleCount <= plan.Budgets.MaxGeneratedTriangles);
                Check.True(scene.Meshes.Any(m => m.Kind == "floor-slab") && scene.Meshes.Any(m => m.Kind == "wall") && scene.Meshes.Any(m => m.Kind == "roof"));
                Check.True(scene.Clearance.Routes.Count != 0 && scene.Clearance.DoorSweeps.Count == plan.Storeys.SelectMany(s => s.Walls).SelectMany(w => w.Openings).Count(o => o.Kind == OpeningKind.Door));
                Check.True(scene.Clearance.MinimumRouteClearance >= -plan.Numerics.LinearTolerance);
                Check.True(scene.Clearance.Routes.Any(r => r.FromId.StartsWith("exterior-approach-", StringComparison.Ordinal) || r.ToId.StartsWith("exterior-approach-", StringComparison.Ordinal)), "Local exterior opening crossing was not measured");
                foreach (var mesh in scene.Meshes)
                {
                    Check.True(mesh.Vertices.All(v => double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z)));
                    Check.True(mesh.Triangles.Count % 3 == 0 && mesh.Triangles.All(i => i >= 0 && i < mesh.Vertices.Count));
                    var edges = new Dictionary<(int, int), (int Count, int Winding)>();
                    double volume = 0;
                    for (var i = 0; i < mesh.Triangles.Count; i += 3)
                    {
                        var a = mesh.Triangles[i]; var b = mesh.Triangles[i + 1]; var c = mesh.Triangles[i + 2];
                        foreach (var (u, v) in new[] { (a, b), (b, c), (c, a) })
                        { var key = (Math.Min(u, v), Math.Max(u, v)); var old = edges.GetValueOrDefault(key); edges[key] = (old.Count + 1, old.Winding + (u < v ? 1 : -1)); }
                        var p = mesh.Vertices[a]; var q = mesh.Vertices[b]; var r = mesh.Vertices[c];
                        volume += (p.X * (q.Y * r.Z - q.Z * r.Y) + p.Y * (q.Z * r.X - q.X * r.Z) + p.Z * (q.X * r.Y - q.Y * r.X)) / 6;
                    }
                    Check.True(edges.Values.All(e => e.Count == 2 && e.Winding == 0), "Unclosed/nonmanifold mesh " + mesh.Id);
                    Check.True(volume > 0, "Nonpositive solid volume " + mesh.Id);
                }
                foreach (var visual in scene.Meshes.Where(m => m.Representation == "visual"))
                {
                    var collision = scene.Meshes.Single(m => m.Id == "collision-" + visual.Id[7..]);
                    Check.True(visual.Vertices.SequenceEqual(collision.Vertices) && visual.Triangles.SequenceEqual(collision.Triangles), "Collision silently replaced architectural geometry");
                }
                if (plan.Stairs.Count != 0)
                    Check.True(scene.Meshes.Any(m => m.Kind == "stair-flight") && scene.Meshes.Any(m => m.Kind == "stair-rail") && scene.Meshes.Any(m => m.Kind == "void-railing") && scene.Meshes.Any(m => m.Kind == "landing"));
            });
        Check.Case("construction geometry: v1 cannot obtain construction by implicit upgrade", () => Check.Throws<PlanValidationException>(() => new ConstructionGeometryCompiler().Compile(PlanExamples.SingleStorey())));
        Check.Case("construction geometry: cancellation and approved mesh limits stop before a scene", () =>
        {
            var plan = ConstructionPlanExamples.SingleStorey(); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Check.Throws<OperationCanceledException>(() => new ConstructionGeometryCompiler().Compile(plan, cancelled.Token));
            Check.Throws<InvalidOperationException>(() => new ConstructionGeometryCompiler().Compile(plan with { Construction = plan.Construction! with { MaxMeshes = 1 } }));
        });
        Check.Case("construction geometry: semantic mesh and route generation reproduces exact native bytes", () =>
        {
            var plan = ConstructionPlanExamples.SingleStorey(); var a = new ConstructionGeometryCompiler().Compile(plan); var b = new ConstructionGeometryCompiler().Compile(plan);
            Check.True(JsonSerializer.SerializeToUtf8Bytes(a, StrictPlanJson.Options).SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(b, StrictPlanJson.Options)));
        });
        Check.Case("construction geometry: shared T-junction wall connects two real required rooms", () =>
        {
            var plan = TwoRooms(); var scene = new ConstructionGeometryCompiler().Compile(plan);
            Check.True(scene.Clearance.Routes.Any(r => r.RoomId == "east-room" && r.ToId == "shared-door"));
            Check.True(scene.Clearance.Routes.Any(r => r.FromId == "ground-entry" && r.ToId == "shared-door"));
            Check.True(scene.Meshes.Count(m => m.Kind == "wall" && m.SemanticId == "shared-wall" && m.Representation == "visual") == 1);
        });
        Check.Case("construction geometry: window geometry retains exact aperture and declared glass material", () =>
        {
            var plan = ConstructionPlanExamples.SingleStorey(); var floor = plan.Storeys[0]; var wall = floor.Walls[1];
            var window = new WallOpening { Id = "side-window", Kind = OpeningKind.Window, Offset = 2, Width = 1.4, Sill = .9, Head = 2.1, Pivot = null, Swing = null };
            plan = plan with { Storeys = [floor with { Walls = floor.Walls.Select(w => w.Id == wall.Id ? w with { Openings = [window] } : w).ToList() }] };
            var scene = new ConstructionGeometryCompiler().Compile(plan); var glass = scene.Meshes.Single(m => m.Kind == "window-glass" && m.Representation == "visual");
            Check.True(glass.MaterialId == plan.Construction!.GlassMaterialId && Math.Abs(glass.Vertices.Min(v => v.Z) - .9) < plan.Numerics.LinearTolerance && Math.Abs(glass.Vertices.Max(v => v.Z) - 2.1) < plan.Numerics.LinearTolerance);
            Check.True(Math.Abs(glass.Vertices.Max(v => v.X) - glass.Vertices.Min(v => v.X) - plan.Construction.GlassThickness) < plan.Numerics.LinearTolerance);
        });
        Check.Case("construction geometry: explicit end hinge reverses signed swing without moving the closed leaf", () =>
        {
            var plan = ConstructionPlanExamples.SingleStorey(); var floor = plan.Storeys[0]; var wall = floor.Walls[0]; var opening = wall.Openings[0] with { Pivot = DoorPivot.End };
            plan = plan with { Storeys = [floor with { Walls = floor.Walls.Select(w => w.Id == wall.Id ? w with { Openings = [opening] } : w).ToList() }] };
            var scene = new ConstructionGeometryCompiler().Compile(plan); var pivot = scene.Anchors.Single(a => a.Kind == "door-pivot");
            Check.True(Math.Abs(pivot.Position.X - (opening.Offset + opening.Width - plan.Construction!.DoorLeafGap)) < plan.Numerics.LinearTolerance);
            Check.True(scene.Clearance.DoorSweeps.Single().AngleDegrees < 0 && scene.Meshes.Single(m => m.Kind == "door-leaf" && m.Representation == "collision").CollisionRole == "dynamic");
        });
        Check.Case("construction geometry: impossible full leaf sweep rejects instead of granting a manifest verdict", () =>
        {
            var plan = ConstructionPlanExamples.SingleStorey(); plan = plan with { Construction = plan.Construction! with { DoorOpenAngleDegrees = 180 } };
            new ConstructionReadiness().RequireReady(plan);
            var error = Check.Throws<InvalidOperationException>(() => new ConstructionGeometryCompiler().Compile(plan));
            Check.True(error.Message.Contains("Door swept leaf", StringComparison.Ordinal));
        });
        Check.Case("construction geometry: sloped roof with leading collinear vertices retains gable infill", () =>
        {
            var plan = ConstructionPlanExamples.SingleStorey(); var roof = plan.Roofs[0]; var first = roof.Vertices[0]; var next = roof.Vertices[1];
            List<Point3> points = [first, new((first.X + next.X) / 2, first.Y, 3), .. roof.Vertices.Skip(1)];
            roof = roof with { Vertices = points.Select(p => p with { Z = 3 + .1 * (p.X - first.X) }).ToList() }; plan = plan with { Roofs = [roof] };
            var scene = new ConstructionGeometryCompiler().Compile(plan);
            Check.True(scene.Meshes.Any(m => m.Kind == "gable-wall") && scene.Meshes.Single(m => m.Kind == "roof" && m.Representation == "visual").Vertices.Max(v => v.Z) > 4);
        });
        Check.Case("construction geometry: mixed corner projections and height bands follow each neighbour profile", () =>
        {
            var plan = ConstructionPlanExamples.SingleStorey(); var floor = plan.Storeys[0]; var bottom = floor.Walls[0]; var right = floor.Walls[1];
            var narrow = plan.DetailProfiles[0] with { Id = "narrow-profile", Name = "Narrow neighbouring corner trim", PlinthProjection = .01, CorniceProjection = .02, PlinthHeight = .1, CorniceHeight = .06 };
            plan = plan with { DetailProfiles = [.. plan.DetailProfiles, narrow], Storeys = [floor with { Walls = floor.Walls.Select(w => w.Id == right.Id ? w with { DetailProfileId = narrow.Id } : w).ToList() }] };
            var scene = new ConstructionGeometryCompiler().Compile(plan); var plinth = scene.Meshes.Single(m => m.Kind == "plinth" && m.SemanticId == bottom.Id && m.Representation == "visual"); var cornice = scene.Meshes.Single(m => m.Kind == "cornice" && m.SemanticId == bottom.Id && m.Representation == "visual"); var e = plan.Numerics.LinearTolerance;
            Check.True(Math.Abs(plinth.Vertices.Max(v => v.X) - 10.11) <= e, "Current trim borrowed its own projection for its neighbour");
            Check.True(Math.Abs(plinth.Vertices.Where(v => v.Z > .1 + e).Max(v => v.X) - 10.1) <= e, "Absent upper neighbour plinth was treated as present");
            Check.True(Math.Abs(cornice.Vertices.Max(v => v.X) - 10.12) <= e);
            Check.True(Math.Abs(cornice.Vertices.Where(v => v.Z < 2.94 - e).Max(v => v.X) - 10.1) <= e, "Neighbour cornice began below its approved height band");
        });
        Check.Case("construction geometry: derived roof thickness cannot exceed the approved coordinate envelope", () =>
        {
            var plan = ConstructionPlanExamples.SingleStorey(); var floor = plan.Storeys[0];
            plan = plan with { Numerics = plan.Numerics with { MaxCoordinateMagnitude = 10.3 }, Storeys = [floor with { Height = 10.3, Walls = floor.Walls.Select(w => w with { Height = 10.3 }).ToList() }], Roofs = plan.Roofs.Select(r => r with { Vertices = r.Vertices.Select(v => v with { Z = 10.3 }).ToList() }).ToList() };
            new ConstructionReadiness().RequireReady(plan);
            var error = Check.Throws<InvalidOperationException>(() => new ConstructionGeometryCompiler().Compile(plan));
            Check.True(error.Message.Contains("Derived construction coordinate", StringComparison.Ordinal));
        });
        Check.Case("construction geometry: acute stairwell guard uses continuous mitered closed sections", () =>
        {
            var plan = ConstructionPlanExamples.MultiStorey(); var upper = plan.Storeys[1]; var opening = upper.SlabOpenings[0];
            opening = opening with { Boundary = new Polygon2 { Outer = [new(.5,3), new(3,2.3), new(6.5,2.3), new(6.5,3.7), new(3,3.7)], Holes = [] } };
            plan = plan with { Storeys = [plan.Storeys[0], upper with { SlabOpenings = [opening] }] };
            var scene = new ConstructionGeometryCompiler().Compile(plan); var guard = scene.Meshes.Single(m => m.Kind == "void-railing" && m.Representation == "visual"); var profile = plan.Construction!;
            var expected = .5 - (profile.RailingOffset + profile.RailThickness) * Math.Sqrt(2.5 * 2.5 + .7 * .7) / .7;
            Check.True(Math.Abs(guard.Vertices.Min(v => v.X) - expected) <= plan.Numerics.LinearTolerance, "Acute corner was left with disconnected butt-end guards");
            var counts = new Dictionary<(int,int),int>();
            for (var i = 0; i < guard.Triangles.Count; i += 3)
                foreach (var (a,b) in new[] { (guard.Triangles[i],guard.Triangles[i+1]),(guard.Triangles[i+1],guard.Triangles[i+2]),(guard.Triangles[i+2],guard.Triangles[i]) })
                { var key = (Math.Min(a,b),Math.Max(a,b)); counts[key] = counts.GetValueOrDefault(key)+1; }
            Check.True(counts.Values.All(c => c == 2), "Mitered guard is not closed indexed geometry");
        });
        Check.Case("construction geometry: an approved oversized trim cannot create a self-intersecting wall miter", () =>
        {
            var plan = ConstructionPlanExamples.SingleStorey(); plan = plan with { DetailProfiles = plan.DetailProfiles.Select(p => p with { PlinthProjection = 6 }).ToList() };
            new ConstructionReadiness().RequireReady(plan);
            var error = Check.Throws<InvalidOperationException>(() => new ConstructionGeometryCompiler().Compile(plan));
            Check.True(error.Message.Contains("Wall miter", StringComparison.Ordinal));
        });
        foreach (var thickness in new[] { .1, .3 })
            Check.Case("construction geometry: structural roof overlap cannot erase or thin a ceiling panel " + thickness, () =>
            {
                var plan = ConstructionPlanExamples.SingleStorey(); plan = plan with { Construction = plan.Construction! with { CeilingThickness = thickness } };
                new ConstructionReadiness().RequireReady(plan);
                var error = Check.Throws<InvalidOperationException>(() => new ConstructionGeometryCompiler().Compile(plan));
                Check.True(error.Message.Contains("ceiling panel intersects structural geometry", StringComparison.Ordinal));
            });
        Check.Case("construction geometry: suspended ceiling retains its complete approved thickness in an actual gap", () =>
        {
            var plan = ConstructionPlanExamples.SingleStorey(); plan = plan with
            {
                Construction = plan.Construction! with { CeilingThickness = .1 },
                Roofs = plan.Roofs.Select(r => r with { Vertices = r.Vertices.Select(v => v with { Z = 3.4 }).ToList() }).ToList()
            };
            var scene = new ConstructionGeometryCompiler().Compile(plan); var ceiling = scene.Meshes.Single(m => m.Kind == "ceiling-panel" && m.Representation == "visual"); var e = plan.Numerics.LinearTolerance;
            Check.True(ceiling.Vertices.All(v => Math.Abs(v.Z - 3) <= e || Math.Abs(v.Z - 3.1) <= e), "Ceiling was vertically sliced by structural ownership");
            Check.True(Math.Abs(ceiling.Vertices.Min(v => v.Z) - 3) <= e && Math.Abs(ceiling.Vertices.Max(v => v.Z) - 3.1) <= e);
            Check.True(Math.Abs(ceiling.Vertices.Min(v => v.X) - .1) <= e && Math.Abs(ceiling.Vertices.Max(v => v.X) - 9.9) <= e && Math.Abs(ceiling.Vertices.Min(v => v.Y) - .1) <= e && Math.Abs(ceiling.Vertices.Max(v => v.Y) - 7.9) <= e, "Wall-boundary columns were not trimmed horizontally");
        });
        Check.Case("construction geometry: stair support lifts cannot pass a tiny player through intervening trim", () =>
        {
            var plan = ConstructionPlanExamples.MultiStorey(); var lower = plan.Storeys[0];
            plan = plan with
            {
                Player = plan.Player with { Height = .1 },
                Storeys = [lower with { Walls = lower.Walls.Select(w => w with { Height = 2.78 }).ToList() }, plan.Storeys[1]],
                DetailProfiles = plan.DetailProfiles.Select(d => d with { CorniceHeight = .05, CorniceProjection = 2.95 }).ToList()
            };
            new ConstructionReadiness().RequireReady(plan);
            var error = Check.Throws<InvalidOperationException>(() => new ConstructionGeometryCompiler().Compile(plan));
            Check.True(error.Message.Contains("stair support lift", StringComparison.Ordinal) && error.Message.Contains("cornice-", StringComparison.Ordinal));
        });
        Check.Case("construction geometry: unobstructed tiny players retain certified stair support lifts", () =>
        {
            var plan = ConstructionPlanExamples.MultiStorey(); plan = plan with { Player = plan.Player with { Height = .1 } };
            var scene = new ConstructionGeometryCompiler().Compile(plan); var route = scene.Clearance.Routes.Single(r => r.FromId == "main-stair-lower-landing" && r.ToId == "main-stair-upper-landing"); var e = plan.Numerics.LinearTolerance;
            Check.True(route.Height == .1 && route.MinimumClearance >= -e);
            Check.True(Enumerable.Range(1, route.Points.Count - 1).Any(i => Math.Abs(route.Points[i].X - route.Points[i - 1].X) <= e && Math.Abs(route.Points[i].Y - route.Points[i - 1].Y) <= e && route.Points[i].Z - route.Points[i - 1].Z > .1 + e), "Lift evidence omitted the actual vertical support transitions");
        });
        Check.Case("construction geometry: structural roof ownership cannot silently shorten a door header", () =>
        {
            var plan = ConstructionPlanExamples.SingleStorey(); var floor = plan.Storeys[0];
            plan = plan with { Storeys = [floor with { Walls = floor.Walls.Select(w => w with { Openings = w.Openings.Select(o => o.Kind == OpeningKind.Door ? o with { Head = 2.99 } : o).ToList() }).ToList() }] };
            new ConstructionReadiness().RequireReady(plan);
            var error = Check.Throws<InvalidOperationException>(() => new ConstructionGeometryCompiler().Compile(plan));
            Check.True(error.Message.Contains("frame intersects structural geometry", StringComparison.Ordinal));
        });
        Check.Case("construction geometry: an actual roof gap retains the requested door header dimensions", () =>
        {
            var plan = ConstructionPlanExamples.SingleStorey(); var floor = plan.Storeys[0];
            plan = plan with
            {
                Storeys = [floor with { Walls = floor.Walls.Select(w => w with { Openings = w.Openings.Select(o => o.Kind == OpeningKind.Door ? o with { Head = 2.99 } : o).ToList() }).ToList() }],
                Roofs = plan.Roofs.Select(r => r with { Vertices = r.Vertices.Select(v => v with { Z = 3.4 }).ToList() }).ToList()
            };
            var scene = new ConstructionGeometryCompiler().Compile(plan); var frame = scene.Meshes.Single(m => m.Kind == "door-frame" && m.Representation == "visual");
            Check.True(Math.Abs(frame.Vertices.Max(v => v.Z) - (2.99 + plan.Construction!.DoorFrameWidth)) <= plan.Numerics.LinearTolerance);
        });
        Check.Case("construction geometry: floor ownership cannot silently shorten a window sill frame", () =>
        {
            var plan = ConstructionPlanExamples.SingleStorey(); var floor = plan.Storeys[0];
            plan = plan with { Storeys = [floor with { Walls = floor.Walls.Select(w => w with { Openings = w.Openings.Select(o => o.Kind == OpeningKind.Window ? o with { Sill = .02 } : o).ToList() }).ToList() }] };
            new ConstructionReadiness().RequireReady(plan);
            var error = Check.Throws<InvalidOperationException>(() => new ConstructionGeometryCompiler().Compile(plan));
            Check.True(error.Message.Contains("frame intersects structural geometry", StringComparison.Ordinal));
        });
        foreach (var cornice in new[] { false, true })
            Check.Case("construction geometry: explicit trim height cannot exceed its supporting wall " + cornice, () =>
            {
                var plan = ConstructionPlanExamples.SingleStorey(); plan = plan with { DetailProfiles = plan.DetailProfiles.Select(d => cornice ? d with { CorniceHeight = 4 } : d with { PlinthHeight = 6 }).ToList() };
                new ConstructionReadiness().RequireReady(plan);
                var error = Check.Throws<InvalidOperationException>(() => new ConstructionGeometryCompiler().Compile(plan));
                Check.True(error.Message.Contains("detail height", StringComparison.Ordinal));
            });
        Check.Case("construction geometry: overlapping trim bands cannot silently shorten either profile", () =>
        {
            var plan = ConstructionPlanExamples.SingleStorey(); plan = plan with { DetailProfiles = plan.DetailProfiles.Select(d => d with { PlinthHeight = 1.8, CorniceHeight = 1.5 }).ToList() };
            new ConstructionReadiness().RequireReady(plan);
            var error = Check.Throws<InvalidOperationException>(() => new ConstructionGeometryCompiler().Compile(plan));
            Check.True(error.Message.Contains("detail height bands overlap", StringComparison.Ordinal));
        });
        Check.Case("construction geometry: fascia cannot hide a blocked bounded exterior entrance approach", () =>
        {
            var plan = ConstructionPlanExamples.SingleStorey(); plan = plan with { DetailProfiles = plan.DetailProfiles.Select(d => d with { FasciaDepth = 3.2 }).ToList() };
            new ConstructionReadiness().RequireReady(plan);
            var error = Check.Throws<InvalidOperationException>(() => new ConstructionGeometryCompiler().Compile(plan));
            Check.True(error.Message.Contains("opening approach", StringComparison.Ordinal) && error.Message.Contains("roof-fascia", StringComparison.Ordinal));
        });
        foreach (var blocked in new[] { true, false })
            Check.Case("construction geometry: exterior approach follows its actual separately profiled roof edge " + blocked, () =>
            {
                var plan = ConstructionPlanExamples.SingleStorey(); var wallDetail = plan.DetailProfiles[0] with { EaveOverhang = 0 }; var roofDetail = wallDetail with { Id = "separate-wide-roof", Name = "Separate approved roof detail", EaveOverhang = 1, FasciaDepth = blocked ? 3.2 : .12 };
                plan = plan with
                {
                    DetailProfiles = [wallDetail, roofDetail],
                    Roofs = [plan.Roofs[0] with { DetailProfileId = roofDetail.Id, Vertices = PlanExamples.Rect(-1.1, -1.1, 11.1, 9.1).Outer.Select(p => new Point3(p.X, p.Y, 3)).ToList() }]
                };
                new ConstructionReadiness().RequireReady(plan);
                if (blocked)
                {
                    var error = Check.Throws<InvalidOperationException>(() => new ConstructionGeometryCompiler().Compile(plan));
                    Check.True(error.Message.Contains("opening approach", StringComparison.Ordinal) && error.Message.Contains("roof-fascia", StringComparison.Ordinal));
                }
                else
                {
                    var scene = new ConstructionGeometryCompiler().Compile(plan); var route = scene.Clearance.Routes.Single(r => r.ToId == "exterior-approach-ground-entry"); var e = plan.Numerics.LinearTolerance;
                    Check.True(Math.Abs(route.Points.Min(p => p.Y) - (-1.1 - plan.Player.Radius - 4 * e)) <= e, "Exterior approach inherited the unrelated wall eave field");
                    Check.True(route.MinimumClearance >= -e);
                }
            });
    }

    public static BuildingPlan TwoRooms()
    {
        var plan = ConstructionPlanExamples.SingleStorey(); var floor = plan.Storeys[0]; var left = floor.Rooms[0] with { Boundary = PlanExamples.Rect(.1, .1, 4.9, 7.9) }; var right = left with { Id = "east-room", Name = "Empty east room", Boundary = PlanExamples.Rect(5.1, .1, 9.9, 7.9) }; var template = floor.Walls[0];
        WallPlan Wall(string id, Point2 start, Point2 end, string room, string? other = null, List<WallOpening>? openings = null)
            => template with { Id = id, Start = start, End = end, LeftRoomId = room, RightRoomId = other, Openings = openings ?? [] };
        List<WallPlan> walls = [
            Wall("bottom-west", new(0,0),new(5,0),left.Id,openings:template.Openings), Wall("bottom-east",new(5,0),new(10,0),right.Id),
            Wall("east-wall",new(10,0),new(10,8),right.Id), Wall("top-east",new(10,8),new(5,8),right.Id),
            Wall("top-west",new(5,8),new(0,8),left.Id), Wall("west-wall",new(0,8),new(0,0),left.Id),
            Wall("shared-wall",new(5,0),new(5,8),left.Id,right.Id,[new WallOpening { Id="shared-door",Kind=OpeningKind.Door,Offset=3.4,Width=1.2,Sill=0,Head=2.2,Pivot=DoorPivot.Start,Swing=DoorSwing.Left }])
        ];
        return plan with { Storeys = [floor with { Rooms = [left,right],Walls = walls }] };
    }
}
