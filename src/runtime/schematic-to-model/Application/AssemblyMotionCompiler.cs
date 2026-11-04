using System.Collections.ObjectModel;
using Bubo.Schematic.Domain;
using static Bubo.Schematic.Domain.RigidTransforms;

namespace Bubo.Schematic.Application;

/// <summary>Pure, bounded rigid articulation. Coupled loops are constraints, never scene parents.</summary>
public sealed class AssemblyMotionCompiler(IAssemblyDesignValidator? validator = null)
{
    /// <summary>Hard host-memory bound on instance pose records across diagnostic rest and all clip samples.</summary>
    public const int MaxPoseRecords = 32_768;

    readonly IAssemblyDesignValidator validator = validator ?? new AssemblyDesignValidator();

    public AssemblyPose Evaluate(AssemblyDesign design, IReadOnlyDictionary<string, double> jointValues, CancellationToken ct = default)
    {
        var context = Create(design, ct);
        return context.Evaluate(jointValues);
    }

    public DiagnosticPlan Compile(AssemblyDesign design, int samplesPerRange = 9, int samplesPerCycle = 33, CancellationToken ct = default)
    {
        var context = Create(design, ct);
        if (samplesPerRange is < 3 or > 1_000_000 || samplesPerCycle is < 5 or > 1_000_000)
            throw new AssemblyMotionException("Range sampling requires at least three samples and cycle sampling at least five, within native bounds");
        var minimumSamples = checked(1L + context.IndependentDrivers.Sum(driver => (long)AngularSamples(driver, driver.Maximum - driver.Minimum, samplesPerRange)));
        if (minimumSamples > design.Budgets.MaxMotionSamples)
            throw new AssemblyMotionException("Independent driver range samples exceed MaxMotionSamples before allocation");
        RequirePoseRecords(minimumSamples);
        var clips = new List<DiagnosticClip>(); var coverage = new List<MotionDriverCoverage>(); var total = 1;
        long materializedSamples = 0;
        var rest = Materialize(new Dictionary<string, double>());
        foreach (var driver in context.IndependentDrivers)
        {
            context.Step();
            var rangeCount = AngularSamples(driver, driver.Maximum - driver.Minimum, samplesPerRange);
            var values = Enumerable.Range(0, rangeCount).Select(i => { context.Step(); return driver.Minimum + (driver.Maximum - driver.Minimum) * i / (rangeCount - 1); }).Append(driver.Rest).Distinct().Order().ToArray();
            AddClip("range-" + driver.Id, driver, values, false);
            var applicable = driver.Kind is JointKind.Rotary or JointKind.Hinge;
            var cycle = applicable ? context.FindCycle(driver) : null;
            if (cycle is { } interval)
            {
                var cycleCount = AngularSamples(driver, interval.Period, samplesPerCycle);
                if ((long)total + cycleCount > design.Budgets.MaxMotionSamples) throw new AssemblyMotionException("Complete cycle samples exceed MaxMotionSamples before allocation");
                var samples = Enumerable.Range(0, cycleCount).Select(i => { context.Step(); return interval.Start + interval.Period * i / (cycleCount - 1); }).ToArray();
                AddClip("cycle-" + driver.Id, driver, samples, true);
            }
            coverage.Add(new(driver.Id, applicable, cycle is not null, !applicable ? "nonperiodic-joint" : cycle is not null ? "complete-mechanism-cycle" : "declared-travel-does-not-contain-a-complete-return-cycle"));
        }
        return new("bubo-schematic-diagnostic-plan/v1", rest, clips.AsReadOnly(), new(samplesPerRange, samplesPerCycle, total, coverage.AsReadOnly()), context.Checks);

        void RequirePoseRecords(long samples)
        {
            if (checked(samples * (long)design.Instances.Count) > MaxPoseRecords)
                throw new AssemblyMotionException($"Diagnostic motion exceeds hard host-memory MaxPoseRecords ({MaxPoseRecords}) across rest and all samples; rejected before pose allocation");
        }

        AssemblyPose Materialize(IReadOnlyDictionary<string, double> values)
        {
            var next = checked(materializedSamples + 1);
            RequirePoseRecords(next);
            materializedSamples = next;
            return context.Evaluate(values);
        }

        void AddClip(string id, JointSpec driver, double[] values, bool cycle)
        {
            if ((long)total + values.Length > design.Budgets.MaxMotionSamples) throw new AssemblyMotionException("Diagnostic motion exceeds MaxMotionSamples, including the rest sample");
            RequirePoseRecords(checked(materializedSamples + values.Length));
            var samples = new List<DiagnosticSample>(values.Length);
            AssemblyPose Pose(double value) => Materialize(new Dictionary<string, double>(StringComparer.Ordinal) { [driver.Id] = value });
            void Emit(double value, AssemblyPose pose)
            {
                context.Step();
                RequirePoseRecords(checked((long)total + 1));
                if (++total > design.Budgets.MaxMotionSamples) throw new AssemblyMotionException("Angular refinement exceeds MaxMotionSamples");
                samples.Add(new((value - values[0]) / (values[^1] - values[0]), pose));
            }
            void Refine(double aValue, AssemblyPose a, double bValue, AssemblyPose b, int depth)
            {
                context.Step();
                var coarse = context.NeedsAngularRefinement(a, b);
                if (!coarse) { Emit(bValue, b); return; }
                if (depth >= 32 || total + 2L > design.Budgets.MaxMotionSamples) throw new AssemblyMotionException("Angular refinement cannot fit the finite motion sample budget");
                var middleValue = aValue + (bValue - aValue) / 2; var middle = Pose(middleValue);
                Refine(aValue, a, middleValue, middle, depth + 1); Refine(middleValue, middle, bValue, b, depth + 1);
            }
            var previous = Pose(values[0]); Emit(values[0], previous);
            for (var i = 1; i < values.Length; i++)
            {
                var current = Pose(values[i]); Refine(values[i - 1], previous, values[i], current, 0); previous = current;
            }
            clips.Add(new(id, driver.Id, design.Mechanisms.FirstOrDefault(m => m.JointIds[0] == driver.Id)?.Id, cycle, samples.AsReadOnly()));
        }
    }

