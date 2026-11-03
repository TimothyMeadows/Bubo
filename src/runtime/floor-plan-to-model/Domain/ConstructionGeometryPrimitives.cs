using static Bubo.FloorPlan.Domain.PlanGeometry;

namespace Bubo.FloorPlan.Domain;

// Fixed analytic primitives used by construction and its independent clearance witnesses.
// Convex cells remain separate closed shells: shared faces are contact, never remeshed.
internal readonly record struct ConstructionPlane(double X, double Y, double C)
{
    public double At(Point2 p) => X * p.X + Y * p.Y + C;
    public static ConstructionPlane Flat(double z) => new(0, 0, z);
    public ConstructionPlane Shift(double z) => this with { C = C + z };
}

internal sealed class ConstructionWork(BuildingPlan plan, CancellationToken token)
{
    public long Checks { get; private set; }
    public double E => plan.Numerics.LinearTolerance;
    public void Debit()
    {
        token.ThrowIfCancellationRequested();
        if (++Checks > plan.Budgets.MaxGeometryChecks) throw new InvalidOperationException("Construction geometry work budget exhausted; no scene has passed compilation.");
    }
    public void Cells(int count)
    {
        Debit();
        if (count > plan.Construction!.MaxArrangementCells) throw new InvalidOperationException("Construction arrangement-cell budget exhausted; no simplification is performed.");
    }
}

internal static class ConstructionGeometryPrimitives
{
    public static Point2 XY(Point3 p) => new(p.X, p.Y);
    public static Point3 XYZ(Point2 p, double z) => new(p.X, p.Y, z);
    public static Point2 Unit(Point2 a, Point2 b) => Mul(Sub(b, a), 1 / Distance(a, b));
    public static Point2 Normal(Point2 d) => new(-d.Y, d.X);
    public static Polygon2 Polygon(List<Point2> ring) => new() { Outer = ring, Holes = [] };
    public static Point2 Centre(IReadOnlyList<Point2> p)
    {
        var origin = p[0]; var sum = new Point2(0, 0);
        foreach (var v in p) sum = Add(sum, Sub(v, origin));
        return Add(origin, Mul(sum, 1d / p.Count));
    }
    public static List<Point2> Rectangle(Point2 origin, Point2 direction, double a, double b, double left, double right)
    {
        var n = Normal(direction);
        return [Add(Add(origin, Mul(direction, a)), Mul(n, right)), Add(Add(origin, Mul(direction, b)), Mul(n, right)), Add(Add(origin, Mul(direction, b)), Mul(n, left)), Add(Add(origin, Mul(direction, a)), Mul(n, left))];
    }
    public static IEnumerable<(Point2 A, Point2 B)> Edges(IReadOnlyList<Point2> p)
    { for (var i = 0; i < p.Count; i++) yield return (p[i], p[(i + 1) % p.Count]); }
    public static IEnumerable<(Point2 A, Point2 B)> Edges(Polygon2 p)
        => Edges(p.Outer).Concat(p.Holes.SelectMany(h => Edges(h)));

    public static List<Point2> Clip(IReadOnlyList<Point2> polygon, ConstructionPlane plane, bool positive, ConstructionWork work)
    {
        var output = new List<Point2>();
        foreach (var (a, b) in Edges(polygon))
        {
            work.Debit();
            var da = plane.At(a) * (positive ? 1 : -1); var db = plane.At(b) * (positive ? 1 : -1);
            var ina = da >= -work.E; var inb = db >= -work.E;
            if (ina) Append(output, a, work.E);
            if (ina != inb)
            {
                var t = da / (da - db);
                Append(output, Add(a, Mul(Sub(b, a), Math.Clamp(t, 0, 1))), work.E);
            }
        }
        Clean(output, work.E);
        return output.Count >= 3 && Math.Abs(Area(output)) > work.E * work.E ? output : [];
    }
    private static void Append(List<Point2> values, Point2 p, double e)
    { if (values.Count == 0 || Distance(values[^1], p) > e) values.Add(p); }
    private static void Clean(List<Point2> p, double e)
    {
        if (p.Count > 1 && Distance(p[0], p[^1]) <= e) p.RemoveAt(p.Count - 1);
        bool changed = true;
        while (changed && p.Count >= 3)
        {
            changed = false;
            for (var i = 0; i < p.Count; i++)
            {
                var a = p[(i + p.Count - 1) % p.Count]; var b = p[i]; var c = p[(i + 1) % p.Count];
                if (Math.Abs(Cross(Sub(b, a), Sub(c, b))) <= e * (Distance(a, b) + Distance(b, c)))
                { p.RemoveAt(i); changed = true; break; }
            }
        }
        if (p.Count >= 3 && Area(p) < 0) p.Reverse();
    }
    public static ConstructionPlane Line(Point2 a, Point2 b)
    { var n = Normal(Unit(a, b)); return new(n.X, n.Y, -Dot(n, a)); }

