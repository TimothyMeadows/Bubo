using Bubo.Schematic.Domain;
using static Bubo.Schematic.Tests.DesignExamples;

namespace Bubo.Schematic.Tests;

/// <summary>Small owned mechanical test benches, with real independent geometry and explicit simplifications.</summary>
public static class ReadinessExamples
{
    public static void Run()
    {
        foreach (var (name, design) in All())
            Check.Case("native readiness bench: " + name + " complete authored design and native plans remain valid", () =>
            {
                var validation = new AssemblyDesignValidator().Validate(design);
                Check.True(validation.IsValid, string.Join("; ", validation.Issues.Select(i => i.Path + ": " + i.Message)));
                var profile = design.AssemblyProfile!;
                var plan = new Bubo.Schematic.Application.AssemblyMotionCompiler().Compile(design, profile.SamplesPerRange, profile.SamplesPerCycle);
                var rigs = new Bubo.Schematic.Application.FlexibleMotionCompiler().Compile(design, plan);
                Check.True(plan.Clips.Count > 0 && plan.Sampling.TotalSamples > 1 && rigs.Count == design.Routes.Count);
                Check.True(plan.Clips.SelectMany(c => c.Samples).All(s => s.Pose.Instances.Count == design.Instances.Count));
            });
    }

    public static IReadOnlyDictionary<string, AssemblyDesign> All() => new Dictionary<string, AssemblyDesign>(StringComparer.Ordinal)
    {
        ["slider"] = Sliding(PartKind.Hose, includeFlex: false),
        ["spur-gears"] = SimpleCoupling(MechanismKind.SpurGears),
        ["rack-pinion"] = SimpleCoupling(MechanismKind.RackAndPinion),
        ["pulley-belt"] = Pulley(),
        ["slider-crank"] = Linkage(false),
        ["four-bar"] = Linkage(true),
        ["hose"] = Sliding(PartKind.Hose),
        ["spring"] = Sliding(PartKind.Spring)
    };

    static AssemblyProfile Profile() => new()
    {
        SchemaVersion = "bubo-assembly-profile/v1", SamplesPerRange = 5, SamplesPerCycle = 17,
        MaxGeometryChecks = 50_000_000, MinimumSeparation = 0, Contacts = [], FlexibleRigs = [], CouplingFits = []
    };
    static AssemblyDesign V2(AssemblyDesign source, AssemblyProfile? profile = null) => source with
    {
        SchemaVersion = "bubo-assembly-design/v2", AssemblyProfile = profile ?? Profile(),
        Budgets = source.Budgets with { MaxValidationChecks = 10_000_000, MaxExportBytes = 536_870_912, MaxGeneratedTriangles = 20_000, MaxMotionSamples = 2048 },
        Observations = [.. source.Observations, new() { Id = "bench-scope", Kind = ObservationKind.Simplified,
            Description = "Owned idealized mechanical bench: separately constructed supports, bored bearings, shafts, bars and coarse teeth where declared. Clearances, joint references and material deformation are measured; gravity, friction, involute manufacturing and finished animation are outside this fixture. No human production acceptance.", Source = "Authored integration fixture" }]
    };
    static ShapeFeature Cylinder(string id, double radius, double z, double height, double x, double y, FeatureOperation operation = FeatureOperation.Add, int segments = 12) => new()
    {
        Id = id, Kind = FeatureKind.Cylinder, Operation = operation, Frame = F(x, y, z),
        Parameters = new() { ["radius"] = radius, ["length"] = height, ["segments"] = segments }, Profile = [], Path = [], Sections = []
    };
    static ContactZone Touch(string id, string first, string second, Vec3 point, double radius = .004) => new()
    {
        Id = id, FirstInstanceId = first, SecondInstanceId = second,
        Frame = F(point.X - radius, point.Y - radius, point.Z - radius), Size = V(2 * radius, 2 * radius, 2 * radius),
        Reason = "Only the authored flexible end-cap/connector mating region; no body or complete-route exemption"
    };

