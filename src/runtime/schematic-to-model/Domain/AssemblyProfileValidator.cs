using static Bubo.Schematic.Domain.RigidTransforms;

namespace Bubo.Schematic.Domain;

/// <summary>Explicit readiness admission. A v1 design never acquires assembly choices implicitly.</summary>
public sealed class AssemblyProfileValidator
{
    public ValidationReport Validate(AssemblyDesign design, CancellationToken ct = default)
    {
        var issues = new List<ValidationIssue>(); long checks = 0;
        var maximum = Math.Clamp(design?.Budgets?.MaxValidationChecks ?? 1, 1, 10_000_000);
        void Step() { ct.ThrowIfCancellationRequested(); if (++checks > maximum) throw new ProfileWorkException(); }
        bool Test(bool condition, string path, string message)
        { Step(); if (!condition && issues.Count < 512) issues.Add(new("assembly-profile", path, message)); return condition; }
        bool Finite(double value) => double.IsFinite(value);
        try
        {
            Step();
            if (!Test(design is not null, "$", "An explicit design is required")) return new(issues, checks);
            if (design!.SchemaVersion == "bubo-assembly-design/v1")
            { Test(design.AssemblyProfile is null, "assemblyProfile", "Legacy v1 designs cannot carry or inherit a v2 assembly profile"); return new(issues, checks); }
            if (!Test(design.SchemaVersion == "bubo-assembly-design/v2" && design.AssemblyProfile is not null, "assemblyProfile", "v2 requires an explicit bubo-assembly-profile/v1")) return new(issues, checks);
            if (!Test(design.Numerics is not null && design.Budgets is not null, "$", "Numerics and finite budgets are required")) return new(issues, checks);
            var p = design.AssemblyProfile!; var numerics = design.Numerics!; var budgets = design.Budgets!;
            Test(p.SchemaVersion == "bubo-assembly-profile/v1", "assemblyProfile.schemaVersion", "Unsupported assembly profile");
            Test(p.SamplesPerRange is >= 3 and <= 4097 && p.SamplesPerCycle is >= 9 and <= 4097
                && p.SamplesPerRange <= budgets.MaxMotionSamples && p.SamplesPerCycle <= budgets.MaxMotionSamples,
                "assemblyProfile.samples", "Range/cycle minima must fit the explicit motion sample budget");
            Test(p.MaxGeometryChecks is >= 1 and <= 50_000_000, "assemblyProfile.maxGeometryChecks", "Finite geometry work must be in [1,50000000]");
            Test(Finite(p.MinimumSeparation) && p.MinimumSeparation >= 0 && p.MinimumSeparation <= numerics.MaxCoordinateMagnitude,
                "assemblyProfile.minimumSeparation", "Separation must be finite, nonnegative and inside the numerical bound");
            if (!Test(p.Contacts is not null && p.Contacts.Count <= 32768 && p.Contacts.All(x => x is not null)
                && p.FlexibleRigs is not null && p.FlexibleRigs.Count <= 32768 && p.FlexibleRigs.All(x => x is not null)
                && p.CouplingFits is not null && p.CouplingFits.Count <= 32768 && p.CouplingFits.All(x => x is not null),
                "assemblyProfile", "Explicit bounded nonnull profile collections are required")) return new(issues, checks);
            if (!Test(design.Parts is not null && design.Instances is not null && design.Routes is not null && design.Joints is not null && design.Mechanisms is not null
                && design.Parts.Count <= 4096 && design.Instances.Count <= 32768 && design.Routes.Count <= 32768 && design.Joints.Count <= 32768 && design.Mechanisms.Count <= 32768,
                "$", "Validated bounded design collections are required")) return new(issues, checks);
            var parts = design.Parts!.DistinctBy(x => x.Id).ToDictionary(x => x.Id, StringComparer.Ordinal);
            var instances = design.Instances!.DistinctBy(x => x.Id).ToDictionary(x => x.Id, StringComparer.Ordinal);
            var routes = design.Routes!.DistinctBy(x => x.Id).ToDictionary(x => x.Id, StringComparer.Ordinal);
            var joints = design.Joints!.DistinctBy(x => x.Id).ToDictionary(x => x.Id, StringComparer.Ordinal);
            var mechanisms = design.Mechanisms!.DistinctBy(x => x.Id).ToDictionary(x => x.Id, StringComparer.Ordinal);
            var tolerance = numerics.LinearTolerance; var bound = numerics.MaxCoordinateMagnitude;
            var parents = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var group in design.Subassemblies) { Step(); parents.Add(group.Id, group.ParentId); }
            foreach (var instance in design.Instances!) { Step(); parents.Add(instance.Id, instance.ParentId); }
            var depths = new Dictionary<string, int>(StringComparer.Ordinal); var active = new HashSet<string>(StringComparer.Ordinal);
            int Depth(string id)
            {
                Step(); if (depths.TryGetValue(id, out var known)) return known;
                if (!active.Add(id) || active.Count > 256 || !parents.TryGetValue(id, out var parent)) throw new ArgumentException("Invalid or excessive v2 hierarchy");
                var depth = parent is null ? 1 : Depth(parent) + 1; active.Remove(id); depths.Add(id, depth);
                if (depth > 256) throw new ArgumentException("v2 hierarchy exceeds the native maximum depth 256");
                return depth;
            }
            foreach (var id in parents.Keys) Depth(id);
            bool Point(Vec3? v) => v is not null && Finite(v.X) && Finite(v.Y) && Finite(v.Z) && Math.Max(Math.Abs(v.X), Math.Max(Math.Abs(v.Y), Math.Abs(v.Z))) <= bound;
            var world = DesignGeometry.CreateWorldPointResolver(design, Step);
            var contactIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var contact in p.Contacts!)
            {
                Step(); var path = "assemblyProfile.contacts." + contact.Id;
                Test(!string.IsNullOrWhiteSpace(contact.Id) && contact.Id.Length <= 80 && contactIds.Add(contact.Id), path, "Contact zone IDs must be explicit and unique");
                var owners = Test(instances.ContainsKey(contact.FirstInstanceId ?? "") && instances.ContainsKey(contact.SecondInstanceId ?? "") && contact.FirstInstanceId != contact.SecondInstanceId,
                    path, "A contact zone must name two distinct physical instances; its box is first-instance local");
                Test(!string.IsNullOrWhiteSpace(contact.Reason) && contact.Reason.Length <= 2048 && !contact.Reason.Any(char.IsControl), path, "An explicit bounded contact reason is required");
                if (Test(contact.Frame is not null && Point(contact.Frame.Origin) && contact.Frame.RotationDegrees is { } rotation && Finite(rotation.X) && Finite(rotation.Y) && Finite(rotation.Z)
                    && Math.Max(Math.Abs(rotation.X), Math.Max(Math.Abs(rotation.Y), Math.Abs(rotation.Z))) <= 360_000
                    && Point(contact.Size) && contact.Size.X > 0 && contact.Size.Y > 0 && contact.Size.Z > 0, path, "A finite positive bounded first-local box is required") && owners)
                    foreach (var corner in DesignGeometry.Corners(Vec3.Zero, contact.Size))
                    { Step(); var local = DesignGeometry.TransformPoint(contact.Frame!, corner); Test(Point(local) && Point(world(contact.FirstInstanceId!, local)), path, "Contact corners exceed declared local/world numerical bounds"); }
            }
            var policies = new Dictionary<string, FlexibleRigPolicy>(StringComparer.Ordinal); long ringCount = 0;
            foreach (var policy in p.FlexibleRigs!)
            {
                Step(); var path = "assemblyProfile.flexibleRigs." + policy.RouteId;
                if (!Test(policy.RouteId is not null && routes.TryGetValue(policy.RouteId, out _) && policies.TryAdd(policy.RouteId, policy), path, "Exactly one policy is required per actual route")) continue;
                var route = routes[policy.RouteId!];
                Test(Finite(policy.MinimumSegmentRatio) && policy.MinimumSegmentRatio > 0 && policy.MinimumSegmentRatio <= 1, path, "Minimum local segment ratio must be in (0,1]");
                if (!Test(instances.TryGetValue(route.InstanceId ?? "", out var instance) && parts.ContainsKey(instance.PartId), path, "Unknown flexible occurrence")) continue;
                var part = parts[instance!.PartId];
                if (!Test(part.Kind is PartKind.Wire or PartKind.Hose or PartKind.Spring or PartKind.Belt && part.Flexible is not null, path, "Only an owned wire, hose, spring or belt is supported")) continue;
                if (!Test(part.Features is { Count: 1 } && part.Features[0].Kind == FeatureKind.Sweep && part.Features[0].Operation == FeatureOperation.Add, path,
                    "Flexible skinning requires the complete original single additive sweep; no detail replacement or resampling is permitted")) continue;
                var feature = part.Features[0];
                if (!Test(route.Points is not null && route.Points.Count is >= 2 and <= 65536 && feature.Path is not null
                    && feature.Path.Count == route.Points.Count + (route.Closed ? 1 : 0), path, "Source sweep rings must correspond one-for-one with the route")) continue;
                ringCount += route.Points!.Count;
                Test(ringCount <= 65536 && ringCount <= budgets.MaxRoutePoints, path, "Total actual flexible source rings exceed the finite native/design bound");
                Test(part.Flexible!.SkinSegments == feature.Path!.Count - 1, path, "SkinSegments must equal the original source path segment count");
                Test(route.Closed == (feature.Path[0] == feature.Path[^1]), path, "Source sweep closure must match the route: only closed belts repeat the first source point, and their route omits the seam duplicate");
                for (var i = 0; i < route.Points.Count; i++)
                { Step(); Test(DesignGeometry.Length(world(route.InstanceId!, DesignGeometry.TransformPoint(feature.Frame, feature.Path[i])), route.Points[i]) <= tolerance, path, "Original framed source sweep differs from the approved world route"); }
                if (route.Closed)
                {
                    Test(part.Kind == PartKind.Belt && route.From is null && route.To is null && policy.MinimumEndpointDistance is null && policy.MaximumEndpointDistance is null,
                        path, "Closed belts have no endpoint-distance or attachment contract");
                    var driverValid = Test(policy.DriverJointId is not null && joints.TryGetValue(policy.DriverJointId, out var driver) && driver.Kind is JointKind.Rotary or JointKind.Hinge
                        && design.Mechanisms!.Any(m => m.Kind == MechanismKind.PulleyBelt && m.JointIds.Count >= 2 && m.JointIds[0] == policy.DriverJointId), path, "A closed belt must name its actual pulley/belt rotary driver");
                    Test(policy.TravelMetresPerDegree is { } travel && Finite(travel) && travel > 0 && travel <= bound, path, "Closed belt travel per degree must be explicit, finite and positive");
                    if (driverValid && policy.TravelMetresPerDegree is { } advance)
                    {
                        foreach (var fit in p.CouplingFits!.Where(f => mechanisms.TryGetValue(f.MechanismId ?? "", out var m) && m.Kind == MechanismKind.PulleyBelt && m.JointIds[0] == policy.DriverJointId))
                            Test(Math.Abs(advance - fit.DriverPitchRadius * Math.PI / 180) <= tolerance / 360, path, "Belt travel must agree with its declared driver pitch circumference");
                    }
                }
                else
                {
                    Test(part.Kind != PartKind.Belt && route.From is not null && route.To is not null && route.From.InstanceId != route.InstanceId && route.To.InstanceId != route.InstanceId,
                        path, "Open flexible endpoints must belong to other physical instances");
                    Test(route.From is not null && route.To is not null && instances.TryGetValue(route.From.InstanceId, out var fromOwner) && instances.TryGetValue(route.To.InstanceId, out var toOwner)
                        && parts[fromOwner.PartId].Kind == PartKind.Rigid && parts[toOwner.PartId].Kind == PartKind.Rigid,
                        path, "Open flexible endpoints require rigid physical owners; deforming attachment chains are unsupported");
                    Test(policy.DriverJointId is null && policy.TravelMetresPerDegree is null, path, "Open flexible routes use owned endpoint motion, without a belt driver");
                    Test(policy.MinimumEndpointDistance is { } min && Finite(min) && min >= 0 && policy.MaximumEndpointDistance is { } max && Finite(max) && max >= min && max <= bound
                        && DesignGeometry.Length(route.Points[0], route.Points[^1]) >= min - tolerance && DesignGeometry.Length(route.Points[0], route.Points[^1]) <= max + tolerance,
                        path, "Explicit finite endpoint-distance bounds must contain the actual rest separation");
                }
                try { FlexibleRingGeometry.RequireLimits(FlexibleRingGeometry.Measure(route.Points, route.Points, route.Closed, Step), part.Flexible, policy, tolerance); }
                catch (FlexibleMotionException error) { Test(false, path, error.Message); }
            }
            Test(policies.Count == routes.Count, "assemblyProfile.flexibleRigs", "Every route requires exactly one explicit flexible policy");
            Test(ringCount == 0 || ringCount <= maximum / 4, "assemblyProfile.flexibleRigs", "Finite validation work cannot contain the declared ring count");
            var fits = new HashSet<string>(StringComparer.Ordinal);
            foreach (var fit in p.CouplingFits!)
            {
                Step(); var path = "assemblyProfile.couplingFits." + fit.MechanismId;
                if (!Test(fit.MechanismId is not null && mechanisms.TryGetValue(fit.MechanismId, out _) && fits.Add(fit.MechanismId), path, "Coupling fits must uniquely name actual simple couplings")) continue;
                var m = mechanisms[fit.MechanismId!];
                Test(m.Kind is MechanismKind.SpurGears or MechanismKind.RackAndPinion or MechanismKind.PulleyBelt, path, "Linkages use their explicit length solver and cannot carry a pitch-fit entry");
                Test(Finite(fit.DriverPitchRadius) && fit.DriverPitchRadius >= numerics.MinimumFeatureSize && fit.DriverPitchRadius <= bound, path, "Driver pitch radius must be finite and positive within bounds");
                if (m.Kind == MechanismKind.RackAndPinion)
                    Test(fit.FollowerPitchRadius is null && fit.FollowerToothCount is null && (fit.DriverToothCount is null or >= 3 and <= 1_000_000), path, "A rack fit has one pitch radius and optional bounded pinion tooth count");
                else
                    Test(fit.FollowerPitchRadius is { } radius && Finite(radius) && radius >= numerics.MinimumFeatureSize && radius <= bound
                        && (fit.DriverToothCount is null && fit.FollowerToothCount is null || fit.DriverToothCount is >= 3 and <= 1_000_000 && fit.FollowerToothCount is >= 3 and <= 1_000_000),
                        path, "Rotary pairs require two positive radii and either both bounded tooth counts or neither");
            }
            foreach (var m in design.Mechanisms!)
                Test(m.Kind is MechanismKind.SliderCrank or MechanismKind.FourBar ? !fits.Contains(m.Id) : fits.Contains(m.Id), "assemblyProfile.couplingFits." + m.Id, "Exactly every simple coupling requires its explicit pitch fit");
            if (issues.Count == 0)
            {
                var poses = design.Instances!.ToDictionary(i => i.Id, i => new RigidPose(world(i.Id, Vec3.Zero), Subtract(world(i.Id, RigidPose.Identity.X), world(i.Id, Vec3.Zero)), Subtract(world(i.Id, RigidPose.Identity.Y), world(i.Id, Vec3.Zero)), Subtract(world(i.Id, RigidPose.Identity.Z), world(i.Id, Vec3.Zero))), StringComparer.Ordinal);
                try { RequireCouplingFits(design, poses, Step); }
                catch (FlexibleMotionException error) { Test(false, "assemblyProfile.couplingFits", error.Message); }
            }
        }
        catch (ProfileWorkException) { if (issues.Count < 512) issues.Add(new("budget", "assemblyProfile", "Profile validation exceeds MaxValidationChecks")); }
        catch (ArgumentException error) { if (issues.Count < 512) issues.Add(new("assembly-profile", "$", "Invalid profile references or hierarchy: " + error.Message)); }
        return new(issues.AsReadOnly(), checks);
    }

    public void RequireValid(AssemblyDesign design, CancellationToken ct = default)
    { var report = Validate(design, ct); if (!report.IsValid) throw new DesignValidationException(report); }

    /// <summary>Recheck pitch geometry in every actual rigid pose, including moving carriers.</summary>
    public static void RequireCouplingFits(AssemblyDesign design, IReadOnlyDictionary<string, RigidPose> poses, Action? step = null)
    {
        if (design.AssemblyProfile is null) throw new FlexibleMotionException("An explicit v2 assembly profile is required");
        if (design.AssemblyProfile.CouplingFits.Count == 0) return;
        var instances = design.Instances.ToDictionary(i => { step?.Invoke(); return i.Id; }, StringComparer.Ordinal); var parts = design.Parts.ToDictionary(p => { step?.Invoke(); return p.Id; }, StringComparer.Ordinal);
        var joints = design.Joints.ToDictionary(j => { step?.Invoke(); return j.Id; }, StringComparer.Ordinal); var mechanisms = design.Mechanisms.ToDictionary(m => { step?.Invoke(); return m.Id; }, StringComparer.Ordinal);
        var linear = design.Numerics.LinearTolerance; var angular = Math.Sin(design.Numerics.AngularToleranceDegrees * Math.PI / 180);
        void Need(bool condition, string id, string message) { step?.Invoke(); if (!condition) throw new FlexibleMotionException(id + ": " + message); }
        RigidPose Port(JointSpec joint) { step?.Invoke(); return Compose(poses[joint.ParentInstanceId], FromFrame(parts[instances[joint.ParentInstanceId].PartId].Interfaces.Single(p => p.Id == joint.ParentInterfaceId).Frame)); }
        foreach (var fit in design.AssemblyProfile.CouplingFits)
        {
            step?.Invoke(); var m = mechanisms[fit.MechanismId]; var a = joints[m.JointIds[0]]; var b = joints[m.JointIds[1]];
            var pa = Port(a); var pb = Port(b); var axisA = Normalize(Direction(pa, a.Axis)); var axisB = Normalize(Direction(pb, b.Axis)); var separation = Subtract(pb.Origin, pa.Origin);
            Need(Math.Abs(Dot(separation, axisA)) <= linear, m.Id, "Pitch centres must lie in the same plane");
            var distance = Length(separation); var radiusA = fit.DriverPitchRadius;
            if (m.Kind == MechanismKind.RackAndPinion)
            {
                Need(Math.Abs(m.Parameters["pitchRadius"] - radiusA) <= linear && Math.Abs(distance - radiusA) <= linear, m.Id, "Rack pitch offset must equal its declared pinion radius");
                Need(distance > 0 && Length(Subtract(axisB, Cross(axisA, Scale(separation, 1 / distance)))) <= angular, m.Id, "Positive rack travel must be tangent to the declared pinion pitch circle");
                continue;
            }
            Need(m.Kind is MechanismKind.SpurGears or MechanismKind.PulleyBelt, m.Id, "Only simple couplings have pitch fits");
            Need(Length(Cross(axisA, axisB)) <= angular, m.Id, "Rotary pitch axes must be parallel");
            var radiusB = fit.FollowerPitchRadius!.Value; var ratio = m.Parameters["ratio"]; var ratioTolerance = linear / Math.Max(radiusA, radiusB);
            Need(Math.Abs(Math.Abs(ratio) - radiusA / radiusB) <= ratioTolerance, m.Id, "Authored ratio differs from the declared pitch radii");
            if (fit.DriverToothCount is { } teethA && fit.FollowerToothCount is { } teethB)
                Need(Math.Abs((double)teethA / teethB - radiusA / radiusB) <= ratioTolerance, m.Id, "Declared tooth counts differ from the pitch ratio");
            if (m.Kind == MechanismKind.SpurGears)
                Need(ratio * Dot(axisA, axisB) < 0 && Math.Abs(distance - radiusA - radiusB) <= linear, m.Id, "External spur gears require opposing world rotation and touching pitch circles");
            else
                Need(ratio * Dot(axisA, axisB) > 0 && distance > radiusA + radiusB + linear, m.Id, "Pulley branch and axis signs must agree, with nonoverlapping pitch circles");
        }
    }

    sealed class ProfileWorkException : Exception;
}
