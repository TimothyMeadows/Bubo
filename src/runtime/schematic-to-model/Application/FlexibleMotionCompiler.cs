using Bubo.Schematic.Domain;
using static Bubo.Schematic.Domain.RigidTransforms;

namespace Bubo.Schematic.Application;

/// <summary>Bounded source-ring deformation. It creates no replacement geometry and never resamples an approved sweep.</summary>
public sealed class FlexibleMotionCompiler(IAssemblyDesignValidator? validator = null)
{
    public const long MaxRigFrameRecords = 16_384;
    readonly IAssemblyDesignValidator validator = validator ?? new AssemblyDesignValidator();

    public IReadOnlyList<FlexibleRigProgram> Compile(AssemblyDesign design, DiagnosticPlan plan, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(plan);
        var validation = validator.Validate(design, ct); if (!validation.IsValid) throw new DesignValidationException(validation);
        var profileValidation = new AssemblyProfileValidator().Validate(design, ct); if (!profileValidation.IsValid) throw new DesignValidationException(profileValidation);
        if (design.SchemaVersion != "bubo-assembly-design/v2" || design.AssemblyProfile is not { } profile) throw new FlexibleMotionException("Flexible readiness requires an explicit approved v2 profile; v1 is design-only");
        long work = validation.Checks + profileValidation.Checks;
        void Step() { ct.ThrowIfCancellationRequested(); if (++work > design.Budgets.MaxValidationChecks) throw new FlexibleMotionException("Flexible motion exceeds MaxValidationChecks"); }
        if (plan.SchemaVersion != "bubo-schematic-diagnostic-plan/v1" || plan.Clips is null || plan.Clips.Count > design.Joints.Count * 2L
            || plan.Clips.Any(c => c is null || c.Samples is null || c.Samples.Count < (c.CompleteCycle ? profile.SamplesPerCycle : profile.SamplesPerRange)))
            throw new FlexibleMotionException("A complete bounded native diagnostic plan is required");
        long samples = 1;
        foreach (var clip in plan.Clips) { Step(); samples += clip.Samples.Count; }
        if (samples > design.Budgets.MaxMotionSamples || samples != plan.Sampling.TotalSamples || plan.Clips.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != plan.Clips.Count)
            throw new FlexibleMotionException("Diagnostic identities or sample totals exceed their declared bounds");
        var ringCount = design.Routes.Sum(r => (long)r.Points.Count);
        if (checked(ringCount * samples) > MaxRigFrameRecords)
            throw new FlexibleMotionException("Flexible rest and clip frames exceed the native hard limit of 16384 rig frame records; the approved source rings and samples cannot be reduced automatically");
        // Bound the complete result and continuation checks before allocating any ring samples.
        if (ringCount > 65536 || samples > (design.Budgets.MaxValidationChecks - work) / Math.Max(1, 16 * ringCount)
            || ringCount * samples > design.Budgets.MaxExportBytes / 192)
            throw new FlexibleMotionException("Flexible ring/sample allocation exceeds finite work or output bounds");
        var parts = design.Parts.ToDictionary(p => p.Id, StringComparer.Ordinal); var instances = design.Instances.ToDictionary(i => i.Id, StringComparer.Ordinal);
        var policies = profile.FlexibleRigs.ToDictionary(p => p.RouteId, StringComparer.Ordinal); var joints = design.Joints.ToDictionary(j => j.Id, StringComparer.Ordinal);
        void CheckPose(AssemblyPose pose)
        {
            Step();
            if (pose is null || pose.Instances.Count != instances.Count || instances.Keys.Any(id => !pose.Instances.ContainsKey(id))) throw new FlexibleMotionException("Diagnostic pose omits actual physical instances");
            foreach (var value in pose.Instances.Values)
            {
                Step();
                if (!Finite(value.Origin) || !Finite(value.X) || !Finite(value.Y) || !Finite(value.Z)
                    || Math.Max(Math.Abs(value.Origin.X), Math.Max(Math.Abs(value.Origin.Y), Math.Abs(value.Origin.Z))) > design.Numerics.MaxCoordinateMagnitude
                    || Math.Abs(Length(value.X) - 1) > 1e-9 || Math.Abs(Length(value.Y) - 1) > 1e-9 || Math.Abs(Length(value.Z) - 1) > 1e-9
                    || Math.Abs(Dot(value.X, value.Y)) > 1e-9 || Math.Abs(Dot(value.X, value.Z)) > 1e-9 || Math.Abs(Dot(value.Y, value.Z)) > 1e-9 || Dot(Cross(value.X, value.Y), value.Z) < 1 - 1e-9)
                    throw new FlexibleMotionException("Diagnostic poses must contain finite unscaled right-handed unit bases");
            }
            foreach (var joint in joints.Values)
            {
                Step(); if (!pose.JointValues.TryGetValue(joint.Id, out var value) || !double.IsFinite(value) || value < joint.Minimum - design.Numerics.LinearTolerance || value > joint.Maximum + design.Numerics.LinearTolerance)
                    throw new FlexibleMotionException("Diagnostic pose omits a finite in-range owned joint coordinate");
            }
            AssemblyProfileValidator.RequireCouplingFits(design, pose.Instances, Step);
        }
        CheckPose(plan.Rest);
        foreach (var clip in plan.Clips)
        {
            Step();
            if (!joints.ContainsKey(clip.DriverJointId)) throw new FlexibleMotionException("Diagnostic driver is unknown");
            foreach (var sample in clip.Samples)
            { Step(); if (!double.IsFinite(sample.TimeSeconds)) throw new FlexibleMotionException("Diagnostic time must be finite"); CheckPose(sample.Pose); }
        }
        var result = new List<FlexibleRigProgram>(design.Routes.Count);
        foreach (var route in design.Routes)
        {
            Step(); var part = parts[instances[route.InstanceId].PartId]; var feature = part.Features.Single(); var policy = policies[route.Id]; var limits = part.Flexible!;
            var sourcePoints = feature.Path.Take(route.Points.Count).ToArray();
            var featurePose = FromFrame(feature.Frame);
            var sourceFrames = Frames(sourcePoints, route.Closed, null, Step).Select(frame => Compose(featurePose, frame)).ToArray();
            var sourceLocal = sourceFrames.Select(frame => frame.Origin).ToArray(); var restOwner = plan.Rest.Instances[route.InstanceId];
            var sourceWorld = sourceLocal.Select(point => Point(restOwner, point)).ToArray();
            for (var i = 0; i < sourceWorld.Length; i++)
            { Step(); if (DesignGeometry.Length(sourceWorld[i], route.Points[i]) > design.Numerics.LinearTolerance) throw new FlexibleMotionException("Diagnostic rest pose differs from the approved original flexible route"); }
            var restMetrics = FlexibleRingGeometry.Measure(sourceWorld, sourceWorld, route.Closed, Step);
            FlexibleRingGeometry.RequireLimits(restMetrics, limits, policy, design.Numerics.LinearTolerance);
            var segmentCount = route.Closed ? sourceLocal.Length : sourceLocal.Length - 1; var lengths = new double[segmentCount]; var cumulative = new double[sourceLocal.Length];
            var minimumSegment = double.PositiveInfinity; double total = 0;
            for (var i = 0; i < segmentCount; i++)
            { Step(); lengths[i] = DesignGeometry.Length(sourceLocal[i], sourceLocal[(i + 1) % sourceLocal.Length]); minimumSegment = Math.Min(minimumSegment, lengths[i]); if (i + 1 < cumulative.Length) cumulative[i + 1] = total + lengths[i]; total += lengths[i]; }
            var boneLength = Math.Max(minimumSegment / 4, design.Numerics.MinimumFeatureSize);
            var clips = new List<FlexibleRigClip>(plan.Clips.Count);
            foreach (var clip in plan.Clips)
            {
                Step(); var output = new List<FlexibleRigSample>(clip.Samples.Count); Vec3[]? previousWorld = null; double? previousDriver = null;
                foreach (var (sample, index) in clip.Samples.Select((value, index) => (value, index)))
                {
                    Step(); var owner = sample.Pose.Instances[route.InstanceId]; var ownerInverse = Inverse(owner); Vec3[] points; Vec3 initialNormal;
                    if (route.Closed)
                    {
                        if (!sample.Pose.JointValues.TryGetValue(policy.DriverJointId!, out var driver) || !double.IsFinite(driver)) throw new FlexibleMotionException("Belt diagnostic sample omits its actual finite driver value");
                        var advance = (driver - joints[policy.DriverJointId!].Rest) * policy.TravelMetresPerDegree!.Value;
                        if (!double.IsFinite(advance)) throw new FlexibleMotionException("Belt phase travel is not finite");
                        if (previousDriver is { } previous && Math.Abs(driver - previous) * policy.TravelMetresPerDegree.Value > minimumSegment / 2 + design.Numerics.LinearTolerance)
                            throw new FlexibleMotionException("Closed belt diagnostic keys are too far apart for the actual source rings; provide an explicitly denser approved sampling profile");
                        previousDriver = driver; points = new Vec3[sourceLocal.Length];
                        (Vec3 Point, Vec3 Normal) Along(double distance)
                        {
                            Step(); var phase = ((distance % total) + total) % total;
                            var found = Array.BinarySearch(cumulative, phase); var segment = found >= 0 ? found : ~found - 1;
                            segment = Math.Clamp(segment, 0, segmentCount - 1); var fraction = (phase - cumulative[segment]) / lengths[segment]; var next = (segment + 1) % sourceLocal.Length;
                            return (Add(Scale(sourceLocal[segment], 1 - fraction), Scale(sourceLocal[next], fraction)), sourceFrames[segment].X);
                        }
                        for (var i = 0; i < points.Length; i++) { Step(); points[i] = Point(owner, Along(cumulative[i] + advance).Point); }
                        initialNormal = Direction(owner, Along(advance).Normal);
                    }
                    else
                    {
                        var from = route.From!; var to = route.To!;
                        var firstDelta = Compose(sample.Pose.Instances[from.InstanceId], Inverse(plan.Rest.Instances[from.InstanceId]));
                        var lastDelta = Compose(sample.Pose.Instances[to.InstanceId], Inverse(plan.Rest.Instances[to.InstanceId]));
                        points = new Vec3[sourceWorld.Length];
                        for (var i = 0; i < points.Length; i++)
                        { Step(); var weight = cumulative[i] / total; points[i] = Add(Scale(Point(firstDelta, sourceWorld[i]), 1 - weight), Scale(Point(lastDelta, sourceWorld[i]), weight)); }
                        initialNormal = Direction(firstDelta, Direction(restOwner, sourceFrames[0].X));
                    }
                    foreach (var point in points)
                    { Step(); if (!Finite(point) || Math.Max(Math.Abs(point.X), Math.Max(Math.Abs(point.Y), Math.Abs(point.Z))) > design.Numerics.MaxCoordinateMagnitude) throw new FlexibleMotionException("Flexible movement exceeds the finite numerical coordinate bound"); }
                    FlexibleRingGeometry.RequireLimits(FlexibleRingGeometry.Measure(points, sourceWorld, route.Closed, Step), limits, policy, design.Numerics.LinearTolerance);
                    // Check a prototype-local linear centreline continuation model and measured
                    // interior probes. Actual Blender/world interpolation is verified separately.
                    var localPoints = points.Select(point => Point(ownerInverse, point)).ToArray();
                    if (previousWorld is not null)
                    {
                        FlexibleRingGeometry.RequireLinearInterval(previousWorld, localPoints, sourceLocal, route.Closed, limits, policy, design.Numerics.LinearTolerance, Step);
                        foreach (var weight in new[] { .25, .5, .75 })
                        {
                            Step(); var between = new Vec3[points.Length];
                            for (var i = 0; i < points.Length; i++) { Step(); between[i] = Add(Scale(previousWorld[i], 1 - weight), Scale(localPoints[i], weight)); }
                            FlexibleRingGeometry.RequireLimits(FlexibleRingGeometry.Measure(between, sourceLocal, route.Closed, Step), limits, policy, design.Numerics.LinearTolerance);
                        }
                    }
                    var frames = Frames(points, route.Closed, initialNormal, Step).Select(frame => Compose(ownerInverse, frame)).ToArray();
                    output.Add(new(index, Array.AsReadOnly(frames))); previousWorld = localPoints;
                }
                clips.Add(new(clip.Id, output.AsReadOnly()));
            }
            result.Add(new(route.InstanceId, route.Closed, checked((int)feature.Parameters["segments"]), boneLength, Array.AsReadOnly(sourceFrames), clips.AsReadOnly()));
        }
        return result.AsReadOnly();
    }