    static AssemblyDesign Sliding(PartKind kind, bool includeFlex = true)
    {
        var spring = kind == PartKind.Spring;
        var path = spring
            ? Enumerable.Range(0, 17).Select(i => V(.12 + .035 * Math.Cos(i * Math.PI / 4), .12 + .035 * Math.Sin(i * Math.PI / 4), .1 + i / 16d)).ToList()
            : Enumerable.Range(0, 5).Select(i => V(.1 + i / 4d, .1, .1)).ToList();
        var startPort = spring ? V(.04, .04, .06) : V(.06, .03, .03);
        var endPort = spring ? V(.04, .04) : V(0, .03, .03);
        var connectorSize = spring ? V(.08, .08, .06) : V(.06, .06, .06);
        var first = Part("fixed-connector-definition", connectorSize, Box(connectorSize.X, connectorSize.Y, connectorSize.Z)) with { Interfaces = [Port("route", startPort)] };
        var last = Part("sliding-connector-definition", connectorSize, Box(connectorSize.X, connectorSize.Y, connectorSize.Z)) with { Interfaces = [Port("route", endPort)] };
        var guide = spring
            ? Part("guide-definition", V(.3, .3, 1.35), Box(.3, .3, .025),
                Box(.02, .14, 1.325, "left-upright") with { Frame = F(.02, .06, .025) },
                Box(.02, .14, 1.325, "right-upright") with { Frame = F(.25, .06, .025) })
            : Part("guide-definition", V(1.4, .2, .16), Box(1.4, .2, .025),
                Box(1.4, .02, .125, "left-rail") with { Frame = F(y: .04, z: .025) },
                Box(1.4, .02, .125, "right-rail") with { Frame = F(y: .14, z: .025) });
        guide = guide with { Interfaces = [Port("start", path[0]), Port("end", path[^1])] };
        var parts = new List<PartDefinition> { guide, first, last };
        var instances = new List<PartInstance> { Instance("guide", guide.Id), Instance("fixed-connector", first.Id) with { Frame = F() with { Origin = Bubo.Schematic.Domain.RigidTransforms.Subtract(path[0], startPort) } },
            Instance("sliding-connector", last.Id) with { Frame = F() with { Origin = Bubo.Schematic.Domain.RigidTransforms.Subtract(path[^1], endPort) } } };
        var profile = Profile(); var routes = new List<RouteSpec>(); var connections = new List<ConnectionSpec>();
        if (includeFlex)
        {
            var length = path.Zip(path.Skip(1), DesignGeometry.Length).Sum();
            var flexible = Part("flexible-definition", spring ? V(.2, .2, 1.2) : V(1.2, .2, .2), new ShapeFeature
            {
                Id = "original-source-sweep", Kind = FeatureKind.Sweep, Operation = FeatureOperation.Add, Frame = F(),
                Parameters = new() { ["radius"] = .002, ["segments"] = 8 }, Profile = [], Path = path, Sections = []
            }) with { Kind = kind, Interfaces = [], Flexible = new() { RestLength = length, MinimumLength = length * .8, MaximumLength = length * 1.2,
                MinimumBendRadius = .005, MaximumStretchRatio = 1.2, SkinSegments = path.Count - 1 } };
            parts.Add(flexible); instances.Add(Instance("flexible", flexible.Id));
            var from = new Endpoint { InstanceId = "fixed-connector", PortId = "route" }; var to = new Endpoint { InstanceId = "sliding-connector", PortId = "route" };
            routes.Add(new() { Id = "flexible-route", InstanceId = "flexible", Points = path, Closed = false, From = from, To = to });
            connections.Add(new() { Id = "mechanical-route", Kind = ConnectionKind.Mechanical, From = from, To = to, RouteId = "flexible-route" });
            profile = profile with { FlexibleRigs = [new() { RouteId = "flexible-route", MinimumSegmentRatio = .8, MinimumEndpointDistance = .8,
                MaximumEndpointDistance = 1.2, DriverJointId = null, TravelMetresPerDegree = null }],
                Contacts = [Touch("fixed-cap", "fixed-connector", "flexible", startPort), Touch("moving-cap", "sliding-connector", "flexible", endPort)] };
        }
        return V2(Base(includeFlex ? kind.ToString().ToLowerInvariant() : "slider", parts, instances) with
        {
            Joints = [Joint("fixed-mount", JointKind.Fixed, "guide", "fixed-connector", "start", "route") with { Clearance = 0 },
                Joint("end-slide", JointKind.Slider, "guide", "sliding-connector", "end", "route") with { Axis = spring ? V(0, 0, 1) : V(1), Minimum = -.08, Maximum = .08, Clearance = 0 }],
            Routes = routes, Connections = connections, Sections = [new() { Id = "guide-section", Axis = SectionAxis.Y, Offset = .1 }]
        }, profile);
    }

