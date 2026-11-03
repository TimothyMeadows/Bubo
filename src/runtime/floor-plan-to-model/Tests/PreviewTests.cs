using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Bubo.FloorPlan.Domain;
using Bubo.FloorPlan.Infrastructure;

namespace Bubo.FloorPlan.Tests;

public static class PreviewTests
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    public static void Run()
    {
        Check.Case("preview complete single-storey architectural drawing inventory", () =>
        {
            var plan = WithWindow(PlanExamples.SingleStorey());
            var drawings = new SvgPlanRenderer().Render(plan);
            Check.True(drawings.Count == 7, "One floor, roof, four elevations and principal section required");
            Check.True(drawings.Keys.All(k => !Path.IsPathRooted(k) && Path.GetFileName(k) == k && k.EndsWith(".svg", StringComparison.Ordinal)), "Only safe relative SVG names allowed");
            foreach (var drawing in drawings.Values) SafeSvg(drawing);
            var floor = XDocument.Parse(drawings["floor-01.svg"]).Root!;
            foreach (var room in plan.Storeys[0].Rooms)
            {
                Check.True(Kind(floor, "room").Any(e => (string?)e.Attribute("data-room-id") == room.Id), "Missing room geometry");
                Check.True(Kind(floor, "room-dimension").Any(e => (string?)e.Attribute("data-room-id") == room.Id && (double?)e.Attribute("data-area-m2") > 0), "Missing room label/area");
            }
            foreach (var wall in plan.Storeys[0].Walls)
            {
                var element = Kind(floor, "wall").Single(e => (string?)e.Attribute("data-wall-id") == wall.Id);
                Check.True((double?)element.Attribute("data-thickness") == wall.Thickness, "Actual wall thickness absent");
                foreach (var opening in wall.Openings)
                {
                    var o = Kind(floor, "opening").Single(e => (string?)e.Attribute("data-opening-id") == opening.Id);
                    Check.True((double?)o.Attribute("data-width") == opening.Width && (double?)o.Attribute("data-sill") == opening.Sill && (double?)o.Attribute("data-head") == opening.Head, "Opening dimensions changed");
                }
            }
            Check.True(Kind(floor, "dimension").Any(e => (string?)e.Attribute("data-dimension-kind") == "overall-width"), "Missing overall dimensions");
        });

        Check.Case("preview four exterior projections show actual facade openings and roof heights", () =>
        {
            var plan = WithWindow(PlanExamples.SingleStorey());
            var drawings = new SvgPlanRenderer().Render(plan);
            foreach (var direction in new[] { "north", "south", "east", "west" })
            {
                var root = XDocument.Parse(drawings["elevation-" + direction + ".svg"]).Root!;
                Check.True((string?)root.Attribute("data-direction") == direction, "Wrong cardinal projection");
                Check.True(Kind(root, "exterior-wall").Any(), "Exterior facade geometry absent");
                Check.True(Kind(root, "slab-edge").Any(e => (double?)e.Attribute("data-thickness") == plan.Storeys[0].SlabThickness), "Exterior slab edges must use actual slab thickness");
                Check.True(Kind(root, "roof-profile").Count() == plan.Roofs.Count, "Roof projection absent");
                Check.True(Kind(root, "floor-level").Any(e => (double?)e.Attribute("data-elevation") == 0), "Common datum missing");
            }
            var south = XDocument.Parse(drawings["elevation-south.svg"]).Root!;
            var east = XDocument.Parse(drawings["elevation-east.svg"]).Root!;
            Check.True(Kind(south, "opening").Any(e => (string?)e.Attribute("data-opening-id") == "ground-entry"), "South exterior door absent");
            Check.True(Kind(east, "opening").Any(e => (string?)e.Attribute("data-opening-id") == "east-window"), "East exterior window absent");
        });

        Check.Case("preview complete stair connection landings slab void and section", () =>
        {
            var plan = PlanExamples.MultiStorey();
            var drawings = new SvgPlanRenderer().Render(plan);
            Check.True(drawings.Count == 9, "Two storeys require nine drawings including stair section");
            foreach (var name in new[] { "floor-01.svg", "floor-02.svg" })
            {
                var root = XDocument.Parse(drawings[name]).Root!;
                Check.True(Kind(root, "stair").Count() == 1, "Stair must appear on both connected storeys");
                Check.True(Kind(root, "landing").Count() == 2, "Both landings must appear");
            }
            var upper = XDocument.Parse(drawings["floor-02.svg"]).Root!;
            Check.True(Kind(upper, "slab-void").Single().Attribute("data-opening-id")!.Value == "upper-flight-void", "Actual upper slab opening absent");
            var section = XDocument.Parse(drawings["section-stair-01.svg"]).Root!;
            var stair = Kind(section, "stair-section").Single();
            Check.True((int?)stair.Attribute("data-riser-count") == 15 && (double?)stair.Attribute("data-riser-height") == .2, "Stair rise changed");
            Check.True((double?)stair.Attribute("data-total-rise") == 3 && (double?)stair.Attribute("data-headroom") == 2.2, "Stair section missing heights");
            Check.True(Kind(section, "landing").Count() == 2 && Kind(section, "section-slab-void").Any(), "Section must cut real landings and slab void");
            var voidLine = Kind(section, "section-slab-void").Single();
            var voidLeft = (double)voidLine.Attribute("x1")!; var voidRight = (double)voidLine.Attribute("x2")!;
            foreach (var slab in Kind(section, "cut-slab").Where(e => (string?)e.Attribute("data-storey-id") == "upper"))
            {
                var left = (double)slab.Attribute("x")!; var right = left + (double)slab.Attribute("width")!;
                Check.True(right <= voidLeft + 1e-7 || left >= voidRight - 1e-7, "Section slab incorrectly bridges stair void");
            }
        });

        Check.Case("preview stacked flights retain separate floor directions callouts and visible void annotations", () =>
        {
            var plan = PlanExamples.StackedThreeStorey();
            var drawings = new SvgPlanRenderer().Render(plan);
            var floors = plan.Storeys.OrderBy(s => s.Elevation).ToList();
            for (var i = 0; i < floors.Count; i++)
            {
                var floor = floors[i]; var root = XDocument.Parse(drawings[$"floor-{i + 1:00}.svg"]).Root!;
                var annotations = Kind(root, "annotation-layer").Single();
                Check.True(root.Elements().Last() == annotations, "Floor annotations must paint after every geometry fill");
                var connected = plan.Stairs.Where(s => s.LowerStoreyId == floor.Id || s.UpperStoreyId == floor.Id).ToList();
                var labels = Kind(annotations, "stair-annotation").ToList();
                Check.True(labels.Count == connected.Count, "Every connected flight needs a top-layer callout");
                Check.True(labels.Select(e => ((double)e.Attribute("data-label-x")!, (double)e.Attribute("data-label-y")!)).Distinct().Count() == labels.Count, "Stacked flight labels must have distinct callout positions");
                foreach (var stair in connected)
                {
                    var direction = stair.LowerStoreyId == floor.Id ? "up" : "down";
                    var destination = direction == "up" ? stair.UpperStoreyId : stair.LowerStoreyId;
                    var label = labels.Single(e => (string?)e.Attribute("data-stair-id") == stair.Id);
                    Check.True((string?)label.Attribute("data-travel-direction") == direction && (string?)label.Attribute("data-to-storey-id") == destination, "Flight direction must refer to the current floor");
                    Check.True(label.Value.Contains(stair.Id, StringComparison.Ordinal) && label.Value.Contains(direction.ToUpperInvariant() + " to " + destination, StringComparison.Ordinal), "Flight identity and direction must remain readable text");
                    Check.True(Kind(annotations, "annotation-leader").Any(e => (string?)e.Attribute("data-stair-id") == stair.Id), "Each callout needs a leader to its flight");
                    var arrow = Kind(annotations, "stair-direction-arrow").Single(e => (string?)e.Attribute("data-stair-id") == stair.Id).Elements(Svg + "line").First();
                    var deltaX = (double)arrow.Attribute("x2")! - (double)arrow.Attribute("x1")!;
                    Check.True(direction == "up" ? deltaX > 0 : deltaX < 0, "Plan arrows must ascend from lower floors and descend from upper floors");
                }
                foreach (var opening in floor.SlabOpenings)
                {
                    var outline = Kind(annotations, "slab-void-outline").Single(e => (string?)e.Attribute("data-opening-id") == opening.Id);
                    Check.True((string?)outline.Element(Svg + "path")!.Attribute("fill") == "none", "Top-layer void outline must preserve tread visibility");
                    var label = Kind(annotations, "slab-void-annotation").Single(e => (string?)e.Attribute("data-opening-id") == opening.Id);
                    Check.True(label.Value.Contains("VOID " + opening.Id, StringComparison.Ordinal), "Void identity must remain visible after stair fills");
                }
                SafeSvg(drawings[$"floor-{i + 1:00}.svg"]);
            }
            var middle = XDocument.Parse(drawings["floor-02.svg"]).Root!;
            Check.True(Kind(middle, "stair-annotation").Select(e => (string?)e.Attribute("data-travel-direction")).ToHashSet().SetEquals(new[] { "up", "down" }), "Middle floor must show both UP and DOWN for coincident flights");
        });

        Check.Case("preview prefix-shaped semantic identities cannot collide with annotation IDs", () =>
        {
            foreach (var plan in PrefixShapedPlans())
            {
                var drawings = new SvgPlanRenderer().Render(plan);
                foreach (var svg in drawings.Values) SafeSvg(svg);
                foreach (var name in drawings.Keys.Where(k => k.StartsWith("floor-", StringComparison.Ordinal)))
                {
                    var root = XDocument.Parse(drawings[name]).Root!;
                    foreach (var annotation in Kind(root, "annotation-layer").Single().Descendants().Where(e => e.Attribute("id") is not null))
                        Check.True(annotation.Attribute("id")!.Value.StartsWith("annotation-", StringComparison.Ordinal), "Annotation identifiers must use a namespace disjoint from semantic geometry identifiers");
                }
            }
        });

        Check.Case("preview angled courtyard holes and roof retain common coordinates", () =>
        {
            var plan = PlanExamples.AngledCourtyard();
            var drawings = new SvgPlanRenderer().Render(plan);
            var floor = XDocument.Parse(drawings["floor-01.svg"]).Root!;
            var roof = XDocument.Parse(drawings["roof-plan.svg"]).Root!;
            Check.True((string?)floor.Attribute("data-world-bounds") == (string?)roof.Attribute("data-world-bounds"), "Floor and roof lost common coordinate bounds");
            var footprint = Kind(floor, "footprint").Single();
            Check.True(footprint.Attribute("d")!.Value.Count(c => c == 'M') == 2 && (string?)footprint.Attribute("fill-rule") == "evenodd", "Courtyard hole must remain open");
            Check.True(Kind(roof, "roof-plane").Count() == 4, "Courtyard roof planes missing");
            var section = XDocument.Parse(drawings["section-principal.svg"]).Root!;
            Check.True(Kind(section, "cut-slab").Count() >= 2, "Principal section must intersect courtyard gap, not a bounding box slab");
            foreach (var drawing in drawings.Values) SafeSvg(drawing);
        });

        Check.Case("preview labels escape XML and cannot introduce active SVG", () =>
        {
            var plan = PlanExamples.SingleStorey();
            const string hostile = "<script>alert('x')</script> & <image href='https://example.invalid/x'/>";
            plan = plan with { Name = hostile, Storeys = [plan.Storeys[0] with { Rooms = [plan.Storeys[0].Rooms[0] with { Name = hostile }] }] };
            var drawings = new SvgPlanRenderer().Render(plan);
            foreach (var drawing in drawings.Values) SafeSvg(drawing);
            var root = XDocument.Parse(drawings["floor-01.svg"]).Root!;
            Check.True(root.Element(Svg + "title")!.Value.Contains(hostile, StringComparison.Ordinal), "Escaped name not preserved");
            Check.True(drawings["floor-01.svg"].Contains("&lt;script&gt;", StringComparison.Ordinal), "User label must be escaped");
        });

        Check.Case("preview exact cumulative UTF8 budget boundary and invariant determinism", () =>
        {
            var plan = PlanExamples.SingleStorey(); var renderer = new SvgPlanRenderer();
            var first = renderer.Render(plan);
            var exactBytes = first.Values.Sum(v => (long)Encoding.UTF8.GetByteCount(v));
            var exact = renderer.Render(plan with { Budgets = plan.Budgets with { MaxPreviewBytes = exactBytes } });
            Check.True(first.All(e => exact[e.Key] == e.Value), "Exact byte budget must succeed without modifying output");
            Check.Throws<InvalidOperationException>(() => renderer.Render(plan with { Budgets = plan.Budgets with { MaxPreviewBytes = exactBytes - 1 } }));
            var culture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                var second = renderer.Render(plan);
                Check.True(first.All(e => second[e.Key] == e.Value), "Locale must not change preview bytes");
            }
            finally { CultureInfo.CurrentCulture = culture; }
        });

        Check.Case("preview stacked windows remain two separate section voids", () =>
        {
            var plan = PlanExamples.SingleStorey(); var floor = plan.Storeys[0]; var wall = floor.Walls[1];
            var windows = new List<WallOpening>
            {
                new() { Id = "lower-window", Kind = OpeningKind.Window, Offset = 3, Width = 2, Sill = .4, Head = 1.2, Pivot = null, Swing = null },
                new() { Id = "upper-window", Kind = OpeningKind.Window, Offset = 3, Width = 2, Sill = 1.6, Head = 2.4, Pivot = null, Swing = null }
            };
            plan = plan with { Storeys = [floor with { Walls = floor.Walls.Select(w => w.Id == wall.Id ? wall with { Openings = windows } : w).ToList() }] };
            var section = XDocument.Parse(new SvgPlanRenderer().Render(plan)["section-principal.svg"]).Root!;
            Check.True(Kind(section, "section-opening").Select(e => (string?)e.Attribute("data-opening-id")).ToHashSet().SetEquals(windows.Select(w => w.Id)), "Every stacked opening must survive the section cut");
            var segments = Kind(section, "cut-wall").Where(e => (string?)e.Attribute("data-wall-id") == wall.Id).ToList();
            Check.True(segments.Count == 3, "Two vertically stacked windows require three solid wall intervals");
            foreach (var segment in segments)
            foreach (var window in windows)
            {
                var low = (double)segment.Attribute("data-bottom-z")!; var high = (double)segment.Attribute("data-top-z")!;
                Check.True(high <= window.Sill || low >= window.Head, "A section wall segment filled an authored window");
            }
        });

        Check.Case("preview finite oblique stair cuts use actual tread intersection spacing and soffit", () =>
        {
            const double angle = Math.PI / 6;
            var plan = TransformXY(PlanExamples.MultiStorey(), p => new(p.X * Math.Cos(angle) - p.Y * Math.Sin(angle), p.X * Math.Sin(angle) + p.Y * Math.Cos(angle)));
            var sheets = new SvgPlanRenderer().Render(plan);
            var principal = XDocument.Parse(sheets["section-principal.svg"]).Root!;
            var stair = Kind(principal, "stair-section").Single();
            var lengths = Kind(stair, "tread-cut").Select(e => (double)e.Attribute("data-cut-end")! - (double)e.Attribute("data-cut-start")!).ToList();
            var expected = plan.Stairs[0].TreadDepth / Math.Cos(angle);
            Check.True(lengths.Any(l => Math.Abs(l - expected) < 1e-7), "Oblique tread spacing must be tread depth / cos(angle), rather than projected centreline spacing");
            Check.True(lengths.All(l => l <= expected + 1e-7), "Finite tread widths must clip the cut");
            var axial = XDocument.Parse(sheets["section-stair-01.svg"]).Root!;
            var soffit = Kind(axial, "stair-soffit").Single();
            Check.True(Math.Abs((double)soffit.Attribute("data-start-z")! + plan.Stairs[0].StructureThickness) < 1e-7, "Soffit must start below the lower finished floor");
            Check.True(Math.Abs((double)soffit.Attribute("data-end-z")! - (3 - .2 - plan.Stairs[0].StructureThickness)) < 1e-7, "Soffit must follow the riser-bottom pitch line");
            SafeSvg(sheets["section-principal.svg"]);
        });

        Check.Case("preview large world datum preserves small areas and roof label positions", () =>
        {
            var source = PlanExamples.SingleStorey();
            var local = TransformXY(source, p => new(p.X * .01, p.Y * .01));
            local = local with
            {
                Player = local.Player with { Radius = .003, MinLandingDepth = .012 },
                Storeys = local.Storeys.Select(s => s with { Walls = s.Walls.Select(w => w with { Thickness = w.Thickness * .01, Openings = w.Openings.Select(o => o with { Offset = o.Offset * .01, Width = o.Width * .01 }).ToList() }).ToList() }).ToList()
            };
            var translated = TransformXY(local, p => new(p.X + 999999, p.Y + 999999)) with { Numerics = local.Numerics with { MaxCoordinateMagnitude = 1_000_000 } };
            var renderer = new SvgPlanRenderer(); var before = renderer.Render(local); var after = renderer.Render(translated);
            var originalArea = (double)Kind(XDocument.Parse(before["floor-01.svg"]).Root!, "room").Single().Attribute("data-area-m2")!;
            var translatedArea = (double)Kind(XDocument.Parse(after["floor-01.svg"]).Root!, "room").Single().Attribute("data-area-m2")!;
            Check.True(Math.Abs(originalArea - translatedArea) < 1e-10, "World translation must not corrupt clear room area");
            foreach (var svg in after.Values) SafeSvg(svg);
            var roof = Kind(XDocument.Parse(after["roof-plan.svg"]).Root!, "roof-plane").Single();
            var label = roof.Elements(Svg + "text").First();
            Check.True((double)label.Attribute("x")! > 100 && (double)label.Attribute("x")! < 1000, "Roof centroid must remain inside the drawing panel");
        });

        Check.Case("preview small byte budget stops stair primitive construction with bounded allocation", () =>
        {
            var plan = ManyLongFlights();
            var nativeValidator = new BuildingPlanValidator();
            nativeValidator.RequireValid(plan);
            var measuredValidator = new AllocationMeasuredValidator(nativeValidator);
            var renderer = new SvgPlanRenderer(measuredValidator);
            var start = GC.GetAllocatedBytesForCurrentThread();
            var error = Check.Throws<InvalidOperationException>(() => renderer.Render(plan));
            var rendererAllocated = GC.GetAllocatedBytesForCurrentThread() - start;
            var validationAllocated = measuredValidator.AllocatedBytes;
            var drawingAllocated = rendererAllocated - validationAllocated;
            Console.WriteLine($"ALLOCATION preview-budget: rendererBytes={rendererAllocated}; nativeValidationBytes={validationAllocated}; additionalDrawingBytes={drawingAllocated}; capBytes=16000000; exact native RequireValid allocation on the same renderer input is subtracted so the cap measures additional SVG construction.");
            Check.True(measuredValidator.Calls == 1 && ReferenceEquals(measuredValidator.ValidatedPlan, plan), "Allocation baseline must be the actual native validation invocation on the same stress input");
            Check.True(error.Message.Contains("constructing drawing primitives", StringComparison.Ordinal), "The budget must abort during construction before serializing the whole floor");
            Check.True(drawingAllocated >= 0 && drawingAllocated < 16_000_000, $"Small preview budget allocated {drawingAllocated} additional SVG bytes (renderer {rendererAllocated}, native validation {validationAllocated}) while constructing many long flights");
        });

        Check.Case("preview cancellation and domain validation precede drawing output", () =>
        {
            var plan = PlanExamples.SingleStorey(); var renderer = new SvgPlanRenderer();
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            Check.Throws<OperationCanceledException>(() => renderer.Render(plan, cancel.Token));
            Check.Throws<PlanValidationException>(() => renderer.Render(plan with { CoordinateSystem = "arbitrary" }));
        });
    }

    private static BuildingPlan WithWindow(BuildingPlan plan)
    {
        var floor = plan.Storeys[0]; var wall = floor.Walls[1];
        var window = new WallOpening { Id = "east-window", Kind = OpeningKind.Window, Offset = 2, Width = 1.5, Sill = 1, Head = 2.4, Pivot = null, Swing = null };
        return plan with { Storeys = [floor with { Walls = floor.Walls.Select(w => w.Id == wall.Id ? wall with { Openings = [window] } : w).ToList() }] };
    }
    private sealed class AllocationMeasuredValidator(IBuildingPlanValidator inner) : IBuildingPlanValidator
    {
        public long AllocatedBytes { get; private set; }
        public int Calls { get; private set; }
        public BuildingPlan? ValidatedPlan { get; private set; }
        public ValidationReport Validate(BuildingPlan plan, CancellationToken cancellationToken = default) => inner.Validate(plan, cancellationToken);
        public void RequireValid(BuildingPlan plan, CancellationToken cancellationToken = default)
        {
            Calls++;
            ValidatedPlan = plan;
            var start = GC.GetAllocatedBytesForCurrentThread();
            try { inner.RequireValid(plan, cancellationToken); }
            finally { AllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - start; }
        }
    }
    private static IEnumerable<BuildingPlan> PrefixShapedPlans()
    {
        var plan = PlanExamples.SingleStorey(); var floor = plan.Storeys[0];
        yield return plan with { Storeys = [floor with { Walls = floor.Walls.Select((w, i) => i < 2 ? w with { Id = i == 0 ? "a" : "label-a" } : w).ToList() }] };

        var windowed = WithWindow(plan);
        yield return windowed with { Storeys = [windowed.Storeys[0] with { Walls = windowed.Storeys[0].Walls.Select(w => w with { Openings = w.Openings.Select(o => o with { Id = o.Kind == OpeningKind.Door ? "a" : "label-a" }).ToList() }).ToList() }] };

        var template = floor.Walls[0];
        var a = floor.Rooms[0] with { Id = "a", Boundary = PlanExamples.Rect(.1, .1, 4.9, 7.9) };
        var b = a with { Id = "label-a", Boundary = PlanExamples.Rect(5.1, .1, 9.9, 7.9) };
        WallPlan Wall(string id, Point2 start, Point2 end, string left, string? right = null) => template with { Id = id, Start = start, End = end, LeftRoomId = left, RightRoomId = right, Openings = [] };
        List<WallPlan> walls =
        [
            Wall("south-a", new(0, 0), new(5, 0), a.Id) with { Openings = template.Openings },
            Wall("south-b", new(5, 0), new(10, 0), b.Id), Wall("east", new(10, 0), new(10, 8), b.Id),
            Wall("north-b", new(10, 8), new(5, 8), b.Id), Wall("north-a", new(5, 8), new(0, 8), a.Id), Wall("west", new(0, 8), new(0, 0), a.Id),
            Wall("partition", new(5, 0), new(5, 8), a.Id, b.Id) with { Openings = [template.Openings[0] with { Id = "connecting-door", Offset = 3 }] }
        ];
        yield return plan with { EntryRoomId = a.Id, Storeys = [floor with { Rooms = [a, b], Walls = walls }] };

        foreach (var suffix in new[] { "label-a", "arrow-a" })
        {
            var stacked = PlanExamples.StackedThreeStorey();
            yield return stacked with { Stairs = stacked.Stairs.Select((s, i) => s with { Id = i == 0 ? "a" : suffix }).ToList() };
        }
        foreach (var suffix in new[] { "label-a", "outline-a" })
        {
            var multiple = PlanExamples.MultiStorey(); var upper = multiple.Storeys[1];
            yield return multiple with
            {
                Storeys = [multiple.Storeys[0], upper with { SlabOpenings = [upper.SlabOpenings[0] with { Id = "a" }, new SlabOpening { Id = suffix, Boundary = PlanExamples.Rect(8, 6, 9, 7) }] }],
                Stairs = [multiple.Stairs[0] with { RequiredSlabOpeningId = "a" }]
            };
        }
    }
    private static BuildingPlan TransformXY(BuildingPlan plan, Func<Point2, Point2> transform)
    {
        Polygon2 Polygon(Polygon2 p) => p with { Outer = p.Outer.Select(transform).ToList(), Holes = p.Holes.Select(h => h.Select(transform).ToList()).ToList() };
        return plan with
        {
            Storeys = plan.Storeys.Select(s => s with { Footprint = Polygon(s.Footprint), Rooms = s.Rooms.Select(r => r with { Boundary = Polygon(r.Boundary) }).ToList(), Walls = s.Walls.Select(w => w with { Start = transform(w.Start), End = transform(w.End) }).ToList(), SlabOpenings = s.SlabOpenings.Select(o => o with { Boundary = Polygon(o.Boundary) }).ToList() }).ToList(),
            Stairs = plan.Stairs.Select(s => s with { Start = transform(s.Start), End = transform(s.End), LowerLanding = Polygon(s.LowerLanding), UpperLanding = Polygon(s.UpperLanding) }).ToList(),
            Roofs = plan.Roofs.Select(r => r with { Vertices = r.Vertices.Select(v => { var p = transform(new(v.X, v.Y)); return new Point3(p.X, p.Y, v.Z); }).ToList() }).ToList()
        };
    }
    private static BuildingPlan ManyLongFlights()
    {
        var plan = PlanExamples.MultiStorey();
        var footprint = PlanExamples.Rect(0, 0, 1030, 134); var room = PlanExamples.Rect(.1, .1, 1029.9, 133.9);
        var storeys = plan.Storeys.Select((s, index) => s with
        {
            Elevation = index == 0 ? 0 : 819.2, Height = index == 0 ? 819 : 2.8,
            Footprint = footprint, Rooms = [s.Rooms[0] with { Boundary = room }],
            Walls = s.Walls.Select((w, i) => w with { Start = footprint.Outer[i], End = footprint.Outer[(i + 1) % 4], Height = index == 0 ? 819 : 2.8 }).ToList(),
            SlabOpenings = index == 0 ? [] : [s.SlabOpenings[0] with { Boundary = PlanExamples.Rect(3, 2.4, 1026.75, 129.6) }]
        }).ToList();
        var flights = Enumerable.Range(0, 64).Select(i => plan.Stairs[0] with
        {
            Id = "flight-" + i.ToString(CultureInfo.InvariantCulture), Start = new(3, 3 + i * 2), End = new(1026.75, 3 + i * 2),
            RiserCount = 4096, RiserHeight = .2, TreadDepth = .25,
            LowerLanding = PlanExamples.Rect(1.8, 2.4 + i * 2, 3, 3.6 + i * 2), UpperLanding = PlanExamples.Rect(1026.75, 2.4 + i * 2, 1027.95, 3.6 + i * 2)
        }).ToList();
        return plan with
        {
            Storeys = storeys, Stairs = flights,
            Roofs = [plan.Roofs[0] with { Vertices = PlanExamples.Rect(-.3, -.3, 1030.3, 134.3).Outer.Select(p => new Point3(p.X, p.Y, 822)).ToList() }],
            Budgets = plan.Budgets with { MaxStairs = 64, MaxGeometryChecks = 5_000_000, MaxPreviewBytes = 80_000 }
        };
    }
    private static IEnumerable<XElement> Kind(XElement root, string kind) => root.Descendants().Where(e => (string?)e.Attribute("data-kind") == kind);
    private static void SafeSvg(string text)
    {
        using var bytes = new MemoryStream(Encoding.UTF8.GetBytes(text));
        var document = XDocument.Load(bytes);
        Check.True(document.Declaration is null || string.Equals(document.Declaration.Encoding, "utf-8", StringComparison.OrdinalIgnoreCase), "SVG bytes must agree with any XML encoding declaration");
        var root = document.Root!;
        Check.True(root.Name == Svg + "svg", "Drawing must be real SVG XML");
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "svg", "title", "desc", "rect", "g", "text", "line", "path" };
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var viewBox = root.Attribute("viewBox")!.Value.Split(' ').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        foreach (var element in root.DescendantsAndSelf())
        {
            Check.True(element.Name.Namespace == Svg && allowed.Contains(element.Name.LocalName), "Unexpected active or external SVG element");
            foreach (var attr in element.Attributes().Where(a => !a.IsNamespaceDeclaration))
            {
                Check.True(!attr.Name.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase) && attr.Name.LocalName is not ("href" or "style"), "Unexpected executable or external attribute");
                if (attr.Name.LocalName == "id") Check.True(ids.Add(attr.Value), "Duplicate SVG semantic id");
            }
            if (element.Name.LocalName == "text")
            {
                var x = (double)element.Attribute("x")!; var y = (double)element.Attribute("y")!;
                Check.True(x > 0 && x < viewBox[2] && y > 0 && y < viewBox[3], "Text anchor clipped outside viewbox");
            }
        }
    }
}
