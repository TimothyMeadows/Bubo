using Bubo.FloorPlan.Domain;

namespace Bubo.FloorPlan.Tests;

public static class PlanExamples
{
    public static BuildingPlan SingleStorey()
    {
        var floor = Rect(0, 0, 10, 8);
        var room = new RoomPlan { Id = "ground-room", Name = "Empty studio", RequiresAccess = true, Boundary = Rect(.1, .1, 9.9, 7.9) };
        return Base([Storey("ground", 0, 3, floor, room, true)], [], [Roof("flat-roof", Rect(-.3, -.3, 10.3, 8.3).Outer, 3)]);
    }

    public static BuildingPlan MultiStorey()
    {
        var lowerRoom = new RoomPlan { Id = "ground-room", Name = "Empty ground hall", RequiresAccess = true, Boundary = Rect(.1, .1, 9.9, 7.9) };
        var upperRoom = lowerRoom with { Id = "upper-room", Name = "Empty upper hall" };
        var lower = Storey("ground", 0, 2.8, Rect(0, 0, 10, 8), lowerRoom, true);
        var upper = Storey("upper", 3, 2.8, Rect(0, 0, 10, 8), upperRoom, false) with
        {
            SlabOpenings = [new SlabOpening { Id = "upper-flight-void", Boundary = Rect(3, 2.4, 6.5, 3.6) }]
        };
        var stair = new StairPlan
        {
            Id = "main-stair", LowerStoreyId = lower.Id, UpperStoreyId = upper.Id,
            LowerRoomId = lowerRoom.Id, UpperRoomId = upperRoom.Id,
            Start = new(3, 3), End = new(6.5, 3), Width = 1.2, RiserCount = 15,
            RiserHeight = .2, TreadDepth = .25, Headroom = 2.2, StructureThickness = .15,
            LowerLanding = Rect(1.8, 2.4, 3, 3.6), UpperLanding = Rect(6.5, 2.4, 7.7, 3.6),
            RequiredSlabOpeningId = "upper-flight-void", MaterialId = "masonry"
        };
        return Base([lower, upper], [stair], [Roof("upper-flat-roof", Rect(-.3, -.3, 10.3, 8.3).Outer, 5.8)]);
    }

    public static BuildingPlan AngledCourtyard()
    {
        List<Point2> outer = [new(0, 0), new(12, 0), new(14, 10), new(2, 10)];
        List<Point2> courtyardCcw = [new(4, 4), new(8, 4), new(8.8, 8), new(4.8, 8)];
        var courtyard = courtyardCcw.AsEnumerable().Reverse().ToList();
        var floor = new Polygon2 { Outer = outer, Holes = [courtyard] };
        var room = new RoomPlan
        {
            Id = "ground-room", Name = "Empty angled courtyard hall", RequiresAccess = true,
            Boundary = new Polygon2 { Outer = OffsetLeft(outer, .1), Holes = [OffsetLeft(courtyard, .1)] }
        };
        var s = Storey("ground", 0, 3, floor, room, true);
        var roofs = new List<RoofPlane>();
        for (var i = 0; i < 4; i++)
        {
            var next = (i + 1) % 4;
            roofs.Add(Roof("courtyard-roof-" + i, [outer[i], outer[next], courtyardCcw[next], courtyardCcw[i]], 3));
        }
        return Base([s], [], roofs) with { Name = "Angled courtyard pavilion", ArchitecturalStyle = "Angled courtyard masonry with flat parapet roofs" };
    }

    public static BuildingPlan StackedThreeStorey()
    {
        var plan = MultiStorey();
        var middle = plan.Storeys[1];
        var topRoom = middle.Rooms[0] with { Id = "top-room", Name = "Empty top hall" };
        var top = middle with
        {
            Id = "top", Name = "top storey", Elevation = 6,
            Rooms = [topRoom],
            Walls = middle.Walls.Select(w => w with { Id = w.Id.Replace("upper-", "top-", StringComparison.Ordinal), LeftRoomId = topRoom.Id }).ToList(),
            SlabOpenings = [middle.SlabOpenings[0] with { Id = "top-flight-void" }]
        };
        var upperStair = plan.Stairs[0] with
        {
            Id = "upper-stair", LowerStoreyId = middle.Id, UpperStoreyId = top.Id,
            LowerRoomId = middle.Rooms[0].Id, UpperRoomId = topRoom.Id,
            RequiredSlabOpeningId = "top-flight-void"
        };
        var roof = plan.Roofs[0] with { Id = "top-flat-roof", Vertices = plan.Roofs[0].Vertices.Select(p => p with { Z = 8.8 }).ToList() };
        return plan with { Name = "Three storeys with vertically stacked straight flights", Storeys = [.. plan.Storeys, top], Stairs = [.. plan.Stairs, upperStair], Roofs = [roof] };
    }

