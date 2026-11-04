using Bubo.Schematic.Domain;
using Bubo.Schematic.Infrastructure;
using static Bubo.Schematic.Domain.RigidTransforms;

namespace Bubo.Schematic.Tests;

/// <summary>Synthetic portable geometry correspondence checks; no native or human model acceptance.</summary>
public static class ExportOutputTests
{
    static Vec3 V(double x, double y, double z) => new() { X = x, Y = y, Z = z };
    static readonly NumericalPolicy Policy = new() { LinearTolerance = 1e-6, AngularToleranceDegrees = .01, MinimumFeatureSize = .0001, MaxCoordinateMagnitude = 2_000_000 };
    static AssemblyExportOutputVerifier.Mesh Source() => new([V(0,0,0),V(1,0,0),V(0,1,0),V(0,0,1)],
        [new(0,2,1),new(0,1,3),new(0,3,2),new(1,2,3)], [0,1,2,3]);
    static AssemblyExportOutputVerifier.Mesh Split()
    {
        var source = Source();
        return new([source.Vertices[2],source.Vertices[0],source.Vertices[3],source.Vertices[1],source.Vertices[0]],
            [new(4,0,3),new(1,3,2),new(1,2,0),new(3,0,2)], [2,0,3,1,0]);
    }
    static void Compare(AssemblyExportOutputVerifier.Mesh actual, AssemblyExportOutputVerifier.Mesh? source = null,
        RigidPose? actualWorld = null, RigidPose? sourceWorld = null, NumericalPolicy? policy = null, GeometryWork? work = null)
        => AssemblyExportOutputVerifier.CompareGeometry(actual, source ?? Source(), actualWorld ?? RigidPose.Identity, sourceWorld ?? RigidPose.Identity, policy ?? Policy, work ?? new(10000));
    public static void Run()
    {
        Check.Case("export accepts split/reordered vertices and cyclic oriented triangles", () => Compare(Split()));
        Check.Case("export rejects a reversed winding with refreshed geometry identity", () =>
        {
            var actual = Split(); actual.Triangles[0] = new(4,3,0); Check.Throws<InvalidOperationException>(() => Compare(actual));
        });
        Check.Case("export rejects substituted duplicate triangle multiplicity", () =>
        {
            var actual = Split(); actual.Triangles[0] = actual.Triangles[1]; Check.Throws<InvalidOperationException>(() => Compare(actual));
        });
        Check.Case("export rejects logical labels used to hide changed geometry", () =>
        {
            var actual = Split(); actual.Vertices[4] = V(.01,0,0); Check.Throws<InvalidOperationException>(() => Compare(actual));
        });
        Check.Case("export rejects a real seam gap even inside the source comparison tolerance", () =>
        {
            var actual = Split(); actual.Vertices[4] = V(Policy.LinearTolerance / 2,0,0);
            Check.Throws<InvalidOperationException>(() => Compare(actual));
        });
        Check.Case("export rejects unreferenced extra split vertices", () =>
        {
            var actual = Source(); actual = actual with { Vertices = [..actual.Vertices,V(0,0,0)], Logical = [..actual.Logical,0] };
            Check.Throws<InvalidOperationException>(() => Compare(actual));
        });
        Check.Case("export rejects missing original source vertices", () =>
        {
            var actual = Split(); actual.Logical[2] = 0; actual.Vertices[2] = V(0,0,0); Check.Throws<InvalidOperationException>(() => Compare(actual));
        });
        Check.Case("export rejects nonfinite local geometry", () =>
        {
            var actual = Split(); actual.Vertices[4] = V(double.NaN,0,0); Check.Throws<InvalidOperationException>(() => Compare(actual));
        });
        Check.Case("export preserves small local hardware at large assembly coordinates", () =>
        {
            var original = Source(); original = original with { Vertices = original.Vertices.Select(v => Scale(v, .0001)).ToArray() };
            var world = RigidPose.Identity with { Origin = V(1_000_000,1_000_000,1_000_000) }; Compare(original, original, world, world);
            var actual = original with { Vertices = original.Vertices.ToArray() }; actual.Vertices[1] = V(0,0,0);
            Check.Throws<InvalidOperationException>(() => Compare(actual, original, world, world));
        });
        Check.Case("export compares actual world vertices as well as local dimensions", () =>
        {
            var moved = RigidPose.Identity with { Origin = V(.01,0,0) };
            Check.Throws<InvalidOperationException>(() => Compare(Split(), actualWorld: moved));
        });
        Check.Case("export complete geometry comparison shares one finite work budget", () =>
        {
            var work = new GeometryWork(20); Compare(Split(), work: work);
            Check.Throws<AssemblyGeometryException>(() => Compare(Split(), work: work));
        });
        Check.Case("export geometry comparison honors cancellation", () =>
        {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            Check.Throws<OperationCanceledException>(() => Compare(Split(), work: new GeometryWork(10000, cancellation.Token)));
        });
    }
}