    public static List<List<Point2>> Partition(IEnumerable<Polygon2> polygons, IEnumerable<ConstructionPlane> additional, ConstructionWork work)
    {
        var p = polygons.ToList(); var points = p.SelectMany(x => x.Outer).ToList();
        if (points.Count == 0) return [];
        var minx = points.Min(x => x.X); var maxx = points.Max(x => x.X); var miny = points.Min(x => x.Y); var maxy = points.Max(x => x.Y);
        var lines = new List<ConstructionPlane>();
        foreach (var plane in p.SelectMany(Edges).Select(edge => Line(edge.A, edge.B)).Concat(additional))
        {
            work.Debit(); var length = Math.Sqrt(plane.X * plane.X + plane.Y * plane.Y); if (length <= work.E) continue;
            var line = new ConstructionPlane(plane.X / length, plane.Y / length, plane.C / length);
            if (line.X < -work.E || (Math.Abs(line.X) <= work.E && line.Y < 0)) line = new(-line.X, -line.Y, -line.C);
            bool duplicate = false;
            foreach (var x in lines)
            {
                work.Debit();
                if (Math.Abs(x.X - line.X) <= work.E && Math.Abs(x.Y - line.Y) <= work.E && Math.Abs(x.C - line.C) <= work.E) { duplicate = true; break; }
            }
            if (!duplicate) lines.Add(line);
        }
        lines = lines.OrderBy(x => x.X).ThenBy(x => x.Y).ThenBy(x => x.C).ToList();
        List<List<Point2>> cells = [[new(minx, miny), new(maxx, miny), new(maxx, maxy), new(minx, maxy)]];
        foreach (var line in lines)
        {
            var next = new List<List<Point2>>();
            foreach (var cell in cells)
            {
                work.Debit(); var distances = cell.Select(line.At).ToArray();
                if (distances.Min() >= -work.E || distances.Max() <= work.E) next.Add(cell);
                else
                {
                    var left = Clip(cell, line, true, work); var right = Clip(cell, line, false, work);
                    if (left.Count != 0) next.Add(left); if (right.Count != 0) next.Add(right);
                }
            }
            work.Cells(next.Count); cells = next;
        }
        return cells;
    }

    public static double PointSegmentDistance(Point2 p, Point2 a, Point2 b)
    {
        var ab = Sub(b, a); var square = Dot(ab, ab);
        return square == 0 ? Distance(p, a) : Distance(p, Add(a, Mul(ab, Math.Clamp(Dot(Sub(p, a), ab) / square, 0, 1))));
    }
    public static double SegmentDistance(Point2 a, Point2 b, Point2 c, Point2 d, ConstructionWork work)
    {
        var geometry = new PlanGeometry(work.E, work.Debit);
        if (geometry.Intersects(a, b, c, d)) return 0;
        return Math.Min(Math.Min(PointSegmentDistance(a, c, d), PointSegmentDistance(b, c, d)), Math.Min(PointSegmentDistance(c, a, b), PointSegmentDistance(d, a, b)));
    }
    public static double PolygonDistance(IReadOnlyList<Point2> a, IReadOnlyList<Point2> b, ConstructionWork work)
    {
        var g = new PlanGeometry(work.E, work.Debit);
        if (g.InRing(a[0], b) >= 0 || g.InRing(b[0], a) >= 0) return 0;
        var minimum = double.PositiveInfinity;
        foreach (var e in Edges(a)) foreach (var f in Edges(b)) minimum = Math.Min(minimum, SegmentDistance(e.A, e.B, f.A, f.B, work));
        return minimum;
    }
    public static List<Point2> Hull(IEnumerable<Point2> source, ConstructionWork work)
    {
        var p = source.Distinct().OrderBy(v => v.X).ThenBy(v => v.Y).ToList();
        if (p.Count < 3) return p;
        List<Point2> Half(IEnumerable<Point2> sequence)
        {
            var h = new List<Point2>();
            foreach (var v in sequence)
            {
                work.Debit(); while (h.Count >= 2 && Cross(Sub(h[^1], h[^2]), Sub(v, h[^1])) <= 0) h.RemoveAt(h.Count - 1);
                h.Add(v);
            }
            return h;
        }
        var lower = Half(p); var upper = Half(p.AsEnumerable().Reverse()); lower.RemoveAt(lower.Count - 1); upper.RemoveAt(upper.Count - 1); return [.. lower, .. upper];
    }
    public static Point2 Rotate(Point2 p, Point2 pivot, double angle)
    { var d = Sub(p, pivot); var c = Math.Cos(angle); var s = Math.Sin(angle); return Add(pivot, new(d.X * c - d.Y * s, d.X * s + d.Y * c)); }
    public static ConstructionPlane Plane(IReadOnlyList<Point3> vertices)
    {
        var a = vertices[0];
        for (var i = 1; i + 1 < vertices.Count; i++)
        {
            var b = vertices[i]; var c = vertices[i + 1];
            var ab = new Point3(b.X - a.X, b.Y - a.Y, b.Z - a.Z); var ac = new Point3(c.X - a.X, c.Y - a.Y, c.Z - a.Z);
            var nx = ab.Y * ac.Z - ab.Z * ac.Y; var ny = ab.Z * ac.X - ab.X * ac.Z; var nz = ab.X * ac.Y - ab.Y * ac.X;
            if (Math.Abs(nz) >= 1e-20) return new(-nx / nz, -ny / nz, a.Z + (nx * a.X + ny * a.Y) / nz);
        }
        throw new InvalidOperationException("Vertical or degenerate roof plane cannot bound architecture.");
    }
}
