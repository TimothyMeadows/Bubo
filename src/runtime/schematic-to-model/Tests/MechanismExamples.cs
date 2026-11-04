using Bubo.Schematic.Domain;
using static Bubo.Schematic.Tests.DesignExamples;

namespace Bubo.Schematic.Tests;

/// <summary>Authored synthetic rigid mechanism fixtures; these grant no model acceptance.</summary>
public static class MechanismExamples
{
    public static AssemblyDesign Pair(JointKind first = JointKind.Rotary, JointKind second = JointKind.Rotary)
    {
        var ground = Part("ground-definition", V(2, .2, .2), Box(2, .2, .2)) with { Interfaces = [Port("first", V()), Port("second", V(1, 0, 0))] };
        var moving = Part("moving-definition", V(.2, .2, .2), Box(.2, .2, .2));
        return Base("rigid-pair", [ground, moving], [Instance("ground", ground.Id), Instance("first", moving.Id), Instance("second", moving.Id, 1), Instance("unrelated-copy", moving.Id, 2)]) with
        {
            Joints = [Joint("first-joint", first, "ground", "first", "first"), Joint("second-joint", second, "ground", "second", "second")]
        };
    }

    public static AssemblyDesign Coupled(MechanismKind kind, string branch = "open", double ratio = -2)
    {
        var d = Pair(second: kind == MechanismKind.RackAndPinion ? JointKind.Slider : JointKind.Rotary);
        return d with
        {
            Joints = d.Joints.Select((j, i) => j with { Minimum = -1440, Maximum = 1440, Axis = kind == MechanismKind.RackAndPinion && i == 1 ? V(1, 0, 0) : V(0, 0, 1) }).ToList(),
            Mechanisms = [new() { Id = "coupling", Kind = kind, JointIds = d.Joints.Select(j => j.Id).ToList(), Branch = branch, Parameters = kind == MechanismKind.RackAndPinion ? new() { ["pitchRadius"] = .1 } : new() { ["ratio"] = ratio } }]
        };
    }

    public static AssemblyDesign SliderCrank(string branch = "open", double offset = 0)
    {
        const double crank = .5, rod = 1;
        var b = V(crank, 0, 0); var c = V(crank + (branch == "open" ? 1 : -1) * Math.Sqrt(rod * rod - offset * offset), offset, 0);
        return Linkage("slider-crank", MechanismKind.SliderCrank, V(), b, c, c, branch, new() { ["crankLength"] = crank, ["rodLength"] = rod, ["offset"] = offset });
    }

    public static AssemblyDesign FourBar(string branch = "open", double inputAngleDegrees = 90, double couplerLength = 3, double outputLength = 2.5)
    {
        var a = V(); var b = V(Math.Cos(inputAngleDegrees * Math.PI / 180), Math.Sin(inputAngleDegrees * Math.PI / 180)); var d = V(3);
        var r = couplerLength; var s = outputLength; var bd = RigidTransforms.Subtract(d, b);
        var distance = RigidTransforms.Length(bd); var x = (r * r - s * s + distance * distance) / (2 * distance); var h = Math.Sqrt(r * r - x * x) * (branch == "open" ? 1 : -1);
        var c = V(b.X + bd.X / distance * x - bd.Y / distance * h, b.Y + bd.Y / distance * x + bd.X / distance * h);
        return Linkage("four-bar", MechanismKind.FourBar, a, b, c, d, branch, new() { ["groundLength"] = 3, ["inputLength"] = 1, ["couplerLength"] = r, ["outputLength"] = s });
    }

    static AssemblyDesign Linkage(string id, MechanismKind kind, Vec3 a, Vec3 b, Vec3 c, Vec3 d, string branch, Dictionary<string, double> parameters)
    {
        var shift = V(1, 1, .1);
        Vec3 Shift(Vec3 p) => RigidTransforms.Add(p, shift);
        var length0 = RigidTransforms.Length(RigidTransforms.Subtract(b, a)); var length1 = RigidTransforms.Length(RigidTransforms.Subtract(c, b)); var length2 = RigidTransforms.Length(RigidTransforms.Subtract(c, d));
        PartDefinition Link(string name, double length) => Part(name, V(length + .1, .1, .1), Box(length + .1, .1, .1)) with { Interfaces = [Port("base", V(.05, .05, .05)), Port("tip", V(length + .05, .05, .05))] };
        PartInstance Place(string name, PartDefinition part, Vec3 start, Vec3 end)
        {
            var angle = Math.Atan2(end.Y - start.Y, end.X - start.X) * 180 / Math.PI;
            var frame = F() with { RotationDegrees = V(0, 0, angle) };
            var origin = RigidTransforms.Subtract(Shift(start), DesignGeometry.TransformPoint(frame, V(.05, .05, .05)));
            return Instance(name, part.Id) with { Frame = frame with { Origin = origin } };
        }
        var ground = Part("ground-definition", V(6, 6, .2), Box(6, 6, .2)) with { Interfaces = [Port("input", Shift(a)), Port("output", Shift(d))] };
        var input = Link("input-definition", length0); var coupler = Link("coupler-definition", length1); var output = Link("output-definition", kind == MechanismKind.FourBar ? length2 : .1);
        var outputInstance = kind == MechanismKind.FourBar ? Place("output", output, d, c) : Place("output", output, c, RigidTransforms.Add(c, V(.1)));
        return Base(id, [ground, input, coupler, output], [Instance("ground", ground.Id), Place("input", input, a, b), Place("coupler", coupler, b, c), outputInstance]) with
        {
            Joints = [
                Joint("input-joint", JointKind.Rotary, "ground", "input", "input", "base") with { Minimum = 0, Maximum = 360 },
                Joint("coupler-joint", JointKind.Rotary, "input", "coupler", "tip", "base") with { Minimum = -720, Maximum = 720 },
                Joint("output-joint", JointKind.Rotary, "coupler", "output", "tip", kind == MechanismKind.FourBar ? "tip" : "base") with { Minimum = -720, Maximum = 720 },
                Joint("closing-joint", kind == MechanismKind.FourBar ? JointKind.Rotary : JointKind.Slider, "ground", "output", "output", "base") with { Axis = kind == MechanismKind.FourBar ? V(0, 0, 1) : V(1), Minimum = kind == MechanismKind.FourBar ? -720 : -2, Maximum = kind == MechanismKind.FourBar ? 720 : 2 }
            ],
            Mechanisms = [new() { Id = "linkage", Kind = kind, JointIds = ["input-joint", "coupler-joint", "output-joint", "closing-joint"], Parameters = parameters, Branch = branch }]
        };
    }
}
