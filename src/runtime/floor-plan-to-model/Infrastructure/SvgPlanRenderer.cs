using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Bubo.FloorPlan.Application;
using Bubo.FloorPlan.Domain;

namespace Bubo.FloorPlan.Infrastructure;

public sealed class SvgPlanRenderer : IArchitecturalPreviewRenderer
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    private const double DrawingLeft = 110, DrawingTop = 170, DrawingWidth = 840, DrawingHeight = 620, ScheduleLeft = 1060, SheetWidth = 1720;
    private readonly IBuildingPlanValidator validator;

    public SvgPlanRenderer() : this(new BuildingPlanValidator()) { }
    public SvgPlanRenderer(IBuildingPlanValidator validator) => this.validator = validator ?? throw new ArgumentNullException(nameof(validator));

    public IReadOnlyDictionary<string, string> Render(BuildingPlan plan, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        validator.RequireValid(plan, cancellationToken);
        var budget = new PreviewBudget(plan.Budgets.MaxPreviewBytes, cancellationToken);
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        long bytes = 0;
        var bounds = Bounds.From(plan.Storeys.SelectMany(s => s.Footprint.Outer.Concat(s.Walls.SelectMany(w => WallPolygon(w.Start, w.End, w.Thickness).Outer))).Concat(plan.Roofs.SelectMany(r => r.Vertices.Select(p => new Point2(p.X, p.Y)))));
        void Add(string name, Sheet sheet)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = sheet.Finish();
            bytes = checked(bytes + Encoding.UTF8.GetByteCount(body));
            if (bytes > plan.Budgets.MaxPreviewBytes)
                throw new InvalidOperationException($"Architectural preview exceeds declared MaxPreviewBytes ({plan.Budgets.MaxPreviewBytes}).");
            result.Add(name, body);
        }
        var storeys = plan.Storeys.OrderBy(s => s.Elevation).ThenBy(s => s.Id, StringComparer.Ordinal).ToList();
        for (var i = 0; i < storeys.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Add($"floor-{i + 1:00}.svg", Floor(plan, storeys[i], bounds, budget, cancellationToken));
        }
        Add("roof-plan.svg", Roof(plan, bounds, budget, cancellationToken));
        foreach (var direction in new[] { "north", "south", "east", "west" })
            Add($"elevation-{direction}.svg", Elevation(plan, direction, budget, cancellationToken));
        var principalBounds = Bounds.From(plan.Storeys.SelectMany(s => s.Footprint.Outer));
        var centre = new Point2((principalBounds.MinX + principalBounds.MaxX) / 2, (principalBounds.MinY + principalBounds.MaxY) / 2);
        var principalDirection = principalBounds.Width >= principalBounds.Height ? new Point2(1, 0) : new Point2(0, 1);
        Add("section-principal.svg", Section(plan, "principal", centre, principalDirection, null, budget, cancellationToken));
        var stairs = plan.Stairs.OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
        for (var i = 0; i < stairs.Count; i++)
            Add($"section-stair-{i + 1:00}.svg", Section(plan, stairs[i].Id, stairs[i].Start, Unit(Sub(stairs[i].End, stairs[i].Start)), stairs[i], budget, cancellationToken));
        return result;
    }

    private static Sheet Floor(BuildingPlan plan, StoreyPlan floor, Bounds bounds, PreviewBudget budget, CancellationToken ct)
    {
        var sheet = new Sheet(plan, "floor-plan", $"{floor.Name} — floor plan", $"Datum {Signed(floor.Elevation)} m • clear storey height {F(floor.Height)} m • north is +Y", floor.Id, budget);
        var map = PlanMap(bounds);
        sheet.Root.SetAttributeValue("data-projection", "orthographic XY; +X right; +Y up; common metres datum");
        sheet.Root.SetAttributeValue("data-elevation", E(floor.Elevation));
        sheet.Root.SetAttributeValue("data-slab-thickness", E(floor.SlabThickness));
        sheet.Root.SetAttributeValue("data-world-bounds", BoundsText(bounds));
        sheet.Path(sheet.PolygonPath(floor.Footprint, map), "#f5f2e9", "#555", 1.5, "footprint-" + floor.Id, "footprint");
        foreach (var room in floor.Rooms.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var area = Area(room.Boundary);
            var group = sheet.Group("room-" + room.Id, "room", ("data-room-id", room.Id), ("data-area-m2", E(area)));
            group.Add(PathElement(sheet.PolygonPath(room.Boundary, map), "#edf2ed", "#b9c4bb", 1));
            var p = map(InteriorLabel(room.Boundary));
            var roomLabel = sheet.AnnotationGroup("annotation-room-label-" + room.Id, "room-annotation", ("data-room-id", room.Id));
            sheet.Text(p.X, p.Y - 3, room.Id, 14, "#243d38", "middle", roomLabel);
            sheet.Text(p.X, p.Y + 15, F(area) + " m²", 11, "#50615b", "middle", roomLabel);
            var b = Bounds.From(room.Boundary.Outer);
            sheet.Row("room-dimension", room.Id + " — " + room.Name, ("data-room-id", room.Id), ("data-area-m2", E(area)), ("data-width", E(b.Width)), ("data-depth", E(b.Height)));
            sheet.Row("room-measurement", $"  Clear bounds {F(b.Width)} × {F(b.Height)} m; area {F(area)} m²");
            var roomEdges = Edges(room.Boundary.Outer).Select(e => Length(Sub(e.B, e.A))).ToList();
            sheet.Row("room-edge-dimensions", "  Edges (m): " + string.Join(", ", roomEdges.Select(F)), ("data-edge-lengths", string.Join(",", roomEdges.Select(E))));
            foreach (var hole in room.Boundary.Holes)
                sheet.Row("room-hole-dimensions", "  Interior void edges (m): " + string.Join(", ", Edges(hole).Select(e => F(Length(Sub(e.B, e.A))))));
        }
        foreach (var wall in floor.Walls.OrderBy(w => w.Id, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var a = map(wall.Start); var b = map(wall.End);
            var wallLength = Length(Sub(wall.End, wall.Start));
            var group = sheet.Group("wall-" + wall.Id, "wall", ("data-wall-id", wall.Id), ("data-thickness", E(wall.Thickness)), ("data-length", E(wallLength)), ("data-height", E(wall.Height)));
            group.SetAttributeValue("data-start-xy", $"{E(wall.Start.X)},{E(wall.Start.Y)}"); group.SetAttributeValue("data-end-xy", $"{E(wall.End.X)},{E(wall.End.Y)}");
            group.Add(LineElement(a, b, "#303c3b", wall.Thickness * PlanScale(bounds)));
            var mid = Lerp(a, b, 0.5);
            var wallLabel = sheet.AnnotationGroup("annotation-wall-label-" + wall.Id, "wall-annotation", ("data-wall-id", wall.Id));
            sheet.Text(mid.X, mid.Y - 8, wall.Id, 10, "#273c40", "middle", wallLabel);
            sheet.Row("wall-dimension", $"{wall.Id}: L {F(wallLength)} m; t {F(wall.Thickness)} m; H {F(wall.Height)} m", ("data-wall-id", wall.Id), ("data-length", E(wallLength)), ("data-thickness", E(wall.Thickness)), ("data-height", E(wall.Height)));
            foreach (var opening in wall.Openings.OrderBy(o => o.Offset).ThenBy(o => o.Id, StringComparer.Ordinal))
            {
                var unit = Unit(Sub(wall.End, wall.Start));
                var oa = Add(wall.Start, Mul(unit, opening.Offset)); var ob = Add(oa, Mul(unit, opening.Width));
                var o = sheet.Group("opening-" + opening.Id, "opening", ("data-opening-id", opening.Id), ("data-wall-id", wall.Id), ("data-kind-name", opening.Kind.ToString().ToLowerInvariant()), ("data-offset", E(opening.Offset)), ("data-width", E(opening.Width)), ("data-sill", E(opening.Sill)), ("data-head", E(opening.Head)));
                o.Add(LineElement(map(oa), map(ob), "#fff", wall.Thickness * PlanScale(bounds) + 2));
                if (opening.Kind == OpeningKind.Window)
                {
                    var normal = Mul(new Point2(-unit.Y, unit.X), wall.Thickness * 0.28);
                    o.Add(LineElement(map(Add(oa, normal)), map(Add(ob, normal)), "#2b6d91", 1.5));
                    o.Add(LineElement(map(Sub(oa, normal)), map(Sub(ob, normal)), "#2b6d91", 1.5));
                }
                else if (opening.Kind == OpeningKind.Door)
                {
                    var hinge = opening.Pivot == DoorPivot.Start ? oa : ob; var closed = opening.Pivot == DoorPivot.Start ? ob : oa;
                    var side = opening.Swing == DoorSwing.Left ? 1 : -1;
                    var opened = Add(hinge, Mul(new Point2(-unit.Y, unit.X), side * opening.Width));
                    o.Add(LineElement(map(hinge), map(opened), "#985d21", 1.5));
                    var hp = map(hinge); var cp = map(closed); var ep = map(opened); var radius = opening.Width * PlanScale(bounds);
                    o.Add(PathElement($"M {P(cp)} A {F(radius)} {F(radius)} 0 0 {(Cross(Sub(cp, hp), Sub(ep, hp)) > 0 ? 1 : 0)} {P(ep)}", "none", "#b7834b", 1));
                }
                var label = map(Lerp(oa, ob, 0.5));
                var openingLabel = sheet.AnnotationGroup("annotation-opening-label-" + opening.Id, "opening-annotation", ("data-opening-id", opening.Id));
                sheet.Text(label.X, label.Y + 18, opening.Id, 10, "#7a4b20", "middle", openingLabel);
                sheet.Row("opening-dimension", $"  {opening.Id} {opening.Kind}: offset {F(opening.Offset)}; W {F(opening.Width)}; sill/head {F(opening.Sill)}/{F(opening.Head)} m", ("data-opening-id", opening.Id), ("data-width", E(opening.Width)), ("data-offset", E(opening.Offset)), ("data-sill", E(opening.Sill)), ("data-head", E(opening.Head)));
            }
        }
        var callout = 0;
        foreach (var opening in floor.SlabOpenings.OrderBy(o => o.Id, StringComparer.Ordinal))
        {
            var e = sheet.Path(sheet.PolygonPath(opening.Boundary, map), "#fff", "#a5483a", 2, "slab-void-" + opening.Id, "slab-void");
            e.SetAttributeValue("stroke-dasharray", "7 4"); e.SetAttributeValue("data-opening-id", opening.Id);
            var outline = sheet.AnnotationLineGroup("annotation-slab-void-outline-" + opening.Id, "slab-void-outline", ("data-opening-id", opening.Id));
            var line = PathElement(sheet.PolygonPath(opening.Boundary, map), "none", "#a5483a", 2);
            line.SetAttributeValue("stroke-dasharray", "7 4"); outline.Add(line);
            sheet.Callout("annotation-slab-void-label-" + opening.Id, "slab-void-annotation", map(InteriorLabel(opening.Boundary)), "VOID " + opening.Id, callout++, "#a5483a", ("data-opening-id", opening.Id));
            sheet.Row("slab-void-dimension", $"Void {opening.Id}: area {F(Area(opening.Boundary))} m²", ("data-opening-id", opening.Id), ("data-area-m2", E(Area(opening.Boundary))));
        }
        foreach (var stair in plan.Stairs.Where(s => s.LowerStoreyId == floor.Id || s.UpperStoreyId == floor.Id).OrderBy(s => s.Id, StringComparer.Ordinal))
            PlanStair(sheet, plan, floor, stair, map, callout++, ct);
        PlanDimensions(sheet, Bounds.From(floor.Footprint.Outer), map);
        var north = new Point2(990, 210);
        sheet.Line(new(north.X, north.Y + 55), north, "#243b42", 2);
        sheet.Line(north, new(north.X - 7, north.Y + 13), "#243b42", 2); sheet.Line(north, new(north.X + 7, north.Y + 13), "#243b42", 2);
        sheet.Text(north.X, north.Y - 10, "N (+Y)", 13, "#243b42", "middle");
        sheet.Row("floor-dimension", $"Finished floor {Signed(floor.Elevation)} m; slab {F(floor.SlabThickness)} m", ("data-elevation", E(floor.Elevation)), ("data-slab-thickness", E(floor.SlabThickness)));
        sheet.Row("section-reference", "Sections: principal" + (plan.Stairs.Count == 0 ? "" : "; " + string.Join(", ", plan.Stairs.Select(s => s.Id))));
        return sheet;
    }

    private static void PlanStair(Sheet sheet, BuildingPlan plan, StoreyPlan floor, StairPlan stair, Func<Point2, Point2> map, int callout, CancellationToken ct)
    {
        var unit = Unit(Sub(stair.End, stair.Start)); var normal = Mul(new Point2(-unit.Y, unit.X), stair.Width / 2);
        var ascending = stair.LowerStoreyId == floor.Id;
        var travel = ascending ? "up" : "down";
        var destination = ascending ? stair.UpperStoreyId : stair.LowerStoreyId;
        var group = sheet.Group("stair-" + stair.Id, "stair", ("data-stair-id", stair.Id), ("data-lower-storey-id", stair.LowerStoreyId), ("data-upper-storey-id", stair.UpperStoreyId), ("data-riser-count", stair.RiserCount.ToString(CultureInfo.InvariantCulture)), ("data-riser-height", E(stair.RiserHeight)), ("data-tread-depth", E(stair.TreadDepth)), ("data-width", E(stair.Width)), ("data-headroom", E(stair.Headroom)));
        group.SetAttributeValue("data-start-xy", $"{E(stair.Start.X)},{E(stair.Start.Y)}"); group.SetAttributeValue("data-end-xy", $"{E(stair.End.X)},{E(stair.End.Y)}"); group.SetAttributeValue("data-run", E(Length(Sub(stair.End, stair.Start))));
        group.SetAttributeValue("data-structure-thickness", E(stair.StructureThickness));
        group.SetAttributeValue("data-travel-direction", travel); group.SetAttributeValue("data-from-storey-id", floor.Id); group.SetAttributeValue("data-to-storey-id", destination);
        group.Add(PathElement(sheet.RingPath([Add(stair.Start, normal), Add(stair.End, normal), Sub(stair.End, normal), Sub(stair.Start, normal)], map), "#f5ead2", "#8e6b39", 1));
        for (var i = 0; i < stair.RiserCount; i++)
        {
            ct.ThrowIfCancellationRequested(); var p = Add(stair.Start, Mul(unit, i * stair.TreadDepth));
            group.Add(LineElement(map(Add(p, normal)), map(Sub(p, normal)), "#8e6b39", 1));
        }
        foreach (var landing in new[] { ("lower", stair.LowerLanding), ("upper", stair.UpperLanding) })
        {
            var e = PathElement(sheet.PolygonPath(landing.Item2, map), "none", "#a48352", 1.5);
            e.SetAttributeValue("data-kind", "landing"); e.SetAttributeValue("data-landing", landing.Item1); e.SetAttributeValue("data-stair-id", stair.Id); group.Add(e);
        }
        var lane = Mul(normal, ascending ? .36 : -.36);
        var start = map(Add(Lerp(stair.Start, stair.End, ascending ? .15 : .85), lane)); var end = map(Add(Lerp(stair.Start, stair.End, ascending ? .85 : .15), lane));
        var color = ascending ? "#765222" : "#3d6782";
        var arrow = sheet.AnnotationLineGroup("annotation-stair-arrow-" + stair.Id, "stair-direction-arrow", ("data-stair-id", stair.Id), ("data-travel-direction", travel));
        arrow.Add(LineElement(start, end, color, 2)); var tangent = Unit(Sub(end, start)); var arrowNormal = new Point2(-tangent.Y, tangent.X);
        arrow.Add(LineElement(end, Add(Sub(end, Mul(tangent, 11)), Mul(arrowNormal, 5)), color, 2)); arrow.Add(LineElement(end, Sub(Sub(end, Mul(tangent, 11)), Mul(arrowNormal, 5)), color, 2));
        var anchor = map(Add(Lerp(stair.Start, stair.End, ascending ? .35 : .65), lane));
        sheet.Callout("annotation-stair-label-" + stair.Id, "stair-annotation", anchor, stair.Id + "  " + travel.ToUpperInvariant() + " to " + destination, callout, color, ("data-stair-id", stair.Id), ("data-travel-direction", travel), ("data-from-storey-id", floor.Id), ("data-to-storey-id", destination));
        var lower = plan.Storeys.Single(s => s.Id == stair.LowerStoreyId); var upper = plan.Storeys.Single(s => s.Id == stair.UpperStoreyId);
        sheet.Row("stair-dimension", $"{stair.Id}: {lower.Name} {Signed(lower.Elevation)} → {upper.Name} {Signed(upper.Elevation)} m", ("data-stair-id", stair.Id));
        sheet.Row("stair-measurement", $"  {stair.RiserCount} risers × {F(stair.RiserHeight)} m; tread {F(stair.TreadDepth)} m; width {F(stair.Width)} m");
        sheet.Row("stair-headroom", $"  Headroom {F(stair.Headroom)} m; upper slab void {stair.RequiredSlabOpeningId}", ("data-headroom", E(stair.Headroom)), ("data-opening-id", stair.RequiredSlabOpeningId));
        sheet.Row("stair-structure-dimension", $"  Vertical waist thickness below riser-bottom pitch: {F(stair.StructureThickness)} m", ("data-structure-thickness", E(stair.StructureThickness)));
    }

    private static Sheet Roof(BuildingPlan plan, Bounds bounds, PreviewBudget budget, CancellationToken ct)
    {
        var sheet = new Sheet(plan, "roof-plan", "Roof plan", "Orthographic XY roof projection • absolute roof elevations in metres", "roof", budget);
        sheet.Root.SetAttributeValue("data-projection", "orthographic XY; +X right; +Y up; common metres datum");
        sheet.Root.SetAttributeValue("data-world-bounds", BoundsText(bounds));
        var map = PlanMap(bounds);
        foreach (var floor in plan.Storeys.OrderBy(s => s.Elevation))
        {
            var path = sheet.Path(sheet.PolygonPath(floor.Footprint, map), "none", "#9ca4a3", 1, "underlay-" + floor.Id, "storey-underlay"); path.SetAttributeValue("stroke-dasharray", "5 5");
        }
        foreach (var roof in plan.Roofs.OrderBy(r => r.Vertices.Average(p => p.Z)).ThenBy(r => r.Id, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            sheet.CheckGeometry(roof.Vertices.Count);
            var points = roof.Vertices.Select(p => new Point2(p.X, p.Y)).ToList();
            var group = sheet.Group("roof-" + roof.Id, "roof-plane", ("data-roof-id", roof.Id), ("data-vertices-xyz", string.Join(";", roof.Vertices.Select(p => $"{E(p.X)},{E(p.Y)},{E(p.Z)}"))));
            group.Add(PathElement(sheet.RingPath(points, map), "#e7ded0", "#715c46", 2));
            var centre = map(Centroid(points)); sheet.Text(centre.X, centre.Y, roof.Id, 13, "#62462c", "middle", group);
            foreach (var v in roof.Vertices)
            {
                var p = map(new(v.X, v.Y)); sheet.Text(p.X + 6, p.Y - 7, Signed(v.Z), 11, "#62462c", "start", group);
            }
            var normal = Normal(roof.Vertices); var pitch = Math.Atan2(Math.Sqrt(normal.X * normal.X + normal.Y * normal.Y), Math.Abs(normal.Z)) * 180 / Math.PI;
            sheet.Row("roof-dimension", $"{roof.Id}: pitch {F(pitch)}°; Z {F(roof.Vertices.Min(p => p.Z))}–{F(roof.Vertices.Max(p => p.Z))} m", ("data-roof-id", roof.Id), ("data-pitch-degrees", E(pitch)));
            var roofEdges = Edges3(roof.Vertices).Select(e => Distance3(e.A, e.B)).ToList();
            sheet.Row("roof-edge-dimensions", "  3D edge lengths (m): " + string.Join(", ", roofEdges.Select(F)), ("data-edge-lengths", string.Join(",", roofEdges.Select(E))));
            var profile = plan.DetailProfiles.Single(p => p.Id == roof.DetailProfileId);
            sheet.Row("roof-profile-dimension", $"  {profile.Name}: eave {F(profile.EaveOverhang)}; fascia {F(profile.FasciaDepth)} m", ("data-profile-id", profile.Id));
        }
        PlanDimensions(sheet, bounds, map); return sheet;
    }

    private static Sheet Elevation(BuildingPlan plan, string direction, PreviewBudget budget, CancellationToken ct)
    {
        (Point2 right, Point2 towards) = direction switch
        {
            "north" => (new Point2(-1, 0), new Point2(0, 1)), "south" => (new Point2(1, 0), new Point2(0, -1)), "east" => (new Point2(0, 1), new Point2(1, 0)), _ => (new Point2(0, -1), new Point2(-1, 0))
        };
        var world = plan.Storeys.SelectMany(s => s.Footprint.Outer).Concat(plan.Roofs.SelectMany(r => r.Vertices.Select(p => new Point2(p.X, p.Y)))).ToList();
        var low = world.Min(p => Dot(p, right)); var high = world.Max(p => Dot(p, right));
        var minZ = plan.Storeys.Min(s => s.Elevation - s.SlabThickness); var maxZ = plan.Roofs.SelectMany(r => r.Vertices).Max(p => p.Z);
        var map = VerticalMap(low, high, minZ, maxZ);
        var sheet = new Sheet(plan, "exterior-elevation", char.ToUpperInvariant(direction[0]) + direction[1..] + " exterior elevation", "Orthographic architectural projection • common XY datum • absolute Z elevations", direction, budget);
        sheet.Root.SetAttributeValue("data-direction", direction); sheet.Root.SetAttributeValue("data-projection", $"screen horizontal = {E(right.X)}*X + {E(right.Y)}*Y; vertical = Z; opaque faces sorted far to near");
        var faces = plan.Storeys.SelectMany(s => s.Walls.Where(w => w.LeftRoomId is null || w.RightRoomId is null).Select(w => (Floor: s, Wall: w)))
            .Where(f => Facing(f.Wall, towards) > plan.Numerics.LinearTolerance)
            .OrderBy(f => Dot(Lerp(f.Wall.Start, f.Wall.End, 0.5), towards)).ThenBy(f => f.Floor.Elevation).ThenBy(f => f.Wall.Id, StringComparer.Ordinal);
        foreach (var face in faces)
        {
            ct.ThrowIfCancellationRequested(); var floor = face.Floor; var wall = face.Wall;
            var x1 = Dot(wall.Start, right); var x2 = Dot(wall.End, right);
            var group = sheet.Group("facade-" + wall.Id, "exterior-wall", ("data-wall-id", wall.Id), ("data-storey-id", floor.Id), ("data-elevation", E(floor.Elevation)), ("data-height", E(wall.Height)), ("data-depth", E(Dot(Lerp(wall.Start, wall.End, 0.5), towards))));
            var mat = plan.Materials.Single(m => m.Id == wall.MaterialId);
            var slab = RectBetween(map(x1, floor.Elevation - floor.SlabThickness), map(x2, floor.Elevation), "#b6b3a9", "#555e56", 0.7);
            slab.SetAttributeValue("data-kind", "slab-edge"); slab.SetAttributeValue("data-storey-id", floor.Id); slab.SetAttributeValue("data-thickness", E(floor.SlabThickness)); group.Add(slab);
            group.Add(RectBetween(map(x1, floor.Elevation), map(x2, floor.Elevation + wall.Height), mat.Color, "#39413f", 1.3));
            var detail = plan.DetailProfiles.Single(p => p.Id == wall.DetailProfileId);
            if (detail.PlinthHeight > 0) group.Add(RectBetween(map(x1, floor.Elevation), map(x2, floor.Elevation + detail.PlinthHeight), "#c1b9a8", "#615d52", 0.7));
            if (detail.CorniceHeight > 0) group.Add(RectBetween(map(x1, floor.Elevation + wall.Height - detail.CorniceHeight), map(x2, floor.Elevation + wall.Height), "#e1dacb", "#615d52", 0.7));
            var unit = Unit(Sub(wall.End, wall.Start));
            foreach (var opening in wall.Openings.OrderBy(o => o.Offset).ThenBy(o => o.Id, StringComparer.Ordinal))
            {
                var a = Add(wall.Start, Mul(unit, opening.Offset)); var b = Add(a, Mul(unit, opening.Width));
                var e = RectBetween(map(Dot(a, right), floor.Elevation + opening.Sill), map(Dot(b, right), floor.Elevation + opening.Head), opening.Kind == OpeningKind.Window ? "#d5e7ed" : "#b9a38a", "#3f504f", 1.2);
                e.SetAttributeValue("id", "elevation-opening-" + opening.Id); e.SetAttributeValue("data-kind", "opening"); e.SetAttributeValue("data-opening-kind", opening.Kind.ToString().ToLowerInvariant()); e.SetAttributeValue("data-opening-id", opening.Id); e.SetAttributeValue("data-width", E(opening.Width)); e.SetAttributeValue("data-sill", E(opening.Sill)); e.SetAttributeValue("data-head", E(opening.Head)); group.Add(e);
                var mid = map((Dot(a, right) + Dot(b, right)) / 2, floor.Elevation + (opening.Sill + opening.Head) / 2); sheet.Text(mid.X, mid.Y, opening.Id, 10, "#263a40", "middle", group);
                sheet.Row("opening-dimension", $"{opening.Id}: W {F(opening.Width)}; sill/head Z {F(floor.Elevation + opening.Sill)}/{F(floor.Elevation + opening.Head)} m", ("data-opening-id", opening.Id));
            }
            sheet.Row("facade-dimension", $"{wall.Id}: base {Signed(floor.Elevation)}; top {Signed(floor.Elevation + wall.Height)} m", ("data-wall-id", wall.Id), ("data-base-z", E(floor.Elevation)), ("data-top-z", E(floor.Elevation + wall.Height)));
        }
        foreach (var roof in plan.Roofs.OrderBy(r => r.Vertices.Average(p => p.X * towards.X + p.Y * towards.Y)).ThenBy(r => r.Id, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested(); sheet.CheckGeometry(roof.Vertices.Count); var path = "M " + string.Join(" L ", roof.Vertices.Select(p => P(map(p.X * right.X + p.Y * right.Y, p.Z)))) + " Z";
            var e = sheet.Path(path, "#ddd1be", "#6d5941", 2, "roof-elevation-" + roof.Id, "roof-profile");
            e.SetAttributeValue("data-roof-id", roof.Id); e.SetAttributeValue("data-min-z", E(roof.Vertices.Min(p => p.Z))); e.SetAttributeValue("data-max-z", E(roof.Vertices.Max(p => p.Z)));
            sheet.Row("roof-height", $"Roof {roof.Id}: Z {F(roof.Vertices.Min(p => p.Z))}–{F(roof.Vertices.Max(p => p.Z))} m", ("data-roof-id", roof.Id));
        }
        VerticalDimensions(sheet, plan, low, high, minZ, maxZ, map); return sheet;
    }

    private static Sheet Section(BuildingPlan plan, string id, Point2 origin, Point2 direction, StairPlan? focus, PreviewBudget budget, CancellationToken ct)
    {
        var points = plan.Storeys.SelectMany(s => s.Footprint.Outer.Concat(s.Walls.SelectMany(w => WallPolygon(w.Start, w.End, w.Thickness).Outer))).Concat(plan.Roofs.SelectMany(r => r.Vertices.Select(p => new Point2(p.X, p.Y)))).ToList();
        var min = points.Min(p => Dot(Sub(p, origin), direction)); var max = points.Max(p => Dot(Sub(p, origin), direction));
        var minZ = plan.Storeys.Min(s => s.Elevation - s.SlabThickness); var maxZ = plan.Roofs.SelectMany(r => r.Vertices).Max(p => p.Z);
        var map = VerticalMap(min, max, minZ, maxZ);
        var sheet = new Sheet(plan, "vertical-section", focus is null ? "Principal vertical section" : "Stair section — " + id, $"Cut origin ({F(origin.X)}, {F(origin.Y)}) m; direction ({F(direction.X)}, {F(direction.Y)}); horizontal distance along cut", id, budget);
        sheet.Root.SetAttributeValue("data-section-id", id); sheet.Root.SetAttributeValue("data-cut-origin", $"{E(origin.X)},{E(origin.Y)}"); sheet.Root.SetAttributeValue("data-cut-direction", $"{E(direction.X)},{E(direction.Y)}");
        sheet.Root.SetAttributeValue("data-projection", "vertical plane origin + t*direction; screen horizontal=t; vertical=Z; actual polygon/plane intersections");
        foreach (var floor in plan.Storeys.OrderBy(s => s.Elevation))
        {
            ct.ThrowIfCancellationRequested(); var voids = floor.SlabOpenings.Select(o => o.Boundary).ToList();
            foreach (var interval in Intervals(floor.Footprint, origin, direction, voids))
            {
                var e = RectBetween(map(interval.A, floor.Elevation - floor.SlabThickness), map(interval.B, floor.Elevation), "#aaa89e", "#373e3b", 1.3);
                e.SetAttributeValue("data-kind", "cut-slab"); e.SetAttributeValue("data-storey-id", floor.Id); e.SetAttributeValue("data-thickness", E(floor.SlabThickness)); sheet.Drawing.Add(e);
            }
            foreach (var opening in floor.SlabOpenings.OrderBy(o => o.Id, StringComparer.Ordinal))
            foreach (var interval in Intervals(opening.Boundary, origin, direction, []))
            {
                var e = LineElement(map(interval.A, floor.Elevation), map(interval.B, floor.Elevation), "#ab4e3d", 1.5); e.SetAttributeValue("stroke-dasharray", "5 4"); e.SetAttributeValue("data-kind", "section-slab-void"); e.SetAttributeValue("data-opening-id", opening.Id); sheet.Drawing.Add(e);
                sheet.Text(map((interval.A + interval.B) / 2, floor.Elevation).X, map(0, floor.Elevation).Y - 8, "VOID " + opening.Id, 10, "#a5483a", "middle");
            }
            foreach (var wall in floor.Walls.OrderBy(w => w.Id, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested(); var cuts = new List<double> { 0, Length(Sub(wall.End, wall.Start)) };
                cuts.AddRange(wall.Openings.SelectMany(o => new[] { o.Offset, o.Offset + o.Width })); cuts.Sort(); var unit = Unit(Sub(wall.End, wall.Start));
                for (var i = 1; i < cuts.Count; i++)
                {
                    if (cuts[i] - cuts[i - 1] <= plan.Numerics.LinearTolerance) continue;
                    var mid = (cuts[i - 1] + cuts[i]) / 2;
                    var openings = wall.Openings.Where(o => mid > o.Offset && mid < o.Offset + o.Width).OrderBy(o => o.Sill).ThenBy(o => o.Head).ToList();
                    var segment = WallPolygon(Add(wall.Start, Mul(unit, cuts[i - 1])), Add(wall.Start, Mul(unit, cuts[i])), wall.Thickness);
                    foreach (var interval in Intervals(segment, origin, direction, []))
                    {
                        var ranges = new List<(double, double)>();
                        var z = 0d;
                        foreach (var opening in openings)
                        {
                            if (opening.Sill > z) ranges.Add((z, opening.Sill));
                            z = Math.Max(z, opening.Head);
                        }
                        if (z < wall.Height) ranges.Add((z, wall.Height));
                        foreach (var range in ranges.Where(r => r.Item2 > r.Item1))
                        {
                            var e = RectBetween(map(interval.A, floor.Elevation + range.Item1), map(interval.B, floor.Elevation + range.Item2), "#7c8179", "#303a35", 1);
                            e.SetAttributeValue("data-kind", "cut-wall"); e.SetAttributeValue("data-wall-id", wall.Id); e.SetAttributeValue("data-thickness", E(wall.Thickness)); e.SetAttributeValue("data-bottom-z", E(floor.Elevation + range.Item1)); e.SetAttributeValue("data-top-z", E(floor.Elevation + range.Item2)); sheet.Drawing.Add(e);
                        }
                        foreach (var opening in openings)
                        {
                            var e = LineElement(map(interval.A, floor.Elevation + opening.Sill), map(interval.B, floor.Elevation + opening.Sill), "#34758a", 1); e.SetAttributeValue("data-kind", "section-opening"); e.SetAttributeValue("data-opening-id", opening.Id); e.SetAttributeValue("data-sill-z", E(floor.Elevation + opening.Sill)); e.SetAttributeValue("data-head-z", E(floor.Elevation + opening.Head)); sheet.Drawing.Add(e);
                        }
                    }
                }
            }
            sheet.Row("floor-section-dimension", $"{floor.Id}: floor {Signed(floor.Elevation)}; clear H {F(floor.Height)}; slab {F(floor.SlabThickness)} m", ("data-storey-id", floor.Id), ("data-elevation", E(floor.Elevation)), ("data-slab-thickness", E(floor.SlabThickness)));
        }
        foreach (var roof in plan.Roofs.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            var intersections = RoofCut(roof.Vertices, origin, direction, plan.Numerics.LinearTolerance);
            for (var i = 1; i < intersections.Count; i += 2)
            {
                var a = intersections[i - 1]; var b = intersections[i]; var e = LineElement(map(a.X, a.Y), map(b.X, b.Y), "#775734", 5); e.SetAttributeValue("data-kind", "cut-roof"); e.SetAttributeValue("data-roof-id", roof.Id); sheet.Drawing.Add(e);
            }
        }
        foreach (var stair in plan.Stairs.Where(s => focus is null || focus.Id == s.Id).OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var flightCuts = Intervals(WallPolygon(stair.Start, stair.End, stair.Width), origin, direction, []);
            if (flightCuts.Count > 0) SectionStair(sheet, plan, stair, origin, direction, flightCuts, map, ct);
        }
        VerticalDimensions(sheet, plan, min, max, minZ, maxZ, map); return sheet;
    }

    private static void SectionStair(Sheet sheet, BuildingPlan plan, StairPlan stair, Point2 origin, Point2 direction, List<(double A, double B)> flightCuts, Func<double, double, Point2> map, CancellationToken ct)
    {
        var lower = plan.Storeys.Single(s => s.Id == stair.LowerStoreyId); var upper = plan.Storeys.Single(s => s.Id == stair.UpperStoreyId); var unit = Unit(Sub(stair.End, stair.Start));
        var group = sheet.Group("section-stair-" + stair.Id, "stair-section", ("data-stair-id", stair.Id), ("data-riser-count", stair.RiserCount.ToString(CultureInfo.InvariantCulture)), ("data-riser-height", E(stair.RiserHeight)), ("data-tread-depth", E(stair.TreadDepth)), ("data-total-rise", E(upper.Elevation - lower.Elevation)), ("data-headroom", E(stair.Headroom)));
        group.SetAttributeValue("data-section-method", "finite tread strips and riser planes intersect the actual vertical cut");
        group.SetAttributeValue("data-structure-thickness", E(stair.StructureThickness));
        double Along(double t) => Dot(Sub(Add(origin, Mul(direction, t)), stair.Start), unit);
        double Soffit(double t) => lower.Elevation + Along(t) / stair.TreadDepth * stair.RiserHeight - stair.StructureThickness;
        var normal = Mul(new Point2(-unit.Y, unit.X), stair.Width / 2);
        for (var i = 0; i < stair.RiserCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            var front = Add(stair.Start, Mul(unit, i * stair.TreadDepth));
            var bottomZ = lower.Elevation + i * stair.RiserHeight; var topZ = bottomZ + stair.RiserHeight;
            if (i < stair.RiserCount - 1)
            {
                var back = Add(stair.Start, Mul(unit, (i + 1) * stair.TreadDepth));
                foreach (var interval in Intervals(WallPolygon(front, back, stair.Width), origin, direction, []))
                {
                    ct.ThrowIfCancellationRequested();
                    var shape = PathElement("M " + P(map(interval.A, topZ)) + " L " + P(map(interval.B, topZ)) + " L " + P(map(interval.B, Soffit(interval.B))) + " L " + P(map(interval.A, Soffit(interval.A))) + " Z", "#e6d1ae", "none", 0);
                    shape.SetAttributeValue("data-kind", "stair-structure"); shape.SetAttributeValue("data-structure-thickness", E(stair.StructureThickness)); group.Add(shape);
                    var tread = LineElement(map(interval.A, topZ), map(interval.B, topZ), "#93652c", 2);
                    tread.SetAttributeValue("data-kind", "tread-cut"); tread.SetAttributeValue("data-index", i.ToString(CultureInfo.InvariantCulture)); tread.SetAttributeValue("data-cut-start", E(interval.A)); tread.SetAttributeValue("data-cut-end", E(interval.B)); tread.SetAttributeValue("data-top-z", E(topZ)); group.Add(tread);
                    var clearance = LineElement(map(interval.A, topZ + stair.Headroom), map(interval.B, topZ + stair.Headroom), "#a68d65", 1); clearance.SetAttributeValue("stroke-dasharray", "5 4"); clearance.SetAttributeValue("data-kind", "headroom-envelope"); group.Add(clearance);
                }
            }
            var a = Add(front, normal); var b = Sub(front, normal);
            var da = Cross(Sub(a, origin), direction); var db = Cross(Sub(b, origin), direction);
            if (Math.Abs(da) <= plan.Numerics.LinearTolerance && Math.Abs(db) <= plan.Numerics.LinearTolerance)
            {
                var face = RectBetween(map(Dot(Sub(a, origin), direction), bottomZ), map(Dot(Sub(b, origin), direction), topZ), "#e6d1ae", "#93652c", 2); face.SetAttributeValue("data-kind", "riser-cut"); group.Add(face);
            }
            else if (da * db <= 0)
            {
                var p = Lerp(a, b, da / (da - db)); var t = Dot(Sub(p, origin), direction);
                var riser = LineElement(map(t, bottomZ), map(t, topZ), "#93652c", 2); riser.SetAttributeValue("data-kind", "riser-cut"); riser.SetAttributeValue("data-cut-position", E(t)); group.Add(riser);
            }
        }
        foreach (var interval in flightCuts)
        {
            var soffit = LineElement(map(interval.A, Soffit(interval.A)), map(interval.B, Soffit(interval.B)), "#93652c", 2);
            soffit.SetAttributeValue("data-kind", "stair-soffit"); soffit.SetAttributeValue("data-start-z", E(Soffit(interval.A))); soffit.SetAttributeValue("data-end-z", E(Soffit(interval.B))); soffit.SetAttributeValue("data-structure-thickness", E(stair.StructureThickness)); group.Add(soffit);
        }
        foreach (var landing in new[] { (stair.LowerLanding, lower.Elevation), (stair.UpperLanding, upper.Elevation) })
        foreach (var interval in Intervals(landing.Item1, origin, direction, []))
        {
            var e = LineElement(map(interval.A, landing.Item2), map(interval.B, landing.Item2), "#93652c", 4); e.SetAttributeValue("data-kind", "landing"); group.Add(e);
        }
        var labelT = (flightCuts[0].A + flightCuts[0].B) / 2;
        var label = map(labelT, lower.Elevation + (Math.Floor(Along(labelT) / stair.TreadDepth) + 1) * stair.RiserHeight + stair.Headroom);
        sheet.Text(label.X, label.Y - 8, "Headroom " + F(stair.Headroom) + " m", 11, "#80643a", "middle", group);
        sheet.Row("stair-section-dimension", $"{stair.Id}: rise {F(upper.Elevation - lower.Elevation)} m; run {F(Length(Sub(stair.End, stair.Start)))} m", ("data-stair-id", stair.Id));
        sheet.Row("stair-section-measurement", $"  {stair.RiserCount} × {F(stair.RiserHeight)} m rise; tread {F(stair.TreadDepth)}; headroom {F(stair.Headroom)} m");
        sheet.Row("stair-structure-dimension", $"  Vertical waist thickness {F(stair.StructureThickness)} m below riser-bottom pitch line", ("data-structure-thickness", E(stair.StructureThickness)));
    }

    private static void PlanDimensions(Sheet sheet, Bounds b, Func<Point2, Point2> map)
    {
        var bottomLeft = map(new(b.MinX, b.MinY)); var bottomRight = map(new(b.MaxX, b.MinY)); var topLeft = map(new(b.MinX, b.MaxY));
        sheet.Dimension(new(bottomLeft.X, bottomLeft.Y + 45), new(bottomRight.X, bottomRight.Y + 45), b.Width, "overall-width");
        sheet.Dimension(new(bottomLeft.X - 45, bottomLeft.Y), new(topLeft.X - 45, topLeft.Y), b.Height, "overall-depth");
        sheet.Root.SetAttributeValue("data-dimension-bounds", BoundsText(b));
        sheet.Row("overall-dimension", $"Plan extents: {F(b.Width)} × {F(b.Height)} m", ("data-width", E(b.Width)), ("data-depth", E(b.Height)));
    }

    private static void VerticalDimensions(Sheet sheet, BuildingPlan plan, double min, double max, double minZ, double maxZ, Func<double, double, Point2> map)
    {
        sheet.Line(map(min - (max - min) * 0.03, 0), map(max + (max - min) * 0.03, 0), "#5d6e65", 1.5);
        sheet.Text(map(min, 0).X, map(min, 0).Y - 8, "Datum ±0.000 m", 11, "#53665c");
        foreach (var floor in plan.Storeys.OrderBy(s => s.Elevation))
        {
            var left = map(min, floor.Elevation); var right = map(max, floor.Elevation);
            var e = LineElement(left, right, "#a6b0aa", 0.7); e.SetAttributeValue("stroke-dasharray", "5 5"); e.SetAttributeValue("data-kind", "floor-level"); e.SetAttributeValue("data-storey-id", floor.Id); e.SetAttributeValue("data-elevation", E(floor.Elevation)); sheet.Drawing.Add(e);
            sheet.Text(right.X + 14, right.Y + 4, Signed(floor.Elevation), 11, "#53665c");
            sheet.Row("storey-height", $"{floor.Name}: FFL {Signed(floor.Elevation)}; wall top {Signed(floor.Elevation + floor.Height)} m", ("data-storey-id", floor.Id), ("data-elevation", E(floor.Elevation)), ("data-height", E(floor.Height)));
        }
        var a = map(min, minZ); var b = map(max, minZ); var top = map(min, maxZ);
        sheet.Dimension(new(a.X, a.Y + 45), new(b.X, b.Y + 45), max - min, "projected-width");
        sheet.Dimension(new(a.X - 45, a.Y), new(top.X - 45, top.Y), maxZ - minZ, "overall-height");
        sheet.Root.SetAttributeValue("data-projected-bounds", $"{E(min)},{E(minZ)},{E(max)},{E(maxZ)}");
        sheet.Row("overall-height", $"Overall height {F(maxZ - minZ)} m; roof high point {Signed(maxZ)} m", ("data-height", E(maxZ - minZ)), ("data-roof-top-z", E(maxZ)));
    }

    private static double Facing(WallPlan wall, Point2 towards)
    {
        var u = Unit(Sub(wall.End, wall.Start));
        var outward = wall.RightRoomId is null ? new Point2(u.Y, -u.X) : new Point2(-u.Y, u.X);
        return Dot(outward, towards);
    }

    private static List<(double A, double B)> Intervals(Polygon2 polygon, Point2 origin, Point2 direction, IReadOnlyList<Polygon2> subtract)
    {
        var cuts = new List<double>();
        foreach (var ring in new[] { polygon.Outer }.Concat(polygon.Holes).Concat(subtract.SelectMany(p => new[] { p.Outer }.Concat(p.Holes))))
        foreach (var edge in Edges(ring))
        {
            var da = Cross(Sub(edge.A, origin), direction); var db = Cross(Sub(edge.B, origin), direction);
            if (Math.Abs(da) < 1e-9) cuts.Add(Dot(Sub(edge.A, origin), direction));
            if (Math.Abs(db) < 1e-9) cuts.Add(Dot(Sub(edge.B, origin), direction));
            if (da * db < 0) cuts.Add(Dot(Sub(Lerp(edge.A, edge.B, da / (da - db)), origin), direction));
        }
        cuts.Sort(); var unique = cuts.DistinctBy(t => Math.Round(t, 9)).ToList(); var result = new List<(double, double)>();
        for (var i = 1; i < unique.Count; i++)
        {
            var point = Add(origin, Mul(direction, (unique[i - 1] + unique[i]) / 2));
            if (Inside(polygon, point) && !subtract.Any(p => Inside(p, point))) result.Add((unique[i - 1], unique[i]));
        }
        return result;
    }

    private static List<Point2> RoofCut(List<Point3> vertices, Point2 origin, Point2 direction, double tolerance)
    {
        var normal = Normal(vertices); var first = vertices[0];
        var polygon = new Polygon2 { Outer = vertices.Select(p => new Point2(p.X, p.Y)).ToList(), Holes = [] };
        Point2 At(double t)
        {
            var p = Add(origin, Mul(direction, t));
            return new(t, first.Z - ((p.X - first.X) * normal.X + (p.Y - first.Y) * normal.Y) / normal.Z);
        }
        return Intervals(polygon, origin, direction, []).SelectMany(i => new[] { At(i.A), At(i.B) }).ToList();
    }

    private static Polygon2 WallPolygon(Point2 a, Point2 b, double thickness)
    {
        var u = Unit(Sub(b, a)); var n = Mul(new Point2(-u.Y, u.X), thickness / 2);
        return new() { Outer = [Sub(a, n), Sub(b, n), Add(b, n), Add(a, n)], Holes = [] };
    }

    private static Point2 InteriorLabel(Polygon2 polygon)
    {
        var centre = Centroid(polygon.Outer);
        if (Inside(polygon, centre)) return centre;
        var b = Bounds.From(polygon.Outer);
        for (var rows = 1; rows < 24; rows++)
        for (var col = 0; col < rows; col++)
        for (var row = 0; row < rows; row++)
        {
            var p = new Point2(b.MinX + b.Width * (col + 0.5) / rows, b.MinY + b.Height * (row + 0.5) / rows);
            if (Inside(polygon, p)) return p;
        }
        return polygon.Outer[0];
    }
    private static bool Inside(Polygon2 polygon, Point2 p) => InRing(polygon.Outer, p) && !polygon.Holes.Any(h => InRing(h, p));
    private static bool InRing(List<Point2> ring, Point2 p)
    {
        var inside = false;
        foreach (var edge in Edges(ring))
            if ((edge.A.Y > p.Y) != (edge.B.Y > p.Y) && p.X < (edge.B.X - edge.A.X) * (p.Y - edge.A.Y) / (edge.B.Y - edge.A.Y) + edge.A.X) inside = !inside;
        return inside;
    }
    private static Point2 Centroid(List<Point2> ring)
    {
        var origin = ring[0];
        var sum = 0d; var x = 0d; var y = 0d;
        foreach (var edge in Edges(ring))
        {
            var a = Sub(edge.A, origin); var b = Sub(edge.B, origin); var c = Cross(a, b);
            sum += c; x += (a.X + b.X) * c; y += (a.Y + b.Y) * c;
        }
        return Add(origin, new(x / (3 * sum), y / (3 * sum)));
    }
    private static double Area(Polygon2 p) => Math.Abs(RingArea(p.Outer)) - p.Holes.Sum(h => Math.Abs(RingArea(h)));
    private static double RingArea(List<Point2> ring) => Edges(ring).Sum(e => Cross(Sub(e.A, ring[0]), Sub(e.B, ring[0]))) / 2;
    private static Point3 Normal(List<Point3> p)
    {
        var x = 0d; var y = 0d; var z = 0d;
        var origin = p[0];
        foreach (var edge in Edges3(p))
        {
            var a = new Point3(edge.A.X - origin.X, edge.A.Y - origin.Y, edge.A.Z - origin.Z);
            var b = new Point3(edge.B.X - origin.X, edge.B.Y - origin.Y, edge.B.Z - origin.Z);
            x += a.Y * b.Z - a.Z * b.Y; y += a.Z * b.X - a.X * b.Z; z += a.X * b.Y - a.Y * b.X;
        }
        return new(x, y, z);
    }
    private static double Distance3(Point3 a, Point3 b) => Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2) + Math.Pow(b.Z - a.Z, 2));
    private static IEnumerable<(Point2 A, Point2 B)> Edges(List<Point2> p) { for (var i = 0; i < p.Count; i++) yield return (p[i], p[(i + 1) % p.Count]); }
    private static IEnumerable<(Point3 A, Point3 B)> Edges3(List<Point3> p) { for (var i = 0; i < p.Count; i++) yield return (p[i], p[(i + 1) % p.Count]); }
    private static double PlanScale(Bounds b) => Math.Min(DrawingWidth / b.Width, DrawingHeight / b.Height);
    private static Func<Point2, Point2> PlanMap(Bounds b)
    {
        var scale = PlanScale(b); var x = DrawingLeft + (DrawingWidth - b.Width * scale) / 2; var y = DrawingTop + (DrawingHeight - b.Height * scale) / 2;
        return p => new(x + (p.X - b.MinX) * scale, y + (b.MaxY - p.Y) * scale);
    }
    private static Func<double, double, Point2> VerticalMap(double min, double max, double minZ, double maxZ)
    {
        var scale = Math.Min(DrawingWidth / (max - min), DrawingHeight / (maxZ - minZ));
        var x = DrawingLeft + (DrawingWidth - (max - min) * scale) / 2; var y = DrawingTop + (DrawingHeight - (maxZ - minZ) * scale) / 2;
        return (horizontal, z) => new(x + (horizontal - min) * scale, y + (maxZ - z) * scale);
    }
    private static string PolygonPath(Polygon2 polygon, Func<Point2, Point2> map) => string.Join(" ", new[] { polygon.Outer }.Concat(polygon.Holes).Select(r => RingPath(r, map)));
    private static string RingPath(List<Point2> ring, Func<Point2, Point2> map) => "M " + string.Join(" L ", ring.Select(p => P(map(p)))) + " Z";
    private static Point2 Add(Point2 a, Point2 b) => new(a.X + b.X, a.Y + b.Y);
    private static Point2 Sub(Point2 a, Point2 b) => new(a.X - b.X, a.Y - b.Y);
    private static Point2 Mul(Point2 a, double t) => new(a.X * t, a.Y * t);
    private static Point2 Lerp(Point2 a, Point2 b, double t) => Add(a, Mul(Sub(b, a), t));
    private static double Dot(Point2 a, Point2 b) => a.X * b.X + a.Y * b.Y;
    private static double Cross(Point2 a, Point2 b) => a.X * b.Y - a.Y * b.X;
    private static double Length(Point2 a) => Math.Sqrt(Dot(a, a));
    private static Point2 Unit(Point2 a) => Mul(a, 1 / Length(a));
    private static string F(double v) => v.ToString("0.#########", CultureInfo.InvariantCulture);
    private static string E(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    private static string Signed(double v) => (v >= 0 ? "+" : "") + F(v);
    private static string P(Point2 p) => F(p.X) + " " + F(p.Y);
    private static string BoundsText(Bounds b) => $"{E(b.MinX)},{E(b.MinY)},{E(b.MaxX)},{E(b.MaxY)}";
    private static XElement PathElement(string path, string fill, string stroke, double width) => new(Svg + "path", new XAttribute("d", path), new XAttribute("fill", fill), new XAttribute("fill-rule", "evenodd"), new XAttribute("stroke", stroke), new XAttribute("stroke-width", F(width)), new XAttribute("stroke-linejoin", "round"));
    private static XElement LineElement(Point2 a, Point2 b, string stroke, double width) => new(Svg + "line", new XAttribute("x1", F(a.X)), new XAttribute("y1", F(a.Y)), new XAttribute("x2", F(b.X)), new XAttribute("y2", F(b.Y)), new XAttribute("stroke", stroke), new XAttribute("stroke-width", F(width)));
    private static XElement RectBetween(Point2 a, Point2 b, string fill, string stroke, double width) => new(Svg + "rect", new XAttribute("x", F(Math.Min(a.X, b.X))), new XAttribute("y", F(Math.Min(a.Y, b.Y))), new XAttribute("width", F(Math.Abs(a.X - b.X))), new XAttribute("height", F(Math.Abs(a.Y - b.Y))), new XAttribute("fill", fill), new XAttribute("stroke", stroke), new XAttribute("stroke-width", F(width)));
    private readonly record struct Bounds(double MinX, double MinY, double MaxX, double MaxY)
    {
        public double Width => MaxX - MinX;
        public double Height => MaxY - MinY;
        public static Bounds From(IEnumerable<Point2> points) { var p = points.ToList(); return new(p.Min(p => p.X), p.Min(p => p.Y), p.Max(p => p.X), p.Max(p => p.Y)); }
    }

    private sealed class PreviewBudget(long maximumBytes, CancellationToken cancellationToken)
    {
        private long minimumBytes;
        public void CheckAdditional(long additionalBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (additionalBytes > maximumBytes - minimumBytes)
                throw new InvalidOperationException($"Architectural preview exceeds declared MaxPreviewBytes ({maximumBytes}) while constructing drawing primitives.");
        }
        private void Admit(long count)
        {
            CheckAdditional(count);
            minimumBytes += count;
        }
        public void Admit(XObject value)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (value is XAttribute attribute) Admit(Encoding.UTF8.GetByteCount(attribute.Name.LocalName) + 4L + Encoding.UTF8.GetByteCount(attribute.Value));
            else if (value is XText text) Admit(Encoding.UTF8.GetByteCount(text.Value));
            else if (value is XElement element)
            {
                Admit(Encoding.UTF8.GetByteCount(element.Name.LocalName) + 4L);
                foreach (var a in element.Attributes()) Admit(a);
                foreach (var node in element.Nodes()) Admit(node);
            }
        }
        public void Changing(object? sender, XObjectChangeEventArgs change)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (change.ObjectChange == XObjectChange.Add && sender is XObject value) Admit(value);
        }
    }

    private sealed class Sheet
    {
        public XElement Root { get; }
        public XElement Drawing { get; }
        private readonly XElement schedule;
        private readonly XElement annotationLines;
        private readonly XElement annotationLabels;
        private readonly PreviewBudget budget;
        private double rowY = 172, minimumHeight = 930;
        public Sheet(BuildingPlan plan, string kind, string title, string subtitle, string semanticId, PreviewBudget budget)
        {
            this.budget = budget;
            Root = new(Svg + "svg", new XAttribute("version", "1.1"), new XAttribute("width", F(SheetWidth)), new XAttribute("role", "img"), new XAttribute("data-kind", kind), new XAttribute("data-semantic-id", semanticId), new XAttribute("data-plan-id", plan.Id), new XAttribute("data-units", "metres"), new XAttribute("data-evidence", "architectural SVG preview only; no 3D mesh or runtime/render acceptance"));
            budget.Admit(Root);
            Root.Changing += budget.Changing;
            Root.Add(new XElement(Svg + "title", plan.Name + " — " + title));
            Root.Add(new XElement(Svg + "desc", "Dimensioned architectural design preview. " + plan.Purpose + ". Style: " + plan.ArchitecturalStyle + ". Declared dimensions are preserved in data attributes; displayed derived dimensions use invariant decimals. Not a building-code certification or model/render evidence."));
            Root.Add(new XElement(Svg + "rect", new XAttribute("width", "100%"), new XAttribute("height", "100%"), new XAttribute("fill", "#fffefa")));
            Drawing = new(Svg + "g", new XAttribute("id", "drawing")); Root.Add(Drawing);
            schedule = new(Svg + "g", new XAttribute("id", "dimension-schedule")); Root.Add(schedule);
            var annotations = new XElement(Svg + "g", new XAttribute("id", "annotations"), new XAttribute("data-kind", "annotation-layer"));
            Root.Add(annotations);
            annotationLines = new(Svg + "g", new XAttribute("id", "annotation-lines")); annotations.Add(annotationLines);
            annotationLabels = new(Svg + "g", new XAttribute("id", "annotation-labels")); annotations.Add(annotationLabels);
            Text(54, 46, plan.Name, 27, "#253e40"); Text(54, 81, title, 21, "#3d5c59"); Wrapped(54, 110, subtitle, 115, 14, Drawing);
            Text(ScheduleLeft, 145, "DIMENSIONS / SEMANTIC SCHEDULE", 15, "#3d5c59", "start", schedule);
            Line(new(54, 134), new(1648, 134), "#aab7ac", 1);
        }
        public XElement Group(string id, string kind, params (string Key, string Value)[] attrs)
        {
            return GroupIn(Drawing, id, kind, attrs);
        }
        private static XElement GroupIn(XElement parent, string id, string kind, (string Key, string Value)[] attrs)
        {
            var e = new XElement(Svg + "g", new XAttribute("id", id), new XAttribute("data-kind", kind)); foreach (var a in attrs) e.SetAttributeValue(a.Key, a.Value); parent.Add(e); return e;
        }
        public XElement AnnotationGroup(string id, string kind, params (string Key, string Value)[] attrs) => GroupIn(annotationLabels, id, kind, attrs);
        public XElement AnnotationLineGroup(string id, string kind, params (string Key, string Value)[] attrs) => GroupIn(annotationLines, id, kind, attrs);
        public void Callout(string id, string kind, Point2 anchor, string text, int slot, string color, params (string Key, string Value)[] attrs)
        {
            var x = DrawingLeft + slot % 2 * 430;
            var y = DrawingTop + DrawingHeight + 94 + slot / 2 * 38;
            var lines = AnnotationLineGroup("annotation-callout-leader-" + id, "annotation-leader", attrs);
            lines.Add(LineElement(anchor, new(x + 200, y - 17), color, 0.8));
            var group = AnnotationGroup(id, kind, attrs);
            group.SetAttributeValue("data-label-x", F(x + 12)); group.SetAttributeValue("data-label-y", F(y));
            group.SetAttributeValue("data-anchor-x", F(anchor.X)); group.SetAttributeValue("data-anchor-y", F(anchor.Y));
            group.Add(RectBetween(new(x, y - 20), new(x + 400, y + 7), "#fffefa", color, 0.8));
            Text(x + 12, y, text, 12, color, "start", group, 376);
        }
        public XElement Path(string path, string fill, string stroke, double width, string id, string kind)
        {
            var e = PathElement(path, fill, stroke, width); e.SetAttributeValue("id", id); e.SetAttributeValue("data-kind", kind); Drawing.Add(e); return e;
        }
        public void CheckGeometry(int vertices) => budget.CheckAdditional(checked(vertices * 6L));
        public string PolygonPath(Polygon2 polygon, Func<Point2, Point2> map)
        {
            CheckGeometry(polygon.Outer.Count + polygon.Holes.Sum(h => h.Count));
            return SvgPlanRenderer.PolygonPath(polygon, map);
        }
        public string RingPath(List<Point2> ring, Func<Point2, Point2> map)
        {
            CheckGeometry(ring.Count);
            return SvgPlanRenderer.RingPath(ring, map);
        }
        public void Line(Point2 a, Point2 b, string stroke, double width) => Drawing.Add(LineElement(a, b, stroke, width));
        public void Text(double x, double y, string text, double size = 13, string color = "#273e3b", string anchor = "start", XElement? target = null, double? maximumWidth = null)
        {
            budget.CheckAdditional(Encoding.UTF8.GetByteCount(text));
            var element = new XElement(Svg + "text", new XAttribute("x", F(x)), new XAttribute("y", F(y)), new XAttribute("font-family", "Arial, sans-serif"), new XAttribute("font-size", F(size)), new XAttribute("fill", color), new XAttribute("text-anchor", anchor), text);
            var available = anchor == "middle" ? 2 * Math.Min(x - 20, SheetWidth - 20 - x) : anchor == "end" ? x - 20 : SheetWidth - 20 - x;
            if (maximumWidth is not null) available = Math.Min(available, maximumWidth.Value);
            if (text.Length * size * 0.65 > available) { element.SetAttributeValue("textLength", F(available)); element.SetAttributeValue("lengthAdjust", "spacingAndGlyphs"); }
            (target ?? Drawing).Add(element); minimumHeight = Math.Max(minimumHeight, y + 70);
        }
        public void Row(string kind, string content, params (string Key, string Value)[] attrs)
        {
            var e = new XElement(Svg + "g", new XAttribute("data-kind", kind)); foreach (var a in attrs) e.SetAttributeValue(a.Key, a.Value); schedule.Add(e); rowY += Wrapped(ScheduleLeft, rowY, content, 70, 12, e) * 17 + 5;
        }
        private int Wrapped(double x, double y, string text, int length, double fontSize, XElement target)
        {
            var words = text.Replace('\r', ' ').Replace('\n', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries); var lines = new List<string>(); var line = "";
            foreach (var word in words)
            {
                var remaining = word;
                while (remaining.Length > length)
                {
                    if (line.Length > 0) { lines.Add(line); line = ""; } lines.Add(remaining[..length]); remaining = remaining[length..];
                }
                if (line.Length + remaining.Length + 1 > length) { lines.Add(line); line = ""; } line = line.Length == 0 ? remaining : line + " " + remaining;
            }
            if (line.Length > 0) lines.Add(line);
            for (var i = 0; i < lines.Count; i++) Text(x, y + i * 17, lines[i], fontSize, "#3f514b", "start", target);
            return Math.Max(1, lines.Count);
        }
        public void Dimension(Point2 a, Point2 b, double value, string kind)
        {
            var group = AnnotationLineGroup("annotation-dimension-" + kind, "dimension", ("data-dimension-kind", kind), ("data-value-metres", E(value))); group.Add(LineElement(a, b, "#627c78", 1)); var u = Unit(Sub(b, a)); var n = new Point2(-u.Y, u.X);
            group.Add(LineElement(Sub(a, Mul(n, 5)), Add(a, Mul(n, 5)), "#627c78", 1)); group.Add(LineElement(Sub(b, Mul(n, 5)), Add(b, Mul(n, 5)), "#627c78", 1));
            var m = Lerp(a, b, 0.5); Text(m.X + (Math.Abs(b.X - a.X) < 1 ? -8 : 0), m.Y - 9, F(value) + " m", 12, "#496965", Math.Abs(b.X - a.X) < 1 ? "end" : "middle", group);
        }
        public string Finish()
        {
            var height = Math.Max(minimumHeight, rowY + 90); Root.SetAttributeValue("height", F(height)); Root.SetAttributeValue("viewBox", $"0 0 {F(SheetWidth)} {F(height)}");
            Line(new(54, height - 54), new(1648, height - 54), "#aab7ac", 1);
            Text(54, height - 30, "Architectural SVG preview • metres • semantic IDs share the specification datum • 3D generation is a separate stage", 12, "#61746a");
            return new XDocument(Root).ToString(SaveOptions.DisableFormatting) + "\n";
        }
    }
}
