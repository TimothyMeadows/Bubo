using Bubo.Schematic.Application;
using Bubo.Schematic.Domain;
using static Bubo.Schematic.Domain.RigidTransforms;
using static Bubo.Schematic.Tests.DesignExamples;

namespace Bubo.Schematic.Tests;

public static class FlexibleTests
{
    public static AssemblyProfile Profile(List<FlexibleRigPolicy>? rigs = null, List<CouplingFit>? fits = null) => new()
    {
        SchemaVersion = "bubo-assembly-profile/v1", SamplesPerRange = 3, SamplesPerCycle = 129, MaxGeometryChecks = 1_000_000,
        MinimumSeparation = 0, Contacts = [], FlexibleRigs = rigs ?? [], CouplingFits = fits ?? []
    };

    public static AssemblyDesign Open(PartKind kind = PartKind.Hose)
    {
        var path = kind == PartKind.Spring
            ? Enumerable.Range(0, 25).Select(i => V(.06 + .03 * Math.Cos(i * Math.PI / 6), .06 + .03 * Math.Sin(i * Math.PI / 6), .02 + i / 24d)).ToList()
            : Enumerable.Range(0, 5).Select(i => V(.02 + i / 4d, .02, .02)).ToList();
        var arc = Enumerable.Range(1, path.Count - 1).Sum(i => DesignGeometry.Length(path[i - 1], path[i]));
        var endpoint = Part("endpoint-definition", V(.1, .1, .1), Box(.1, .1, .1));
        if (kind == PartKind.Wire) endpoint = endpoint with { Terminals = [new() { Id = "terminal", Name = "Power terminal", Kind = TerminalKind.Power, Frame = F() }] };
        var flex = Part("flex-definition", kind == PartKind.Spring ? V(.12, .12, 1.04) : V(1.04, .05, .05), new ShapeFeature
        { Id = "source-sweep", Kind = FeatureKind.Sweep, Operation = FeatureOperation.Add, Frame = F(), Parameters = new() { ["radius"] = .002, ["segments"] = 8 }, Profile = [], Path = path, Sections = [] }) with
        {
            Kind = kind, Interfaces = [], Flexible = new() { RestLength = arc, MinimumLength = arc * .75, MaximumLength = arc * 1.2, MinimumBendRadius = .001, MaximumStretchRatio = 1.2, SkinSegments = path.Count - 1 }
        };
        var from = new Endpoint { InstanceId = "start", PortId = kind == PartKind.Wire ? "terminal" : "mount" }; var to = new Endpoint { InstanceId = "end", PortId = from.PortId };
        var separation = DesignGeometry.Length(path[0], path[^1]);
        return Base("flexible-" + kind.ToString().ToLowerInvariant(), [endpoint, flex], [Instance("start", endpoint.Id) with { Frame = F() with { Origin = path[0] } }, Instance("end", endpoint.Id) with { Frame = F() with { Origin = path[^1] } }, Instance("flex", flex.Id)]) with
        {
            SchemaVersion = "bubo-assembly-design/v2", Routes = [new() { Id = "route", InstanceId = "flex", Points = path, Closed = false, From = from, To = to }],
            Connections = [new() { Id = "connection", Kind = kind == PartKind.Wire ? ConnectionKind.Power : ConnectionKind.Mechanical, From = from, To = to, RouteId = "route" }],
            AssemblyProfile = Profile([new() { RouteId = "route", MinimumSegmentRatio = .75, MinimumEndpointDistance = separation * .75, MaximumEndpointDistance = separation * 1.2, DriverJointId = null, TravelMetresPerDegree = null }])
        };
    }

