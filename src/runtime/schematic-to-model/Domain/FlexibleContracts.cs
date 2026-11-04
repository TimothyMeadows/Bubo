namespace Bubo.Schematic.Domain;

/// <summary>One real source-mesh ring per bone; poses are prototype-local, Y tangent, and right-handed.</summary>
public sealed record FlexibleRigProgram(string InstanceId, bool ClosedPath, int RadialSegments, double BoneLength,
    IReadOnlyList<RigidPose> RestRingFrames, IReadOnlyList<FlexibleRigClip> Clips);
public sealed record FlexibleRigClip(string ClipId, IReadOnlyList<FlexibleRigSample> Samples);
public sealed record FlexibleRigSample(int SampleIndex, IReadOnlyList<RigidPose> RingFrames);
/// <summary>Measured centreline constraints, not a physics or manufacturing certification.</summary>
public sealed record FlexibleRingMetrics(double ArcLength, double MinimumSegmentRatio, double MaximumSegmentRatio, double MinimumBendRadius, double? EndpointDistance);
public sealed class FlexibleMotionException(string message) : ArgumentException(message);

/// <summary>Shared double-precision measured checks for authored and independently evaluated rings.</summary>
public static class FlexibleRingGeometry
{
    public static FlexibleRingMetrics Measure(IReadOnlyList<Vec3> points, IReadOnlyList<Vec3> rest, bool closed, Action? step = null)
    {
        if (points.Count != rest.Count || points.Count < (closed ? 3 : 2)) throw new FlexibleMotionException("Flexible ring count differs from the original source");
        double arc = 0, minRatio = double.PositiveInfinity, maxRatio = 0, bend = double.PositiveInfinity;
        var count = closed ? points.Count : points.Count - 1;
        for (var i = 0; i < count; i++)
        {
            step?.Invoke(); var j = (i + 1) % points.Count;
            var length = DesignGeometry.Length(points[i], points[j]); var reference = DesignGeometry.Length(rest[i], rest[j]);
            if (!double.IsFinite(length) || !double.IsFinite(reference) || reference <= 0 || length <= 0) throw new FlexibleMotionException("Flexible rings contain a nonfinite or collapsed segment");
            arc += length; var ratio = length / reference; minRatio = Math.Min(minRatio, ratio); maxRatio = Math.Max(maxRatio, ratio);
        }
        for (var i = closed ? 0 : 1; i < (closed ? points.Count : points.Count - 1); i++)
        {
            step?.Invoke(); var before = points[(i + points.Count - 1) % points.Count]; var current = points[i]; var after = points[(i + 1) % points.Count];
            var a = RigidTransforms.Subtract(current, before); var b = RigidTransforms.Subtract(after, current);
            var cross = RigidTransforms.Length(RigidTransforms.Cross(a, b)); var ab = RigidTransforms.Length(a) * RigidTransforms.Length(b);
            if (RigidTransforms.Dot(a, b) <= -ab + ab * 1e-12) throw new FlexibleMotionException("Flexible source or movement folds back on a ring");
            if (cross > ab * 1e-12)
                bend = Math.Min(bend, ab * DesignGeometry.Length(before, after) / (2 * cross));
        }
        if (!double.IsFinite(arc)) throw new FlexibleMotionException("Flexible arc length is not finite");
        return new(arc, minRatio, maxRatio, bend, closed ? null : DesignGeometry.Length(points[0], points[^1]));
    }

    public static void RequireLimits(FlexibleRingMetrics metrics, FlexibleLimits limits, FlexibleRigPolicy policy, double tolerance)
    {
        if (metrics.ArcLength < limits.MinimumLength - tolerance || metrics.ArcLength > limits.MaximumLength + tolerance
            || metrics.MinimumSegmentRatio < policy.MinimumSegmentRatio - 1e-12 || metrics.MaximumSegmentRatio > limits.MaximumStretchRatio + 1e-12
            || metrics.MinimumBendRadius < limits.MinimumBendRadius - tolerance
            || metrics.EndpointDistance is { } distance && (policy.MinimumEndpointDistance is not { } minimum || policy.MaximumEndpointDistance is not { } maximum || distance < minimum - tolerance || distance > maximum + tolerance))
            throw new FlexibleMotionException("Measured flexible arc, local strain, bend radius or owned endpoint distance exceeds its approved limits");
    }

