using Bubo.FloorPlan.Domain;

namespace Bubo.FloorPlan.Tests;

public static class DomainTests
{
    private static readonly BuildingPlanValidator Validator = new();

    public static void Run()
    {
        Check.Case("domain: overlap is symmetric for aligned contained rectangles and excludes boundary-only contact", () =>
        {
            var geometry = new PlanGeometry(.000001, () => { });
            var landing = PlanExamples.Rect(1.8, 2.4, 3, 3.6);
            var tread = PlanExamples.Rect(1.8, 3, 3, 3.25);
            Check.True(geometry.Overlap(landing, tread), "A contained tread has positive area even when every vertex is on the landing's side boundaries.");
            Check.True(geometry.Overlap(tread, landing), "Positive overlap is independent of operand order.");
            var touching = PlanExamples.Rect(3, 2.4, 4, 3.6);
            Check.True(!geometry.Overlap(landing, touching) && !geometry.Overlap(touching, landing), "A common edge alone has no positive-area overlap.");
        });
        Check.Case("domain: three complete architectural examples validate", () =>
        {
            foreach (var plan in new[] { PlanExamples.SingleStorey(), PlanExamples.MultiStorey(), PlanExamples.AngledCourtyard() })
            {
                var report = Validator.Validate(plan);
                Check.True(report.IsValid, string.Join("; ", report.Issues.Select(i => $"{i.Code}: {i.Path}: {i.Message}")));
                Check.True(report.Counts.Walls > 0 && report.Counts.Vertices > 0 && report.Counts.GeometryChecks > 0, "Counts include checked geometry.");
            }
        });
        Check.Case("domain: a shared wall is authored once with a real connecting door", () => Valid(TwoRooms()));
        Check.Case("domain: windows do not connect required rooms", () =>
        {
            var p = TwoRooms(); var s = p.Storeys[0]; var w = s.Walls.Single(w => w.Id == "partition");
            var window = w.Openings[0] with { Kind = OpeningKind.Window, Sill = 1, Head = 2.2, Pivot = null, Swing = null };
            Invalid(ReplaceWall(p, w.Id, w with { Openings = [window] }), "disconnected-room");
        });
        Check.Case("domain: null plan is typed invalid", () => Invalid(null!, "missing-plan"));
        Check.Case("domain: missing numerical policy is invalid", () => Invalid(PlanExamples.SingleStorey() with { Numerics = null! }, "missing-policy"));
        Check.Case("domain: missing collections are invalid", () => Invalid(PlanExamples.SingleStorey() with { Storeys = null! }, "missing-collection"));
        Check.Case("domain: schema and coordinate system cannot drift", () =>
        {
            Invalid(PlanExamples.SingleStorey() with { SchemaVersion = "v99" }, "schema-version");
            Invalid(PlanExamples.SingleStorey() with { CoordinateSystem = "feet-y-up" }, "coordinate-system");
        });
        Check.Case("domain: identities are globally unique", () =>
        {
            var p = PlanExamples.SingleStorey(); Invalid(p with { Materials = [p.Materials[0] with { Id = p.Storeys[0].Id }] }, "duplicate-id");
        });
        Check.Case("domain: identifiers and hashes reject trailing newlines", () =>
        {
            Invalid(PlanExamples.SingleStorey() with { Id = "valid-id\n" }, "invalid-id");
            Invalid(PlanExamples.SingleStorey() with { StyleSha256 = new string('a', 64) + "\n" }, "invalid-style-hash");
        });
        Check.Case("domain: nonfinite coordinates and elevations are rejected", () =>
        {
            var p = PlanExamples.SingleStorey();
            Invalid(ReplaceWall(p, p.Storeys[0].Walls[0].Id, p.Storeys[0].Walls[0] with { Start = new(double.NaN, 0) }), "nonfinite");
            Invalid(p with { Storeys = [p.Storeys[0] with { Elevation = double.PositiveInfinity }] }, "nonfinite");
        });
        Check.Case("domain: finite enormous dimensions cannot overflow geometry", () =>
        {
            var p = PlanExamples.SingleStorey(); Invalid(ReplaceWall(p, p.Storeys[0].Walls[0].Id, p.Storeys[0].Walls[0] with { Thickness = double.MaxValue }), "invalid-dimension");
        });
        Check.Case("domain: zero width walls and unknown material references fail", () =>
        {
            var p = PlanExamples.SingleStorey(); var w = p.Storeys[0].Walls[0];
            Invalid(ReplaceWall(p, w.Id, w with { Thickness = 0 }), "invalid-dimension");
            Invalid(ReplaceWall(p, w.Id, w with { MaterialId = "absent" }), "unknown-reference");
        });
        Check.Case("domain: self crossing and explicitly repeated closing vertices fail", () =>
        {
            var p = PlanExamples.SingleStorey(); var s = p.Storeys[0];
            Invalid(p with { Storeys = [s with { Footprint = new Polygon2 { Outer = [new(0, 0), new(10, 8), new(0, 8), new(10, 0)], Holes = [] } }] }, "self-intersection");
            Invalid(p with { Storeys = [s with { Footprint = s.Footprint with { Outer = [.. s.Footprint.Outer, s.Footprint.Outer[0]] } }] }, "short-edge");
        });
        Check.Case("domain: courtyard holes cannot touch the outside", () =>
        {
            var p = PlanExamples.SingleStorey(); var s = p.Storeys[0];
            var hole = PlanExamples.Rect(0, 2, 2, 4).Outer.AsEnumerable().Reverse().ToList();
            Invalid(p with { Storeys = [s with { Footprint = s.Footprint with { Holes = [hole] } }] }, "hole-outside");
        });
        Check.Case("domain: same-room geometric clones overlap", () =>
        {
            var p = PlanExamples.SingleStorey(); var s = p.Storeys[0];
            Invalid(p with { Storeys = [s with { Rooms = [.. s.Rooms, s.Rooms[0] with { Id = "clone-room" }] }] }, "room-overlap");
        });
        Check.Case("domain: clear room polygons must use physical wall faces", () =>
        {
            var p = PlanExamples.SingleStorey(); var s = p.Storeys[0];
            Invalid(p with { Storeys = [s with { Rooms = [s.Rooms[0] with { Boundary = PlanExamples.Rect(1, 1, 9, 7) }] }] }, "unenclosed-room");
        });
        Check.Case("domain: reversed and partial duplicate shared walls fail", () =>
        {
            foreach (var reverse in new[] { true, false })
            {
                var p = PlanExamples.SingleStorey(); var s = p.Storeys[0]; var w = s.Walls[0];
                var duplicate = w with { Id = "duplicate", Start = reverse ? w.End : new(2, 0), End = reverse ? w.Start : new(4, 0), LeftRoomId = reverse ? null : w.LeftRoomId, RightRoomId = reverse ? w.LeftRoomId : null, Openings = [] };
                Invalid(p with { Storeys = [s with { Walls = [.. s.Walls, duplicate] }] }, "duplicate-shared-wall");
            }
        });
        Check.Case("domain: offset parallel walls cannot duplicate physical material", () =>
        {
            var p = PlanExamples.SingleStorey(); var s = p.Storeys[0];
            var duplicate = s.Walls[0] with { Id = "offset-duplicate", Start = new(1, .05), End = new(9, .05), Openings = [] };
            Invalid(p with { Storeys = [s with { Walls = [.. s.Walls, duplicate] }] }, "wall-volume-overlap");
        });
        Check.Case("domain: T junctions require split centre lines", () =>
        {
            var p = PlanExamples.SingleStorey(); var s = p.Storeys[0];
            var branch = s.Walls[0] with { Id = "unsplit-branch", Start = new(5, 0), End = new(5, 4), Openings = [] };
            Invalid(p with { Storeys = [s with { Walls = [.. s.Walls, branch] }] }, "wall-junction-not-split");
        });
        Check.Case("domain: clear rooms exclude unpartitioned internal wall material", () =>
        {
            var p = PlanExamples.SingleStorey(); var s = p.Storeys[0];
            var internalWall = s.Walls[0] with { Id = "hidden-internal-wall", Start = new(2, 2), End = new(8, 2), Openings = [] };
            Invalid(p with { Storeys = [s with { Walls = [.. s.Walls, internalWall] }] }, "wall-in-clear-room");
        });
        Check.Case("domain: doors need pivots and explicit known kinds", () =>
        {
            Invalid(ChangeEntry(o => o with { Pivot = null }), "door-pivot");
            Invalid(ChangeEntry(o => o with { Kind = (OpeningKind)999 }), "unknown-opening-kind");
        });
        Check.Case("domain: opening dimensions and player clearance are enforced", () =>
        {
            Invalid(ChangeEntry(o => o with { Offset = 9.8 }), "opening-dimensions");
            Invalid(ChangeEntry(o => o with { Width = .5 }), "opening-clearance");
            Invalid(ChangeEntry(o => o with { Sill = .4 }), "opening-clearance");
        });
        Check.Case("domain: opening span must align with room clear space", () => Invalid(ChangeEntry(o => o with { Offset = 0 }), "opening-room-alignment"));
        Check.Case("domain: a portal inside a room does not establish boundary adjacency", () =>
        {
            var p = PlanExamples.SingleStorey(); var s = p.Storeys[0];
            var fakePortal = s.Walls[0] with { Id = "fake-portal-wall", Start = new(2, 2), End = new(8, 2), Openings = [s.Walls[0].Openings[0] with { Id = "fake-portal" }] };
            Invalid(p with { Storeys = [s with { Walls = [.. s.Walls, fakePortal] }] }, "opening-room-alignment");
        });
        Check.Case("domain: a real door must have a supported clear approach", () =>
        {
            var p = PlanExamples.SingleStorey(); var s = p.Storeys[0];
            Invalid(p with { Storeys = [s with { SlabOpenings = [new SlabOpening { Id = "entry-pit", Boundary = PlanExamples.Rect(1, .2, 2.2, .6) }] }] }, "opening-approach-over-void");
        });
        Check.Case("domain: overlapping door volumes fail", () =>
        {
            var p = PlanExamples.SingleStorey(); var w = p.Storeys[0].Walls[0];
            Invalid(ReplaceWall(p, w.Id, w with { Openings = [w.Openings[0], w.Openings[0] with { Id = "overlap-door", Offset = 1.5 }] }), "opening-overlap");
        });
        Check.Case("domain: exterior entry must be real", () =>
        {
            var p = PlanExamples.SingleStorey(); var w = p.Storeys[0].Walls[0];
            Invalid(ReplaceWall(p, w.Id, w with { Openings = [] }), "missing-exterior-entry");
        });
        Check.Case("domain: stairs must match both rise and run", () =>
        {
            Invalid(ChangeStair(s => s with { RiserHeight = .19 }), "stair-rise-run");
            Invalid(ChangeStair(s => s with { End = new(6.6, 3) }), "stair-rise-run");
        });
        Check.Case("domain: stacked three-storey flights have measured soffit clearance", () => Valid(PlanExamples.StackedThreeStorey()));
        Check.Case("domain: duplicate flights cannot share the same physical volume", () =>
        {
            var p = PlanExamples.MultiStorey();
            Invalid(p with { Stairs = [p.Stairs[0], p.Stairs[0] with { Id = "duplicate-stair" }] }, "stair-flight-overlap");
        });
        Check.Case("domain: reversed flights cannot share a slab void and physical volume", () =>
        {
            var p = PlanExamples.MultiStorey(); var s = p.Stairs[0];
            var opposite = s with { Id = "opposite-stair", Start = s.End, End = s.Start, LowerLanding = s.UpperLanding, UpperLanding = s.LowerLanding };
            Invalid(p with { Stairs = [s, opposite] }, "stair-flight-overlap");
        });
        Check.Case("domain: stacked flights require actual headroom below the upper soffit", () =>
        {
            var p = PlanExamples.StackedThreeStorey();
            Invalid(p with { Stairs = [p.Stairs[0] with { Headroom = 2.7 }, p.Stairs[1]] }, "stair-flight-headroom");
        });
        Check.Case("domain: stair structure thickness is explicit finite bounded geometry", () =>
        {
            Invalid(ChangeStair(s => s with { StructureThickness = 0 }), "stair-structure-thickness");
            Invalid(ChangeStair(s => s with { StructureThickness = double.NaN }), "nonfinite");
            Invalid(ChangeStair(s => s with { StructureThickness = 1.1 }), "stair-structure-thickness");
        });
        Check.Case("domain: stairs require aligned complete slab cuts", () =>
        {
            Invalid(ChangeStair(s => s with { RequiredSlabOpeningId = "missing" }), "stair-slab-alignment");
            var p = PlanExamples.MultiStorey(); var upper = p.Storeys[1];
            Invalid(p with { Storeys = [p.Storeys[0], upper with { SlabOpenings = [upper.SlabOpenings[0] with { Boundary = PlanExamples.Rect(3.2, 2.4, 6.5, 3.6) }] }] }, "stair-slab-alignment");
        });
        Check.Case("domain: stair landings must have supported usable clearance", () =>
        {
            Invalid(ChangeStair(s => s with { LowerLanding = PlanExamples.Rect(2.5, 2.4, 3, 3.6) }), "stair-landing-clearance");
            var p = PlanExamples.MultiStorey(); var upper = p.Storeys[1];
            Invalid(p with { Storeys = [p.Storeys[0], upper with { SlabOpenings = [upper.SlabOpenings[0] with { Boundary = PlanExamples.Rect(3, 2.4, 7.7, 3.6) }] }] }, "stair-landing-over-void");
        });
        Check.Case("domain: lower landing cannot extend into its own low treads", () =>
        {
            Invalid(ChangeStair(s => s with { LowerLanding = PlanExamples.Rect(1.8, 2.4, 4, 3.6) }), "stair-landing-over-flight");
        });
        Check.Case("domain: foreign flight cannot occupy another landing slab", () =>
        {
            Invalid(ForeignFlightPlan(2.4), "stair-landing-flight-overlap");
            Invalid(ForeignFlightPlan(3), "stair-landing-flight-overlap");
        });
        Check.Case("domain: foreign flight must leave actual headroom above a landing", () => Invalid(ForeignFlightPlan(2), "stair-landing-flight-headroom"));
        Check.Case("domain: aligned low flight cells cannot hide insufficient foreign landing headroom", () =>
        {
            var p = PlanExamples.StackedThreeStorey();
            var b = p.Stairs[1] with
            {
                Start = new(2.4, 2.45), End = new(2.4, 5.95), StructureThickness = 1,
                LowerLanding = PlanExamples.Rect(1.8, 1.25, 3, 2.45), UpperLanding = PlanExamples.Rect(1.8, 5.95, 3, 7.15)
            };
            var top = p.Storeys[2] with { SlabOpenings = [p.Storeys[2].SlabOpenings[0] with { Boundary = PlanExamples.Rect(1.8, 2.45, 3, 5.95) }] };
            Invalid(p with { Storeys = [p.Storeys[0], p.Storeys[1], top], Stairs = [p.Stairs[0], b] }, "stair-landing-flight-headroom");
        });
        Check.Case("domain: concave landing intersections retain disconnected clearance witnesses", () =>
        {
            var p = ForeignFlightPlan(2); var a = p.Stairs[0]; var b = p.Stairs[1];
            var landing = new Polygon2 { Outer = [new(1, 2.4), new(3, 2.4), new(3, 3.6), new(1, 3.6), new(1, 3.2), new(1.7, 3.2), new(1.7, 2.8), new(1, 2.8)], Holes = [] };
            b = b with { Start = new(1.3, 2), End = new(1.3, 5.5), Width = .6, LowerLanding = PlanExamples.Rect(1, .8, 1.6, 2), UpperLanding = PlanExamples.Rect(1, 5.5, 1.6, 6.7) };
            var upper = p.Storeys[1] with { SlabOpenings = [p.Storeys[1].SlabOpenings[0], p.Storeys[1].SlabOpenings[1] with { Boundary = PlanExamples.Rect(1, 2, 1.6, 5.5) }] };
            Invalid(p with { Storeys = [p.Storeys[0], upper], Stairs = [a with { LowerLanding = landing }, b] }, "stair-landing-flight-headroom");
        });
        Check.Case("domain: foreign landing slab cannot cover another flight's high treads", () =>
        {
            var p = ForeignFlightPlan(2); var a = p.Stairs[0];
            var overhang = new Polygon2 { Outer = [new(6.5, 2.4), new(7.7, 2.4), new(7.7, 6), new(1.8, 6), new(1.8, 5.4), new(6.5, 5.4)], Holes = [] };
            Invalid(p with { Stairs = [a with { UpperLanding = overhang }, p.Stairs[1]] }, "stair-landing-flight-overlap");
        });
        Check.Case("domain: foreign landings and flights may overlap XY with sufficient clearance in either vertical order", () => Valid(ShiftedStackedPlan()));
        Check.Case("domain: stair headroom and player step limit are explicit", () =>
        {
            Invalid(ChangeStair(s => s with { Headroom = 1.5 }), "stair-dimensions");
            Invalid(ChangeStair(s => s with { RiserHeight = .3 }), "stair-dimensions");
        });
        Check.Case("domain: storey slab and wall vertical volumes cannot overlap", () =>
        {
            var p = PlanExamples.MultiStorey(); Invalid(p with { Storeys = [p.Storeys[0], p.Storeys[1] with { Elevation = 2.9 }] }, "storey-elevation-overlap");
        });
        Check.Case("domain: roofs must be planar and upward wound", () =>
        {
            var p = PlanExamples.SingleStorey(); var r = p.Roofs[0]; var vertices = r.Vertices.ToList(); vertices[2] = vertices[2] with { Z = 3.5 };
            Invalid(p with { Roofs = [r with { Vertices = vertices }] }, "nonplanar-roof");
            Invalid(p with { Roofs = [r with { Vertices = r.Vertices.AsEnumerable().Reverse().ToList() }] }, "ring-winding-area");
        });
        Check.Case("domain: roof cannot cut through the building", () =>
        {
            var p = PlanExamples.SingleStorey(); var r = p.Roofs[0]; Invalid(p with { Roofs = [r with { Vertices = r.Vertices.Select(v => v with { Z = 2 }).ToList() }] }, "roof-wall-clearance");
        });
        Check.Case("domain: roof union must cover the whole building", () =>
        {
            var p = PlanExamples.SingleStorey(); var r = p.Roofs[0];
            Invalid(p with { Roofs = [r with { Vertices = PlanExamples.Rect(0, 0, 5, 8).Outer.Select(v => new Point3(v.X, v.Y, 3)).ToList() }] }, "incomplete-roof-coverage");
            // Every outer corner has roof coverage, but the middle is an uncovered island.
            var courtyard = PlanExamples.AngledCourtyard(); var s = courtyard.Storeys[0];
            Invalid(courtyard with { Storeys = [s with { Footprint = s.Footprint with { Holes = [] } }] }, "incomplete-roof-coverage");
        });
        Check.Case("domain: open courtyard cannot be roofed over", () =>
        {
            var p = PlanExamples.AngledCourtyard(); var roof = p.Roofs[0] with { Vertices = p.Storeys[0].Footprint.Outer.Select(v => new Point3(v.X, v.Y, 3)).ToList() };
            Invalid(p with { Roofs = [roof] }, "roof-covers-courtyard");
        });
        Check.Case("domain: duplicate or stacked roof projections are explicit unsupported input", () =>
        {
            var p = PlanExamples.SingleStorey(); Invalid(p with { Roofs = [p.Roofs[0], p.Roofs[0] with { Id = "stacked-roof" }] }, "roof-projection-overlap");
        });
        Check.Case("domain: declared budgets are finite and enforced", () =>
        {
            var p = PlanExamples.SingleStorey();
            Invalid(p with { Budgets = p.Budgets with { MaxGeometryChecks = 0 } }, "invalid-budget");
            Invalid(p with { Budgets = p.Budgets with { MaxGeometryChecks = 1 } }, "validation-budget-exhausted");
            Invalid(p with { Budgets = p.Budgets with { MaxVertices = 1 } }, "validation-budget-exhausted");
            Invalid(p with { Budgets = p.Budgets with { MaxWalls = 1 } }, "input-budget-exceeded");
        });
        Check.Case("domain: cancellation rejects the request immediately", () =>
        {
            using var source = new CancellationTokenSource(); source.Cancel();
            Check.Throws<OperationCanceledException>(() => Validator.Validate(PlanExamples.SingleStorey(), source.Token));
        });
        Check.Case("domain: RequireValid preserves typed report", () =>
        {
            var report = Validator.Validate(PlanExamples.SingleStorey() with { EntryRoomId = "missing" });
            Check.True(!report.IsValid && report.Issues.Any(i => i.Code == "unknown-reference"), "Report contains typed issue.");
            Check.Throws<PlanValidationException>(() => Validator.RequireValid(PlanExamples.SingleStorey() with { EntryRoomId = "missing" }));
        });
    }