    static PartDefinition Gear(string id, double phase = 0)
    {
        var features = new List<ShapeFeature> { Cylinder("root-disc", .25, .2, .04, .35, .35) };
        for (var i = 0; i < 12; i++)
        {
            var angle = phase + i * 30; var a = angle * Math.PI / 180;
            features.Add(Box(.08, .045, .04, "tooth-" + i) with { Frame = F(.35 + .24 * Math.Cos(a) + .0225 * Math.Sin(a), .35 + .24 * Math.Sin(a) - .0225 * Math.Cos(a), .2) with { RotationDegrees = V(0, 0, angle) } });
        }
        features.Add(Cylinder("bearing-bore", .06, .19, .06, .35, .35, FeatureOperation.Subtract));
        return Part(id, V(.7, .7, .24), features.ToArray()) with { Interfaces = [Port("bearing", V(.35, .35, .12))],
            Detail = "Twelve separately authored coarse rectangular teeth fused only to their owned root disc, with a real bored bearing. Explicit non-involute engagement fixture, not manufacturing certification." };
    }
    static List<ContactZone> ToothContacts(string first, string second)
    {
        var result = new List<ContactZone>();
        for (var i = 0; i < 12; i++)
        {
            var angle = i * Math.PI / 6;
            result.Add(new() { Id = "tooth-engagement-" + i, FirstInstanceId = first, SecondInstanceId = second,
                Frame = F(.35 + .239 * Math.Cos(angle) + .0235 * Math.Sin(angle), .35 + .239 * Math.Sin(angle) - .0235 * Math.Cos(angle), .199) with { RotationDegrees = V(0, 0, i * 30) },
                Size = V(.082, .047, .042), Reason = "Only this individually authored coarse tooth engagement volume. Root discs, shaft bores, supports and unrelated bodies remain subject to interference checks." });
        }
        return result;
    }
    static AssemblyDesign SimpleCoupling(MechanismKind kind)
    {
        var rack = kind == MechanismKind.RackAndPinion;
        var first = Gear("driver-definition"); var second = Gear("follower-definition", 15);
        if (rack)
        {
            var features = new List<ShapeFeature> { Box(2.4, .1, .04, "rack-back") with { Frame = F(y: .06, z: .2) } };
            for (var i = 0; i < 16; i++) features.Add(Box(.045, .07, .04, "rack-tooth-" + i) with { Frame = F(.03 + i * Math.PI / 20, z: .2) });
            second = Part("rack-definition", V(2.5, .16, .24), features.ToArray()) with { Interfaces = [Port("pitch-reference", V(1.2, .02, .12))], Detail = "Independently modeled linear rack with sixteen coarse teeth and a continuous backing bar; authored pitch ratio is checked, involute contact simulation is excluded." };
        }
        var a = V(1.5, .5, .12); var b = rack ? V(1.5, .8, .12) : V(2.1, .5, .12);
        var groundFeatures = new List<ShapeFeature> { Box(3, 1.2, .04), Cylinder("driver-post", .04, .04, .2, a.X, a.Y) };
        if (!rack) groundFeatures.Add(Cylinder("follower-post", .04, .04, .2, b.X, b.Y));
        var support = Part("support-definition", V(3, 1.2, .24), groundFeatures.ToArray()) with { Interfaces = [Port("driver", a), Port("follower", b)] };
        var instances = new List<PartInstance> { Instance("support", support.Id), Instance("driver", first.Id, a.X - .35, a.Y - .35),
            rack ? Instance("follower", second.Id, b.X - 1.2, b.Y - .02) : Instance("follower", second.Id, b.X - .35, b.Y - .35) };
        var mechanism = new MechanismSpec { Id = "coupling", Kind = kind, JointIds = ["driver-joint", "follower-joint"], Branch = "open",
            Parameters = rack ? new() { ["pitchRadius"] = .3 } : new() { ["ratio"] = -1 } };
        var contacts = rack ? new List<ContactZone> { new() { Id = "rack-tooth-tip-engagement", FirstInstanceId = "follower", SecondInstanceId = "driver",
            Frame = F(y: -.001, z: .199), Size = V(2.5, .056, .042),
            Reason = "Only the protruding rack tooth-tip band. Multiple coarse teeth may engage together, so this narrow row strip includes their intervening empty gaps for conservative overlap enclosure. The continuous backing begins at local Y=0.06 and remains outside the band; root discs, bores, shafts and supports have no exemption." } }
            : ToothContacts("driver", "follower");
        var profile = Profile() with { Contacts = contacts, CouplingFits = [new() { MechanismId = mechanism.Id, DriverPitchRadius = .3,
            FollowerPitchRadius = rack ? null : .3, DriverToothCount = 12, FollowerToothCount = rack ? null : 12 }] };
        return V2(Base(rack ? "rack-pinion" : "spur-gears", [support, first, second], instances) with
        {
            Joints = [Joint("driver-joint", JointKind.Rotary, "support", "driver", "driver", "bearing") with { Minimum = rack ? -90 : 0, Maximum = rack ? 90 : 360, Clearance = 0 },
                Joint("follower-joint", rack ? JointKind.Slider : JointKind.Rotary, "support", "follower", "follower", rack ? "pitch-reference" : "bearing") with { Axis = rack ? V(-1) : V(0, 0, 1), Minimum = rack ? -.5 : -360, Maximum = rack ? .5 : 0, Clearance = 0 }],
            Mechanisms = [mechanism], Sections = [new() { Id = "tooth-and-bearing-section", Axis = SectionAxis.Z, Offset = .22 }]
        }, profile);
    }