    static bool Finite(Vec3 point) => double.IsFinite(point.X) && double.IsFinite(point.Y) && double.IsFinite(point.Z);

    static RigidPose[] Frames(IReadOnlyList<Vec3> points, bool closed, Vec3? initialNormal, Action step)
    {
        var count = points.Count; var directions = new Vec3[closed ? count : count - 1]; var tangents = new Vec3[count]; var normals = new Vec3[count];
        for (var i = 0; i < directions.Length; i++) { step(); directions[i] = Normalize(Subtract(points[(i + 1) % count], points[i])); }
        for (var i = 0; i < count; i++)
        { step(); tangents[i] = !closed && i == 0 ? directions[0] : !closed && i == count - 1 ? directions[^1] : Normalize(Add(directions[(i + directions.Length - 1) % directions.Length], directions[i % directions.Length])); }
        if (initialNormal is not null)
        {
            var projected = Subtract(initialNormal, Scale(tangents[0], Dot(initialNormal, tangents[0])));
            if (Length(projected) <= 1e-12) throw new FlexibleMotionException("Flexible cross-section orientation is singular");
            normals[0] = Normalize(projected);
        }
        else
        {
            var seed = new[] { RigidPose.Identity.X, RigidPose.Identity.Y, RigidPose.Identity.Z }.MinBy(axis => Math.Abs(Dot(axis, tangents[0])))!;
            normals[0] = Normalize(Cross(tangents[0], seed));
        }
        Vec3 Transport(Vec3 normal, Vec3 before, Vec3 after)
        {
            step(); var cross = Cross(before, after); var sine = Length(cross); var cosine = Math.Clamp(Dot(before, after), -1, 1);
            if (sine < 1e-12) { if (cosine <= 0) throw new FlexibleMotionException("Flexible tangent reverses"); return normal; }
            var axis = Scale(cross, 1 / sine); var rotated = Add(Add(Scale(normal, cosine), Scale(Cross(axis, normal), sine)), Scale(axis, Dot(axis, normal) * (1 - cosine)));
            return Normalize(Subtract(rotated, Scale(after, Dot(rotated, after))));
        }
        for (var i = 1; i < count; i++) { step(); normals[i] = Transport(normals[i - 1], tangents[i - 1], tangents[i]); }
        if (closed)
        {
            var final = Transport(normals[^1], tangents[^1], tangents[0]); var twist = Math.Atan2(Dot(tangents[0], Cross(final, normals[0])), Dot(final, normals[0]));
            for (var i = 1; i < count; i++) { step(); var angle = twist * i / count; normals[i] = Add(Scale(normals[i], Math.Cos(angle)), Scale(Cross(tangents[i], normals[i]), Math.Sin(angle))); }
        }
        var frames = new RigidPose[count];
        for (var i = 0; i < count; i++) { step(); var x = Normalize(normals[i]); var y = tangents[i]; frames[i] = new(points[i], x, y, Normalize(Cross(x, y))); }
        return frames;
    }
}