    public static AssemblyDesign Belt()
    {
        var d = MechanismExamples.Coupled(MechanismKind.PulleyBelt, ratio: 1);
        var path = Enumerable.Range(0, 16).Select(i => V(.6 + .4 * Math.Cos(i * Math.PI / 8), .6 + .4 * Math.Sin(i * Math.PI / 8), .02)).ToList();
        var arc = Enumerable.Range(0, path.Count).Sum(i => DesignGeometry.Length(path[i], path[(i + 1) % path.Count]));
        var part = Part("belt-definition", V(1.02, 1.02, .04), new ShapeFeature
        { Id = "closed-sweep", Kind = FeatureKind.Sweep, Operation = FeatureOperation.Add, Frame = F(), Parameters = new() { ["radius"] = .002, ["segments"] = 8 }, Profile = [], Path = [.. path, path[0]], Sections = [] }) with
        { Kind = PartKind.Belt, Interfaces = [], Flexible = new() { RestLength = arc, MinimumLength = arc * .97, MaximumLength = arc * 1.01, MinimumBendRadius = .1, MaximumStretchRatio = 1.01, SkinSegments = path.Count } };
        return d with
        {
            SchemaVersion = "bubo-assembly-design/v2", Parts = [.. d.Parts, part], Instances = [.. d.Instances, Instance("belt", part.Id)],
            Joints = d.Joints.Select(j => j with { Minimum = 0, Maximum = 360 }).ToList(), Routes = [new() { Id = "belt-route", InstanceId = "belt", Points = path, Closed = true, From = null, To = null }],
            AssemblyProfile = Profile([new() { RouteId = "belt-route", MinimumSegmentRatio = .97, MinimumEndpointDistance = null, MaximumEndpointDistance = null, DriverJointId = "first-joint", TravelMetresPerDegree = .2 * Math.PI / 180 }],
                [new() { MechanismId = "coupling", DriverPitchRadius = .2, FollowerPitchRadius = .2, DriverToothCount = null, FollowerToothCount = null }]) with { SamplesPerRange = 129 }
        };
    }

