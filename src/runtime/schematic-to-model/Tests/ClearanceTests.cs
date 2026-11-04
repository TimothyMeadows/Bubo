using Bubo.Schematic.Application;
using Bubo.Schematic.Domain;
using static Bubo.Schematic.Tests.DesignExamples;

namespace Bubo.Schematic.Tests;

public static class ClearanceTests
{
    private static readonly TriangleIndex[] CubeFaces = [new(0, 2, 1), new(0, 3, 2), new(4, 5, 6), new(4, 6, 7), new(0, 1, 5), new(0, 5, 4), new(1, 2, 6), new(1, 6, 5), new(2, 3, 7), new(2, 7, 6), new(3, 0, 4), new(3, 4, 7)];
    private static PosedMesh Cube(string id, Vec3 low, Vec3 high) => new(id, RigidPose.Identity,
        [V(low.X, low.Y, low.Z), V(high.X, low.Y, low.Z), V(high.X, high.Y, low.Z), V(low.X, high.Y, low.Z), V(low.X, low.Y, high.Z), V(high.X, low.Y, high.Z), V(high.X, high.Y, high.Z), V(low.X, high.Y, high.Z)], CubeFaces);
    private static AssemblyDesign Design(params PosedMesh[] meshes)
    {
        var p = Part("solid", V(10, 10, 10), Box(10, 10, 10));
        return Base("clearance-fixture", [p], meshes.Select(m => Instance(m.InstanceId, p.Id)).ToList()) with
        {
            SchemaVersion = "bubo-assembly-design/v2",
            AssemblyProfile = new() { SchemaVersion = "bubo-assembly-profile/v1", SamplesPerRange = 5, SamplesPerCycle = 9, MaxGeometryChecks = 50_000_000, MinimumSeparation = 0, Contacts = [], FlexibleRigs = [], CouplingFits = [] }
        };
    }
    private static ClearanceReport Verify(AssemblyDesign d, params PosedMesh[] meshes)
    {
        var measured = AssemblyClearanceVerifier.Verify(d, meshes, new(d.AssemblyProfile!.MaxGeometryChecks));
        var certificate = AssemblyClearanceVerifier.Verify(d, meshes, new(d.AssemblyProfile.MaxGeometryChecks), false);
        Check.True(measured.IsValid == certificate.IsValid, "Certificate and measured modes must agree on actual interference and required clearance");
        foreach (var pair in certificate.Pairs)
            Check.True(pair.MinimumSurfaceDistance is null && pair.FirstWitness is null && pair.SecondWitness is null
                && pair.DistanceEvidenceKind is "aabb-separation-lower-bound" or "triangle-clearance-lower-bound"
                && double.IsFinite(pair.SurfaceDistanceLowerBound) && pair.SurfaceDistanceLowerBound >= 0, "Certificate-only evidence must not invent a minimum or closest-surface witness");
        return measured;
    }
    private static ContactZone Contact(string id, Vec3 low, Vec3 size) => new() { Id = id, FirstInstanceId = "a", SecondInstanceId = "b", Frame = F() with { Origin = low }, Size = size, Reason = "Explicit owned fixture contact region" };
    private static void Valid(ClearanceReport report) => Check.True(report.IsValid, string.Join("; ", report.Issues.Select(i => i.Code + ": " + i.Message)));