    static int AngularSamples(JointSpec driver, double span, int requested)
        => driver.Kind == JointKind.Slider ? requested : Math.Max(requested, checked((int)Math.Ceiling(span / 45)) + 1);

    Context Create(AssemblyDesign design, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var report = validator.Validate(design, ct);
        if (!report.IsValid) throw new DesignValidationException(report);
        return new(design, report.Checks, ct);
    }

    sealed class Context
    {
        readonly AssemblyDesign design;
        readonly CancellationToken ct;
        readonly Dictionary<string, JointSpec> joints;
        readonly Dictionary<string, (RigidPose Frame, string? Parent)> nodes = new(StringComparer.Ordinal);
        readonly Dictionary<string, RigidPose> rest = new(StringComparer.Ordinal);
        readonly Dictionary<string, RigidPose> ports = new(StringComparer.Ordinal);
        readonly Dictionary<string, JointSpec> owners;
        readonly HashSet<string> followers;
        readonly List<MechanismSpec> mechanisms;
        readonly Dictionary<string, Linkage> linkages = new(StringComparer.Ordinal);
        public long Checks { get; private set; }
        public IReadOnlyList<JointSpec> IndependentDrivers { get; }

        public Context(AssemblyDesign design, long checks, CancellationToken ct)
        {
            this.design = design; this.ct = ct; Checks = checks;
            joints = design.Joints.ToDictionary(j => j.Id, StringComparer.Ordinal);
            followers = design.Mechanisms.SelectMany(m => m.JointIds.Skip(1)).ToHashSet(StringComparer.Ordinal);
            IndependentDrivers = design.Joints.Where(j => j.Kind != JointKind.Fixed && !followers.Contains(j.Id)).ToArray();
            var closing = design.Mechanisms.Where(m => m.Kind is MechanismKind.SliderCrank or MechanismKind.FourBar).Select(m => m.JointIds[3]).ToHashSet(StringComparer.Ordinal);
            owners = design.Joints.Where(j => !closing.Contains(j.Id)).ToDictionary(j => j.ChildInstanceId, StringComparer.Ordinal);
            foreach (var group in design.Subassemblies) { Step(); nodes.Add(group.Id, (FromFrame(group.Frame), group.ParentId)); }
            foreach (var instance in design.Instances) { Step(); nodes.Add(instance.Id, (FromFrame(instance.Frame), instance.ParentId)); }
            var active = new HashSet<string>(StringComparer.Ordinal);
            RigidPose Rest(string id)
            {
                Step();
                if (rest.TryGetValue(id, out var pose)) return pose;
                if (!active.Add(id) || active.Count > 256) throw new AssemblyMotionException("Invalid rest hierarchy");
                var node = nodes[id]; pose = Compose(node.Parent is null ? RigidPose.Identity : Rest(node.Parent), node.Frame);
                active.Remove(id); rest.Add(id, pose); return pose;
            }
            foreach (var id in nodes.Keys) Rest(id);
            var parts = design.Parts.ToDictionary(p => p.Id, StringComparer.Ordinal);
            var instances = design.Instances.ToDictionary(i => i.Id, StringComparer.Ordinal);
            foreach (var joint in design.Joints)
            {
                Step();
                ports.Add(joint.Id, FromFrame(parts[instances[joint.ParentInstanceId].PartId].Interfaces.Single(p => p.Id == joint.ParentInterfaceId).Frame));
            }
            ChildPorts = design.Joints.ToDictionary(j => j.Id, j => FromFrame(parts[instances[j.ChildInstanceId].PartId].Interfaces.Single(p => p.Id == j.ChildInterfaceId).Frame), StringComparer.Ordinal);
            // Reject mixed scene/joint cycles before any sample is emitted.
            var done = new HashSet<string>(StringComparer.Ordinal);
            void Visit(string id)
            {
                Step(); if (done.Contains(id)) return;
                if (!active.Add(id) || active.Count > 256) throw new AssemblyMotionException("Owning joints and scene hierarchy contain a cycle or exceed depth 256");
                var parent = owners.TryGetValue(id, out var owner) ? owner.ParentInstanceId : nodes[id].Parent;
                if (parent is not null) Visit(parent);
                active.Remove(id); done.Add(id);
            }
            foreach (var id in nodes.Keys) Visit(id);
            mechanisms = [];
            var followerOwners = new Dictionary<string, MechanismSpec>(StringComparer.Ordinal);
            var dependents = design.Mechanisms.ToDictionary(m => m.Id, _ => new List<MechanismSpec>(), StringComparer.Ordinal);
            foreach (var mechanism in design.Mechanisms)
            {
                Step();
                foreach (var id in mechanism.JointIds.Skip(1)) { Step(); followerOwners.Add(id, mechanism); }
            }
            var ready = new Queue<MechanismSpec>();
            foreach (var mechanism in design.Mechanisms)
            {
                Step();
                if (followerOwners.TryGetValue(mechanism.JointIds[0], out var dependency)) dependents[dependency.Id].Add(mechanism);
                else ready.Enqueue(mechanism);
            }
            while (ready.TryDequeue(out var mechanism))
            {
                Step(); mechanisms.Add(mechanism);
                if (mechanism.Kind is MechanismKind.SliderCrank or MechanismKind.FourBar) linkages.Add(mechanism.Id, PrepareLinkage(mechanism));
                else ValidateCouplingAxes(mechanism);
                foreach (var dependent in dependents[mechanism.Id])
                {
                    Step(); ready.Enqueue(dependent);
                }
            }
            if (mechanisms.Count != design.Mechanisms.Count) throw new AssemblyMotionException("Coupling dependencies have no independent driver");
        }
        readonly Dictionary<string, RigidPose> ChildPorts;