    /// <summary>Conservative bounds for a prototype-local linear centreline continuation model; actual native interpolation is separate evidence.</summary>
    public static void RequireLinearInterval(IReadOnlyList<Vec3> before, IReadOnlyList<Vec3> after, IReadOnlyList<Vec3> rest,
        bool closed, FlexibleLimits limits, FlexibleRigPolicy policy, double tolerance, Action? step = null)
    {
        if (before.Count != after.Count || before.Count != rest.Count) throw new FlexibleMotionException("Flexible interval ring inventory changed");
        static double MinimumNorm(Vec3 a, Vec3 b)
        {
            var delta = RigidTransforms.Subtract(b, a); var squared = RigidTransforms.Dot(delta, delta);
            var weight = squared == 0 ? 0 : Math.Clamp(-RigidTransforms.Dot(a, delta) / squared, 0, 1);
            return RigidTransforms.Length(RigidTransforms.Add(a, RigidTransforms.Scale(delta, weight)));
        }
        double arcLowerBound = 0;
        for (var i = 0; i < (closed ? before.Count : before.Count - 1); i++)
        {
            step?.Invoke(); var next = (i + 1) % before.Count;
            var minimum = MinimumNorm(RigidTransforms.Subtract(before[next], before[i]), RigidTransforms.Subtract(after[next], after[i]));
            var reference = DesignGeometry.Length(rest[i], rest[next]); arcLowerBound += minimum;
            if (!double.IsFinite(minimum) || minimum <= 0 || minimum / reference < policy.MinimumSegmentRatio - 1e-12)
                throw new FlexibleMotionException("An interpolated source-ring segment collapses or exceeds approved local compression between diagnostic keys");
        }
        if (arcLowerBound < limits.MinimumLength - tolerance)
            throw new FlexibleMotionException("The approved arc minimum cannot be certified between diagnostic keys; explicitly denser sampling is required");
        if (!closed)
        {
            step?.Invoke(); var minimum = MinimumNorm(RigidTransforms.Subtract(before[^1], before[0]), RigidTransforms.Subtract(after[^1], after[0]));
            if (policy.MinimumEndpointDistance is not { } approved || minimum < approved - tolerance)
                throw new FlexibleMotionException("Owned endpoint separation violates its minimum between diagnostic keys");
        }
        for (var i = closed ? 0 : 1; i < (closed ? before.Count : before.Count - 1); i++)
        {
            step?.Invoke(); var previous = (i + before.Count - 1) % before.Count; var next = (i + 1) % before.Count;
            var a0 = RigidTransforms.Subtract(before[i], before[previous]); var b0 = RigidTransforms.Subtract(before[next], before[i]);
            var a1 = RigidTransforms.Subtract(after[i], after[previous]); var b1 = RigidTransforms.Subtract(after[next], after[i]);
            // Cross(a(t),b(t)) is a quadratic Bezier curve. Its norm cannot exceed
            // the largest control-vector norm; the side/chord minima are exact.
            var q0 = RigidTransforms.Cross(a0, b0); var q2 = RigidTransforms.Cross(a1, b1);
            var q1 = RigidTransforms.Scale(RigidTransforms.Add(RigidTransforms.Cross(a0, b1), RigidTransforms.Cross(a1, b0)), .5);
            var maximumCross = Math.Max(RigidTransforms.Length(q0), Math.Max(RigidTransforms.Length(q1), RigidTransforms.Length(q2)));
            var chordMinimum = MinimumNorm(RigidTransforms.Add(a0, b0), RigidTransforms.Add(a1, b1));
            if (chordMinimum <= 0) throw new FlexibleMotionException("Flexible interpolation folds back between diagnostic keys");
            if (maximumCross > 0)
            {
                var radiusLowerBound = MinimumNorm(a0, a1) * MinimumNorm(b0, b1) * chordMinimum / (2 * maximumCross);
                if (!double.IsFinite(radiusLowerBound) || radiusLowerBound < limits.MinimumBendRadius - tolerance)
                    throw new FlexibleMotionException("The approved bend radius cannot be certified between diagnostic keys; explicitly denser sampling is required");
            }
        }
    }
}