    static DiagnosticPlan OpenPlan(AssemblyDesign design, double endScale)
    {
        var poses = design.Instances.ToDictionary(i => i.Id, i => FromFrame(i.Frame), StringComparer.Ordinal);
        var rest = new AssemblyPose(new Dictionary<string, double> { ["end-slide"] = 0 }, poses, []); var path = design.Routes[0].Points;
        var samples = new List<DiagnosticSample>();
        for (var i = 0; i < 3; i++)
        {
            var fraction = i / 2d; var end = Add(path[0], Scale(Subtract(path[^1], path[0]), 1 + (endScale - 1) * fraction));
            var current = poses.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal); current["end"] = current["end"] with { Origin = end };
            samples.Add(new(fraction, new(new Dictionary<string, double> { ["end-slide"] = (endScale - 1) * fraction * DesignGeometry.Length(path[0], path[^1]) }, current, [])));
        }
        // Native compiler consumes a rigid plan; this synthetic test owns an explicit slider identity.
        return new("bubo-schematic-diagnostic-plan/v1", rest, [new("range-end-slide", "end-slide", null, false, samples)], new(3, 129, 4, []), 0);
    }

    static AssemblyDesign MovingOpen(PartKind kind)
    {
        var d = Open(kind);
        var support = Part("slide-base-definition", V(1.2, .2, 1.2), Box(1.2, .2, 1.2)) with { Interfaces = [Port("mount", d.Routes[0].Points[^1])] };
        return d with { Parts = [.. d.Parts, support], Instances = [.. d.Instances, Instance("slide-base", support.Id)],
            Joints = [Joint("end-slide", JointKind.Slider, "slide-base", "end") with { Axis = kind == PartKind.Spring ? V(0, 0, 1) : V(1), Minimum = -.75, Maximum = .5 }] };
    }

    public static void Run()
    {
        var profiles = new AssemblyProfileValidator(); var compiler = new FlexibleMotionCompiler();
        void Valid(AssemblyDesign d) { var report = profiles.Validate(d); Check.True(report.IsValid, string.Join("; ", report.Issues.Select(i => i.Path + ": " + i.Message))); }
        void Invalid(AssemblyDesign d) => Check.True(!profiles.Validate(d).IsValid);
        void Near(double a, double b, double tolerance = 1e-8) => Check.True(Math.Abs(a - b) <= tolerance, $"Expected {b}, got {a}");
        foreach (var kind in new[] { PartKind.Wire, PartKind.Hose, PartKind.Spring })
            Check.Case("owned " + kind + " source rings preserve rest, exact endpoints and explicit compression/stretch", () =>
            {
                var d = MovingOpen(kind); Valid(d);
                foreach (var scale in new[] { .9, 1.1 })
                {
                    var plan = OpenPlan(d, scale); var program = compiler.Compile(d, plan).Single(); var path = d.Routes[0].Points;
                    Check.True(program.RestRingFrames.Count == path.Count && program.Clips.Single().Samples.Count == 3 && program.RadialSegments == 8);
                    foreach (var sample in program.Clips.Single().Samples)
                    {
                        Near(DesignGeometry.Length(sample.RingFrames[0].Origin, path[0]), 0);
                        Near(DesignGeometry.Length(sample.RingFrames[^1].Origin, plan.Clips[0].Samples[sample.SampleIndex].Pose.Instances["end"].Origin), 0);
                        foreach (var pose in sample.RingFrames) { Near(Length(pose.X), 1); Near(Length(pose.Y), 1); Near(Length(pose.Z), 1); Near(Dot(Cross(pose.X, pose.Y), pose.Z), 1); }
                    }
                }
            });
        Check.Case("closed belt retains original seam rings and phase advances along the actual closed route", () =>
        {
            var d = Belt(); Valid(d); var plan = new AssemblyMotionCompiler().Compile(d, 129, 129); var program = compiler.Compile(d, plan).Single();
            Check.True(program.ClosedPath && program.RestRingFrames.Count == 16 && program.RestRingFrames.Count == d.Parts[^1].Flexible!.SkinSegments);
            var clip = program.Clips[0]; Check.True(DesignGeometry.Length(clip.Samples[0].RingFrames[0].Origin, clip.Samples[^1].RingFrames[0].Origin) > .1);
            Near(DesignGeometry.Length(program.RestRingFrames[0].Origin, d.Routes[0].Points[0]), 0);
        });
        Check.Case("v1 never admits an assembly profile or flexible readiness automatically", () =>
        {
            var d = Open(); Invalid(d with { SchemaVersion = "bubo-assembly-design/v1" });
            var old = d with { SchemaVersion = "bubo-assembly-design/v1", AssemblyProfile = null }; Check.True(profiles.Validate(old).IsValid);
            Check.Throws<FlexibleMotionException>(() => compiler.Compile(old, new("bubo-schematic-diagnostic-plan/v1", new(new Dictionary<string, double>(), new Dictionary<string, RigidPose>(), []), [], new(3, 129, 1, []), 0)));
        });
        Check.Case("framed original source rings remain exact without replacing their cross sections", () =>
        {
            var d = MovingOpen(PartKind.Hose); var part = d.Parts[1]; var feature = part.Features[0]; var frame = F(.04, 0, 0) with { RotationDegrees = V(0, 0, 90) };
            var inverse = Inverse(FromFrame(frame));
            d = d with { Parts = [d.Parts[0], part with { Features = [feature with { Frame = frame, Path = feature.Path.Select(point => Point(inverse, point)).ToList() }] }, d.Parts[2]] };
            Valid(d); var program = compiler.Compile(d, OpenPlan(d, 1.1)).Single();
            for (var i = 0; i < program.RestRingFrames.Count; i++) Near(DesignGeometry.Length(program.RestRingFrames[i].Origin, d.Routes[0].Points[i]), 0);
        });
        Check.Case("actual endpoint-owned hinge rotation bends the route while both attachment positions remain exact", () =>
        {
            var d = MovingOpen(PartKind.Hose); d = d with { Joints = [d.Joints[0] with { Kind = JointKind.Hinge, Axis = V(0, 0, 1), Minimum = 0, Maximum = 20 }] };
            var plan = new AssemblyMotionCompiler().Compile(d, 3, 129); var program = compiler.Compile(d, plan).Single(); var sample = program.Clips[0].Samples[^1];
            Near(DesignGeometry.Length(sample.RingFrames[0].Origin, d.Routes[0].Points[0]), 0); Near(DesignGeometry.Length(sample.RingFrames[^1].Origin, d.Routes[0].Points[^1]), 0);
            Check.True(DesignGeometry.Length(sample.RingFrames[2].Origin, program.RestRingFrames[2].Origin) > .01);
        });
        Check.Case("repeated flexible definitions retain independently owned endpoints and motion", () =>
        {
            var d = MovingOpen(PartKind.Hose); var offset = V(0, .5); var route = d.Routes[0]; var copiedRoute = route with { Id = "route-copy", InstanceId = "flex-copy", Points = route.Points.Select(p => Add(p, offset)).ToList(), From = route.From! with { InstanceId = "start-copy" }, To = route.To! with { InstanceId = "end-copy" } };
            d = d with
            {
                Instances = [.. d.Instances, d.Instances[0] with { Id = "start-copy", Frame = F() with { Origin = Add(d.Instances[0].Frame.Origin, offset) } }, d.Instances[1] with { Id = "end-copy", Frame = F() with { Origin = Add(d.Instances[1].Frame.Origin, offset) } }, d.Instances[2] with { Id = "flex-copy", Frame = F() with { Origin = offset } }],
                Routes = [route, copiedRoute], Connections = [.. d.Connections, d.Connections[0] with { Id = "connection-copy", RouteId = copiedRoute.Id, From = copiedRoute.From!, To = copiedRoute.To! }],
                AssemblyProfile = d.AssemblyProfile! with { FlexibleRigs = [d.AssemblyProfile.FlexibleRigs[0], d.AssemblyProfile.FlexibleRigs[0] with { RouteId = copiedRoute.Id }] }
            };
            Valid(d); var programs = compiler.Compile(d, OpenPlan(d, 1.1)); Check.True(programs.Count == 2);
            var copy = programs.Single(p => p.InstanceId == "flex-copy"); foreach (var sample in copy.Clips[0].Samples)
                for (var i = 0; i < copy.RestRingFrames.Count; i++) Near(DesignGeometry.Length(sample.RingFrames[i].Origin, copy.RestRingFrames[i].Origin), 0);
            Check.True(DesignGeometry.Length(programs[0].Clips[0].Samples[^1].RingFrames[^1].Origin, programs[0].RestRingFrames[^1].Origin) > .09);
        });
        Check.Case("profile requires exact policy coverage, original sweep ownership and source segment count", () =>
        {
            var d = Open(); Invalid(d with { AssemblyProfile = null }); Invalid(d with { AssemblyProfile = d.AssemblyProfile! with { FlexibleRigs = [] } });
            Invalid(d with { AssemblyProfile = d.AssemblyProfile! with { FlexibleRigs = [d.AssemblyProfile.FlexibleRigs[0], d.AssemblyProfile.FlexibleRigs[0]] } });
            Invalid(d with { Parts = [d.Parts[0], d.Parts[1] with { Flexible = d.Parts[1].Flexible! with { SkinSegments = 16 } }] });
            Invalid(d with { Parts = [d.Parts[0], d.Parts[1] with { Features = [.. d.Parts[1].Features, Box(.01, .01, .01, "extra")] }] });
            Invalid(d with { Routes = [d.Routes[0] with { Points = d.Routes[0].Points.Select((p, i) => i == 2 ? Add(p, V(0, .01)) : p).ToList() }] });
        });
        Check.Case("profile rejects nonfinite/implicit endpoint and travel limits, local fold and geometry excess", () =>
        {
            var d = Open(); var p = d.AssemblyProfile!; var rig = p.FlexibleRigs[0];
            foreach (var bad in new[] { rig with { MinimumEndpointDistance = null }, rig with { MaximumEndpointDistance = double.NaN }, rig with { MinimumSegmentRatio = 0 }, rig with { MinimumSegmentRatio = 1.1 }, rig with { DriverJointId = "invented" } })
                Invalid(d with { AssemblyProfile = p with { FlexibleRigs = [bad] } });
            Invalid(d with { AssemblyProfile = p with { MaxGeometryChecks = 50_000_001 } }); Invalid(d with { AssemblyProfile = p with { MinimumSeparation = double.PositiveInfinity } });
            Check.Throws<FlexibleMotionException>(() => FlexibleRingGeometry.Measure([V(), V(1), V()], [V(), V(1), V()], false));
        });
        Check.Case("an open route cannot disguise a geometrically closed source sweep with a duplicated seam", () =>
        {
            var d = Open(); var path = new List<Vec3> { V(.02, .02, .02), V(.04, .02, .02), V(.04, .04, .02), V(.02, .04, .02), V(.02, .02, .02) }; var part = d.Parts[1];
            d = d with { Parts = [d.Parts[0], part with { Features = [part.Features[0] with { Path = path }], Flexible = part.Flexible! with { RestLength = .08, MinimumLength = .06, MaximumLength = .1 } }],
                Routes = [d.Routes[0] with { Points = path }], AssemblyProfile = d.AssemblyProfile! with { FlexibleRigs = [d.AssemblyProfile.FlexibleRigs[0] with { MinimumEndpointDistance = 0, MaximumEndpointDistance = .1 }] } };
            Check.True(profiles.Validate(d).Issues.Any(issue => issue.Message.Contains("closure", StringComparison.Ordinal)));
        });
        Check.Case("measured flexible limits reject excessive stretch, compression, bend and attachment separation", () =>
        {
            var d = MovingOpen(PartKind.Hose); Check.Throws<FlexibleMotionException>(() => compiler.Compile(d, OpenPlan(d, 1.3))); Check.Throws<FlexibleMotionException>(() => compiler.Compile(d, OpenPlan(d, .5)));
            var spring = MovingOpen(PartKind.Spring); var part = spring.Parts[1];
            Invalid(spring with { Parts = [spring.Parts[0], part with { Flexible = part.Flexible! with { MinimumBendRadius = 10 } }] });
            var tight = d with { AssemblyProfile = d.AssemblyProfile! with { FlexibleRigs = [d.AssemblyProfile.FlexibleRigs[0] with { MaximumEndpointDistance = 1.01 }] } };
            Check.Throws<FlexibleMotionException>(() => compiler.Compile(tight, OpenPlan(tight, 1.1)));
        });
        Check.Case("closed belt rejects missing driver, fabricated circumference and inadequate motion keys", () =>
        {
            var d = Belt(); var p = d.AssemblyProfile!; var rig = p.FlexibleRigs[0];
            Invalid(d with { AssemblyProfile = p with { FlexibleRigs = [rig with { DriverJointId = "absent" }] } });
            Invalid(d with { AssemblyProfile = p with { FlexibleRigs = [rig with { TravelMetresPerDegree = rig.TravelMetresPerDegree * 2 }] } });
            var coarse = d with { AssemblyProfile = p with { SamplesPerRange = 3, SamplesPerCycle = 9 } };
            Check.Throws<FlexibleMotionException>(() => compiler.Compile(coarse, new AssemblyMotionCompiler().Compile(coarse, 3, 9)));
        });
        Check.Case("physical spur fits enforce pitch ratio, tooth ratio, axis sign and external centre distance", () =>
        {
            var d = MechanismExamples.Coupled(MechanismKind.SpurGears, ratio: -2) with { SchemaVersion = "bubo-assembly-design/v2", AssemblyProfile = Profile(fits: [new() { MechanismId = "coupling", DriverPitchRadius = 2d / 3, FollowerPitchRadius = 1d / 3, DriverToothCount = 20, FollowerToothCount = 10 }]) };
            Valid(d); var fit = d.AssemblyProfile!.CouplingFits[0];
            foreach (var bad in new[] { fit with { DriverPitchRadius = .5 }, fit with { DriverToothCount = 21 }, fit with { FollowerToothCount = null } }) Invalid(d with { AssemblyProfile = d.AssemblyProfile with { CouplingFits = [bad] } });
            Invalid(d with { Mechanisms = [d.Mechanisms[0] with { Parameters = new() { ["ratio"] = 2 } }] });
            var opposite = d with { Joints = [d.Joints[0], d.Joints[1] with { Axis = V(0, 0, -1) }], Mechanisms = [d.Mechanisms[0] with { Parameters = new() { ["ratio"] = 2 } }] }; Valid(opposite);
        });
        Check.Case("rack pitch fit requires signed tangent and actual pitch-circle offset", () =>
        {
            var d = MechanismExamples.Coupled(MechanismKind.RackAndPinion) with { SchemaVersion = "bubo-assembly-design/v2", AssemblyProfile = Profile(fits: [new() { MechanismId = "coupling", DriverPitchRadius = .1, FollowerPitchRadius = null, DriverToothCount = 16, FollowerToothCount = null }]) };
            d = d with { Parts = [d.Parts[0] with { Interfaces = [d.Parts[0].Interfaces[0], d.Parts[0].Interfaces[1] with { Frame = F(.1) }] }, d.Parts[1]], Instances = d.Instances.Select(i => i.Id == "second" ? i with { Frame = F(.1) } : i).ToList(), Joints = [d.Joints[0], d.Joints[1] with { Axis = V(0, 1) }] };
            Valid(d); Invalid(d with { Joints = [d.Joints[0], d.Joints[1] with { Axis = V(0, -1) }] });
            Invalid(d with { AssemblyProfile = d.AssemblyProfile! with { CouplingFits = [] } });
        });
        Check.Case("pulley fits preserve explicit branch and world-axis signs while rejecting overlapping pitch circles", () =>
        {
            var d = MechanismExamples.Coupled(MechanismKind.PulleyBelt, ratio: 1) with { SchemaVersion = "bubo-assembly-design/v2", AssemblyProfile = Profile(fits: [new() { MechanismId = "coupling", DriverPitchRadius = .2, FollowerPitchRadius = .2, DriverToothCount = 16, FollowerToothCount = 16 }]) };
            Valid(d); Valid(d with { Mechanisms = [d.Mechanisms[0] with { Branch = "crossed" }] });
            Invalid(d with { Mechanisms = [d.Mechanisms[0] with { Parameters = new() { ["ratio"] = -1 } }] });
            Invalid(d with { AssemblyProfile = d.AssemblyProfile! with { CouplingFits = [d.AssemblyProfile.CouplingFits[0] with { DriverPitchRadius = .6, FollowerPitchRadius = .6 }] } });
            Invalid(d with { Joints = [d.Joints[0], d.Joints[1] with { Axis = V(0, 1) }] });
        });
        Check.Case("first-local contact boxes require distinct owners, bounded finite dimensions and explanation", () =>
        {
            var d = Open(); var contact = new ContactZone { Id = "contact", FirstInstanceId = "start", SecondInstanceId = "flex", Frame = F(), Size = V(.01, .01, .01), Reason = "Explicit endpoint clamp contact" };
            Valid(d with { AssemblyProfile = d.AssemblyProfile! with { Contacts = [contact] } });
            foreach (var bad in new[] { contact with { SecondInstanceId = "start" }, contact with { Reason = "" }, contact with { Size = V(double.NaN, 1, 1) }, contact with { Size = V(0, 1, 1) } })
                Invalid(d with { AssemblyProfile = d.AssemblyProfile! with { Contacts = [bad] } });
        });
        Check.Case("flexible finite work, sample totals and cancellation reject before unbounded allocation", () =>
        {
            var d = MovingOpen(PartKind.Hose); var plan = OpenPlan(d, 1.1);
            Check.Throws<FlexibleMotionException>(() => compiler.Compile(d, plan with { Sampling = plan.Sampling with { TotalSamples = 99999 } }));
            Check.True(!profiles.Validate(d with { Budgets = d.Budgets with { MaxValidationChecks = 2 } }).IsValid);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            Check.Throws<OperationCanceledException>(() => compiler.Compile(d, plan, cancellation.Token)); Check.Throws<OperationCanceledException>(() => profiles.Validate(d, cancellation.Token));
        });
        Check.Case("flexible frame-record hard bound rejects before output frame allocation", () =>
        {
            var d = MovingOpen(PartKind.Hose); var plan = OpenPlan(d, 1); var samples = Enumerable.Repeat(plan.Clips[0].Samples[0], 9000).ToArray();
            var excessive = plan with { Clips = [plan.Clips[0] with { Samples = samples }], Sampling = plan.Sampling with { TotalSamples = 9001 } };
            Check.True(Check.Throws<FlexibleMotionException>(() => compiler.Compile(d, excessive)).Message.Contains("16384", StringComparison.Ordinal));
        });
        Check.Case("linear centreline continuation rejects a narrow local compression missed by quarter probes", () =>
        {
            var before = new[] { V(), V(1, .0001) }; var after = new[] { V(), V(-1.5, .0001) };
            var limits = new FlexibleLimits { RestLength = 1, MinimumLength = .01, MaximumLength = 2, MinimumBendRadius = .001, MaximumStretchRatio = 2, SkinSegments = 1 };
            var policy = new FlexibleRigPolicy { RouteId = "synthetic", MinimumSegmentRatio = .2, MinimumEndpointDistance = 0, MaximumEndpointDistance = 2, DriverJointId = null, TravelMetresPerDegree = null };
            foreach (var weight in new[] { .25, .5, .75 })
                FlexibleRingGeometry.RequireLimits(FlexibleRingGeometry.Measure([V(), Add(Scale(before[1], 1 - weight), Scale(after[1], weight))], before, false), limits, policy, .000001);
            Check.Throws<FlexibleMotionException>(() => FlexibleRingGeometry.RequireLinearInterval(before, after, before, false, limits, policy, .000001));
        });
        Check.Case("v2 hierarchy depth is measured through cached parents and capped at 256", () =>
        {
            var d = MechanismExamples.Pair() with { SchemaVersion = "bubo-assembly-design/v2", AssemblyProfile = Profile() };
            var groups = new List<Subassembly>(d.Subassemblies);
            for (var i = 0; i < 255; i++) groups.Add(new() { Id = "nested-" + i, Name = "Synthetic nested group", ParentId = i == 0 ? "assembly" : "nested-" + (i - 1), Frame = F() });
            Invalid(d with { Subassemblies = groups, Instances = d.Instances.Select(instance => instance with { ParentId = "nested-254" }).ToList() });
        });
    }
}