        public void Step()
        {
            ct.ThrowIfCancellationRequested();
            if (++Checks > design.Budgets.MaxValidationChecks) throw new AssemblyMotionException("Motion compilation exceeds MaxValidationChecks");
        }
        double LinearTolerance => design.Numerics.LinearTolerance;
        double AngleTolerance => design.Numerics.AngularToleranceDegrees;
        void Require(bool condition, string message) { Step(); if (!condition) throw new AssemblyMotionException(message); }
        Vec3 Pivot(JointSpec j) => Point(rest[j.ParentInstanceId], ports[j.Id].Origin);
        Vec3 Axis(JointSpec j, IReadOnlyDictionary<string, RigidPose>? poses = null) => Normalize(Direction(Compose((poses ?? rest)[j.ParentInstanceId], ports[j.Id]), j.Axis));

        void ValidateCouplingAxes(MechanismSpec m, IReadOnlyDictionary<string, RigidPose>? poses = null)
        {
            var a = Axis(joints[m.JointIds[0]], poses); var b = Axis(joints[m.JointIds[1]], poses);
            var error = m.Kind == MechanismKind.RackAndPinion ? Math.Abs(Dot(a, b)) : Length(Cross(a, b));
            Require(error <= Math.Sin(AngleTolerance * Math.PI / 180), m.Id + ": coupling axes are incompatible with its planar family");
        }