    private static void Valid(BuildingPlan plan)
    {
        var report = Validator.Validate(plan);
        Check.True(report.IsValid, string.Join("; ", report.Issues.Select(i => i.Code + ": " + i.Path)));
    }

    private static void Invalid(BuildingPlan plan, string expectedCode)
    {
        var report = Validator.Validate(plan);
        Check.True(!report.IsValid && report.Issues.Any(i => i.Code == expectedCode), "Expected " + expectedCode + "; found " + string.Join(",", report.Issues.Select(i => i.Code)));
    }

    private static BuildingPlan ReplaceWall(BuildingPlan p, string id, WallPlan wall) => p with
    {
        Storeys = p.Storeys.Select(s => s with { Walls = s.Walls.Select(w => w.Id == id ? wall : w).ToList() }).ToList()
    };

    private static BuildingPlan ChangeEntry(Func<WallOpening, WallOpening> change)
    {
        var p = PlanExamples.SingleStorey(); var w = p.Storeys[0].Walls[0]; return ReplaceWall(p, w.Id, w with { Openings = [change(w.Openings[0])] });
    }

    private static BuildingPlan ChangeStair(Func<StairPlan, StairPlan> change)
    {
        var p = PlanExamples.MultiStorey(); return p with { Stairs = [change(p.Stairs[0])] };
    }

