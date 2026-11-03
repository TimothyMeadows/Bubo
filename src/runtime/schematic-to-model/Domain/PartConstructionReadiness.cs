namespace Bubo.Schematic.Domain;

/// <summary>Admission for the bounded fixed part author. Declared resolution is never reduced.</summary>
public sealed class PartConstructionReadiness
{
    public const int MaximumVertices = 2_000_000;
    public const int MaximumTriangles = 2_000_000;
    public const int MaximumJobBytes = 8 * 1024 * 1024;

    public PartReadinessReport Validate(AssemblyDesign design, CancellationToken cancellation = default)
    {
        var declaration = new AssemblyDesignValidator().Validate(design, cancellation);
        var issues = declaration.Issues.ToList();
        if (!declaration.IsValid) return new(issues);
        void Error(string code, string path, string message)
        {
            cancellation.ThrowIfCancellationRequested();
            if (issues.Count < 128) issues.Add(new(code, path, message));
        }
        if (new[] { design.Numerics.LinearTolerance, design.Numerics.AngularToleranceDegrees, design.Numerics.MinimumFeatureSize, design.Numerics.MaxCoordinateMagnitude }.Any(v => v < 1e-12))
            Error("part-precision", "numerics", "Fixed part construction requires every numerical policy value to resolve at least 1e-12.");
        var identities = new[] { design.Id }.Concat(design.Observations.Select(x => x.Id)).Concat(design.Materials.Select(x => x.Id))
            .Concat(design.Parts.Select(x => x.Id)).Concat(design.Parts.SelectMany(x => x.Features.Select(f => f.Id)))
            .Concat(design.Parts.SelectMany(x => x.Interfaces.Select(p => p.Id).Concat(x.Terminals.Select(p => p.Id))))
            .Concat(design.Subassemblies.Select(x => x.Id)).Concat(design.Instances.Select(x => x.Id)).Concat(design.Joints.Select(x => x.Id))
            .Concat(design.Mechanisms.Select(x => x.Id)).Concat(design.Routes.Select(x => x.Id)).Concat(design.Connections.Select(x => x.Id)).Concat(design.Sections.Select(x => x.Id));
        foreach (var identity in identities)
        {
            cancellation.ThrowIfCancellationRequested(); var prefix = identity.Split('.')[0].ToUpperInvariant();
            if (prefix is "CON" or "PRN" or "AUX" or "NUL" || prefix.Length == 4 && (prefix.StartsWith("COM", StringComparison.Ordinal) || prefix.StartsWith("LPT", StringComparison.Ordinal)) && char.IsAsciiDigit(prefix[3]))
                Error("part-identity", "id", "Fixed portable construction cannot use a reserved Windows device identity: " + identity);
        }
        long vertices = 0, triangles = 0, weighted = 0, work = 0;
        var occurrences = design.Instances.GroupBy(i => i.PartId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.LongCount(), StringComparer.Ordinal);
        try
        {
            foreach (var part in design.Parts)
            {
                cancellation.ThrowIfCancellationRequested();
                if (part.Features[0].Operation != FeatureOperation.Add)
                    Error("part-feature-order", "parts." + part.Id + ".features", "Fixed construction requires the first feature to add a solid; leading subtractive features are not reordered silently.");
                if (part.Features.Count > 4096) Error("part-feature-count", "parts." + part.Id + ".features", "Fixed part construction permits at most 4096 declared features per definition.");
                long partVertices = 0, partTriangles = 0;
                foreach (var feature in part.Features)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var path = "parts." + part.Id + ".features." + feature.Id;
                    long v, t;
                    var segments = feature.Parameters.GetValueOrDefault("segments");
                    if (segments > 1024) Error("part-resolution", path, "Fixed construction currently admits at most 1024 explicitly declared segments; resolution is never lowered automatically.");
                    if (feature.Parameters.Values.Any(v => v < 1e-12)) Error("part-precision", path, "Fixed construction requires feature parameters to resolve at least 1e-12; smaller values require an explicitly reviewed construction profile.");
                    work = checked(work + (long)feature.Profile.Count * feature.Profile.Count + (long)feature.Path.Count * feature.Path.Count
                        + feature.Sections.Sum(s => (long)s.Profile.Count * s.Profile.Count) + 1);
                    if (work > Math.Min(design.Budgets.MaxValidationChecks, 5_000_000))
                    { Error("part-work-budget", path, "Fixed construction shape admission exceeds the approved finite work budget."); return new(issues); }
                    if (feature.Kind is FeatureKind.Extrude or FeatureKind.Revolve && !Convex(feature.Profile, cancellation))
                        Error("part-profile", path, "Fixed extrusion and revolution require strictly convex CCW profiles; a different declared outline is never repaired silently.");
                    switch (feature.Kind)
                    {
                        case FeatureKind.Box: v = 8; t = 12; break;
                        case FeatureKind.Cylinder: v = checked(2 * (long)segments); t = checked(4 * (long)segments - 4); break;
                        case FeatureKind.Extrude: v = checked(2L * feature.Profile.Count); t = checked(4L * feature.Profile.Count - 4); break;
                        case FeatureKind.Revolve:
                            v = checked(((long)segments + 1) * feature.Profile.Count);
                            t = checked(2L * (long)segments * feature.Profile.Count + 2L * Math.Max(0, feature.Profile.Count - 2));
                            break;
                        case FeatureKind.Sweep:
                            if (feature.Path.Count > 4096 || !Sweep(feature.Path, feature.Parameters["radius"], cancellation))
                                Error("part-sweep-profile", path, "Fixed circular sweep requires distinct finite path vertices, nonreversing bends and nonlocal tube clearance; an explicitly repeated first endpoint closes the sweep.");
                            var closed = feature.Path[0] == feature.Path[^1];
                            var rings = feature.Path.Count - (closed ? 1 : 0);
                            v = checked(rings * (long)segments);
                            t = closed ? checked(2 * rings * (long)segments) : checked(2 * (rings - 1) * (long)segments + 2 * ((long)segments - 2));
                            break;
                        case FeatureKind.Loft:
                            if (!Loft(feature.Sections, cancellation)) Error("part-loft-profile", path, "Fixed loft requires parallel, consistently indexed convex profiles on increasing axial planes, related by positive independent X/Y scales and translation.");
                            v = checked((long)feature.Sections.Count * feature.Sections[0].Profile.Count);
                            t = checked(2L * (feature.Sections.Count - 1) * feature.Sections[0].Profile.Count + 2L * (feature.Sections[0].Profile.Count - 2));
                            break;
                        default: Error("part-feature", path, "Unsupported fixed part feature."); continue;
                    }
                    partVertices = checked(partVertices + v); partTriangles = checked(partTriangles + t);
                }
                vertices = checked(vertices + partVertices); triangles = checked(triangles + partTriangles);
                weighted = checked(weighted + partTriangles * occurrences.GetValueOrDefault(part.Id));
            }
        }
        catch (OverflowException) { Error("part-budget", "budgets", "Declared construction complexity exceeds the finite native limits."); }
        if (vertices > MaximumVertices || triangles > Math.Min(MaximumTriangles, design.Budgets.MaxGeneratedTriangles) || weighted > Math.Min(MaximumTriangles, design.Budgets.MaxGeneratedTriangles))
            Error("part-budget", "budgets.maxGeneratedTriangles", "Declared full-resolution definition and occurrence-weighted geometry exceeds the approved or hard construction limits; increase approved budgets or author a revised design, never lower detail silently.");
        return new(issues);
    }

    public void RequireReady(AssemblyDesign design, CancellationToken cancellation = default)
    {
        var report = Validate(design, cancellation);
        if (!report.IsReady) throw new DesignValidationException(new(report.Issues, 0));
    }

    static bool Convex(IReadOnlyList<Vec2> points, CancellationToken cancellation)
    {
        if (points.Count is < 3 or > 4096) return false;
        for (var i = 0; i < points.Count; i++)
        {
            var a = points[i]; var b = points[(i + 1) % points.Count];
            for (var j = 0; j < points.Count; j++)
            {
                cancellation.ThrowIfCancellationRequested(); if (j == i || j == (i + 1) % points.Count) continue;
                var c = points[j]; if ((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X) <= 1e-16) return false;
            }
        }
        return true;
    }
    static Vec3 Sub(Vec3 a, Vec3 b) => new() { X = a.X - b.X, Y = a.Y - b.Y, Z = a.Z - b.Z };
    static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    static Vec3 Mul(Vec3 p, double t) => new() { X = p.X * t, Y = p.Y * t, Z = p.Z * t };
    static bool Loft(IReadOnlyList<LoftSection> sections, CancellationToken cancellation)
    {
        if (sections.Count is < 2 or > 1024) return false;
        var first = sections[0]; var basePoints = first.Profile;
        var normal = Sub(DesignGeometry.TransformPoint(first.Frame, new() { X = 0, Y = 0, Z = 1 }), first.Frame.Origin);
        var previous = double.NegativeInfinity;
        foreach (var section in sections)
        {
            cancellation.ThrowIfCancellationRequested();
            var offset = Dot(Sub(section.Frame.Origin, first.Frame.Origin), normal);
            if (section.Frame.RotationDegrees != first.Frame.RotationDegrees || section.Profile.Count != basePoints.Count
                || !Convex(section.Profile, cancellation) || offset <= previous + 1e-12) return false;
            previous = offset;
            // A positive diagonal map preserves convexity for every interpolated
            // ring; monotonic axial planes prevent different rings intersecting.
            for (var axis = 0; axis < 2; axis++)
            {
                double Coordinate(Vec2 p) => axis == 0 ? p.X : p.Y;
                var index = Enumerable.Range(1, basePoints.Count - 1).MaxBy(i => Math.Abs(Coordinate(basePoints[i]) - Coordinate(basePoints[0])));
                if (Math.Abs(Coordinate(basePoints[index]) - Coordinate(basePoints[0])) <= 1e-12) return false;
                var scale = (Coordinate(section.Profile[index]) - Coordinate(section.Profile[0])) / (Coordinate(basePoints[index]) - Coordinate(basePoints[0]));
                if (!(scale > 0)) return false;
                for (var i = 0; i < basePoints.Count; i++)
                    if (Math.Abs((Coordinate(section.Profile[i]) - Coordinate(section.Profile[0])) - scale * (Coordinate(basePoints[i]) - Coordinate(basePoints[0]))) > 1e-10) return false;
            }
        }
        return true;
    }
    static double SegmentDistance(Vec3 a, Vec3 b, Vec3 c, Vec3 d)
    {
        var u = Sub(b, a); var v = Sub(d, c); var w = Sub(a, c);
        var aa = Dot(u,u); var bb = Dot(u,v); var cc = Dot(v,v); var dd = Dot(u,w); var ee = Dot(v,w); var denominator = aa * cc - bb * bb;
        var s = denominator > 1e-24 * aa * cc ? Math.Clamp((bb * ee - cc * dd) / denominator, 0, 1) : 0;
        var t = (bb * s + ee) / cc;
        if (t < 0) { t = 0; s = Math.Clamp(-dd / aa, 0, 1); }
        else if (t > 1) { t = 1; s = Math.Clamp((bb - dd) / aa, 0, 1); }
        var delta = new Vec3 { X = w.X + s * u.X - t * v.X, Y = w.Y + s * u.Y - t * v.Y, Z = w.Z + s * u.Z - t * v.Z };
        return Math.Sqrt(Dot(delta, delta));
    }
    static bool Sweep(IReadOnlyList<Vec3> path, double radius, CancellationToken cancellation)
    {
        var closed = path[0] == path[^1]; var points = closed ? path.Take(path.Count - 1).ToArray() : path.ToArray();
        if (points.Length < (closed ? 3 : 2) || points.Distinct().Count() != points.Length) return false;
        var count = closed ? points.Length : points.Length - 1; var directions = new Vec3[count]; var lengths = new double[count];
        for (var i = 0; i < count; i++)
        {
            cancellation.ThrowIfCancellationRequested(); var delta = Sub(points[(i + 1) % points.Length], points[i]); var length = Math.Sqrt(Dot(delta, delta));
            if (length <= 1e-10) return false; lengths[i] = length; directions[i] = Mul(delta, 1 / length);
        }
        for (var i = 0; i < points.Length; i++)
        {
            cancellation.ThrowIfCancellationRequested(); if (!closed && (i == 0 || i == points.Length - 1)) continue;
            var before = (i + count - 1) % count; var after = i % count; var cosine = Math.Clamp(Dot(directions[before], directions[after]), -1, 1);
            if (cosine <= -.5) return false;
            var sine = Math.Sqrt(Math.Max(0, 1 - cosine * cosine));
            if (sine > 1e-12)
            {
                var a = lengths[before]; var b = lengths[after]; var chord = Math.Sqrt(a * a + b * b + 2 * a * b * cosine);
                if (chord / (2 * sine) <= radius * (1 + 1e-8)) return false;
            }
        }
        var tangents = new Vec3[points.Length];
        for (var i = 0; i < points.Length; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!closed && i == 0) tangents[i] = directions[0];
            else if (!closed && i == points.Length - 1) tangents[i] = directions[^1];
            else
            {
                var a = directions[(i + count - 1) % count]; var b = directions[i % count];
                var sum = new Vec3 { X = a.X + b.X, Y = a.Y + b.Y, Z = a.Z + b.Z }; tangents[i] = Mul(sum, 1 / Math.Sqrt(Dot(sum,sum)));
            }
        }
        for (var i = 0; i < points.Length; i++)
        foreach (var step in new[] {-1,1})
        {
            cancellation.ThrowIfCancellationRequested(); var j = i + step;
            if (!closed && (j < 0 || j >= points.Length)) continue; j = (j + points.Length) % points.Length;
            var projection = step * Dot(Sub(points[j],points[i]),tangents[i]); var cosine = Math.Clamp(Dot(tangents[i],tangents[j]),-1,1);
            var radialProjection = radius * Math.Sqrt(Math.Max(0,1-cosine*cosine));
            if (projection <= radialProjection * (1+1e-8) + 1e-12) return false;
        }
        for (var i = 0; i < count; i++)
        for (var j = i + 1; j < count; j++)
        {
            cancellation.ThrowIfCancellationRequested(); if (j == i + 1 || closed && i == 0 && j == count - 1) continue;
            if (SegmentDistance(points[i], points[(i + 1) % points.Length], points[j], points[(j + 1) % points.Length]) <= 2 * radius * (1 + 1e-8)) return false;
        }
        return true;
    }
}