        Linkage PrepareLinkage(MechanismSpec m)
        {
            var js = m.JointIds.Select(id => joints[id]).ToArray();
            var a = Pivot(js[0]); var b = Pivot(js[1]); var c = Pivot(js[2]); var d = Pivot(js[3]); var n = Normalize(Axis(js[0]));
            var angular = Math.Sin(AngleTolerance * Math.PI / 180);
            for (var i = 1; i < 3; i++) Require(Length(Cross(n, Axis(js[i]))) <= angular, m.Id + ": linkage rotary axes must be parallel");
            if (m.Kind == MechanismKind.FourBar) Require(Length(Cross(n, Axis(js[3]))) <= angular, m.Id + ": closing axis must be parallel");
            foreach (var p in new[] { b, c, d }) Require(Math.Abs(Dot(Subtract(p, a), n)) <= LinearTolerance, m.Id + ": linkage pivots must be coplanar");
            void LengthIs(Vec3 p, Vec3 q, string name) => Require(Math.Abs(Length(Subtract(q, p)) - m.Parameters[name]) <= LinearTolerance, m.Id + ": actual interface distance differs from " + name);
            LengthIs(a, b, m.Kind == MechanismKind.FourBar ? "inputLength" : "crankLength");
            LengthIs(b, c, m.Kind == MechanismKind.FourBar ? "couplerLength" : "rodLength");
            var u = m.Kind == MechanismKind.FourBar ? Normalize(Subtract(d, a)) : Normalize(Axis(js[3]));
            if (m.Kind == MechanismKind.FourBar) { LengthIs(a, d, "groundLength"); LengthIs(d, c, "outputLength"); }
            else
            {
                Require(Math.Abs(Dot(n, u)) <= angular, m.Id + ": slider direction must lie in the linkage plane");
                var v = Cross(n, u);
                Require(Math.Abs(Dot(Subtract(d, a), v) - m.Parameters["offset"]) <= LinearTolerance, m.Id + ": actual slider-line offset differs from offset");
                Require(Length(Cross(Subtract(c, d), u)) <= LinearTolerance, m.Id + ": rest slider pin is not on its declared line");
            }
            var result = new Linkage(m, js, a, b, c, d, n, u);
            var solved = SolvePoint(result, 0);
            Require(Length(Subtract(solved, c)) <= LinearTolerance, m.Id + ": declared branch contradicts the actual rest interfaces");
            return result;
        }