    private static BuildingPlan ForeignFlightPlan(double startY)
    {
        var p = PlanExamples.MultiStorey(); var a = p.Stairs[0];
        var b = a with
        {
            Id = "cross-stair", Start = new(2.4, startY), End = new(2.4, startY + 3.5),
            LowerLanding = PlanExamples.Rect(1.8, startY - 1.2, 3, startY),
            UpperLanding = PlanExamples.Rect(1.8, startY + 3.5, 3, startY + 4.7), RequiredSlabOpeningId = "cross-flight-void"
        };
        var upper = p.Storeys[1] with { SlabOpenings = [p.Storeys[1].SlabOpenings[0], new SlabOpening { Id = "cross-flight-void", Boundary = PlanExamples.Rect(1.8, startY, 3, startY + 3.5) }] };
        return p with { Storeys = [p.Storeys[0], upper], Stairs = [a, b] };
    }

    private static BuildingPlan ShiftedStackedPlan()
    {
        var p = PlanExamples.StackedThreeStorey(); var b = p.Stairs[1] with
        {
            Start = new(1.5, 3), End = new(5, 3), LowerLanding = PlanExamples.Rect(.3, 2.4, 1.5, 3.6), UpperLanding = PlanExamples.Rect(5, 2.4, 6.2, 3.6)
        };
        var top = p.Storeys[2] with { SlabOpenings = [p.Storeys[2].SlabOpenings[0] with { Boundary = PlanExamples.Rect(1.5, 2.4, 5, 3.6) }] };
        return p with { Storeys = [p.Storeys[0], p.Storeys[1], top], Stairs = [p.Stairs[0], b] };
    }

