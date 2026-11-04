using Bubo.Schematic.Domain;
using static Bubo.Schematic.Tests.DesignExamples;

namespace Bubo.Schematic.Tests;

/// <summary>Explicit engineering examples, not finished production models or human asset approvals.</summary>
public static class AssemblyExamples
{
    static AssemblyProfile Profile(List<FlexibleRigPolicy>? flexible = null) => new()
    {
        SchemaVersion = "bubo-assembly-profile/v1", SamplesPerRange = 5, SamplesPerCycle = 17,
        MaxGeometryChecks = 50_000_000, MinimumSeparation = 0, Contacts = [], FlexibleRigs = flexible ?? [], CouplingFits = []
    };
    static AssemblyDesign Version2(AssemblyDesign design, List<FlexibleRigPolicy>? flexible = null, List<ContactZone>? contacts = null) => design with
    {
        SchemaVersion = "bubo-assembly-design/v2", AssemblyProfile = Profile(flexible) with { Contacts = contacts ?? [] },
        Budgets = design.Budgets with { MaxValidationChecks = 10_000_000, MaxExportBytes = 536_870_912 },
        Observations = [.. design.Observations, new() { Id = "readiness-example-scope", Kind = ObservationKind.Simplified,
            Description = "Explicit toy engineering assembly for dimensional, articulation and clearance QA. No unlisted commercial systems, physics/manufacturing certification or human asset acceptance.", Source = "Owned procedural example" }]
    };
    static ShapeFeature Cylinder(string id, double radius, double length, LocalFrame frame, int segments = 24, FeatureOperation operation = FeatureOperation.Add) => new()
    {
        Id = id, Kind = FeatureKind.Cylinder, Operation = operation, Frame = frame,
        Parameters = new() { ["radius"] = radius, ["length"] = length, ["segments"] = segments }, Profile = [], Path = [], Sections = []
    };

    public static AssemblyDesign Vehicle()
    {
        var ports = new[] { ("front-left", .7, .25), ("front-right", .7, 2.35), ("rear-left", 3.5, .25), ("rear-right", 3.5, 2.35) };
        var chassis = Part("chassis-definition", V(4.2, 2.6, .45), Box(4.2, 1.8, .45) with { Frame = F(y: .4) },
            Cylinder("front-fixed-spindle", .04, 2.2, F(.7, 2.4, .15) with { RotationDegrees = V(90) }),
            Cylinder("rear-fixed-spindle", .04, 2.2, F(3.5, 2.4, .15) with { RotationDegrees = V(90) })) with
        {
            Interfaces = ports.Select(p => Port(p.Item1, V(p.Item2, p.Item3, .15)) with { Frame = F(p.Item2, p.Item3, .15) with { RotationDegrees = V(90) } }).ToList(),
            Detail = "Chassis platform and fixed spindle supports; individually instanced bored wheels rotate around their actual bearing centres"
        };
        var wheel = DesignExamples.Vehicle().Parts.Single(p => p.Id == "wheel-definition");
        var instances = new List<PartInstance> { Instance("chassis", chassis.Id, z: .15) };
        var joints = new List<JointSpec>();
        foreach (var port in ports)
        {
            var id = "wheel-" + port.Item1;
            instances.Add(Instance(id, wheel.Id, port.Item2 - .3, port.Item3 + .1) with { Frame = F(port.Item2 - .3, port.Item3 + .1) with { RotationDegrees = V(90) } });
            joints.Add(Joint(port.Item1 + "-bearing", JointKind.Rotary, "chassis", id, port.Item1, "axle") with { Minimum = -180, Maximum = 180, Clearance = .01 });
        }
        return Version2(Base("vehicle-assembly-example", [chassis, wheel], instances) with
        {
            Joints = joints, Sections = [new() { Id = "axle-section", Axis = SectionAxis.X, Offset = .7 }, new() { Id = "longitudinal-section", Axis = SectionAxis.Y, Offset = 1.3 }]
        });
    }

