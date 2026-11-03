[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Bubo.FloorPlan.Tests")]

namespace Bubo.FloorPlan.Domain;

internal sealed class GeometryCapacityException : Exception;

// All potentially quadratic predicates debit the caller's finite work budget.
internal sealed class PlanGeometry(double tolerance, Action debit)
{
    public static Point2 Add(Point2 a, Point2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Point2 Sub(Point2 a, Point2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Point2 Mul(Point2 a, double s) => new(a.X * s, a.Y * s);
    public static double Dot(Point2 a, Point2 b) => a.X * b.X + a.Y * b.Y;
    public static double Cross(Point2 a, Point2 b) => a.X * b.Y - a.Y * b.X;
    public static double Length(Point2 a) => Math.Sqrt(Dot(a, a));
    public static double Distance(Point2 a, Point2 b) => Length(Sub(a, b));
    public static double Area(IReadOnlyList<Point2> ring)
    {
        var origin = ring[0];
        double area = 0;
        for (var i = 1; i + 1 < ring.Count; i++) area += Cross(Sub(ring[i], origin), Sub(ring[i + 1], origin));
        return area / 2;
    }

    public bool OnSegment(Point2 p, Point2 a, Point2 b)
    {
        var d = Sub(b, a);
        var length = Length(d);
        if (length <= tolerance) return Distance(p, a) <= tolerance;
        return Math.Abs(Cross(d, Sub(p, a))) <= tolerance * length
            && Dot(Sub(p, a), d) >= -tolerance * length
            && Dot(Sub(p, b), d) <= tolerance * length;
    }

    public bool Intersects(Point2 a, Point2 b, Point2 c, Point2 d, bool properOnly = false)
    {
        debit();
        var ab = Sub(b, a);
        var cd = Sub(d, c);
        var x1 = Cross(ab, Sub(c, a));
        var x2 = Cross(ab, Sub(d, a));
        var x3 = Cross(cd, Sub(a, c));
        var x4 = Cross(cd, Sub(b, c));
        var e1 = tolerance * Length(ab);
        var e2 = tolerance * Length(cd);
        if (((x1 > e1 && x2 < -e1) || (x1 < -e1 && x2 > e1))
            && ((x3 > e2 && x4 < -e2) || (x3 < -e2 && x4 > e2))) return true;
        return !properOnly && (OnSegment(a, c, d) || OnSegment(b, c, d) || OnSegment(c, a, b) || OnSegment(d, a, b));
    }

    public bool CollinearOverlap(Point2 a, Point2 b, Point2 c, Point2 d)
    {
        debit();
        var ab = Sub(b, a);
        var length = Length(ab);
        if (Math.Abs(Cross(ab, Sub(c, a))) > tolerance * length || Math.Abs(Cross(ab, Sub(d, a))) > tolerance * length) return false;
        var t1 = Dot(Sub(c, a), ab) / length;
        var t2 = Dot(Sub(d, a), ab) / length;
        return Math.Min(length, Math.Max(t1, t2)) - Math.Max(0, Math.Min(t1, t2)) > tolerance;
    }

    // -1 outside, 0 boundary, 1 strictly inside.
    public int InRing(Point2 p, IReadOnlyList<Point2> ring)
    {
        bool inside = false;
        for (var i = 0; i < ring.Count; i++)
        {
            debit();
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            if (OnSegment(p, a, b)) return 0;
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside ? 1 : -1;
    }

    public int InPolygon(Point2 p, Polygon2 polygon)
    {
        var outer = InRing(p, polygon.Outer);
        if (outer < 0) return -1;
        foreach (var hole in polygon.Holes)
        {
            var relation = InRing(p, hole);
            if (relation > 0) return -1;
            if (relation == 0) return 0;
        }
        return outer;
    }

    public IEnumerable<IReadOnlyList<Point2>> Rings(Polygon2 polygon)
    {
        yield return polygon.Outer;
        foreach (var hole in polygon.Holes) yield return hole;
    }

    // Split at every boundary intersection: endpoint-only containment fails for concave polygons.
    public bool ContainsSegment(Polygon2 polygon, Point2 a, Point2 b)
    {
        if (InPolygon(a, polygon) < 0 || InPolygon(b, polygon) < 0) return false;
        var parameters = new List<double> { 0, 1 };
        var ab = Sub(b, a);
        var squared = Dot(ab, ab);
        foreach (var ring in Rings(polygon))
        for (var i = 0; i < ring.Count; i++)
        {
            debit();
            var c = ring[i];
            var d = ring[(i + 1) % ring.Count];
            var cd = Sub(d, c);
            var denominator = Cross(ab, cd);
            if (Math.Abs(denominator) > tolerance * Math.Max(Length(ab), Length(cd)))
            {
                var t = Cross(Sub(c, a), cd) / denominator;
                var u = Cross(Sub(c, a), ab) / denominator;
                if (t > 0 && t < 1 && u >= 0 && u <= 1) parameters.Add(t);
            }
            else
            {
                if (OnSegment(c, a, b)) parameters.Add(Math.Clamp(Dot(Sub(c, a), ab) / squared, 0, 1));
                if (OnSegment(d, a, b)) parameters.Add(Math.Clamp(Dot(Sub(d, a), ab) / squared, 0, 1));
            }
        }
        parameters.Sort();
        for (var i = 1; i < parameters.Count; i++)
            if (InPolygon(Add(a, Mul(ab, (parameters[i] + parameters[i - 1]) / 2)), polygon) < 0) return false;
        return true;
    }

    public bool Contains(Polygon2 outer, Polygon2 inner)
    {
        foreach (var ring in Rings(inner))
        for (var i = 0; i < ring.Count; i++)
            if (!ContainsSegment(outer, ring[i], ring[(i + 1) % ring.Count])) return false;
        // A child polygon must not fill an excluded courtyard even if none of its edges crosses it.
        foreach (var hole in outer.Holes)
            if (hole.Any(p => InPolygon(p, inner) > 0)) return false;
        return true;
    }

    public bool BoundaryContainsSegment(Polygon2 polygon, Point2 a, Point2 b)
    {
        var ab = Sub(b, a); var length = Length(ab);
        var spans = new List<(double Start, double End)>();
        foreach (var ring in Rings(polygon))
        for (var i = 0; i < ring.Count; i++)
        {
            debit();
            var c = ring[i]; var d = ring[(i + 1) % ring.Count];
            if (Math.Abs(Cross(ab, Sub(c, a))) > tolerance * length || Math.Abs(Cross(ab, Sub(d, a))) > tolerance * length) continue;
            var start = Dot(Sub(c, a), ab) / length; var end = Dot(Sub(d, a), ab) / length;
            spans.Add((Math.Min(start, end), Math.Max(start, end)));
        }
        double until = 0;
        foreach (var span in Merge(spans))
        {
            if (span.End < until - tolerance) continue;
            if (span.Start > until + tolerance) break;
            until = Math.Max(until, span.End);
        }
        return until >= length - tolerance;
    }

    public bool Overlap(Polygon2 a, Polygon2 b)
    {
        foreach (var ar in Rings(a))
        foreach (var br in Rings(b))
        for (var i = 0; i < ar.Count; i++)
        for (var j = 0; j < br.Count; j++)
            if (Intersects(ar[i], ar[(i + 1) % ar.Count], br[j], br[(j + 1) % br.Count], true)) return true;
        bool HasInteriorWitness(Polygon2 own, Polygon2 other)
        {
            foreach (var ring in Rings(own))
            for (var i = 0; i < ring.Count; i++)
            {
                if (InPolygon(ring[i], other) > 0) return true;
                var next = ring[(i + 1) % ring.Count];
                var direction = Sub(next, ring[i]);
                var length = Length(direction);
                var parameters = new List<double> { 0, 1 };
                // A crossing at the other polygon's vertex is not a proper segment
                // crossing. Split there so a short overlapping subedge cannot be
                // hidden by the midpoint of its longer authored edge.
                foreach (var otherRing in Rings(other))
                foreach (var vertex in otherRing)
                {
                    debit();
                    if (OnSegment(vertex, ring[i], next)) parameters.Add(Math.Clamp(Dot(Sub(vertex, ring[i]), direction) / (length * length), 0, 1));
                }
                parameters.Sort();
                for (var part = 1; part < parameters.Count; part++)
                {
                    if ((parameters[part] - parameters[part - 1]) * length <= tolerance) continue;
                    var midpoint = Add(ring[i], Mul(direction, (parameters[part] + parameters[part - 1]) / 2));
                    if (InPolygon(midpoint, other) > 0) return true;
                    // Left is the filled side for both mandated outer/hole windings.
                    var probe = Add(midpoint, Mul(new Point2(-direction.Y, direction.X), tolerance * 4 / length));
                    if (InPolygon(probe, own) > 0 && InPolygon(probe, other) > 0) return true;
                }
            }
            return false;
        }
        // Contained polygons can have every vertex on the other's boundary; interior edge
        // witnesses are therefore required in BOTH directions, including aligned tread cells.
        return HasInteriorWitness(a, b) || HasInteriorWitness(b, a);
    }

    public Polygon2 Rectangle(Point2 a, Point2 b, double width)
    {
        var d = Sub(b, a);
        var n = Mul(new Point2(-d.Y, d.X), width / (2 * Length(d)));
        return new Polygon2 { Outer = [Sub(a, n), Sub(b, n), Add(b, n), Add(a, n)], Holes = [] };
    }

    // Sutherland-Hodgman clipping for the convex, CCW authored flight/tread rectangles.
    public List<Point2> ConvexIntersection(IReadOnlyList<Point2> subject, IReadOnlyList<Point2> clip)
    {
        var output = subject.ToList();
        for (var i = 0; i < clip.Count && output.Count > 0; i++)
        {
            var a = clip[i]; var b = clip[(i + 1) % clip.Count]; var direction = Sub(b, a);
            var input = output; output = [];
            var previous = input[^1]; var previousDistance = Cross(direction, Sub(previous, a));
            foreach (var current in input)
            {
                debit();
                var currentDistance = Cross(direction, Sub(current, a));
                var previousInside = previousDistance >= -tolerance * Length(direction);
                var currentInside = currentDistance >= -tolerance * Length(direction);
                if (previousInside != currentInside)
                {
                    var denominator = previousDistance - currentDistance;
                    if (Math.Abs(denominator) > double.Epsilon)
                    {
                        var ratio = Math.Clamp(previousDistance / denominator, 0, 1);
                        output.Add(Add(previous, Mul(Sub(current, previous), ratio)));
                    }
                }
                if (currentInside) output.Add(current);
                previous = current; previousDistance = currentDistance;
            }
        }
        return output;
    }

    // All vertices of an intersection, including disconnected components of concave polygons.
    // A linear height function reaches its extrema among these actual boundary witnesses.
    public List<Point2> IntersectionVertices(Polygon2 a, Polygon2 b)
    {
        var result = new List<Point2>();
        foreach (var ring in Rings(a))
        foreach (var p in ring) if (InPolygon(p, b) >= 0) result.Add(p);
        foreach (var ring in Rings(b))
        foreach (var p in ring) if (InPolygon(p, a) >= 0) result.Add(p);
        foreach (var ar in Rings(a))
        foreach (var br in Rings(b))
        for (var i = 0; i < ar.Count; i++)
        for (var j = 0; j < br.Count; j++)
        {
            debit();
            var start = ar[i]; var end = ar[(i + 1) % ar.Count];
            var otherStart = br[j]; var otherEnd = br[(j + 1) % br.Count];
            var direction = Sub(end, start); var otherDirection = Sub(otherEnd, otherStart);
            var denominator = Cross(direction, otherDirection);
            if (Math.Abs(denominator) <= double.Epsilon) continue;
            var t = Cross(Sub(otherStart, start), otherDirection) / denominator;
            var u = Cross(Sub(otherStart, start), direction) / denominator;
            if (t >= 0 && t <= 1 && u >= 0 && u <= 1) result.Add(Add(start, Mul(direction, t)));
        }
        return result;
    }

    /// <summary>Exact straight-edge arrangement sweep, modulo declared tolerance; no raster sampling.</summary>
    public bool Covers(IReadOnlyList<Polygon2> subjects, IReadOnlyList<Polygon2> covers)
    {
        var edges = subjects.Concat(covers).SelectMany(Rings)
            .SelectMany(ring => Enumerable.Range(0, ring.Count).Select(i => (A: ring[i], B: ring[(i + 1) % ring.Count]))).ToList();
        var critical = edges.Select(e => e.A.X).ToList();
        for (var i = 0; i < edges.Count; i++)
        for (var j = i + 1; j < edges.Count; j++)
        {
            debit();
            var a = edges[i]; var b = edges[j]; var da = Sub(a.B, a.A); var db = Sub(b.B, b.A);
            var denominator = Cross(da, db);
            if (Math.Abs(denominator) <= tolerance * Math.Max(Length(da), Length(db))) continue;
            var t = Cross(Sub(b.A, a.A), db) / denominator;
            var u = Cross(Sub(b.A, a.A), da) / denominator;
            if (t > 0 && t < 1 && u > 0 && u < 1)
            {
                critical.Add(a.A.X + t * da.X);
                // Bound arrangement storage as well as pairwise arithmetic.
                if (critical.Count > 100_000) throw new GeometryCapacityException();
            }
        }
        critical.Sort();
        for (var i = 1; i < critical.Count; i++)
        {
            debit();
            if (critical[i] - critical[i - 1] <= tolerance) continue;
            var x = (critical[i] + critical[i - 1]) / 2;
            var needed = Merge(subjects.SelectMany(p => Intervals(p, x)));
            var available = Merge(covers.SelectMany(p => Intervals(p, x)));
            foreach (var need in needed)
            {
                var until = need.Start;
                foreach (var supply in available)
                {
                    debit();
                    if (supply.End < until - tolerance) continue;
                    if (supply.Start > until + tolerance) break;
                    until = Math.Max(until, supply.End);
                    if (until >= need.End - tolerance) break;
                }
                if (until < need.End - tolerance) return false;
            }
        }
        return true;
    }

    private List<(double Start, double End)> Intervals(Polygon2 polygon, double x)
    {
        var intersections = new List<double>();
        foreach (var ring in Rings(polygon))
        for (var i = 0; i < ring.Count; i++)
        {
            debit();
            var a = ring[i]; var b = ring[(i + 1) % ring.Count];
            if ((a.X > x) == (b.X > x)) continue;
            intersections.Add(a.Y + (b.Y - a.Y) * (x - a.X) / (b.X - a.X));
        }
        intersections.Sort();
        var result = new List<(double Start, double End)>();
        for (var i = 1; i < intersections.Count; i += 2) result.Add((intersections[i - 1], intersections[i]));
        return result;
    }

    private List<(double Start, double End)> Merge(IEnumerable<(double Start, double End)> spans)
    {
        var result = new List<(double Start, double End)>();
        foreach (var span in spans.OrderBy(s => s.Start))
        {
            debit();
            if (result.Count == 0 || span.Start > result[^1].End + tolerance) result.Add(span);
            else result[^1] = (result[^1].Start, Math.Max(result[^1].End, span.End));
        }
        return result;
    }
}