    private static BuildingPlan TwoRooms()
    {
        var p = PlanExamples.SingleStorey(); var s = p.Storeys[0]; var template = s.Walls[0];
        var a = s.Rooms[0] with { Boundary = PlanExamples.Rect(.1, .1, 4.9, 7.9) };
        var b = a with { Id = "second-room", Name = "Second empty room", Boundary = PlanExamples.Rect(5.1, .1, 9.9, 7.9) };
        WallPlan Wall(string id, Point2 start, Point2 end, string left, string? right = null) => template with { Id = id, Start = start, End = end, LeftRoomId = left, RightRoomId = right, Openings = [] };
        List<WallPlan> walls =
        [
            Wall("south-a", new(0,0), new(5,0), a.Id) with { Openings = template.Openings },
            Wall("south-b", new(5,0), new(10,0), b.Id), Wall("east", new(10,0), new(10,8), b.Id),
            Wall("north-b", new(10,8), new(5,8), b.Id), Wall("north-a", new(5,8), new(0,8), a.Id),
            Wall("west", new(0,8), new(0,0), a.Id),
            Wall("partition", new(5,0), new(5,8), a.Id,b.Id) with { Openings = [template.Openings[0] with { Id = "connecting-door", Offset = 3 }] }
        ];
        return p with { Storeys = [s with { Rooms = [a, b], Walls = walls }] };
    }
}