    public static AssemblyDesign Device()
    {
        var d = DesignExamples.Device();
        var shell = d.Parts.Single(p => p.Id == "shell-definition");
        var holes = new[] { ("left", .021), ("right", .779) };
        shell = shell with { Interfaces = [.. shell.Interfaces, .. holes.Select(h => Port("fastener-" + h.Item1, V(h.Item2, .021, .14)))],
            Features = [.. shell.Features, .. holes.SelectMany(h => new[] {
                Cylinder(h.Item1 + "-shaft-bore", .007, .13, F(h.Item2, .021, .12), 24, FeatureOperation.Subtract),
                Cylinder(h.Item1 + "-head-counterbore", .010, .081, F(h.Item2, .021, .1695), 24, FeatureOperation.Subtract) })] };
        var battery = d.Parts.Single(p => p.Id == "battery-definition") with { Terminals = [new() { Id = "positive", Name = "External power terminal", Kind = TerminalKind.Power, Frame = F(.1, .05, .025) }] };
        var motor = d.Parts.Single(p => p.Id == "motor-definition") with { Terminals = [new() { Id = "power", Name = "External motor power", Kind = TerminalKind.Power, Frame = F(0, .05, .025) }] };
        var thread = Enumerable.Range(0, 97).Select(i => V(.009 + .0055 * Math.Cos(i * Math.PI / 4), .009 + .0055 * Math.Sin(i * Math.PI / 4), .002 + .024 * i / 96)).ToList();
        var screw = Part("screw-definition", V(.018, .018, .035), Cylinder("shaft", .0055, .03, F(.009, .009), 24),
            new() { Id = "authored-thread", Kind = FeatureKind.Sweep, Operation = FeatureOperation.Add, Frame = F(), Parameters = new() { ["radius"] = .0005, ["segments"] = 8 }, Profile = [], Path = thread, Sections = [] },
            new() { Id = "hex-head", Kind = FeatureKind.Extrude, Operation = FeatureOperation.Add, Frame = F(z: .03), Parameters = new() { ["depth"] = .005 },
                Profile = Enumerable.Range(0, 6).Select(i => new Vec2 { X = .009 + .009 * Math.Cos(i * Math.PI / 3), Y = .009 + .009 * Math.Sin(i * Math.PI / 3) }).ToList(), Path = [], Sections = [] }) with
        { Interfaces = [Port("bearing", V(.009, .009, 0))], Detail = "Separate coarse threaded fastener: shaft, twelve-turn 2 mm pitch tube thread and hex head; no manufacturing certification" };
        var path = Enumerable.Range(0, 25).Select(i => V(.002 + .12 * i / 24, .004, .004)).ToList();
        var wire = Part("wire-definition", V(.124, .008, .008), new ShapeFeature { Id = "original-wire-rings", Kind = FeatureKind.Sweep, Operation = FeatureOperation.Add, Frame = F(),
            Parameters = new() { ["radius"] = .0015, ["segments"] = 8 }, Profile = [], Path = path, Sections = [] }) with
        { Kind = PartKind.Wire, Interfaces = [], Flexible = new() { RestLength = .12, MinimumLength = .119, MaximumLength = .122, MinimumBendRadius = .01, MaximumStretchRatio = 1.02, SkinSegments = 24 } };
        var from = new Endpoint { InstanceId = "battery", PortId = "positive" }; var to = new Endpoint { InstanceId = "motor", PortId = "power" };
        var instances = d.Instances.Select(i => i.Id switch
        {
            "screw-left" => i with { Frame = F(.012, .012, .14) }, "screw-right" => i with { Frame = F(.770, .012, .14) },
            "power-wire" => i with { Frame = F(.198, .146, .071) }, _ => i
        }).ToList();
        var route = new RouteSpec { Id = "power-route", InstanceId = "power-wire", Points = path.Select(p => V(p.X + .198, p.Y + .146, p.Z + .071)).ToList(), Closed = false, From = from, To = to };
        return Version2(d with { Id = "device-assembly-example", Name = "Opening electronic device with independently modeled threaded hardware", Parts = [shell, d.Parts.Single(p => p.Id == "lid-definition"), battery, motor, screw, wire], Instances = instances,
            Joints = [d.Joints[0] with { Clearance = 0 }, .. holes.Select(h => Joint(h.Item1 + "-fastener-joint", JointKind.Screw, "shell", "screw-" + h.Item1, "fastener-" + h.Item1, "bearing") with { Minimum = 0, Maximum = 720, Pitch = .002, Clearance = .0002 })],
            Routes = [route], Connections = [new() { Id = "motor-power-connection", Kind = ConnectionKind.Power, From = from, To = to, RouteId = route.Id }] },
            [new() { RouteId = route.Id, MinimumSegmentRatio = .98, MinimumEndpointDistance = .119, MaximumEndpointDistance = .122, DriverJointId = null, TravelMetresPerDegree = null }],
            [new() { Id = "battery-wire-terminal", FirstInstanceId = "battery", SecondInstanceId = "power-wire", Frame = F(.098, .048, .023), Size = V(.004, .004, .004), Reason = "Only the wire end-cap mating with this owned external power terminal; no battery-body exemption" },
             new() { Id = "motor-wire-terminal", FirstInstanceId = "motor", SecondInstanceId = "power-wire", Frame = F(-.002, .048, .023), Size = V(.004, .004, .004), Reason = "Only the wire end-cap mating with this owned external motor terminal; no motor-body exemption" },
             new() { Id = "lid-hinge-contact", FirstInstanceId = "shell", SecondInstanceId = "lid", Frame = F(-.001, -.001, .199), Size = V(.802, .002, .002), Reason = "Only the declared lid hinge-axis contact line, bounded within a 2 mm strip; no shell or lid body exemption" }]);
    }

