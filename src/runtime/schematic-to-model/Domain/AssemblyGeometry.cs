namespace Bubo.Schematic.Domain;

public readonly record struct TriangleIndex(int A, int B, int C);

/// <summary>Actual evaluated triangle skin in local double precision coordinates. Retain every measured pose-basis entry; a native float matrix is not an ideal mathematical rotation.</summary>
public sealed record PosedMesh(string InstanceId, RigidPose Pose, IReadOnlyList<Vec3> Vertices, IReadOnlyList<TriangleIndex> Triangles);

/// <summary>One operation owns this cumulative ledger, including every rest and diagnostic pose.</summary>
public sealed class GeometryWork
{
    public const long HardMaximum = 50_000_000;
    public long MaximumChecks { get; }
    public long Checks { get; private set; }
    public CancellationToken CancellationToken { get; }
    public GeometryWork(long maxGeometryChecks, CancellationToken cancellationToken = default)
    {
        if (maxGeometryChecks is < 1 or > HardMaximum) throw new ArgumentOutOfRangeException(nameof(maxGeometryChecks), "Geometry work must be in [1,50000000].");
        MaximumChecks = maxGeometryChecks; CancellationToken = cancellationToken;
    }
    public void Charge(long checks = 1)
    {
        CancellationToken.ThrowIfCancellationRequested();
        if (checks < 0 || checks > MaximumChecks - Checks) throw new AssemblyGeometryException("Cumulative geometry work budget exhausted; this operation cannot certify remaining poses.");
        Checks += checks;
    }
}

public sealed class AssemblyGeometryException(string message) : ArgumentException(message);
public sealed record ClearanceIssue(string Code, string FirstInstanceId, string? SecondInstanceId, string Message, Vec3? Witness);
public sealed record PairClearance(string FirstInstanceId, string SecondInstanceId, double? MinimumSurfaceDistance, double RequiredSeparation,
    IReadOnlyList<string> ContactZoneIds, Vec3? FirstWitness, Vec3? SecondWitness,
    string DistanceEvidenceKind = "actual-surface-minimum", double SurfaceDistanceLowerBound = 0);
/// <summary>Finite sampled geometry evidence; never continuous collision, physics or manufacturing certification.</summary>
public sealed record ClearanceReport(bool IsValid, IReadOnlyList<ClearanceIssue> Issues, IReadOnlyList<PairClearance> Pairs, long WorkChecks, string SamplingBasis);