        public AssemblyPose Evaluate(IReadOnlyDictionary<string, double> requested)
        {
            ArgumentNullException.ThrowIfNull(requested);
            var values = joints.ToDictionary(j => j.Key, j => j.Value.Rest, StringComparer.Ordinal);
            foreach (var (id, value) in requested)
            {
                Step(); Require(joints.ContainsKey(id), "Unknown joint value: " + id);
                Require(double.IsFinite(value), "Joint values must be finite: " + id);
                values[id] = value;
            }
            foreach (var m in mechanisms)
            {
                Step(); var driver = joints[m.JointIds[0]]; var delta = values[driver.Id] - driver.Rest;
                if (m.Kind is MechanismKind.FourBar or MechanismKind.SliderCrank) SolveLinkage(linkages[m.Id], delta, values);
                else
                {
                    var follower = joints[m.JointIds[1]];
                    var change = m.Kind == MechanismKind.RackAndPinion ? delta * Math.PI / 180 * m.Parameters["pitchRadius"] : delta * m.Parameters["ratio"] * (m.Kind == MechanismKind.PulleyBelt && m.Branch == "crossed" ? -1 : 1);
                    values[follower.Id] = follower.Rest + change;
                }
            }
            foreach (var (id, value) in requested)
                if (followers.Contains(id)) Require(Math.Abs(values[id] - value) <= (joints[id].Kind == JointKind.Slider ? LinearTolerance : AngleTolerance), "Requested follower conflicts with its coupling: " + id);
            foreach (var (id, value) in values)
            {
                Step(); RequireTravel(joints[id], value);
            }
            var poses = new Dictionary<string, RigidPose>(StringComparer.Ordinal);
            RigidPose Resolve(string id)
            {
                Step(); if (poses.TryGetValue(id, out var existing)) return existing;
                RigidPose pose;
                if (owners.TryGetValue(id, out var joint)) pose = Expected(joint, Resolve(joint.ParentInstanceId), values[joint.Id]);
                else { var node = nodes[id]; pose = Compose(node.Parent is null ? RigidPose.Identity : Resolve(node.Parent), node.Frame); }
                Require(Finite(pose) && Math.Max(Math.Abs(pose.Origin.X), Math.Max(Math.Abs(pose.Origin.Y), Math.Abs(pose.Origin.Z))) <= design.Numerics.MaxCoordinateMagnitude, "Motion exceeds finite coordinate bounds: " + id);
                poses.Add(id, pose); return pose;
            }
            foreach (var id in nodes.Keys) Resolve(id);
            foreach (var mechanism in mechanisms)
            {
                Step();
                if (mechanism.Kind is not (MechanismKind.FourBar or MechanismKind.SliderCrank)) ValidateCouplingAxes(mechanism, poses);
            }
            var residuals = new List<JointClosureMetric>();
            foreach (var joint in design.Joints)
            {
                Step(); var expected = Compose(Expected(joint, poses[joint.ParentInstanceId], values[joint.Id]), ChildPorts[joint.Id]);
                var actual = Compose(poses[joint.ChildInstanceId], ChildPorts[joint.Id]);
                var linear = Length(Subtract(expected.Origin, actual.Origin)); var angular = AngularErrorDegrees(expected, actual);
                Require(linear <= LinearTolerance && angular <= AngleTolerance, "Joint constraint does not close: " + joint.Id);
                residuals.Add(new(joint.Id, linear, angular));
            }
            return new(new ReadOnlyDictionary<string, double>(values), new ReadOnlyDictionary<string, RigidPose>(design.Instances.ToDictionary(i => i.Id, i => poses[i.Id], StringComparer.Ordinal)), residuals.AsReadOnly());
        }

        public bool NeedsAngularRefinement(AssemblyPose a, AssemblyPose b)
        {
            // World-basis keys can alias a complete turn even when each local joint
            // changes little. Sum absolute angular travel through the effective
            // owning-joint/scene-parent ancestry as a conservative path bound.
            var cache = new Dictionary<string, double>(StringComparer.Ordinal);
            double Travel(string id)
            {
                Step(); if (cache.TryGetValue(id, out var travel)) return travel;
                var parent = nodes[id].Parent; travel = 0;
                if (owners.TryGetValue(id, out var joint))
                {
                    parent = joint.ParentInstanceId;
                    if (joint.Kind is JointKind.Hinge or JointKind.Rotary or JointKind.Screw) travel = Math.Abs(a.JointValues[joint.Id] - b.JointValues[joint.Id]);
                }
                if (parent is not null) travel += Travel(parent);
                cache.Add(id, travel); return travel;
            }
            foreach (var instance in design.Instances) if (Travel(instance.Id) > 45 + 1e-9) return true;
            return false;
        }

        RigidPose Expected(JointSpec j, RigidPose parent, double value)
        {
            Step(); var delta = value - j.Rest;
            var angle = j.Kind is JointKind.Hinge or JointKind.Rotary or JointKind.Screw ? delta : 0;
            var distance = j.Kind == JointKind.Slider ? delta : j.Kind == JointKind.Screw ? delta * j.Pitch / 360 : 0;
            var anchor = ports[j.Id];
            var restOffset = Compose(Inverse(Compose(rest[j.ParentInstanceId], anchor)), rest[j.ChildInstanceId]);
            return Compose(Compose(Compose(parent, anchor), AxisMotion(j.Axis, angle, distance)), restOffset);
        }

        void RequireTravel(JointSpec joint, double value)
        {
            // The tiny arithmetic guard is independent of the authored geometric
            // tolerance; it never grants a clamp-sized extension of joint travel.
            Require(double.IsFinite(value) && value >= joint.Minimum - 1e-10 && value <= joint.Maximum + 1e-10, "Joint travel exceeded: " + joint.Id);
        }