    static AssemblyDesign Pulley()
    {
        var a = V(.4, .4, .12); var b = V(1, .4, .12);
        var support = Part("pulley-support-definition", V(1.4, .8, .24), Box(1.4, .8, .04), Cylinder("driver-post", .04, .04, .2, a.X, a.Y), Cylinder("follower-post", .04, .04, .2, b.X, b.Y)) with { Interfaces = [Port("driver", a), Port("follower", b)] };
        var pulley = Part("pulley-definition", V(.4, .4, .24), Cylinder("pulley-rim", .185, .2, .04, .2, .2, segments: 16), Cylinder("bore", .06, .19, .06, .2, .2, FeatureOperation.Subtract)) with
        { Interfaces = [Port("bearing", V(.2, .2, .12))], Detail = "Faceted pulley with bored bearing and an explicit 15 mm radius allowance below its 200 mm kinematic pitch circle. Belt has a 3 mm tube radius; remaining sampled clearance accommodates the approved polygonal source path and phase interpolation. This measures articulation and material travel, not friction/tension/contact certification." };
        var points = new List<Vec3>();
        // Two authored semicircles joined by straight tangent runs, in world/source-local coordinates.
        for (var i = 0; i <= 8; i++) { var theta = (90 + i * 22.5) * Math.PI / 180; points.Add(V(a.X + .2 * Math.Cos(theta), a.Y + .2 * Math.Sin(theta), .22)); }
        for (var i = 1; i < 8; i++) points.Add(V(a.X + (b.X - a.X) * i / 8d, a.Y - .2, .22));
        for (var i = 0; i <= 8; i++) { var theta = (-90 + i * 22.5) * Math.PI / 180; points.Add(V(b.X + .2 * Math.Cos(theta), b.Y + .2 * Math.Sin(theta), .22)); }
        for (var i = 1; i < 8; i++) points.Add(V(b.X - (b.X - a.X) * i / 8d, a.Y + .2, .22));
        var length = points.Select((point, i) => DesignGeometry.Length(point, points[(i + 1) % points.Count])).Sum();
        var belt = Part("belt-definition", V(1.3, .7, .24), new ShapeFeature { Id = "capsule-belt", Kind = FeatureKind.Sweep, Operation = FeatureOperation.Add,
            Frame = F(), Parameters = new() { ["radius"] = .003, ["segments"] = 8 }, Profile = [], Path = [.. points, points[0]], Sections = [] }) with
        { Kind = PartKind.Belt, Interfaces = [], Detail = "Owned integration belt with an explicitly approved eight-sided tube cross-section and all thirty-two original capsule rings. This finite engineering fixture exercises full 360 degree pulley travel and real independent skinning; it is not a finished production belt or a friction/tension certificate. Source rings, complete range/cycle samples and material bounds are retained.",
            Flexible = new() { RestLength = length, MinimumLength = length * .94, MaximumLength = length * 1.02, MinimumBendRadius = .06, MaximumStretchRatio = 1.02, SkinSegments = points.Count } };
        var profile = Profile() with { SamplesPerRange = 65, SamplesPerCycle = 65, FlexibleRigs = [new() { RouteId = "belt-route", MinimumSegmentRatio = .9,
            MinimumEndpointDistance = null, MaximumEndpointDistance = null, DriverJointId = "driver-joint", TravelMetresPerDegree = .2 * Math.PI / 180 }],
            CouplingFits = [new() { MechanismId = "pulley-coupling", DriverPitchRadius = .2, FollowerPitchRadius = .2, DriverToothCount = null, FollowerToothCount = null }] };
        return V2(Base("pulley-belt", [support, pulley, belt], [Instance("support", support.Id), Instance("driver", pulley.Id, a.X - .2, a.Y - .2), Instance("follower", pulley.Id, b.X - .2, b.Y - .2), Instance("belt", belt.Id)]) with
        {
            Joints = [Joint("driver-joint", JointKind.Rotary, "support", "driver", "driver", "bearing") with { Minimum = 0, Maximum = 360, Clearance = 0 },
                Joint("follower-joint", JointKind.Rotary, "support", "follower", "follower", "bearing") with { Minimum = 0, Maximum = 360, Clearance = 0 }],
            Mechanisms = [new() { Id = "pulley-coupling", Kind = MechanismKind.PulleyBelt, JointIds = ["driver-joint", "follower-joint"], Branch = "open", Parameters = new() { ["ratio"] = 1 } }],
            Routes = [new() { Id = "belt-route", InstanceId = "belt", Points = points, Closed = true, From = null, To = null }],
            Sections = [new() { Id = "belt-pitch-section", Axis = SectionAxis.Z, Offset = .22 }]
        }, profile);
    }