    public static AssemblyDesign Vessel()
    {
        var d = DesignExamples.Vessel();
        var hull = d.Parts.Single(p => p.Id == "hull-definition");
        var outer = hull.Features[0];
        List<Vec2> Ring(double width) => [new() { X = .1, Y = 1.5 }, new() { X = .6, Y = 1.5 - width / 2 }, new() { X = 11.4, Y = 1.5 - width / 2 }, new() { X = 11.9, Y = 1.5 }, new() { X = 11.4, Y = 1.5 + width / 2 }, new() { X = .6, Y = 1.5 + width / 2 }];
        var cavity = outer with { Id = "open-hull-interior", Operation = FeatureOperation.Subtract, Sections = [new() { Frame = F(z: .1), Profile = Ring(.8) }, new() { Frame = F(z: 1), Profile = Ring(2.2) }, new() { Frame = F(z: 2.2), Profile = Ring(2.8) }] };
        var bearingHousing = new ShapeFeature { Id = "annular-shaft-support", Kind = FeatureKind.Revolve, Operation = FeatureOperation.Add, Frame = F(10, 1.5, .05),
            Parameters = new() { ["angleDegrees"] = 360, ["segments"] = 32 }, Profile = [new() { X = .065, Y = 0 }, new() { X = .2, Y = 0 }, new() { X = .2, Y = .5 }, new() { X = .065, Y = .5 }], Path = [], Sections = [] };
        hull = hull with { Size = V(12, 3, 2), Features = [outer, cavity, bearingHousing],
            Detail = "Curved open cast hull with an integral 400 mm diameter annular bearing housing, 130 mm actual shaft bore and 100 mm retained hull floor. The annulus is authored before union, preserving the full bore above the floor without a late drilling operation through overlapping cast solids.",
            Interfaces = [Port("shaft-bearing", V(10, 1.5, .5)), Port("bracket-foot", V(11.2, 1.5, 0))] };
        var shaft = d.Parts.Single(p => p.Id == "shaft-definition") with { Interfaces = [Port("bearing", V(.05, .05, .25))] };
        var bracket = Part("rudder-bracket-definition", V(1.2, .2, .8), Box(1.1, .2, .1, "under-keel-arm") with { Frame = F(.1) },
            Box(.2, .2, .12, "hull-foot") with { Frame = F(.1, z: .08) }, Box(.15, .2, .8, "external-upright") with { Frame = F(1.05) }) with
        { Name = "Under-keel rudder bracket", Interfaces = [Port("hull-mount", V(.2, .1, .2)), Port("rudder-pivot", V(1.15, .1, .8))],
            Detail = "Separate connected cast bracket: a welded foot meets the flat hull underside, the arm runs 100 mm below the keel, and the upright remains outside the pointed stern. It preserves the declared rudder pivot without overlapping the hull or omitting the structural connection. No fluid or structural certification." };
        return Version2(d with { Id = "vessel-assembly-example", Name = "Curved open hull with annular bearing and independently modeled rudder bracket", Parts = [hull, d.Parts.Single(p => p.Id == "deck-definition"), shaft, d.Parts.Single(p => p.Id == "rudder-definition"), bracket],
            Numerics = d.Numerics with { LinearTolerance = .000004 },
            Observations = [.. d.Observations, new() { Id = "vessel-precision-selection", Kind = ObservationKind.Assumed,
                Description = "The twelve-metre vessel explicitly selects a four-micrometre linear tolerance for single-precision stored/evaluated Blender transforms. This resolves the declared 100-micrometre minimum feature by a factor of twenty-five; delivered geometry and ports must demonstrate that bound.", Source = "Explicit asset precision choice before complete-package approval" }],
            Instances = [.. d.Instances.Select(i => i.Id == "propeller-shaft" ? i with { Frame = F(9.95, 1.45, .25) } : i.Id == "rudder" ? i with { Frame = F(12.15, 1.5, .6) } : i), Instance("rudder-bracket", bracket.Id, 11, 1.4, -.2)],
            Joints = [d.Joints[0] with { Minimum = -180, Maximum = 180 }, d.Joints[1] with { ParentInstanceId = "rudder-bracket", Clearance = 0 },
                Joint("bracket-hull-weld", JointKind.Fixed, "hull", "rudder-bracket", "bracket-foot", "hull-mount") with { Clearance = 0 }] });
    }
}