        Vec3 SolvePoint(Linkage l, double angle)
        {
            Step();
            var b = Add(l.A, Direction(AxisMotion(l.N, angle), Subtract(l.B, l.A)));
            var p = l.Spec.Parameters;
            if (l.Spec.Kind == MechanismKind.SliderCrank)
            {
                var v = Cross(l.N, l.U); var relative = Subtract(b, l.A); var y = p["offset"] - Dot(relative, v);
                var radicand = p["rodLength"] * p["rodLength"] - y * y;
                Require(radicand > LinearTolerance * LinearTolerance, l.Spec.Id + ": slider-crank reaches a singular or impossible closure");
                var x = Dot(relative, l.U) + (l.Spec.Branch == "open" ? 1 : -1) * Math.Sqrt(radicand);
                return Add(Add(l.A, Scale(l.U, x)), Scale(v, p["offset"]));
            }
            var bd = Subtract(l.D, b); var distance = Length(bd); var r = p["couplerLength"]; var s = p["outputLength"];
            Require(distance > LinearTolerance, l.Spec.Id + ": coincident circle centres are an unsupported linkage singularity");
            var xAlong = (r * r - s * s + distance * distance) / (2 * distance); var heightSquared = r * r - xAlong * xAlong;
            Require(heightSquared > LinearTolerance * LinearTolerance, l.Spec.Id + ": four-bar reaches a singular or impossible closure");
            var along = Scale(bd, 1 / distance); var across = Cross(l.N, along);
            return Add(Add(b, Scale(along, xAlong)), Scale(across, (l.Spec.Branch == "open" ? 1 : -1) * Math.Sqrt(heightSquared)));
        }