    static PartDefinition Bar(string id, double length, double layer, bool pinAtTip, double pinBottom, double pinTop)
    {
        var features = new List<ShapeFeature> { Box(length + .12, .12, .04, "bar") with { Frame = F(z: layer) } };
        if (pinAtTip) features.Add(Cylinder("integral-tip-pin", .03, pinBottom, pinTop - pinBottom, length + .06, .06));
        else features.Add(Cylinder("tip-bore", .05, layer - .01, .06, length + .06, .06, FeatureOperation.Subtract));
        features.Add(Cylinder("base-bore", .05, layer - .01, .06, .06, .06, FeatureOperation.Subtract));
        return Part(id, V(length + .12, .12, Math.Max(layer + .04, pinTop)), features.ToArray()) with { Interfaces = [Port("base", V(.06, .06, .12)), Port("tip", V(length + .06, .06, .12))] };
    }
    static PartInstance Place(string id, PartDefinition part, Vec3 start, Vec3 end)
    {
        var angle = Math.Atan2(end.Y - start.Y, end.X - start.X); var x = start.X - .06 * Math.Cos(angle) + .06 * Math.Sin(angle); var y = start.Y - .06 * Math.Sin(angle) - .06 * Math.Cos(angle);
        return Instance(id, part.Id) with { Frame = F(x, y) with { RotationDegrees = V(0, 0, angle * 180 / Math.PI) } };
    }
    static AssemblyDesign Linkage(bool fourBar)
    {
        var source = fourBar ? MechanismExamples.FourBar() : MechanismExamples.SliderCrank();
        // Keep the already independently tested analytical rest linkage, replacing every overlapping
        // abstract box with an actual layered bar, bore, pin and common support assembly.
        var world = DesignGeometry.CreateWorldPointResolver(source);
        Vec3 JointPoint(int index)
        {
            var joint = source.Joints[index]; var parent = source.Instances.Single(i => i.Id == joint.ParentInstanceId); var definition = source.Parts.Single(p => p.Id == parent.PartId);
            return world(parent.Id, definition.Interfaces.Single(p => p.Id == joint.ParentInterfaceId).Frame.Origin) with { Z = .12 };
        }
        var a = JointPoint(0); var b = JointPoint(1); var c = JointPoint(2); var d = JointPoint(3);
        var inputLength = DesignGeometry.Length(a, b); var rodLength = DesignGeometry.Length(b, c); var outputLength = DesignGeometry.Length(d, c);
        var supportFeatures = new List<ShapeFeature> { Box(6, 4, .04), Cylinder("input-post", .03, .04, .2, a.X, a.Y) };
        if (fourBar) supportFeatures.Add(Cylinder("output-post", .03, .04, .44, d.X, d.Y));
        else
        {
            supportFeatures.Add(Box(2.2, .02, .12, "left-slider-rail") with { Frame = F(d.X - 1.1, d.Y - .1, .04) });
            supportFeatures.Add(Box(2.2, .02, .12, "right-slider-rail") with { Frame = F(d.X - 1.1, d.Y + .08, .04) });
        }
        var support = Part("support-definition", V(6, 4, .48), supportFeatures.ToArray()) with { Interfaces = [Port("input", a), Port("output", d)] };
        var input = Bar("input-definition", inputLength, .2, true, .2, .36);
        var rod = Bar("coupler-definition", rodLength, .32, false, 0, .36);
        var output = fourBar ? Bar("output-definition", outputLength, .44, true, .32, .48)
            : Part("slider-definition", V(.12, .12, .48), Box(.12, .12, .04, "guided-foot") with { Frame = F(z: .08) },
                Box(.12, .12, .04, "slider-head") with { Frame = F(z: .44) }, Cylinder("integral-pin", .03, .08, .4, .06, .06)) with { Interfaces = [Port("base", V(.06, .06, .12))] };
        var outputInstance = fourBar ? Place("output", output, d, c) : Instance("output", output.Id, c.X - .06, c.Y - .06);
        var j = new List<JointSpec>
        {
            Joint("input-joint", JointKind.Rotary, "support", "input", "input", "base") with { Minimum = 0, Maximum = 360, Clearance = 0 },
            Joint("coupler-joint", JointKind.Rotary, "input", "coupler", "tip", "base") with { Minimum = -720, Maximum = 720, Clearance = 0 },
            Joint("output-joint", JointKind.Rotary, "coupler", "output", "tip", fourBar ? "tip" : "base") with { Minimum = -720, Maximum = 720, Clearance = 0 },
            Joint("closing-joint", fourBar ? JointKind.Rotary : JointKind.Slider, "support", "output", "output", "base") with { Axis = fourBar ? V(0, 0, 1) : V(1), Minimum = fourBar ? -720 : -2, Maximum = fourBar ? 720 : 2, Clearance = 0 }
        };
        return V2(Base(fourBar ? "four-bar" : "slider-crank", [support, input, rod, output], [Instance("support", support.Id), Place("input", input, a, b), Place("coupler", rod, b, c), outputInstance]) with
        { Joints = j, Mechanisms = [source.Mechanisms[0]], Sections = [new() { Id = "layered-bearing-section", Axis = SectionAxis.Z, Offset = .34 }] });
    }
}