    public static void Run()
    {
        Check.Case("actual clearance rejects crossed rods with every vertex outside the other rod", () =>
        {
            var a = Cube("a", V(-2, -.25, -.25), V(2, .25, .25)); var b = Cube("b", V(-.25, -2, -.25), V(.25, 2, .25));
            var r = Verify(Design(a, b), a, b); Check.True(!r.IsValid && r.Issues.Any(i => i.Code == "interference"));
        });
        Check.Case("actual solid containment rejects disjoint surfaces enclosing another part", () =>
        {
            var a = Cube("a", V(), V(2, 2, 2)); var b = Cube("b", V(.5, .5, .5), V(1.5, 1.5, 1.5));
            var r = Verify(Design(a, b), a, b); Check.True(!r.IsValid && r.Pairs.Single().MinimumSurfaceDistance!.Value > 0);
            var d = Design(a, b); d = d with { AssemblyProfile = d.AssemblyProfile! with { Contacts = [Contact("contained-fit", V(.5, .5, .5), V(1, 1, 1))] } }; Valid(Verify(d, a, b));
        });
        Check.Case("actual hollow opening remains empty despite nested broad-phase boxes", () =>
        {
            var a = SquareTube(); var b = Cube("b", V(-.5, -.5, .5), V(.5, .5, 1.5));
            var r = Verify(Design(a, b), a, b); Valid(r); Check.True(Math.Abs(r.Pairs.Single().MinimumSurfaceDistance!.Value - .5) < 1e-12);
        });
        Check.Case("coplanar external face touch passes zero separation while coincident material fails", () =>
        {
            var a = Cube("a", V(), V(1, 1, 1)); var b = Cube("b", V(1), V(2, 1, 1));
            Valid(Verify(Design(a, b), a, b));
            var rotated = RigidTransforms.FromFrame(F(20, -30, 4) with { RotationDegrees = V(21, -32, 47) });
            Valid(Verify(Design(a, b), a with { Pose = rotated }, b with { Pose = rotated }));
            b = b with { Vertices = a.Vertices }; Check.True(!Verify(Design(a, b), a, b).IsValid);
        });
        Check.Case("material penetration below linear tolerance still requires an explicit contact", () =>
        {
            var a = Cube("a", V(), V(1, 1, 1)); var b = Cube("b", V(1 - 1e-10), V(2, 1, 1));
            Check.True(!Verify(Design(a, b), a, b).IsValid);
        });
        Check.Case("accepted measured float32 basis scale cannot hide actual world material overlap", () =>
        {
            var scale = 1 + Math.ScaleB(1, -20); var overlap = Math.ScaleB(1, -21);
            var pose = RigidPose.Identity with { X = V(scale) };
            var a = Cube("a", V(), V(1, 1, 1)) with { Pose = pose };
            var b = a with { InstanceId = "b", Pose = pose with { Origin = V(scale - overlap) } };
            var d = Design(a, b); d = d with { Numerics = d.Numerics with { LinearTolerance = 1e-9 } };
            var report = Verify(d, a, b);
            Check.True(!report.IsValid && report.Issues.Any(i => i.Code == "interference"));
            Check.True(report.Pairs.Single().MinimumSurfaceDistance == 0, "Raw world surfaces overlap by 2^-21 metres");
        });
        Check.Case("measured affine contact boxes retain scale and never use transpose inverse membership", () =>
        {
            var scale = 1 + Math.ScaleB(1, -20); var overlap = Math.ScaleB(1, -21);
            var pose = RigidPose.Identity with { X = V(scale) };
            var a = Cube("a", V(), V(1, 1, 1)) with { Pose = pose };
            var b = a with { InstanceId = "b", Pose = pose with { Origin = V(scale - overlap) } };
            var d = Design(a, b); d = d with { Numerics = d.Numerics with { LinearTolerance = 1e-9 }, AssemblyProfile = d.AssemblyProfile! with { Contacts = [Contact("actual-affine-fit", V(), V(1, 1, 1))] } };
            Valid(Verify(d, a, b));
            d = d with { AssemblyProfile = d.AssemblyProfile! with { Contacts = [Contact("short-local-fit", V(), V(1 - Math.ScaleB(1, -22), 1, 1))] } };
            Check.True(Verify(d, a, b).Issues.Any(i => i.Code == "interference"));
        });
        Check.Case("exact translated origins retain non-Sterbenz overlap instead of rounding to face contact", () =>
        {
            var a = Cube("a", V(), V(1, 1, 1)) with { Pose = RigidPose.Identity with { Origin = V(999999) } };
            var b = Cube("b", V(999998), V(999999, 1, 1)) with { Pose = RigidPose.Identity with { Origin = V(Math.ScaleB(1, -35)) } };
            var d = Design(a, b); d = d with { Numerics = d.Numerics with { MaxCoordinateMagnitude = 1_000_000 } };
            Check.True(Verify(d, a, b).Issues.Any(i => i.Code == "interference"));
        });
        Check.Case("raw float32 rotated bases preserve actual metric under strict separation", () =>
        {
            var component = (double)(float)Math.Sqrt(.5);
            var pose = RigidPose.Identity with { X = V(component, component), Y = V(-component, component) };
            var a = Cube("a", V(), V(1, 1, 1)) with { Pose = pose };
            var b = a with { InstanceId = "b", Pose = pose with { Origin = RigidTransforms.Scale(pose.X, 2) } };
            var measuredGap = RigidTransforms.Length(pose.X);
            var d = Design(a, b); d = d with { Numerics = d.Numerics with { LinearTolerance = 1e-10 }, AssemblyProfile = d.AssemblyProfile! with { MinimumSeparation = measuredGap - 1e-8 } };
            var report = Verify(d, a, b); Valid(report); Check.True(Math.Abs(report.Pairs.Single().MinimumSurfaceDistance!.Value - measuredGap) < 1e-12);
            d = d with { AssemblyProfile = d.AssemblyProfile! with { MinimumSeparation = measuredGap + 1e-8 } };
            Check.True(Verify(d, a, b).Issues.Any(i => i.Code == "clearance"));
        });
        Check.Case("actual topology rejects open and folded self-intersecting skins", () =>
        {
            var a = Cube("a", V(), V(1, 1, 1));
            Check.True(Verify(Design(a), a with { Triangles = CubeFaces.Skip(1).ToArray() }).Issues.Any(i => i.Code == "topology"));
            var vertices = a.Vertices.ToArray(); vertices[6] = V(-.5, .5, -.5);
            Check.True(!Verify(Design(a), a with { Vertices = vertices }).IsValid);
        });
        Check.Case("explicit contact covers complete overlap and remains first-instance local", () =>
        {
            var a = Cube("a", V(), V(1, 1, 1)); var b = Cube("b", V(.5), V(1.5, 1, 1)); var d = Design(a, b);
            d = d with { AssemblyProfile = d.AssemblyProfile! with { Contacts = [Contact("fit", V(.5), V(.5, 1, 1))] } };
            var r = Verify(d, a, b); Valid(r); Check.True(r.Pairs.Single().ContactZoneIds.Contains("fit"));
            var relocated = RigidTransforms.FromFrame(F(1000, -2000, 500) with { RotationDegrees = V(0, 0, 90) });
            // A generous interior contact avoids certifying trig-roundoff on a mathematical box boundary.
            d = d with { AssemblyProfile = d.AssemblyProfile! with { Contacts = [Contact("fit", V(.49, -.01, -.01), V(.52, 1.02, 1.02))] } };
            Valid(Verify(d, a with { Pose = relocated }, b with { Pose = relocated }));
            d = d with { AssemblyProfile = d.AssemblyProfile! with { Contacts = [Contact("wrong", V(0), V(.1, 1, 1))] } };
            Check.True(!Verify(d, a, b).IsValid);
        });
        Check.Case("contact union covers continuous segments and rejects a gap between approved endpoints", () =>
        {
            var a = Cube("a", V(), V(1, 1, 1)); var b = Cube("b", V(.5), V(1.5, 1, 1)); var d = Design(a, b);
            d = d with { AssemblyProfile = d.AssemblyProfile! with { Contacts = [Contact("low", V(.5), V(.5, .5, 1)), Contact("high", V(.5, .5), V(.5, .5, 1))] } };
            Valid(Verify(d, a, b));
            d = d with { AssemblyProfile = d.AssemblyProfile! with { Contacts = [Contact("low", V(.5), V(.5, .49, 1)), Contact("high", V(.5, .51), V(.5, .49, 1))] } };
            Check.True(!Verify(d, a, b).IsValid);
            var low = Math.BitDecrement(.5); var high = Math.BitIncrement(.5);
            d = d with { AssemblyProfile = d.AssemblyProfile! with { Contacts = [Contact("low", V(.5), V(.5, low, 1)), Contact("high", V(.5, high), V(.5, 1 - high, 1))] } };
            Check.True(!Verify(d, a, b).IsValid, "A gap smaller than the authored linear tolerance is still outside the approved boxes");
        });
        Check.Case("contact union covering all overlap boundary faces cannot hide an unapproved material interior", () =>
        {
            var a = Cube("a", V(), V(1, 1, 1)); var b = Cube("b", V(.5), V(1.5, 1, 1)); var d = Design(a, b);
            d = d with { AssemblyProfile = d.AssemblyProfile! with { Contacts = [Contact("left", V(.5), V(.125, 1, 1)), Contact("right", V(.875), V(.125, 1, 1)), Contact("front", V(.5), V(.5, .125, 1)), Contact("back", V(.5, .875), V(.5, .125, 1)), Contact("bottom", V(.5), V(.5, 1, .125)), Contact("top", V(.5, 0, .875), V(.5, 1, .125))] } };
            Check.True(Verify(d, a, b).Issues.Any(i => i.Code == "interference"));
        });
        Check.Case("exact signs and squared distances survive subnormal intermediate area products", () =>
        {
            var a = Cube("a", V(), V(1e-100, 1e-100, 1e-100)); var b = a with { InstanceId = "b" };
            Check.True(Verify(Design(a, b), a, b).Issues.Any(i => i.Code == "interference"));
            b = Cube("b", V(2e-100), V(3e-100, 1e-100, 1e-100)); Valid(Verify(Design(a, b), a, b));
        });
        Check.Case("surface separation uses stricter global or joint requirement", () =>
        {
            var a = Cube("a", V(), V(1, 1, 1)); var b = Cube("b", V(1.25), V(2.25, 1, 1)); var d = Design(a, b);
            d = d with { AssemblyProfile = d.AssemblyProfile! with { MinimumSeparation = .2 } }; var r = Verify(d, a, b); Valid(r);
            var pair = r.Pairs.Single(); Check.True(pair.MinimumSurfaceDistance is null && pair.DistanceEvidenceKind == "aabb-separation-lower-bound" && pair.FirstWitness is null && pair.SecondWitness is null);
            Check.True(pair.SurfaceDistanceLowerBound <= .25 && pair.SurfaceDistanceLowerBound > .25 - 1e-12);
            d = d with { Joints = [Joint("mount", JointKind.Fixed, "a", "b") with { Clearance = .3 }] }; Check.True(!Verify(d, a, b).IsValid);
        });
        Check.Case("small closest-point contact cannot exempt other near portions of broad surfaces", () =>
        {
            var a = Cube("a", V(), V(1, 1, 1)); var b = Cube("b", V(1.125), V(2.125, 1, 1)); var d = Design(a, b);
            d = d with { AssemblyProfile = d.AssemblyProfile! with { MinimumSeparation = .25, Contacts = [Contact("corner-only", V(.75, -.01, -.01), V(.5, .1, .1))] } };
            Check.True(!Verify(d, a, b).IsValid);
        });
        Check.Case("local hardware coordinates survive large assembly translation", () =>
        {
            var a = Cube("a", V(), V(.0001, .0001, .0001)) with { Pose = RigidPose.Identity with { Origin = V(10000, 10000, 10000) } };
            var b = a with { InstanceId = "b", Pose = a.Pose with { Origin = V(10000.00015, 10000, 10000) } };
            var d = Design(a, b); d = d with { Numerics = d.Numerics with { MaxCoordinateMagnitude = 1_000_000 } };
            var r = Verify(d, a, b); Valid(r); Check.True(r.Pairs.Single().MinimumSurfaceDistance is null && Math.Abs(r.Pairs.Single().SurfaceDistanceLowerBound - .00005) < 1e-11);
        });
        Check.Case("far affine corner bounds round outward before certifying gaps at large origins", () =>
        {
            var size = .0005; var a = Cube("a", V(), V(size, size, size)) with { Pose = RigidPose.Identity with { Origin = V(950000, 950000, 950000) } };
            var b = a with { InstanceId = "b", Pose = a.Pose with { Origin = V(950000.0015, 950000, 950000) } };
            var gap = b.Pose.Origin.X - a.Pose.Origin.X - size; var d = Design(a, b);
            d = d with { Numerics = d.Numerics with { MaxCoordinateMagnitude = 1_000_000, LinearTolerance = 1e-12 }, AssemblyProfile = d.AssemblyProfile! with { MinimumSeparation = gap + 2e-11 } };
            var report = Verify(d, a, b); Check.True(!report.IsValid && report.Issues.Any(i => i.Code == "clearance"));
            Check.True(Math.Abs(report.Pairs.Single().MinimumSurfaceDistance!.Value - gap) < 1e-12, "A coarse far enclosure must retain precise translated-triangle fallback");
        });
        Check.Case("cumulative geometry ledger cannot reset between evaluated poses", () =>
        {
            var a = Cube("a", V(), V(1, 1, 1)); var d = Design(a); var count = Verify(d, a).WorkChecks;
            // Reserve exactly the measured first proof: memoization may make a second
            // proof cheaper, but it can never replenish an exhausted operation ledger.
            var work = new GeometryWork(count); Valid(AssemblyClearanceVerifier.Verify(d, [a], work));
            Check.Throws<AssemblyGeometryException>(() => AssemblyClearanceVerifier.Verify(d, [a], work));
            Check.Throws<AssemblyGeometryException>(() => AssemblyClearanceVerifier.Verify(d, [a], new(1)));
        });
        Check.Case("operation-local topology reuse retains exact geometry and current pose admission", () =>
        {
            var a = Cube("a", V(), V(1, 1, 1)); var d = Design(a); var work = new GeometryWork(50_000_000);
            Valid(AssemblyClearanceVerifier.Verify(d, [a], work)); var first = work.Checks;
            Valid(AssemblyClearanceVerifier.Verify(d, [a with { Pose = a.Pose with { Origin = V(3) } }], work));
            Check.True(work.Checks > first && work.Checks - first < first, "Unchanged local proof is reused without resetting cumulative work");
            var open = AssemblyClearanceVerifier.Verify(d, [a with { Triangles = CubeFaces.Skip(1).ToArray() }], work);
            Check.True(!open.IsValid && open.Issues.Any(i => i.Code == "topology"));
            var reflected = AssemblyClearanceVerifier.Verify(d, [a with { Pose = a.Pose with { X = V(-1) } }], work);
            Check.True(!reflected.IsValid && reflected.Issues.Any(i => i.Code == "topology"));
        });
        Check.Case("pair proof reuse binds raw poses separation contacts and exact local geometry", () =>
        {
            var a = Cube("a", V(), V(1, 1, 1)); var b = Cube("b", V(3), V(4, 1, 1)); var d = Design(a, b); var work = new GeometryWork(50_000_000);
            Valid(AssemblyClearanceVerifier.Verify(d, [a, b], work)); var first = work.Checks;
            Valid(AssemblyClearanceVerifier.Verify(d, [a, b], work)); Check.True(work.Checks > first && work.Checks - first < first);
            var moved = b with { Pose = b.Pose with { Origin = V(-2.5) } };
            Check.True(!AssemblyClearanceVerifier.Verify(d, [a, moved], work).IsValid);
            var required = d with { AssemblyProfile = d.AssemblyProfile! with { MinimumSeparation = 3 } };
            Check.True(AssemblyClearanceVerifier.Verify(required, [a, b], work).Issues.Any(i => i.Code == "clearance"));
            var contact = d with { AssemblyProfile = d.AssemblyProfile! with { Contacts = [Contact("fit", V(.5), V(.5, 1, 1))] } };
            Valid(AssemblyClearanceVerifier.Verify(contact, [a, moved], work));
            contact = contact with { AssemblyProfile = contact.AssemblyProfile! with { Contacts = [Contact("fit", V(), V(.1, 1, 1))] } };
            Check.True(!AssemblyClearanceVerifier.Verify(contact, [a, moved], work).IsValid);
            var changed = b with { Vertices = b.Vertices.Select(p => p with { X = p.X - 2.5 }).ToArray() };
            Check.True(!AssemblyClearanceVerifier.Verify(d, [a, changed], work).IsValid);
        });
        Check.Case("geometry cancellation precedes material allocation and exact predicates", () =>
        {
            var a = Cube("a", V(), V(1, 1, 1)); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            Check.Throws<OperationCanceledException>(() => AssemblyClearanceVerifier.Verify(Design(a), [a], new(50_000_000, cancellation.Token)));
            Check.Throws<OperationCanceledException>(() => AssemblyClearanceVerifier.Verify(Design(a), [a], new(50_000_000, cancellation.Token), false));
        });
        Check.Case("certificate-only mode has independent pair-cache identity and keeps cumulative limits", () =>
        {
            var c = (double)(float)Math.Sqrt(.5); var pose = RigidPose.Identity with { X = V(c, c), Y = V(-c, c) };
            var a = Cube("a", V(), V(1, 1, 1)) with { Pose = pose }; var b = a with { InstanceId = "b", Pose = pose with { Origin = RigidTransforms.Scale(pose.X, 2) } };
            var d = Design(a, b); var work = new GeometryWork(50_000_000);
            var certificate = AssemblyClearanceVerifier.Verify(d, [a, b], work, false); Valid(certificate);
            Check.True(certificate.Pairs.Single().MinimumSurfaceDistance is null && certificate.Pairs.Single().DistanceEvidenceKind == "triangle-clearance-lower-bound");
            var before = work.Checks; var measured = AssemblyClearanceVerifier.Verify(d, [a, b], work); Valid(measured);
            Check.True(work.Checks > before && measured.Pairs.Single().MinimumSurfaceDistance is not null && measured.Pairs.Single().DistanceEvidenceKind == "actual-surface-minimum");
            certificate = AssemblyClearanceVerifier.Verify(d, [a, b], work, false); Valid(certificate); Check.True(certificate.Pairs.Single().MinimumSurfaceDistance is null);
            Check.Throws<AssemblyGeometryException>(() => AssemblyClearanceVerifier.Verify(d, [a, b], new(1), false));
        });
    }

    private static PosedMesh SquareTube()
    {
        var vertices = new List<Vec3>();
        foreach (var z in new[] { 0d, 2d }) { vertices.AddRange(new[] { V(-2, -2, z), V(2, -2, z), V(2, 2, z), V(-2, 2, z), V(-1, -1, z), V(1, -1, z), V(1, 1, z), V(-1, 1, z) }); }
        var faces = new List<TriangleIndex>(); void Quad(int a, int b, int c, int d) { faces.Add(new(a, b, c)); faces.Add(new(a, c, d)); }
        for (var i = 0; i < 4; i++) { var next = (i + 1) % 4; Quad(i, next, next + 8, i + 8); Quad(i + 4, i + 12, next + 12, next + 4); Quad(i + 8, next + 8, next + 12, i + 12); Quad(i, i + 4, next + 4, next); }
        return new("a", RigidPose.Identity, vertices, faces);
    }
}