        void SolveLinkage(Linkage l, double delta, Dictionary<string, double> values)
        {
            if (l.Spec.Kind == MechanismKind.FourBar) RequireFourBarPath(l, delta);
            else RequireSliderCrankPath(l, delta);
            // Follow the declared branch from rest; unwrap rotations rather than jumping at atan2's cut.
            var steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(delta) / 5));
            var rodAngle = 0d; var outputAngle = 0d;
            for (var i = 1; i <= steps; i++)
            {
                Step(); var angle = delta * i / steps; var b = Add(l.A, Direction(AxisMotion(l.N, angle), Subtract(l.B, l.A))); var c = SolvePoint(l, angle);
                rodAngle = Unwrap(SignedAngle(Subtract(l.C, l.B), Subtract(c, b), l.N), rodAngle);
                outputAngle = l.Spec.Kind == MechanismKind.SliderCrank ? 0 : Unwrap(SignedAngle(Subtract(l.C, l.D), Subtract(c, l.D), l.N), outputAngle);
                values[l.Joints[1].Id] = l.Joints[1].Rest + (rodAngle - angle) / Dot(Axis(l.Joints[1]), l.N);
                values[l.Joints[2].Id] = l.Joints[2].Rest + (outputAngle - rodAngle) / Dot(Axis(l.Joints[2]), l.N);
                values[l.Joints[3].Id] = l.Joints[3].Rest + (l.Spec.Kind == MechanismKind.SliderCrank ? Dot(Subtract(c, l.C), l.U) : outputAngle / Dot(Axis(l.Joints[3]), l.N));
                for (var follower = 1; follower < 4; follower++) RequireTravel(l.Joints[follower], values[l.Joints[follower].Id]);
            }
        }

        void RequireFourBarPath(Linkage l, double delta)
        {
            // Circle closure depends on the input-tip/ground-pivot distance. Bound
            // its exact extrema over the complete requested path, including tiny
            // forbidden intervals which any fixed sample grid could step across.
            var ground = Subtract(l.D, l.A); var input = Subtract(l.B, l.A);
            var g = Length(ground); var a = Length(input);
            var start = Math.Atan2(Dot(l.N, Cross(ground, input)), Dot(ground, input));
            var end = start + delta * Math.PI / 180;
            var maximumCosine = ContainsPhase(start, end, 0) ? 1 : Math.Max(Math.Cos(start), Math.Cos(end));
            var minimumCosine = ContainsPhase(start, end, Math.PI) ? -1 : Math.Min(Math.Cos(start), Math.Cos(end));
            var minimum = Math.Sqrt(Math.Max(0, g * g + a * a - 2 * g * a * maximumCosine));
            var maximum = Math.Sqrt(Math.Max(0, g * g + a * a - 2 * g * a * minimumCosine));
            var r = l.Spec.Parameters["couplerLength"]; var s = l.Spec.Parameters["outputLength"];
            Require(minimum > Math.Abs(r - s) + LinearTolerance && maximum < r + s - LinearTolerance,
                l.Spec.Id + ": requested four-bar path crosses a singular or impossible closure interval");
        }

        void RequireSliderCrankPath(Linkage l, double delta)
        {
            var input = Subtract(l.B, l.A); var r = Length(input); var rod = l.Spec.Parameters["rodLength"]; var offset = l.Spec.Parameters["offset"];
            Require(rod > r + Math.Abs(offset), l.Spec.Id + ": actual slider-crank dimensions do not admit a nonsingular cycle");
            var start = Math.Atan2(Dot(input, Cross(l.N, l.U)), Dot(input, l.U)); var end = start + delta * Math.PI / 180;
            // Rod orientation extrema occur with the crank normal to the slide.
            // Slider extrema occur when rod and crank are collinear. The input-to-
            // rod relative angle is strictly monotonic because rod > crank+|offset|,
            // so its endpoint checks in continuation bound that follower's travel.
            var phases = l.Spec.Branch == "open"
                ? new[] { Math.PI / 2, 3 * Math.PI / 2, Math.Asin(offset / (r + rod)), Math.PI + Math.Asin(offset / (rod - r)) }
                : new[] { Math.PI / 2, 3 * Math.PI / 2, Math.PI - Math.Asin(offset / (r + rod)), -Math.Asin(offset / (rod - r)) };
            foreach (var phase in phases)
            {
                Step(); if (!ContainsPhase(start, end, phase)) continue;
                var angle = (phase - start) * 180 / Math.PI;
                var b = Add(l.A, Direction(AxisMotion(l.N, angle), input)); var c = SolvePoint(l, angle);
                var rodAngle = SignedAngle(Subtract(l.C, l.B), Subtract(c, b), l.N);
                RequireTravel(l.Joints[2], l.Joints[2].Rest - rodAngle / Dot(Axis(l.Joints[2]), l.N));
                RequireTravel(l.Joints[3], l.Joints[3].Rest + Dot(Subtract(c, l.C), l.U));
            }
        }

        bool ContainsPhase(double start, double end, double phase)
        {
            Step(); const double turn = 2 * Math.PI;
            var low = Math.Min(start, end); var high = Math.Max(start, end);
            return Math.Ceiling((low - phase) / turn) <= Math.Floor((high - phase) / turn);
        }

        public (double Start, double Period)? FindCycle(JointSpec driver)
        {
            // Search the entire admitted driver travel, bounded by both its native angular
            // limits and the caller's work budget; do not invent a tooth count or truncate
            // a long, but explicitly authored, rational mechanism period.
            for (var revolutions = 1; 360d * revolutions <= driver.Maximum - driver.Minimum; revolutions++)
            {
                Step(); var period = revolutions * 360d; var start = Math.Clamp(driver.Rest, driver.Minimum, driver.Maximum - period);
                var a = Evaluate(new Dictionary<string, double> { [driver.Id] = start }); var b = Evaluate(new Dictionary<string, double> { [driver.Id] = start + period });
                var returns = true;
                foreach (var p in a.Instances)
                {
                    Step();
                    if (Length(Subtract(p.Value.Origin, b.Instances[p.Key].Origin)) > LinearTolerance || AngularErrorDegrees(p.Value, b.Instances[p.Key]) > AngleTolerance) { returns = false; break; }
                }
                if (returns) return (start, period);
            }
            return null;
        }

        static double SignedAngle(Vec3 a, Vec3 b, Vec3 n) => Math.Atan2(Dot(n, Cross(a, b)), Dot(a, b)) * 180 / Math.PI;
        static double Unwrap(double angle, double previous) => angle + 360 * Math.Round((previous - angle) / 360);
        static bool Finite(RigidPose p) => new[] { p.Origin, p.X, p.Y, p.Z }.All(v => double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z));
        sealed record Linkage(MechanismSpec Spec, JointSpec[] Joints, Vec3 A, Vec3 B, Vec3 C, Vec3 D, Vec3 N, Vec3 U);
    }
}