    public static Polygon2 Rect(double left, double bottom, double right, double top) => new()
    {
        Outer = [new(left, bottom), new(right, bottom), new(right, top), new(left, top)], Holes = []
    };

    private static BuildingPlan Base(List<StoreyPlan> storeys, List<StairPlan> stairs, List<RoofPlane> roofs) => new()
    {
        SchemaVersion = "bubo-building-plan/v1", Id = "building-example", Name = "Empty masonry building",
        Purpose = "Architectural planning of an empty navigable game building", ArchitecturalStyle = "Restrained masonry with flat parapet roof",
        StyleSha256 = new string('a', 64), CoordinateSystem = "xy-metres-z-up", EntryRoomId = "ground-room",
        Numerics = new NumericalPolicy { LinearTolerance = .000001, AngularToleranceDegrees = .01, MaxCoordinateMagnitude = 10000, MinimumFeatureSize = .001 },
        Player = new PlayerEnvelope { Radius = .3, Height = 1.8, MaxStepHeight = .22, MinLandingDepth = 1.2 },
        Budgets = new PlanBudgets { MaxStoreys = 8, MaxRooms = 64, MaxWalls = 256, MaxOpenings = 256, MaxStairs = 16, MaxRoofPlanes = 64, MaxVertices = 10000, MaxGeometryChecks = 1_000_000, MaxPreviewBytes = 4_194_304, MaxExportBytes = 16_777_216, MaxGeneratedTriangles = 100_000 },
        Materials = [new MaterialSpec { Id = "masonry", Name = "Warm masonry", Color = "#C7B9A1", Roughness = .8, Metallic = 0 }],
        DetailProfiles = [new DetailProfile { Id = "restrained-detail", Name = "Low plinth and cornice", PlinthHeight = .2, PlinthProjection = .03, CorniceHeight = .12, CorniceProjection = .08, EaveOverhang = .3, FasciaDepth = .12 }],
        Storeys = storeys, Stairs = stairs, Roofs = roofs
    };

    private static StoreyPlan Storey(string id, double elevation, double height, Polygon2 floor, RoomPlan room, bool entry)
    {
        var walls = new List<WallPlan>();
        var ringIndex = 0;
        foreach (var ring in new[] { floor.Outer }.Concat(floor.Holes))
        {
            for (var i = 0; i < ring.Count; i++) walls.Add(new WallPlan
            {
                Id = $"{id}-wall-{ringIndex}-{i}", Start = ring[i], End = ring[(i + 1) % ring.Count],
                Thickness = .2, Height = height, MaterialId = "masonry", DetailProfileId = "restrained-detail",
                LeftRoomId = room.Id, RightRoomId = null,
                Openings = entry && ringIndex == 0 && i == 0
                    ? [new WallOpening { Id = id + "-entry", Kind = OpeningKind.Door, Offset = 1, Width = 1.2, Sill = 0, Head = 2.2, Pivot = DoorPivot.Start, Swing = DoorSwing.Left }]
                    : []
            });
            ringIndex++;
        }
        return new StoreyPlan
        {
            Id = id, Name = id + " storey", Elevation = elevation, Height = height, SlabThickness = .2,
            FloorMaterialId = "masonry", Footprint = floor, Rooms = [room], Walls = walls, SlabOpenings = []
        };
    }

    private static RoofPlane Roof(string id, List<Point2> ring, double z) => new()
    {
        Id = id, Vertices = ring.Select(p => new Point3(p.X, p.Y, z)).ToList(), MaterialId = "masonry", DetailProfileId = "restrained-detail"
    };

    // Intersections of adjacent left-offset lines preserve the exact angled wall faces.
    private static List<Point2> OffsetLeft(List<Point2> ring, double offset)
    {
        static Point2 Sub(Point2 a, Point2 b) => new(a.X - b.X, a.Y - b.Y);
        static Point2 Add(Point2 a, Point2 b) => new(a.X + b.X, a.Y + b.Y);
        static Point2 Mul(Point2 a, double s) => new(a.X * s, a.Y * s);
        static double Cross(Point2 a, Point2 b) => a.X * b.Y - a.Y * b.X;
        var shifted = new List<(Point2 A, Point2 D)>();
        for (var i = 0; i < ring.Count; i++)
        {
            var d = Sub(ring[(i + 1) % ring.Count], ring[i]);
            var length = Math.Sqrt(d.X * d.X + d.Y * d.Y);
            shifted.Add((Add(ring[i], Mul(new(-d.Y, d.X), offset / length)), d));
        }
        var result = new List<Point2>();
        for (var i = 0; i < shifted.Count; i++)
        {
            var current = shifted[i]; var previous = shifted[(i + shifted.Count - 1) % shifted.Count];
            var t = Cross(Sub(previous.A, current.A), previous.D) / Cross(current.D, previous.D);
            result.Add(Add(current.A, Mul(current.D, t)));
        }
        return result;
    }
}
