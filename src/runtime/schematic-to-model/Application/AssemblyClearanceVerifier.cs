using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Bubo.Schematic.Domain;
using static Bubo.Schematic.Domain.RigidTransforms;

namespace Bubo.Schematic.Application;

/// <summary>Independent actual-triangle solid checks. Broad-phase boxes never certify solid occupancy.</summary>
public static class AssemblyClearanceVerifier
{
    private const int MaximumElements = 250_000;
    private const int MaximumContacts = 32;
    private const string Basis = "One supplied evaluated pose; measured affine-basis entries are retained in translated world triangle surfaces, with exact-sign predicates, required separation and solid containment. Pair evidence distinguishes measured finite-triangle minima from certified lower bounds. Caller must retain every diagnostic sample and the same cumulative work ledger. No continuous collision or physics claim. Contact overlap enclosure is conservative; uncertifiable contact unions are rejected.";
    [ThreadStatic] private static PrecisionContext? precision;
    private static readonly ConditionalWeakTable<GeometryWork, LocalProofCache> localProofs = new();

    public static ClearanceReport Verify(AssemblyDesign design, IReadOnlyList<PosedMesh> meshes, GeometryWork work)
        => Verify(design, meshes, work, true);

    /// <summary>False certifies interference and required gaps without measuring a global surface minimum; pair evidence then reports an explicit lower bound and null minimum.</summary>
    public static ClearanceReport Verify(AssemblyDesign design, IReadOnlyList<PosedMesh> meshes, GeometryWork work, bool measureSurfaceMinimum)
    {
        var previous = precision; precision = new();
        try { return VerifyCore(design, meshes, work, measureSurfaceMinimum); }
        finally { precision = previous; }
    }
    private static ClearanceReport VerifyCore(AssemblyDesign design, IReadOnlyList<PosedMesh> meshes, GeometryWork work, bool measureSurfaceMinimum)
    {
        ArgumentNullException.ThrowIfNull(design); ArgumentNullException.ThrowIfNull(meshes); ArgumentNullException.ThrowIfNull(work);
        work.Charge();
        var profile = design.AssemblyProfile ?? throw new AssemblyGeometryException("Actual assembly clearance requires an approved assembly profile.");
        if (work.MaximumChecks > profile.MaxGeometryChecks || profile.MaxGeometryChecks is < 1 or > GeometryWork.HardMaximum)
            throw new AssemblyGeometryException("The cumulative geometry ledger exceeds the approved profile budget.");
        var tolerance = design.Numerics.LinearTolerance;
        if (!double.IsFinite(tolerance) || tolerance <= 0 || !double.IsFinite(profile.MinimumSeparation) || profile.MinimumSeparation < 0 || !double.IsFinite(design.Numerics.MaxCoordinateMagnitude) || design.Numerics.MaxCoordinateMagnitude is <= 0 or > 1_000_000)
            throw new AssemblyGeometryException("Finite approved numerical tolerance and minimum separation are required.");
        if (meshes.Count != design.Instances.Count || meshes.Count > 4096 || profile.Contacts.Count > MaximumContacts)
            throw new AssemblyGeometryException("Actual meshes must match all approved instances; at most 4096 meshes and 32 contact zones are supported.");
        long vertices = 0, triangles = 0;
        foreach (var mesh in meshes)
        {
            work.Charge();
            if (mesh is null || mesh.Vertices is null || mesh.Triangles is null) throw new AssemblyGeometryException("Actual geometry collections cannot be null.");
            vertices += mesh.Vertices.Count; triangles += mesh.Triangles.Count;
            if (vertices > MaximumElements || triangles > MaximumElements) throw new AssemblyGeometryException("Actual pose exceeds the bounded 250000 vertex/triangle verification limit.");
        }
        // Reserve all operation-owned indexing/storage before allocating or comparing identities.
        work.Charge(checked(vertices * 6 + triangles * 12 + meshes.Count * (long)meshes.Count));
        var ids = design.Instances.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mesh in meshes)
            if (!ids.Contains(mesh.InstanceId) || !actual.Add(mesh.InstanceId)) throw new AssemblyGeometryException("Missing, duplicated or unapproved actual physical instance.");
        var issues = new List<ClearanceIssue>(); var pairs = new List<PairClearance>();
        var solids = new List<Solid>(meshes.Count);
        var localCache = localProofs.GetValue(work, _ => new());
        foreach (var mesh in meshes)
        {
            var key = LocalProofCache.Key(mesh, design.Numerics, work);
            if (localCache.Find(key, mesh, work) is { } verified)
            {
                var poseError = PoseProblem(mesh.Pose, design.Numerics);
                if (poseError is not null) issues.Add(new("topology", mesh.InstanceId, null, poseError, null));
                else solids.Add(verified.WithMesh(mesh));
                continue;
            }
            var solid = Solid.Create(mesh, design.Numerics, work);
            if (solid.Error is not null) { issues.Add(new("topology", mesh.InstanceId, null, solid.Error, null)); continue; }
            var self = SelfIntersection(solid, work);
            if (self is not null) issues.Add(new("self-intersection", mesh.InstanceId, null, "Actual closed mesh intersects itself away from its shared topological boundary.", Point(mesh.Pose, self)));
            else localCache.Remember(key, solid);
            solids.Add(solid);
        }
        // Invalid solids cannot support inside/outside or clearance certification.
        if (issues.Count != 0) return new(false, issues, pairs, work.Checks, Basis);
        for (var i = 0; i < solids.Count; i++)
            for (var j = i + 1; j < solids.Count; j++)
            {
                work.Charge(); precision!.Clear(); var originalA = solids[i]; var originalB = solids[j];
                // Translate the common origin only. Even accepted float32 rotation bases are
                // not exactly orthonormal; transpose inverses and basis cancellation can hide
                // actual material overlap or alter measured distances.
                var firstPose = originalA.Mesh.Pose with { Origin = Vec3.Zero };
                var secondPose = originalB.Mesh.Pose with { Origin = Subtract(originalB.Mesh.Pose.Origin, originalA.Mesh.Pose.Origin) };
                var secondOrigin = EV.Raw(originalB.Mesh.Pose.Origin) - EV.Raw(originalA.Mesh.Pose.Origin);
                var required = profile.MinimumSeparation;
                foreach (var joint in design.Joints)
                    if (Pair(joint.ParentInstanceId, joint.ChildInstanceId, originalA.Mesh.InstanceId, originalB.Mesh.InstanceId)) required = Math.Max(required, joint.Clearance);
                work.Charge(profile.Contacts.Count + 1L);
                var contacts = profile.Contacts.Where(c => Pair(c.FirstInstanceId, c.SecondInstanceId, originalA.Mesh.InstanceId, originalB.Mesh.InstanceId)).ToArray();
                var pairKey = LocalProofCache.PairKey(originalA.ProofIdentity, originalB.ProofIdentity, originalA.Mesh, originalB.Mesh, required, contacts, work, measureSurfaceMinimum);
                if (localCache.FindPair(pairKey) is { } proven) { pairs.Add(proven); continue; }
                // A posed world enclosure is shared across all pairs and samples with the
                // exact same local proof and measured affine pose. Use it only for a
                // disjointness/gap certificate; near pairs retain translated exact geometry.
                var worldA = localCache.WorldBounds(originalA, work); var worldB = localCache.WorldBounds(originalB, work);
                work.Charge(64); var worldSeparation = worldA.ExactDistanceSquared(worldB);
                if (worldSeparation.Sign > 0 && (worldSeparation - D(required) * D(required)).Sign >= 0)
                {
                    var farWorld = new PairClearance(originalA.Mesh.InstanceId, originalB.Mesh.InstanceId, null, required, [], null, null,
                        "aabb-separation-lower-bound", new Fraction(worldSeparation, D(1)).SqrtLowerBound());
                    pairs.Add(farWorld); localCache.RememberPair(pairKey, farWorld); continue;
                }
                var a = originalA.Transformed(firstPose, work, false);
                var b = originalB.Transformed(secondPose, work, false, secondOrigin);
                work.Charge(64); var separated = a.Root!.Bounds.ExactDistanceSquared(b.Root!.Bounds);
                if (separated.Sign > 0 && (separated - D(required) * D(required)).Sign >= 0)
                {
                    var far = new PairClearance(a.Mesh.InstanceId, b.Mesh.InstanceId, null, required, [], null, null,
                        "aabb-separation-lower-bound", new Fraction(separated, D(1)).SqrtLowerBound()); pairs.Add(far); localCache.RememberPair(pairKey, far);
                    continue;
                }
                a.EnsureHierarchy(work); b.EnsureHierarchy(work);
                var zones = contacts.Select(c => Zone.Create(c, c.FirstInstanceId == a.Mesh.InstanceId ? firstPose : secondPose, work,
                    c.FirstInstanceId == a.Mesh.InstanceId ? EV.Raw(Vec3.Zero) : secondOrigin)).ToArray();
                var state = new PairState(a, b, zones, required, tolerance, work, measureSurfaceMinimum);
                CompareNodes(a.Root!, b.Root!, state);
                InspectInterior(a, b, state); InspectInterior(b, a, state);
                var overlapCovered = !state.MaterialOverlap || state.OverlapCovered();
                if (!overlapCovered)
                    issues.Add(new("interference", a.Mesh.InstanceId, b.Mesh.InstanceId,
                        "Actual material overlap is outside approved contacts, or its complete conservative enclosure cannot be certified by the supported contact union.", state.World(state.Overlap.Min)));
                if (state.ClearanceFailure)
                    issues.Add(new("clearance", a.Mesh.InstanceId, b.Mesh.InstanceId,
                        "Actual surfaces violate the global/joint minimum separation outside a certifiable complete contact region.", state.World(state.FirstWitness)));
                var measured = measureSurfaceMinimum
                    ? new PairClearance(a.Mesh.InstanceId, b.Mesh.InstanceId, state.ExactMinimum!.Value.SqrtApproximate(), required,
                        state.Used.Order(StringComparer.Ordinal).ToArray(), state.World(state.FirstWitness), state.World(state.SecondWitness),
                        "actual-surface-minimum", state.ExactMinimum.Value.SqrtLowerBound())
                    : new PairClearance(a.Mesh.InstanceId, b.Mesh.InstanceId, null, required, state.Used.Order(StringComparer.Ordinal).ToArray(), null, null,
                        "triangle-clearance-lower-bound", state.ClearanceFailure || state.MaterialOverlap || state.Used.Count != 0 ? 0 : SeparationSquared(required, tolerance).SqrtLowerBound());
                pairs.Add(measured);
                if (overlapCovered && !state.ClearanceFailure) localCache.RememberPair(pairKey, measured);
            }
        return new(issues.Count == 0, issues, pairs, work.Checks, Basis);
    }

    private static bool Pair(string a, string b, string c, string d) => a == c && b == d || a == d && b == c;
    private static bool Finite(Vec3 p) => p is not null && double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Z);
    private static double Coordinate(Vec3 p, int axis) => axis == 0 ? p.X : axis == 1 ? p.Y : p.Z;
    private static Vec3 Interpolate(Vec3 a, Vec3 b, double t) => t <= 0 ? a : t >= 1 ? b : Add(Scale(a, 1 - t), Scale(b, t));
    private static string? PoseProblem(RigidPose pose, NumericalPolicy numerics)
    {
        if (!Finite(pose.Origin) || !Finite(pose.X) || !Finite(pose.Y) || !Finite(pose.Z)) return "Actual pose is nonfinite.";
        if (Math.Abs(pose.Origin.X) > numerics.MaxCoordinateMagnitude || Math.Abs(pose.Origin.Y) > numerics.MaxCoordinateMagnitude || Math.Abs(pose.Origin.Z) > numerics.MaxCoordinateMagnitude) return "Actual pose exceeds approved coordinate bounds.";
        var angular = Math.Sin(numerics.AngularToleranceDegrees * Math.PI / 180);
        return Math.Abs(Length(pose.X) - 1) > angular || Math.Abs(Length(pose.Y) - 1) > angular || Math.Abs(Length(pose.Z) - 1) > angular || Math.Abs(Dot(pose.X, pose.Y)) > angular || Math.Abs(Dot(pose.X, pose.Z)) > angular || Math.Abs(Dot(pose.Y, pose.Z)) > angular || Dot(Cross(pose.X, pose.Y), pose.Z) <= 0 ? "Actual pose is not an admitted right-handed basis." : null;
    }
    private sealed class LocalProofCache
    {
        private readonly Dictionary<string, Solid> entries = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PairClearance> pairEntries = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Bounds> worldBounds = new(StringComparer.Ordinal);
        private long bytes, pairBytes, worldBytes, nextProofIdentity;
        public static string Key(PosedMesh mesh, NumericalPolicy numerics, GeometryWork work)
        {
            // Hash every IEEE coordinate bit and source triangle index; a hash hit is also
            // followed by full equality, so no approximate or collision-based reuse occurs.
            work.Charge(mesh.Vertices.Count + mesh.Triangles.Count + 1L);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); Span<byte> buffer = stackalloc byte[32];
            BinaryPrimitives.WriteInt32LittleEndian(buffer, mesh.Vertices.Count); BinaryPrimitives.WriteInt32LittleEndian(buffer[4..], mesh.Triangles.Count); hash.AppendData(buffer[..8]);
            foreach (var value in new[] { numerics.LinearTolerance, numerics.AngularToleranceDegrees, numerics.MinimumFeatureSize, numerics.MaxCoordinateMagnitude }) { BinaryPrimitives.WriteInt64LittleEndian(buffer, BitConverter.DoubleToInt64Bits(value)); hash.AppendData(buffer[..8]); }
            foreach (var p in mesh.Vertices)
            {
                BinaryPrimitives.WriteInt64LittleEndian(buffer, BitConverter.DoubleToInt64Bits(p.X)); BinaryPrimitives.WriteInt64LittleEndian(buffer[8..], BitConverter.DoubleToInt64Bits(p.Y)); BinaryPrimitives.WriteInt64LittleEndian(buffer[16..], BitConverter.DoubleToInt64Bits(p.Z)); hash.AppendData(buffer[..24]);
            }
            foreach (var t in mesh.Triangles) { BinaryPrimitives.WriteInt32LittleEndian(buffer, t.A); BinaryPrimitives.WriteInt32LittleEndian(buffer[4..], t.B); BinaryPrimitives.WriteInt32LittleEndian(buffer[8..], t.C); hash.AppendData(buffer[..12]); }
            work.Charge(0); return Convert.ToHexString(hash.GetHashAndReset());
        }
        public Solid? Find(string key, PosedMesh mesh, GeometryWork work)
        {
            if (!entries.TryGetValue(key, out var solid)) return null;
            work.Charge(mesh.Vertices.Count + mesh.Triangles.Count + 1L);
            if (solid.Points.Length != mesh.Vertices.Count || solid.Faces.Length != mesh.Triangles.Count) return null;
            for (var i = 0; i < solid.Points.Length; i++) if (solid.Points[i] != mesh.Vertices[i]) return null;
            for (var i = 0; i < solid.Faces.Length; i++) if (solid.Faces[i] != mesh.Triangles[i]) return null;
            return solid;
        }
        public void Remember(string key, Solid solid)
        {
            solid.ProofIdentity = ++nextProofIdentity;
            var storage = solid.Points.Length * 128L + solid.Faces.Length * 512L + 1024;
            if (entries.Count >= 64 || storage > 67_108_864 - bytes || entries.ContainsKey(key)) return;
            entries.Add(key, solid); bytes += storage;
        }
        public PairClearance? FindPair(string key) => pairEntries.GetValueOrDefault(key);
        public Bounds WorldBounds(Solid solid, GeometryWork work)
        {
            work.Charge(64); var pose = solid.Mesh.Pose; var key = new StringBuilder(solid.ProofIdentity.ToString("X16", System.Globalization.CultureInfo.InvariantCulture));
            foreach (var p in new[] { pose.Origin, pose.X, pose.Y, pose.Z })
                foreach (var value in new[] { p.X, p.Y, p.Z }) key.Append(BitConverter.DoubleToInt64Bits(value).ToString("X16", System.Globalization.CultureInfo.InvariantCulture));
            var identity = key.ToString(); if (worldBounds.TryGetValue(identity, out var stored)) return stored;
            var bounds = Bounds.Empty; var origin = EV.Raw(pose.Origin); var x = EV.Raw(pose.X); var y = EV.Raw(pose.Y); var z = EV.Raw(pose.Z);
            // An affine image of the validated local AABB encloses every actual vertex.
            // Its eight corners may be looser than the posed mesh, but can only decline a
            // far-pair optimization; an overlapping box never certifies solid occupancy.
            work.Charge(9);
            foreach (var p in solid.Root!.Bounds.Corners())
                bounds = bounds.Union(PrecisionContext.EncloseExact(origin + x * D(p.X) + y * D(p.Y) + z * D(p.Z), work));
            var storage = identity.Length * 2L + 512;
            if (worldBounds.Count < 4096 && storage <= 8_388_608 - worldBytes) { worldBounds.Add(identity, bounds); worldBytes += storage; }
            return bounds;
        }
        public void RememberPair(string key, PairClearance pair)
        {
            var storage = key.Length * 2L + 1024 + pair.ContactZoneIds.Sum(id => id.Length * 2L + 64);
            if (pairEntries.Count >= 2048 || storage > 16_777_216 - pairBytes || pairEntries.ContainsKey(key)) return;
            pairEntries.Add(key, pair with { ContactZoneIds = Array.AsReadOnly(pair.ContactZoneIds.ToArray()) });
            pairBytes += storage;
        }
        public static string PairKey(long firstProof, long secondProof, PosedMesh first, PosedMesh second, double required, ContactZone[] contacts, GeometryWork work, bool measureSurfaceMinimum)
        {
            work.Charge(64L + contacts.Length * 64L);
            var builder = new StringBuilder();
            void Text(string value) => builder.Append(value.Length).Append(':').Append(value);
            void Number(double value) => builder.Append(BitConverter.DoubleToInt64Bits(value).ToString("X16", System.Globalization.CultureInfo.InvariantCulture));
            void Vector(Vec3 value) { Number(value.X); Number(value.Y); Number(value.Z); }
            void Pose(RigidPose value) { Vector(value.Origin); Vector(value.X); Vector(value.Y); Vector(value.Z); }
            builder.Append(measureSurfaceMinimum ? 'M' : 'C').Append(firstProof.ToString("X16")).Append(secondProof.ToString("X16")); Text(first.InstanceId); Text(second.InstanceId); Pose(first.Pose); Pose(second.Pose); Number(required); builder.Append(contacts.Length.ToString("X8"));
            foreach (var contact in contacts) { Text(contact.Id); Text(contact.FirstInstanceId); Text(contact.SecondInstanceId); Vector(contact.Frame.Origin); Vector(contact.Frame.RotationDegrees); Vector(contact.Size); }
            return builder.ToString();
        }
    }

    private readonly record struct Bounds(Vec3 Min, Vec3 Max)
    {
        public static Bounds Empty => new(new() { X = double.PositiveInfinity, Y = double.PositiveInfinity, Z = double.PositiveInfinity }, new() { X = double.NegativeInfinity, Y = double.NegativeInfinity, Z = double.NegativeInfinity });
        public bool IsEmpty => Min.X > Max.X || Min.Y > Max.Y || Min.Z > Max.Z;
        public Bounds Add(Vec3 p) => new(new() { X = Math.Min(Min.X, p.X), Y = Math.Min(Min.Y, p.Y), Z = Math.Min(Min.Z, p.Z) }, new() { X = Math.Max(Max.X, p.X), Y = Math.Max(Max.Y, p.Y), Z = Math.Max(Max.Z, p.Z) });
        public Bounds Union(Bounds b) => IsEmpty ? b : b.IsEmpty ? this : Add(b.Min).Add(b.Max);
        public Bounds Expand(double d) => new(new() { X = Math.BitDecrement(Min.X - d), Y = Math.BitDecrement(Min.Y - d), Z = Math.BitDecrement(Min.Z - d) }, new() { X = Math.BitIncrement(Max.X + d), Y = Math.BitIncrement(Max.Y + d), Z = Math.BitIncrement(Max.Z + d) });
        public Bounds Intersect(Bounds b) => new(new() { X = Math.Max(Min.X, b.Min.X), Y = Math.Max(Min.Y, b.Min.Y), Z = Math.Max(Min.Z, b.Min.Z) }, new() { X = Math.Min(Max.X, b.Max.X), Y = Math.Min(Max.Y, b.Max.Y), Z = Math.Min(Max.Z, b.Max.Z) });
        public bool Contains(Vec3 p) => p.X >= Min.X && p.X <= Max.X && p.Y >= Min.Y && p.Y <= Max.Y && p.Z >= Min.Z && p.Z <= Max.Z;
        public bool Contains(Bounds b) => Contains(b.Min) && Contains(b.Max);
        public double DistanceSquared(Bounds b)
        {
            var x = Math.Max(0, Math.Max(Min.X - b.Max.X, b.Min.X - Max.X)); var y = Math.Max(0, Math.Max(Min.Y - b.Max.Y, b.Min.Y - Max.Y)); var z = Math.Max(0, Math.Max(Min.Z - b.Max.Z, b.Min.Z - Max.Z)); return x * x + y * y + z * z;
        }
        public double DistanceLowerSquared(Bounds b)
        {
            var ownMin = Min; var ownMax = Max;
            double Gap(int axis)
            {
                var gap = Math.Max(Coordinate(ownMin, axis) - Coordinate(b.Max, axis), Coordinate(b.Min, axis) - Coordinate(ownMax, axis));
                return gap > 0 ? Math.Max(0, Math.BitDecrement(gap)) : 0;
            }
            double Square(double value) => value > 0 ? Math.Max(0, Math.BitDecrement(value * value)) : 0;
            return Math.Max(0, Math.BitDecrement(Math.BitDecrement(Square(Gap(0)) + Square(Gap(1))) + Square(Gap(2))));
        }
        public Dyadic ExactDistanceSquared(Bounds b)
        {
            var total = Dyadic.Zero;
            for (var axis = 0; axis < 3; axis++)
            {
                var x = D(Coordinate(Min, axis)) - D(Coordinate(b.Max, axis)); var y = D(Coordinate(b.Min, axis)) - D(Coordinate(Max, axis));
                var gap = x.Sign > 0 ? x : y.Sign > 0 ? y : Dyadic.Zero; total += gap * gap;
            }
            return total;
        }
        public IEnumerable<Vec3> Corners() { for (var i = 0; i < 8; i++) yield return new() { X = (i & 1) == 0 ? Min.X : Max.X, Y = (i & 2) == 0 ? Min.Y : Max.Y, Z = (i & 4) == 0 ? Min.Z : Max.Z }; }
    }
    private sealed class Node(Bounds bounds, int start, int count)
    {
        public Bounds Bounds = bounds; public int Start = start, Count = count; public Node? Left, Right;
        public bool Leaf => Left is null;
    }
    private sealed class PrecisionContext
    {
        // Pair-local, bounded exact residuals prevent world-coordinate rounding from changing
        // material occupancy. The public witness remains a double; predicates retain the exact
        // binary affine expression. No input arrays or process-global geometry are cached.
        private readonly Dictionary<Vec3, (EV Exact, Bounds Bounds, long Bytes)> values = new(ReferenceEqualityComparer.Instance);
        private long bytes;
        public void Clear() { values.Clear(); bytes = 0; }
        public bool Contains(Vec3 p) => values.ContainsKey(p);
        public EV Exact(Vec3 p) => values.TryGetValue(p, out var value) ? value.Exact : EV.Raw(p);
        public Bounds Enclosure(Vec3 p) => values.TryGetValue(p, out var value) ? value.Bounds : new(p, p);
        public Bounds Register(Vec3 p, EV exact, GeometryWork work)
        {
            work.Charge(64);
            // Exactly representable affine results need no residual entry. They keep the
            // ordinary filtered predicates; only genuinely rounded coordinates use the
            // additional exact path. This is an equality proof, not a numeric tolerance.
            if ((exact.X - D(p.X)).Sign == 0 && (exact.Y - D(p.Y)).Sign == 0 && (exact.Z - D(p.Z)).Sign == 0) return new(p, p);
            // Include dictionary capacity, point/enclosure objects and BigInteger array overhead,
            // in addition to significant bits, before admitting another retained expression.
            var storage = 512 + (BigInteger.Abs(exact.X.Numerator).GetBitLength() + BigInteger.Abs(exact.Y.Numerator).GetBitLength() + BigInteger.Abs(exact.Z.Numerator).GetBitLength() + 7) / 8;
            if (storage > 67_108_864 - bytes || values.Count >= MaximumElements + 1) throw new AssemblyGeometryException("Exact transformed-geometry residuals exceed the bounded 64 MiB pair-local storage limit.");
            var bounds = EncloseCoordinates(p, exact); values.Add(p, (exact, bounds, storage)); bytes += storage; return bounds;
        }
        public static Bounds EncloseExact(EV exact, GeometryWork work)
        {
            work.Charge(64); return EncloseCoordinates(exact.Approximate(), exact);
        }
        private static Bounds EncloseCoordinates(Vec3 p, EV exact)
        {
            if (!Finite(p)) throw new AssemblyGeometryException("Measured affine world enclosure is nonfinite.");
            double Bound(Dyadic coordinate, double value, bool lower)
            {
                for (var i = 0; i < 8; i++)
                {
                    var sign = (coordinate - D(value)).Sign;
                    if (lower ? sign >= 0 : sign <= 0) return value;
                    value = lower ? Math.BitDecrement(value) : Math.BitIncrement(value);
                }
                throw new AssemblyGeometryException("Measured affine coordinate exceeds the bounded eight-ULP world enclosure.");
            }
            var low = new Vec3 { X = Bound(exact.X, p.X, true), Y = Bound(exact.Y, p.Y, true), Z = Bound(exact.Z, p.Z, true) };
            var high = new Vec3 { X = Bound(exact.X, p.X, false), Y = Bound(exact.Y, p.Y, false), Z = Bound(exact.Z, p.Z, false) };
            return new(low, high);
        }
        public void Remove(Vec3 p) { if (values.Remove(p, out var value)) bytes -= value.Bytes; }
    }
    private sealed class Solid
    {
        public required PosedMesh Mesh; public required Vec3[] Points; public required TriangleIndex[] Faces;
        public required int[] Order; public required Dictionary<(int, int), (int First, int Second)> Edges;
        public Node? Root; public int Orientation; public string? Error; public long ProofIdentity;
        public Bounds[]? VertexBounds;
        public (Vec3 A, Vec3 B, Vec3 C) Triangle(int id) { var t = Faces[id]; return (Points[t.A], Points[t.B], Points[t.C]); }
        public Solid WithMesh(PosedMesh mesh) => new() { Mesh = mesh, Points = Points, Faces = Faces, Order = Order, Edges = Edges, Root = Root, Orientation = Orientation, ProofIdentity = ProofIdentity };
        public Bounds FaceBounds(int id) { var t = Faces[id]; return VertexBounds is null ? Bounds.Empty.Add(Points[t.A]).Add(Points[t.B]).Add(Points[t.C]) : VertexBounds[t.A].Union(VertexBounds[t.B]).Union(VertexBounds[t.C]); }
        public static Solid Create(PosedMesh mesh, NumericalPolicy numerics, GeometryWork work)
        {
            var s = new Solid { Mesh = mesh, Points = mesh.Vertices.ToArray(), Faces = mesh.Triangles.ToArray(), Order = new int[mesh.Triangles.Count], Edges = new() };
            void Fail(string message) => s.Error ??= message;
            if (s.Points.Length < 4 || s.Faces.Length < 4) { Fail("A closed solid requires at least four vertices and triangles."); return s; }
            if (!Finite(mesh.Pose.Origin) || !Finite(mesh.Pose.X) || !Finite(mesh.Pose.Y) || !Finite(mesh.Pose.Z)) { Fail("Actual pose is nonfinite."); return s; }
            var pose = mesh.Pose;
            if (Math.Abs(pose.Origin.X) > numerics.MaxCoordinateMagnitude || Math.Abs(pose.Origin.Y) > numerics.MaxCoordinateMagnitude || Math.Abs(pose.Origin.Z) > numerics.MaxCoordinateMagnitude) Fail("Actual pose exceeds approved coordinate bounds.");
            // Rigid numeric roundoff is admitted only under the authored angular tolerance.
            var angular = Math.Sin(numerics.AngularToleranceDegrees * Math.PI / 180);
            if (Math.Abs(Length(pose.X) - 1) > angular || Math.Abs(Length(pose.Y) - 1) > angular || Math.Abs(Length(pose.Z) - 1) > angular || Math.Abs(Dot(pose.X, pose.Y)) > angular || Math.Abs(Dot(pose.X, pose.Z)) > angular || Math.Abs(Dot(pose.Y, pose.Z)) > angular || Dot(Cross(pose.X, pose.Y), pose.Z) <= 0)
                Fail("Actual pose is not a right-handed rigid basis.");
            foreach (var p in s.Points) if (!Finite(p) || Math.Abs(p.X) > numerics.MaxCoordinateMagnitude || Math.Abs(p.Y) > numerics.MaxCoordinateMagnitude || Math.Abs(p.Z) > numerics.MaxCoordinateMagnitude) Fail("Actual local vertex exceeds approved finite coordinate bounds.");
            if (s.Error is not null) return s;
            var used = new bool[s.Points.Length]; var neighbours = new List<int>[s.Faces.Length];
            var directions = new Dictionary<(int, int), (int Face, int Direction)>();
            for (var i = 0; i < s.Faces.Length; i++)
            {
                work.Charge(); s.Order[i] = i; neighbours[i] = [];
                var t = s.Faces[i];
                if (t.A < 0 || t.B < 0 || t.C < 0 || t.A >= s.Points.Length || t.B >= s.Points.Length || t.C >= s.Points.Length || t.A == t.B || t.B == t.C || t.C == t.A) { Fail("Actual triangle has invalid or repeated indices."); continue; }
                used[t.A] = used[t.B] = used[t.C] = true;
                if (Collinear(s.Points[t.A], s.Points[t.B], s.Points[t.C], work)) Fail("Actual triangle has exactly zero area.");
                foreach (var (u, v) in new[] { (t.A, t.B), (t.B, t.C), (t.C, t.A) })
                {
                    var edge = u < v ? (u, v) : (v, u); var sign = u < v ? 1 : -1;
                    if (!directions.TryGetValue(edge, out var previous)) { directions.Add(edge, (i, sign)); s.Edges.Add(edge, (i, -1)); }
                    else if (s.Edges[edge].Second >= 0 || previous.Direction == sign) Fail("Actual triangle skin is nonmanifold or inconsistently wound.");
                    else { s.Edges[edge] = (previous.Face, i); neighbours[i].Add(previous.Face); neighbours[previous.Face].Add(i); }
                }
            }
            if (used.Any(v => !v) || s.Edges.Values.Any(e => e.Second < 0)) Fail("Actual solid has unused vertices or an open boundary.");
            if (s.Error is not null) return s;
            var seen = new bool[s.Faces.Length]; var stack = new Stack<int>(); stack.Push(0); seen[0] = true; var connected = 0;
            while (stack.Count > 0) { work.Charge(); var f = stack.Pop(); connected++; foreach (var n in neighbours[f]) if (!seen[n]) { seen[n] = true; stack.Push(n); } }
            if (connected != s.Faces.Length) { Fail("Each physical instance must remain one connected closed solid."); return s; }
            // Paired opposite edges alone do not exclude a pinched/bow-tie vertex.
            var firstFace = Enumerable.Repeat(-1, s.Points.Length).ToArray(); var incidentCount = new int[s.Points.Length]; var stamps = new int[s.Faces.Length];
            for (var i = 0; i < s.Faces.Length; i++) foreach (var vertex in new[] { s.Faces[i].A, s.Faces[i].B, s.Faces[i].C }) { firstFace[vertex] = i; incidentCount[vertex]++; }
            for (var vertex = 0; vertex < s.Points.Length; vertex++)
            {
                var count = 0; var stamp = vertex + 1; stack.Push(firstFace[vertex]); stamps[firstFace[vertex]] = stamp;
                while (stack.Count > 0)
                {
                    work.Charge(4); var face = stack.Pop(); count++;
                    foreach (var neighbour in neighbours[face])
                    {
                        var f = s.Faces[neighbour];
                        if (stamps[neighbour] == stamp || f.A != vertex && f.B != vertex && f.C != vertex) continue;
                        stamps[neighbour] = stamp; stack.Push(neighbour);
                    }
                }
                if (count != incidentCount[vertex]) { Fail("Actual skin contains a nonmanifold pinched vertex."); return s; }
            }
            var anchor = s.Points[0]; var intervalVolume = new Interval(0, 0); var intervalAnchor = IP.Of(anchor);
            foreach (var face in s.Faces)
            {
                work.Charge(8); intervalVolume += IP.Dot(IP.Of(s.Points[face.A]) - intervalAnchor, IP.Cross(IP.Of(s.Points[face.B]) - intervalAnchor, IP.Of(s.Points[face.C]) - intervalAnchor));
            }
            s.Orientation = intervalVolume.Sign;
            if (s.Orientation == 0)
            {
                var volume = Dyadic.Zero;
                foreach (var face in s.Faces) { work.Charge(64); volume += ExactOrient(s.Points[face.A], s.Points[face.B], s.Points[face.C], anchor); }
                s.Orientation = volume.Sign;
            }
            if (s.Orientation == 0) { Fail("Actual closed skin encloses exactly zero oriented volume."); return s; }
            s.Root = s.Build(0, s.Faces.Length, work); return s;
        }
        public Solid Transformed(RigidPose pose, GeometryWork work, bool buildHierarchy = true, EV? exactOrigin = null)
        {
            work.Charge(Points.Length * 3L + Faces.Length * 4L);
            var s = new Solid { Mesh = Mesh, Points = new Vec3[Points.Length], VertexBounds = new Bounds[Points.Length], Faces = Faces, Edges = Edges, Order = new int[Faces.Length], Orientation = Orientation };
            for (var i = 0; i < Points.Length; i++)
            {
                var p = Points[i];
                var exact = (exactOrigin ?? EV.Raw(pose.Origin)) + EV.Raw(pose.X) * D(p.X) + EV.Raw(pose.Y) * D(p.Y) + EV.Raw(pose.Z) * D(p.Z);
                var point = exact.Approximate(); s.Points[i] = point;
                if (!Finite(point)) throw new AssemblyGeometryException("Translated world actual geometry is nonfinite.");
                s.VertexBounds[i] = precision!.Register(point, exact, work);
            }
            if (s.Points.Any(p => !Finite(p))) throw new AssemblyGeometryException("Relative actual geometry is nonfinite.");
            for (var i = 0; i < s.Order.Length; i++) s.Order[i] = i;
            if (buildHierarchy) s.Root = s.Build(0, s.Faces.Length, work);
            else { var bounds = Bounds.Empty; foreach (var b in s.VertexBounds) bounds = bounds.Union(b); s.Root = new(bounds, 0, s.Faces.Length); }
            return s;
        }
        public void EnsureHierarchy(GeometryWork work) { if (Faces.Length > 4 && Root!.Leaf) Root = Build(0, Faces.Length, work); }
        private Node Build(int start, int count, GeometryWork work)
        {
            work.Charge(count + 1L); var bounds = Bounds.Empty;
            for (var i = start; i < start + count; i++) bounds = bounds.Union(FaceBounds(Order[i]));
            var node = new Node(bounds, start, count); if (count <= 4) return node;
            var size = Subtract(bounds.Max, bounds.Min); var axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
            var mid = start + count / 2; var lo = start; var hi = start + count - 1;
            double Center(int index) { var b = FaceBounds(Order[index]); return Coordinate(b.Min, axis) / 2 + Coordinate(b.Max, axis) / 2; }
            while (lo < hi)
            {
                work.Charge(2L * (hi - lo + 1)); var pivot = Center(lo + (hi - lo) / 2); var left = lo; var right = hi;
                while (left <= right) { while (Center(left) < pivot) left++; while (Center(right) > pivot) right--; if (left <= right) { (Order[left], Order[right]) = (Order[right], Order[left]); left++; right--; } }
                if (mid <= right) hi = right; else if (mid >= left) lo = left; else break;
            }
            node.Left = Build(start, mid - start, work); node.Right = Build(mid, start + count - mid, work); return node;
        }
    }

    private sealed class Zone
    {
        public required string Id; public required Vec3 Size;
        public required EV Origin, X, Y, Z, NX, NY, NZ;
        public required Dyadic Determinant;
        public static Zone Create(ContactZone c, RigidPose first, GeometryWork work, EV exactOrigin)
        {
            work.Charge(512);
            var frame = FromFrame(c.Frame);
            // Compose the approved first-local box with the actual measured basis in exact
            // dyadic arithmetic. Cofactors give its true affine inverse halfspaces, without
            // projecting a near-unit basis back onto an ideal rotation.
            EV DirectionOf(Vec3 p) => EV.Of(first.X) * D(p.X) + EV.Of(first.Y) * D(p.Y) + EV.Of(first.Z) * D(p.Z);
            var x = DirectionOf(frame.X); var y = DirectionOf(frame.Y); var z = DirectionOf(frame.Z);
            var nx = EV.Cross(y, z); var ny = EV.Cross(z, x); var nz = EV.Cross(x, y); var determinant = EV.Dot(x, nx);
            if (determinant.Sign <= 0) throw new AssemblyGeometryException("Approved contact box has an unsupported singular or reflected measured affine basis.");
            return new() { Id = c.Id, Size = c.Size, Origin = exactOrigin + DirectionOf(frame.Origin), X = x, Y = y, Z = z, NX = nx, NY = ny, NZ = nz, Determinant = determinant };
        }
        private Dyadic Value(Vec3 p, int axis)
        {
            var normal = axis == 0 ? NX : axis == 1 ? NY : NZ;
            return EV.Dot(normal, EV.Of(p) - Origin);
        }
        public bool Contains(Vec3 p, GeometryWork work)
        {
            work.Charge(128);
            for (var axis = 0; axis < 3; axis++) { var v = Value(p, axis); if (v.Sign < 0 || (v - D(Coordinate(Size, axis)) * Determinant).Sign > 0) return false; }
            return true;
        }
        public (Fraction Lo, Fraction Hi)? Interval(Vec3 a, Vec3 b, GeometryWork work)
        {
            work.Charge(384); var lo = Fraction.Zero; var hi = Fraction.One;
            for (var axis = 0; axis < 3; axis++)
                for (var upper = 0; upper < 2; upper++)
                {
                    var va = Value(a, axis); var vb = Value(b, axis);
                    if (upper == 0) { va = -va; vb = -vb; } else { var size = D(Coordinate(Size, axis)) * Determinant; va -= size; vb -= size; }
                    if (va.Sign > 0 && vb.Sign > 0) return null;
                    if (va.Sign > 0 || vb.Sign > 0)
                    {
                        var cut = new Fraction(va, va - vb);
                        if (va.Sign > 0) { if (cut.CompareTo(lo) > 0) lo = cut; } else if (cut.CompareTo(hi) < 0) hi = cut;
                        if (lo.CompareTo(hi) > 0) return null;
                    }
                }
            return (lo, hi);
        }
        public Bounds? AxisBounds()
        {
            bool Axis(EV v) => new[] { v.X.Sign, v.Y.Sign, v.Z.Sign }.Count(c => c != 0) == 1;
            if (!Axis(X) || !Axis(Y) || !Axis(Z)) return null;
            var b = Bounds.Empty;
            foreach (var p in new Bounds(Vec3.Zero, Size).Corners())
            {
                var exact = Origin + X * D(p.X) + Y * D(p.Y) + Z * D(p.Z);
                var point = new Vec3 { X = new Fraction(exact.X, D(1)).Value, Y = new Fraction(exact.Y, D(1)).Value, Z = new Fraction(exact.Z, D(1)).Value };
                if ((exact.X - D(point.X)).Sign != 0 || (exact.Y - D(point.Y)).Sign != 0 || (exact.Z - D(point.Z)).Sign != 0) return null;
                b = b.Add(point);
            }
            return b;
        }
    }

    private sealed class PairState(Solid first, Solid second, Zone[] zones, double required, double tolerance, GeometryWork work, bool measureSurfaceMinimum)
    {
        public Solid First = first, Second = second; public Zone[] Zones = zones; public GeometryWork Work = work;
        public readonly bool MeasureSurfaceMinimum = measureSurfaceMinimum;
        public double Required = required, Tolerance = tolerance, MinimumSquared = double.PositiveInfinity;
        public double MinimumUpperSquared = double.PositiveInfinity;
        public readonly double RequiredUpperSquared = SeparationSquared(required, tolerance).UpperValue();
        public Fraction? ExactMinimum;
        public Vec3 FirstWitness = first.Points[0], SecondWitness = second.Points[0];
        public bool MaterialOverlap, ClearanceFailure, HasSurfaceIntersection; public Bounds Overlap = Bounds.Empty;
        public HashSet<string> Used = new(StringComparer.Ordinal);
        public Vec3 World(Vec3 p) => Add(First.Mesh.Pose.Origin, p);
        public bool SegmentCovered(Vec3 a, Vec3 b)
        {
            Work.Charge(Zones.Length * 8L + 1);
            var intervals = new List<(Fraction Lo, Fraction Hi, string Id)>(Zones.Length);
            foreach (var zone in Zones) if (zone.Interval(a, b, Work) is { } interval) intervals.Add((interval.Lo, interval.Hi, zone.Id));
            // Prepaid bounded sort; exact fractions prevent tiny uncovered gaps being rounded shut.
            Work.Charge(intervals.Count * 32L); intervals.Sort((a, b) => a.Lo.CompareTo(b.Lo));
            var covered = Fraction.Zero;
            foreach (var interval in intervals)
            {
                if (interval.Lo.CompareTo(covered) > 0) return false;
                Used.Add(interval.Id); if (interval.Hi.CompareTo(covered) > 0) covered = interval.Hi;
                if (covered.CompareTo(Fraction.One) >= 0) return true;
            }
            return false;
        }
        public bool BoxCovered(Bounds box)
        {
            if (box.IsEmpty) return false;
            foreach (var zone in Zones)
                if (box.Corners().All(p => zone.Contains(p, Work))) { Used.Add(zone.Id); return true; }
            // Exact axis-aligned decomposition proves coverage of the entire enclosure,
            // including its interior. Rotated multi-box coverage is conservatively unsupported.
            Work.Charge(Zones.Length * 256L);
            var aligned = Zones.Select(z => (Zone: z, Bounds: z.AxisBounds())).Where(z => z.Bounds is not null).Select(z => (z.Zone, Bounds: z.Bounds!.Value)).ToArray();
            if (aligned.Length == 0) return false;
            var cuts = new List<double>[3];
            for (var axis = 0; axis < 3; axis++)
            {
                Work.Charge(aligned.Length * 32L + 2); var low = Coordinate(box.Min, axis); var high = Coordinate(box.Max, axis);
                cuts[axis] = [low, high];
                foreach (var z in aligned) { var a = Coordinate(z.Bounds.Min, axis); var b = Coordinate(z.Bounds.Max, axis); if (a > low && a < high) cuts[axis].Add(a); if (b > low && b < high) cuts[axis].Add(b); }
                cuts[axis] = cuts[axis].Distinct().Order().ToList();
            }
            var nx = Math.Max(1, cuts[0].Count - 1); var ny = Math.Max(1, cuts[1].Count - 1); var nz = Math.Max(1, cuts[2].Count - 1);
            Work.Charge(checked((long)nx * ny * nz * (aligned.Length + 1)));
            for (var x = 0; x < nx; x++) for (var y = 0; y < ny; y++) for (var z = 0; z < nz; z++)
            {
                var cell = new Bounds(new() { X = cuts[0][x], Y = cuts[1][y], Z = cuts[2][z] }, new() { X = cuts[0][Math.Min(x + 1, cuts[0].Count - 1)], Y = cuts[1][Math.Min(y + 1, cuts[1].Count - 1)], Z = cuts[2][Math.Min(z + 1, cuts[2].Count - 1)] });
                var owner = Array.FindIndex(aligned, a => a.Bounds.Contains(cell)); if (owner < 0) return false; Used.Add(aligned[owner].Zone.Id);
            }
            return true;
        }
        public bool OverlapCovered() => BoxCovered(Overlap);
    }

    private static void CompareNodes(Node a, Node b, PairState s)
    {
        s.Work.Charge();
        if (Prune(a.Bounds, b.Bounds, s)) return;
        if (a.Leaf && b.Leaf)
        {
            for (var i = a.Start; i < a.Start + a.Count; i++) for (var j = b.Start; j < b.Start + b.Count; j++)
            {
                s.Work.Charge(); var ai = s.First.Order[i]; var bi = s.Second.Order[j];
                var ba = s.First.FaceBounds(ai); var bb = s.Second.FaceBounds(bi);
                if (Prune(ba, bb, s)) continue;
                var ta = s.First.Triangle(ai); var tb = s.Second.Triangle(bi);
                if (FastDistancePrune(ta, tb, s)) continue;
                var intersection = Intersections(ta, tb, s.Work, out var coplanar);
                Vec3 pa, pb; double distance; Fraction exactDistance;
                if (intersection.Count > 0)
                {
                    s.HasSurfaceIntersection = true;
                    distance = 0; exactDistance = Fraction.Zero; pa = pb = intersection[0];
                    s.Overlap = s.Overlap.Union(intersection.Enclosure);
                    var area = coplanar && HasArea(intersection, ta, s.Work);
                    s.Work.Charge(128);
                    var penetrating = area && EV.Dot(EV.Cross(EV.Of(ta.B) - EV.Of(ta.A), EV.Of(ta.C) - EV.Of(ta.A)), EV.Cross(EV.Of(tb.B) - EV.Of(tb.A), EV.Of(tb.C) - EV.Of(tb.A))).Sign * s.First.Orientation * s.Second.Orientation > 0;
                    if (!coplanar && intersection.HasSpan)
                        penetrating |= PatchCrosses(s.First, ai, tb, s.Work) && PatchCrosses(s.Second, bi, ta, s.Work);
                    if (penetrating)
                    {
                        s.MaterialOverlap = true;
                        // Every intersection segment is covered continuously, not only its endpoints.
                        for (var p = 0; p < intersection.Count; p++) for (var q = p; q < intersection.Count; q++)
                            if (!s.SegmentCovered(intersection[p], intersection[q])) s.ClearanceFailure = true;
                    }
                }
                else
                {
                    // Occupancy checks still run for every candidate. Once zero is proven,
                    // another separated triangle cannot improve it; positive clearance
                    // requirements retain their independent full near-surface checks.
                    if ((!s.MeasureSurfaceMinimum || s.ExactMinimum is { } zero && zero.CompareTo(Fraction.Zero) == 0) && s.Required <= s.Tolerance) continue;
                    if (PlaneDistancePrune(ta, tb, s)) continue;
                    distance = TriangleDistance(ta, tb, s.Work, out pa, out pb, out exactDistance);
                }
                if (s.ExactMinimum is null || exactDistance.CompareTo(s.ExactMinimum.Value) < 0) { s.ExactMinimum = exactDistance; s.MinimumSquared = distance; s.MinimumUpperSquared = exactDistance.UpperValue(); s.FirstWitness = pa; s.SecondWitness = pb; }
                var threshold = D(s.Required) - D(s.Tolerance);
                if (threshold.Sign > 0 && exactDistance.CompareTo(new(threshold * threshold, D(1))) < 0)
                {
                    // Entire near-surface regions are enclosed; a closest point inside a small
                    // contact zone cannot exempt other near portions of the same large triangles.
                    var region = ba.Intersect(bb.Expand(s.Required)).Union(bb.Intersect(ba.Expand(s.Required)));
                    if (!s.SegmentCovered(pa, pb) || !s.BoxCovered(region)) s.ClearanceFailure = true;
                }
            }
            return;
        }
        if (b.Leaf || !a.Leaf && a.Count >= b.Count)
        {
            var near = a.Left!.Bounds.DistanceSquared(b.Bounds) <= a.Right!.Bounds.DistanceSquared(b.Bounds) ? a.Left : a.Right; var far = ReferenceEquals(near, a.Left) ? a.Right : a.Left;
            CompareNodes(near!, b, s); CompareNodes(far!, b, s);
        }
        else
        {
            var near = b.Left!.Bounds.DistanceSquared(a.Bounds) <= b.Right!.Bounds.DistanceSquared(a.Bounds) ? b.Left : b.Right; var far = ReferenceEquals(near, b.Left) ? b.Right : b.Left;
            CompareNodes(a, near!, s); CompareNodes(a, far!, s);
        }
    }
    private static bool Prune(Bounds a, Bounds b, PairState state)
    {
        if (state.MeasureSurfaceMinimum && state.ExactMinimum is null) return false;
        var intervalLower = a.DistanceLowerSquared(b);
        // Both scalar operands are directed bounds on exact quantities. Plainly separated
        // BVH nodes need no BigInteger comparison; uncertain/equal cases retain exact proof.
        if (intervalLower > 0 && (!state.MeasureSurfaceMinimum || intervalLower >= state.MinimumUpperSquared) && intervalLower >= state.RequiredUpperSquared) return true;
        var lower = a.DistanceSquared(b); var threshold = Math.Max(0, state.Required - state.Tolerance);
        if (lower <= 0 || state.MeasureSurfaceMinimum && lower < Math.BitDecrement(Math.BitDecrement(state.MinimumSquared)) || lower < threshold * threshold) return false;
        state.Work.Charge(64); var exact = a.ExactDistanceSquared(b); var required = D(state.Required) - D(state.Tolerance);
        return exact.Sign > 0 && (!state.MeasureSurfaceMinimum || new Fraction(exact, D(1)).CompareTo(state.ExactMinimum!.Value) >= 0) && (required.Sign <= 0 || (exact - required * required).Sign >= 0);
    }
    private static Fraction SeparationSquared(double required, double tolerance)
    {
        var threshold = D(required) - D(tolerance); return threshold.Sign > 0 ? new(threshold * threshold, D(1)) : Fraction.Zero;
    }
    private static bool PlaneDistancePrune((Vec3 A, Vec3 B, Vec3 C) a, (Vec3 A, Vec3 B, Vec3 C) b, PairState state)
    {
        if (state.MeasureSurfaceMinimum && state.ExactMinimum is null) return false;
        var threshold = D(state.Required) - D(state.Tolerance);
        bool Bound((Vec3 A, Vec3 B, Vec3 C) plane, (Vec3 A, Vec3 B, Vec3 C) other)
        {
            state.Work.Charge(256);
            var origin = EV.Of(plane.A); var normal = EV.Cross(EV.Of(plane.B) - origin, EV.Of(plane.C) - origin);
            var x = EV.Dot(normal, EV.Of(other.A) - origin); var y = EV.Dot(normal, EV.Of(other.B) - origin); var z = EV.Dot(normal, EV.Of(other.C) - origin);
            if (x.Sign == 0 || x.Sign != y.Sign || x.Sign != z.Sign) return false;
            if (x.Sign < 0) { x = -x; y = -y; z = -z; }
            var gap = (x - y).Sign <= 0 ? x : y; if ((z - gap).Sign < 0) gap = z;
            var normalSquared = EV.Dot(normal, normal);
            var lower = new Fraction(gap * gap, normalSquared);
            return (!state.MeasureSurfaceMinimum || lower.CompareTo(state.ExactMinimum!.Value) >= 0) && (threshold.Sign <= 0 || lower.CompareTo(new(threshold * threshold, D(1))) >= 0);
        }
        // Infinite-plane separation is a lower bound only. Reported minima always come
        // from actual finite triangle closest points, never this bound or a broad-phase box.
        return Bound(a, b) || Bound(b, a);
    }
    private static bool FastDistancePrune((Vec3 A, Vec3 B, Vec3 C) a, (Vec3 A, Vec3 B, Vec3 C) b, PairState state)
    {
        if (state.MeasureSurfaceMinimum && state.ExactMinimum is null) return false;
        state.Work.Charge(64); ApproximateClosest(a, b, out var first, out var second); var axis = Subtract(second, first);
        if (!Finite(axis) || axis == Vec3.Zero) return false;
        (double Low, double High) ProjectionOf(Vec3 p)
        {
            var bounds = precision!.Enclosure(p); var low = 0d; var high = 0d;
            for (var i = 0; i < 3; i++)
            {
                var n = Coordinate(axis, i); var min = Coordinate(bounds.Min, i); var max = Coordinate(bounds.Max, i); if (n < 0) (min, max) = (max, min);
                low = Math.BitDecrement(low + Math.BitDecrement(min * n)); high = Math.BitIncrement(high + Math.BitIncrement(max * n));
            }
            return (low, high);
        }
        var ap = new[] { ProjectionOf(a.A), ProjectionOf(a.B), ProjectionOf(a.C) }; var bp = new[] { ProjectionOf(b.A), ProjectionOf(b.B), ProjectionOf(b.C) };
        var gap = Math.Max(Math.BitDecrement(bp.Min(p => p.Low) - ap.Max(p => p.High)), Math.BitDecrement(ap.Min(p => p.Low) - bp.Max(p => p.High)));
        if (!(gap > 0)) return false;
        var normUpper = 0d; for (var i = 0; i < 3; i++) { var n = Coordinate(axis, i); normUpper = Math.BitIncrement(normUpper + Math.BitIncrement(n * n)); }
        var lower = Math.BitDecrement(Math.BitDecrement(gap * gap) / normUpper);
        if (!(lower > 0) || !double.IsFinite(lower)) return false;
        var squared = new Fraction(D(lower), D(1)); var threshold = D(state.Required) - D(state.Tolerance);
        // A direction from approximate closest points is merely a candidate separating
        // axis. Outward projection intervals prove the bound on the actual vertices; an
        // inaccurate approximate solve can only miss this optimization, never certify it.
        return (!state.MeasureSurfaceMinimum || squared.CompareTo(state.ExactMinimum!.Value) >= 0) && (threshold.Sign <= 0 || squared.CompareTo(new(threshold * threshold, D(1))) >= 0);
    }
    private static void ApproximateClosest((Vec3 A, Vec3 B, Vec3 C) a, (Vec3 A, Vec3 B, Vec3 C) b, out Vec3 first, out Vec3 second)
    {
        var pa = a.A; var pb = b.A; var delta = Subtract(pa, pb); var best = Dot(delta, delta); var av = new[] { a.A, a.B, a.C }; var bv = new[] { b.A, b.B, b.C };
        void Candidate(Vec3 p, Vec3 q) { var d = Subtract(p, q); var squared = Dot(d, d); if (double.IsFinite(squared) && squared < best) { best = squared; pa = p; pb = q; } }
        foreach (var p in av) Candidate(p, ApproximatePoint(p, b)); foreach (var p in bv) Candidate(ApproximatePoint(p, a), p);
        for (var i = 0; i < 3; i++) for (var j = 0; j < 3; j++)
        {
            var p = av[i]; var q = bv[j]; var u = Subtract(av[(i + 1) % 3], p); var v = Subtract(bv[(j + 1) % 3], q); var d = Subtract(p, q);
            var aa = Dot(u, u); var cc = Dot(v, v); var bb = Dot(u, v); var dd = Dot(u, d); var ee = Dot(v, d);
            if (!(aa > 0 && cc > 0)) continue;
            var cross = Cross(u, v); var denominator = Dot(cross, cross); var s = denominator > 0 ? Math.Clamp((bb * ee - cc * dd) / denominator, 0, 1) : 0; var t = (bb * s + ee) / cc;
            if (t < 0) { t = 0; s = Math.Clamp(-dd / aa, 0, 1); } else if (t > 1) { t = 1; s = Math.Clamp((bb - dd) / aa, 0, 1); }
            Candidate(Interpolate(p, av[(i + 1) % 3], s), Interpolate(q, bv[(j + 1) % 3], t));
        }
        first = pa; second = pb;
    }
    private static Vec3 ApproximatePoint(Vec3 p, (Vec3 A, Vec3 B, Vec3 C) t)
    {
        var ab = Subtract(t.B, t.A); var ac = Subtract(t.C, t.A); var ap = Subtract(p, t.A); var d1 = Dot(ab, ap); var d2 = Dot(ac, ap); if (d1 <= 0 && d2 <= 0) return t.A;
        var bp = Subtract(p, t.B); var d3 = Dot(ab, bp); var d4 = Dot(ac, bp); if (d3 >= 0 && d4 <= d3) return t.B;
        var vc = d1 * d4 - d3 * d2; if (vc <= 0 && d1 >= 0 && d3 <= 0) return Interpolate(t.A, t.B, d1 / (d1 - d3));
        var cp = Subtract(p, t.C); var d5 = Dot(ab, cp); var d6 = Dot(ac, cp); if (d6 >= 0 && d5 <= d6) return t.C;
        var vb = d5 * d2 - d1 * d6; if (vb <= 0 && d2 >= 0 && d6 <= 0) return Interpolate(t.A, t.C, d2 / (d2 - d6));
        var va = d3 * d6 - d5 * d4; if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return Interpolate(t.B, t.C, (d4 - d3) / (d4 - d3 + d5 - d6));
        var denominator = va + vb + vc; if (!(denominator > 0) || !double.IsFinite(denominator)) return t.A;
        return Add(t.A, Add(Scale(ab, vb / denominator), Scale(ac, vc / denominator)));
    }
    private static bool PatchCrosses(Solid mesh, int index, (Vec3 A, Vec3 B, Vec3 C) plane, GeometryWork work)
    {
        var face = mesh.Faces[index]; var indices = new[] { face.A, face.B, face.C };
        var signs = indices.Select(i => Orient(plane.A, plane.B, plane.C, mesh.Points[i], work)).ToArray();
        if (signs.Any(s => s < 0) && signs.Any(s => s > 0)) return true;
        if (signs.Count(s => s == 0) != 2) return false;
        var ends = indices.Where((_, i) => signs[i] == 0).ToArray(); var edge = ends[0] < ends[1] ? (ends[0], ends[1]) : (ends[1], ends[0]);
        var adjacent = mesh.Edges[edge]; var other = adjacent.First == index ? adjacent.Second : adjacent.First;
        var t = mesh.Faces[other]; var opposite = new[] { t.A, t.B, t.C }.First(i => i != edge.Item1 && i != edge.Item2);
        return Orient(plane.A, plane.B, plane.C, mesh.Points[opposite], work) * signs.First(s => s != 0) < 0;
    }

    private static Vec3? SelfIntersection(Solid s, GeometryWork work)
    {
        Vec3? result = null;
        void Visit(Node a, Node b)
        {
            if (result is not null) return; work.Charge(); if (a.Bounds.DistanceSquared(b.Bounds) > 0) return;
            if (a.Leaf && b.Leaf)
            {
                for (var i = a.Start; i < a.Start + a.Count; i++) for (var j = b.Start; j < b.Start + b.Count; j++)
                {
                    var ai = s.Order[i]; var bi = s.Order[j]; if (ReferenceEquals(a, b) && i >= j) continue; work.Charge();
                    if (s.FaceBounds(ai).DistanceSquared(s.FaceBounds(bi)) > 0) continue;
                    var first = s.Faces[ai]; var second = s.Faces[bi]; var shared = new[] { first.A, first.B, first.C }.Intersect(new[] { second.A, second.B, second.C }).ToArray();
                    if (shared.Length == 2)
                    {
                        var ta = s.Triangle(ai); var other = new[] { second.A, second.B, second.C }.First(v => v != shared[0] && v != shared[1]);
                        // Noncoplanar convex triangles sharing an edge can meet only on
                        // that edge. Coplanar third vertices must lie on opposite sides.
                        if (Orient(ta.A, ta.B, ta.C, s.Points[other], work) != 0) continue;
                        var own = new[] { first.A, first.B, first.C }.First(v => v != shared[0] && v != shared[1]); var drop = Projection(ta, work);
                        var sideA = Orient2(s.Points[shared[0]], s.Points[shared[1]], s.Points[own], drop, work); var sideB = Orient2(s.Points[shared[0]], s.Points[shared[1]], s.Points[other], drop, work);
                        if (sideA * sideB < 0) continue;
                        result = s.Points[shared[0]]; return;
                    }
                    if (shared.Length == 1 && SharedVertexSeparated(s, ai, bi, shared[0], work)) continue;
                    var points = Intersections(s.Triangle(ai), s.Triangle(bi), work, out _);
                    if (shared.Length > 0 && points.HasRoundedPoint) { result = points[0]; return; }
                    foreach (var p in points)
                    {
                        var allowed = shared.Length == 1 ? p == s.Points[shared[0]] : shared.Length == 2 && OnSegment(s.Points[shared[0]], s.Points[shared[1]], p, work);
                        if (!allowed || shared.Length == 3) { result = p; return; }
                    }
                }
            }
            else if (ReferenceEquals(a, b)) { Visit(a.Left!, a.Left!); Visit(a.Left!, a.Right!); Visit(a.Right!, a.Right!); }
            else if (b.Leaf || !a.Leaf && a.Count >= b.Count) { Visit(a.Left!, b); Visit(a.Right!, b); }
            else { Visit(a, b.Left!); Visit(a, b.Right!); }
        }
        Visit(s.Root!, s.Root!); return result;
    }
    private static bool SharedVertexSeparated(Solid solid, int first, int second, int shared, GeometryWork work)
    {
        var a = solid.Faces[first]; var b = solid.Faces[second]; var av = new[] { a.A, a.B, a.C }.Where(i => i != shared).ToArray(); var bv = new[] { b.A, b.B, b.C }.Where(i => i != shared).ToArray();
        var at = solid.Triangle(first); var bt = solid.Triangle(second);
        var x = Orient(at.A, at.B, at.C, solid.Points[bv[0]], work); var y = Orient(at.A, at.B, at.C, solid.Points[bv[1]], work);
        if (x != 0 && x == y) return true;
        if (x != 0 || y != 0)
        {
            x = Orient(bt.A, bt.B, bt.C, solid.Points[av[0]], work); y = Orient(bt.A, bt.B, bt.C, solid.Points[av[1]], work);
            if (x != 0 && x == y) return true;
        }
        var drop = Projection(at, work); var origin = solid.Points[shared];
        bool Outside(int[] own, int[] other)
        {
            for (var edge = 0; edge < 2; edge++)
            {
                var axis = solid.Points[own[edge]]; var inside = Orient2(origin, axis, solid.Points[own[1 - edge]], drop, work);
                var firstSide = Orient2(origin, axis, solid.Points[other[0]], drop, work); var secondSide = Orient2(origin, axis, solid.Points[other[1]], drop, work);
                if (inside * firstSide < 0 && inside * secondSide < 0) return true;
            }
            return false;
        }
        return Outside(av, bv) || Outside(bv, av);
    }

    private static void InspectInterior(Solid source, Solid target, PairState state)
    {
        if (!state.HasSurfaceIntersection)
        {
            // Connected closed surfaces with no boundary intersections cannot change
            // inside/outside classification. One exact parity proof classifies the whole
            // source surface; an enclosed source still contributes every vertex to the
            // conservative material/contact enclosure.
            state.Work.Charge(); var point = source.Points[0];
            if (target.Root!.Bounds.DistanceSquared(precision!.Enclosure(point)) > 0 || Locate(point, target, state.Work) != Location.Inside) return;
            state.MaterialOverlap = true;
            foreach (var p in source.Points) { state.Work.Charge(); state.Overlap = state.Overlap.Union(precision.Enclosure(p)); }
            return;
        }
        foreach (var point in source.Points)
        {
            state.Work.Charge();
            var bounds = precision!.Enclosure(point);
            if (target.Root!.Bounds.DistanceSquared(bounds) > 0 || Locate(point, target, state.Work) != Location.Inside) continue;
            state.MaterialOverlap = true; state.Overlap = state.Overlap.Union(bounds);
            if (!state.SegmentCovered(point, point)) state.ClearanceFailure = true;
        }
    }
    private enum Location { Outside, Boundary, Inside }
    private static Location Locate(Vec3 p, Solid solid, GeometryWork work)
    {
        bool Boundary(Node node)
        {
            work.Charge(); if (node.Bounds.DistanceSquared(precision!.Enclosure(p)) > 0) return false;
            if (!node.Leaf) return Boundary(node.Left!) || Boundary(node.Right!);
            for (var i = node.Start; i < node.Start + node.Count; i++)
            {
                var t = solid.Triangle(solid.Order[i]);
                if (Orient(t.A, t.B, t.C, p, work) == 0 && InTriangle(p, t, work)) return true;
            }
            return false;
        }
        if (Boundary(solid.Root!)) return Location.Boundary;
        // Exact line-side tests retry deterministic rays when a ray meets an edge or vertex.
        // Exhaustion fails closed rather than guessing parity.
        for (var attempt = 1; attempt <= 32; attempt++)
        {
            var direction = new Vec3 { X = 1, Y = attempt, Z = attempt * attempt + 1 };
            var exactQ = EV.Of(p) + EV.Raw(direction); var q = exactQ.Approximate(); precision!.Register(q, exactQ, work);
            var count = 0; var ambiguous = false;
            void Ray(Node node)
            {
                if (ambiguous) return; work.Charge(); if (!RayBounds(p, direction, node.Bounds)) return;
                if (!node.Leaf) { Ray(node.Left!); Ray(node.Right!); return; }
                for (var i = node.Start; i < node.Start + node.Count; i++)
                {
                    var t = solid.Triangle(solid.Order[i]); var plane = Orient(t.A, t.B, t.C, p, work);
                    var forward = NormalDirection(t, direction, work);
                    if (plane == 0 || forward == 0 || plane != forward) continue;
                    var a = Orient(p, q, t.A, t.B, work); var b = Orient(p, q, t.B, t.C, work); var c = Orient(p, q, t.C, t.A, work);
                    if (!(a >= 0 && b >= 0 && c >= 0 || a <= 0 && b <= 0 && c <= 0)) continue;
                    if (a == 0 || b == 0 || c == 0) { ambiguous = true; return; }
                    count++;
                }
            }
            try { Ray(solid.Root!); }
            finally { precision.Remove(q); }
            if (!ambiguous) return (count & 1) == 0 ? Location.Outside : Location.Inside;
        }
        throw new AssemblyGeometryException("Solid containment could not resolve a nondegenerate ray within the finite 32-ray limit.");
    }
    private static bool RayBounds(Vec3 p, Vec3 direction, Bounds bounds)
    {
        var lo = 0d; var hi = double.PositiveInfinity;
        var originBounds = precision!.Enclosure(p);
        for (var axis = 0; axis < 3; axis++)
        {
            var d = Coordinate(direction, axis); var originLow = Coordinate(originBounds.Min, axis); var originHigh = Coordinate(originBounds.Max, axis);
            if (d == 0) { if (originHigh < Coordinate(bounds.Min, axis) || originLow > Coordinate(bounds.Max, axis)) return false; continue; }
            // Outward rounding bounds subtraction and division; broad phase cannot remove a true crossing.
            var a = Coordinate(bounds.Min, axis) - originHigh; var b = Coordinate(bounds.Max, axis) - originLow;
            var low = d > 0 ? Math.BitDecrement(Math.BitDecrement(a) / d) : Math.BitDecrement(Math.BitIncrement(b) / d);
            var high = d > 0 ? Math.BitIncrement(Math.BitIncrement(b) / d) : Math.BitIncrement(Math.BitDecrement(a) / d);
            lo = Math.Max(lo, low); hi = Math.Min(hi, high); if (lo > hi) return false;
        }
        return true;
    }

    private sealed class IntersectionPoints : List<Vec3>
    {
        public Bounds Enclosure = Bounds.Empty;
        public readonly List<(Fraction X, Fraction Y, Fraction Z)> Exact = new(24);
        public bool HasSpan, HasRoundedPoint;
        public void AddExact(Fraction x, Fraction y, Fraction z)
        {
            if (Exact.Count > 0) HasSpan |= x.CompareTo(Exact[0].X) != 0 || y.CompareTo(Exact[0].Y) != 0 || z.CompareTo(Exact[0].Z) != 0;
            Exact.Add((x, y, z));
        }
    }
    private static IntersectionPoints Intersections((Vec3 A, Vec3 B, Vec3 C) a, (Vec3 A, Vec3 B, Vec3 C) b, GeometryWork work, out bool coplanar)
    {
        work.Charge(64); var result = new IntersectionPoints(); var av = new[] { a.A, a.B, a.C }; var bv = new[] { b.A, b.B, b.C };
        var sa = av.Select(p => Orient(b.A, b.B, b.C, p, work)).ToArray(); var sb = bv.Select(p => Orient(a.A, a.B, a.C, p, work)).ToArray();
        coplanar = sa.All(s => s == 0) && sb.All(s => s == 0);
        if (sa.All(s => s > 0) || sa.All(s => s < 0) || sb.All(s => s > 0) || sb.All(s => s < 0)) return result;
        void AddPoint(Vec3 p) { work.Charge(16); if (!result.Contains(p)) result.Add(p); result.Enclosure = result.Enclosure.Union(precision!.Enclosure(p)); var exact = EV.Of(p); result.AddExact(new(exact.X, D(1)), new(exact.Y, D(1)), new(exact.Z, D(1))); }
        void AddCut(Vec3 p, Vec3 q, Fraction t)
        {
            work.Charge(256); var ep = EV.Of(p); var eq = EV.Of(q); var x = t.Interpolate(ep.X, eq.X); var y = t.Interpolate(ep.Y, eq.Y); var z = t.Interpolate(ep.Z, eq.Z);
            var point = new Vec3 { X = x.Value, Y = y.Value, Z = z.Value }; if (!Finite(point)) throw new AssemblyGeometryException("Nonfinite exact triangle intersection.");
            var sx = x.CompareTo(new(D(point.X), D(1))); var sy = y.CompareTo(new(D(point.Y), D(1))); var sz = z.CompareTo(new(D(point.Z), D(1)));
            result.HasRoundedPoint |= sx != 0 || sy != 0 || sz != 0;
            double Bound(Fraction coordinate, double value, bool lower)
            {
                for (var i = 0; i < 8; i++)
                {
                    work.Charge(16); var sign = coordinate.CompareTo(new(D(value), D(1)));
                    if (lower ? sign >= 0 : sign <= 0) return value;
                    value = lower ? Math.BitDecrement(value) : Math.BitIncrement(value);
                }
                throw new AssemblyGeometryException("Exact intersection rounding exceeds the bounded eight-ULP enclosure.");
            }
            var low = new Vec3 { X = Bound(x, point.X, true), Y = Bound(y, point.Y, true), Z = Bound(z, point.Z, true) };
            var high = new Vec3 { X = Bound(x, point.X, false), Y = Bound(y, point.Y, false), Z = Bound(z, point.Z, false) };
            if (!result.Contains(point)) result.Add(point); result.Enclosure = result.Enclosure.Add(low).Add(high); result.AddExact(x, y, z);
        }
        if (coplanar)
        {
            foreach (var p in av) if (InTriangle(p, b, work)) AddPoint(p);
            foreach (var p in bv) if (InTriangle(p, a, work)) AddPoint(p);
            var drop = Projection(a, work);
            for (var i = 0; i < 3; i++) for (var j = 0; j < 3; j++)
            {
                var p = av[i]; var q = av[(i + 1) % 3]; var r = bv[j]; var s = bv[(j + 1) % 3];
                var pr = Orient2(p, q, r, drop, work); var ps = Orient2(p, q, s, drop, work); var rp = Orient2(r, s, p, drop, work); var rq = Orient2(r, s, q, drop, work);
                if (pr * ps < 0 && rp * rq < 0)
                {
                    work.Charge(64); var x = ExactOrient2(r, s, p, drop); var y = ExactOrient2(r, s, q, drop); AddCut(p, q, new(x, x - y));
                }
                if (pr == 0 && OnSegment(p, q, r, work)) AddPoint(r); if (ps == 0 && OnSegment(p, q, s, work)) AddPoint(s);
                if (rp == 0 && OnSegment(r, s, p, work)) AddPoint(p); if (rq == 0 && OnSegment(r, s, q, work)) AddPoint(q);
            }
        }
        else
        {
            void Edges(Vec3[] vertices, int[] signs, (Vec3 A, Vec3 B, Vec3 C) triangle)
            {
                for (var i = 0; i < 3; i++)
                {
                    var p = vertices[i]; var q = vertices[(i + 1) % 3]; var sp = signs[i]; var sq = signs[(i + 1) % 3];
                    if (sp == 0 && InTriangle(p, triangle, work)) AddPoint(p);
                    if (sp * sq >= 0) continue;
                    var x = Orient(p, q, triangle.A, triangle.B, work); var y = Orient(p, q, triangle.B, triangle.C, work); var z = Orient(p, q, triangle.C, triangle.A, work);
                    if (!(x >= 0 && y >= 0 && z >= 0 || x <= 0 && y <= 0 && z <= 0)) continue;
                    work.Charge(128); var dp = ExactOrient(triangle.A, triangle.B, triangle.C, p); var dq = ExactOrient(triangle.A, triangle.B, triangle.C, q);
                    AddCut(p, q, new(dp, dp - dq));
                }
            }
            Edges(av, sa, b); Edges(bv, sb, a);
        }
        return result;
    }
    private static bool HasArea(IntersectionPoints points, (Vec3 A, Vec3 B, Vec3 C) triangle, GeometryWork work)
    {
        var drop = Projection(triangle, work);
        (Fraction X, Fraction Y) ProjectionOf((Fraction X, Fraction Y, Fraction Z) p) => drop == 0 ? (p.Y, p.Z) : drop == 1 ? (p.X, p.Z) : (p.X, p.Y);
        for (var i = 1; i < points.Exact.Count; i++) for (var j = i + 1; j < points.Exact.Count; j++)
        {
            work.Charge(128); var a = ProjectionOf(points.Exact[0]); var b = ProjectionOf(points.Exact[i]); var c = ProjectionOf(points.Exact[j]);
            if (((a.X - c.X) * (b.Y - c.Y) - (a.Y - c.Y) * (b.X - c.X)).CompareTo(Fraction.Zero) != 0) return true;
        }
        return false;
    }
    private static int Projection((Vec3 A, Vec3 B, Vec3 C) t, GeometryWork work)
    {
        var n = Cross(Subtract(t.B, t.A), Subtract(t.C, t.A));
        var axis = Math.Abs(n.X) >= Math.Abs(n.Y) && Math.Abs(n.X) >= Math.Abs(n.Z) ? 0 : Math.Abs(n.Y) >= Math.Abs(n.Z) ? 1 : 2;
        if (Orient2(t.A, t.B, t.C, axis, work) != 0) return axis;
        for (var i = 0; i < 3; i++) if (Orient2(t.A, t.B, t.C, i, work) != 0) return i;
        throw new AssemblyGeometryException("Zero-area triangle cannot be projected.");
    }
    private static bool InTriangle(Vec3 p, (Vec3 A, Vec3 B, Vec3 C) t, GeometryWork work)
    {
        if (ReferenceEquals(p, t.A) || ReferenceEquals(p, t.B) || ReferenceEquals(p, t.C)) { work.Charge(); return true; }
        var drop = Projection(t, work); var a = Orient2(t.A, t.B, p, drop, work); var b = Orient2(t.B, t.C, p, drop, work); var c = Orient2(t.C, t.A, p, drop, work);
        return a >= 0 && b >= 0 && c >= 0 || a <= 0 && b <= 0 && c <= 0;
    }
    private static bool Collinear(Vec3 a, Vec3 b, Vec3 c, GeometryWork work) => Orient2(a, b, c, 0, work) == 0 && Orient2(a, b, c, 1, work) == 0 && Orient2(a, b, c, 2, work) == 0;
    private static bool OnSegment(Vec3 a, Vec3 b, Vec3 p, GeometryWork work)
    {
        if (ReferenceEquals(p, a) || ReferenceEquals(p, b)) { work.Charge(); return true; }
        return Bounds.Empty.Add(a).Add(b).Contains(p) && Collinear(a, b, p, work);
    }

    private static double TriangleDistance((Vec3 A, Vec3 B, Vec3 C) a, (Vec3 A, Vec3 B, Vec3 C) b, GeometryWork work, out Vec3 pa, out Vec3 pb, out Fraction exactDistance)
    {
        work.Charge(4096); var av = new[] { a.A, a.B, a.C }; var bv = new[] { b.A, b.B, b.C };
        Fraction? best = null; var first = a.A; var second = b.A;
        void Candidate(Fraction squared, Vec3 p, Vec3 q)
        {
            if (squared.CompareTo(Fraction.Zero) < 0) throw new AssemblyGeometryException("Invalid exact squared distance.");
            if (best is null || squared.CompareTo(best.Value) < 0) { best = squared; first = p; second = q; }
        }
        void Vertex(Vec3 p, (Vec3 A, Vec3 B, Vec3 C) triangle, bool reverse)
        {
            var vertices = new[] { triangle.A, triangle.B, triangle.C };
            void AddCandidate(Fraction squared, Vec3 point) { if (reverse) Candidate(squared, point, p); else Candidate(squared, p, point); }
            foreach (var v in vertices) { var delta = EV.Of(p) - EV.Of(v); AddCandidate(new(EV.Dot(delta, delta), D(1)), v); }
            for (var i = 0; i < 3; i++)
            {
                var x = vertices[i]; var y = vertices[(i + 1) % 3]; var edge = EV.Of(y) - EV.Of(x); var delta = EV.Of(p) - EV.Of(x); var length = EV.Dot(edge, edge); var projection = EV.Dot(delta, edge);
                if (projection.Sign <= 0 || (projection - length).Sign >= 0) continue;
                AddCandidate(new(EV.Dot(delta, delta) * length - projection * projection, length), Interpolate(x, y, new Fraction(projection, length).Value));
            }
            var ab = EV.Of(triangle.B) - EV.Of(triangle.A); var ac = EV.Of(triangle.C) - EV.Of(triangle.A); var ap = EV.Of(p) - EV.Of(triangle.A);
            var aa = EV.Dot(ab, ab); var cc = EV.Dot(ac, ac); var bb = EV.Dot(ab, ac); var abp = EV.Dot(ab, ap); var acp = EV.Dot(ac, ap); var det = aa * cc - bb * bb;
            var vNum = cc * abp - bb * acp; var wNum = aa * acp - bb * abp;
            if (det.Sign > 0 && vNum.Sign >= 0 && wNum.Sign >= 0 && (det - vNum - wNum).Sign >= 0)
            {
                var normal = EV.Cross(ab, ac); var height = EV.Dot(normal, ap); var norm = EV.Dot(normal, normal);
                var v = new Fraction(vNum, det).Value; var w = new Fraction(wNum, det).Value;
                var projected = Add(triangle.A, Add(Scale(Subtract(triangle.B, triangle.A), v), Scale(Subtract(triangle.C, triangle.A), w)));
                AddCandidate(new(height * height, norm), projected);
            }
        }
        foreach (var p in av) Vertex(p, b, false); foreach (var p in bv) Vertex(p, a, true);
        for (var i = 0; i < 3; i++) for (var j = 0; j < 3; j++)
        {
            var p = av[i]; var q = bv[j]; var u = EV.Of(av[(i + 1) % 3]) - EV.Of(p); var v = EV.Of(bv[(j + 1) % 3]) - EV.Of(q); var delta = EV.Of(p) - EV.Of(q);
            var aa = EV.Dot(u, u); var bb = EV.Dot(u, v); var cc = EV.Dot(v, v); var dd = EV.Dot(u, delta); var ee = EV.Dot(v, delta); var det = aa * cc - bb * bb;
            if (det.Sign <= 0) continue;
            var sn = bb * ee - cc * dd; var tn = aa * ee - bb * dd;
            if (sn.Sign < 0 || tn.Sign < 0 || (sn - det).Sign > 0 || (tn - det).Sign > 0) continue;
            Candidate(new(EV.Dot(delta, delta) * det + dd * sn - ee * tn, det), Interpolate(p, av[(i + 1) % 3], new Fraction(sn, det).Value), Interpolate(q, bv[(j + 1) % 3], new Fraction(tn, det).Value));
        }
        exactDistance = best ?? throw new AssemblyGeometryException("Actual triangle has no finite distance candidate.");
        pa = first; pb = second; return exactDistance.Value;
    }
    private readonly record struct Interval(double Low, double High)
    {
        public int Sign => Low > 0 ? 1 : High < 0 ? -1 : 0;
        public static Interval operator +(Interval a, Interval b) => new(Math.BitDecrement(a.Low + b.Low), Math.BitIncrement(a.High + b.High));
        public static Interval operator -(Interval a, Interval b) => new(Math.BitDecrement(a.Low - b.High), Math.BitIncrement(a.High - b.Low));
        public static Interval operator *(Interval a, Interval b)
        {
            var x = a.Low * b.Low; var y = a.Low * b.High; var z = a.High * b.Low; var w = a.High * b.High;
            return new(Math.BitDecrement(Math.Min(Math.Min(x, y), Math.Min(z, w))), Math.BitIncrement(Math.Max(Math.Max(x, y), Math.Max(z, w))));
        }
    }
    private readonly record struct IP(Interval X, Interval Y, Interval Z)
    {
        public static IP Of(Vec3 p) { var b = precision!.Enclosure(p); return new(new(b.Min.X, b.Max.X), new(b.Min.Y, b.Max.Y), new(b.Min.Z, b.Max.Z)); }
        public static IP operator -(IP a, IP b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Interval Dot(IP a, IP b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public static IP Cross(IP a, IP b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    }
    private readonly record struct EV(Dyadic X, Dyadic Y, Dyadic Z)
    {
        public static EV Raw(Vec3 p) => new(D(p.X), D(p.Y), D(p.Z));
        public static EV Of(Vec3 p) => precision?.Exact(p) ?? Raw(p);
        public Vec3 Approximate() => new() { X = new Fraction(X, D(1)).Value, Y = new Fraction(Y, D(1)).Value, Z = new Fraction(Z, D(1)).Value };
        public static EV operator +(EV a, EV b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static EV operator -(EV a, EV b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static EV operator *(EV a, Dyadic scale) => new(a.X * scale, a.Y * scale, a.Z * scale);
        public static Dyadic Dot(EV a, EV b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public static EV Cross(EV a, EV b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    }

    // Filter bounds are arithmetic error bounds, not geometric epsilons. Uncertain signs
    // use the exact binary values supplied by the native evaluator; no tolerance widens a solid.
    private static int Orient(Vec3 a, Vec3 b, Vec3 c, Vec3 d, GeometryWork work)
    {
        // Shared topology gives exact identity even when rounded display coordinates have
        // residuals. Distinct objects with equal rounded coordinates do not get this shortcut.
        if (ReferenceEquals(a, b) || ReferenceEquals(a, c) || ReferenceEquals(a, d) || ReferenceEquals(b, c) || ReferenceEquals(b, d) || ReferenceEquals(c, d)) { work.Charge(); return 0; }
        var mapped = precision!.Contains(a) || precision.Contains(b) || precision.Contains(c) || precision.Contains(d);
        if (!mapped && (a.X == b.X && a.X == c.X && a.X == d.X || a.Y == b.Y && a.Y == c.Y && a.Y == d.Y || a.Z == b.Z && a.Z == c.Z && a.Z == d.Z)) { work.Charge(); return 0; }
        if (mapped)
        {
            work.Charge(8); var origin = IP.Of(d); var sign = IP.Dot(IP.Of(a) - origin, IP.Cross(IP.Of(b) - origin, IP.Of(c) - origin)).Sign;
            if (sign != 0) return sign; work.Charge(64); return ExactOrient(a, b, c, d).Sign;
        }
        work.Charge(); var x = Subtract(a, d); var y = Subtract(b, d); var z = Subtract(c, d);
        var det = x.X * (y.Y * z.Z - y.Z * z.Y) - x.Y * (y.X * z.Z - y.Z * z.X) + x.Z * (y.X * z.Y - y.Y * z.X);
        var permanent = Math.Abs(x.X) * (Math.Abs(y.Y * z.Z) + Math.Abs(y.Z * z.Y)) + Math.Abs(x.Y) * (Math.Abs(y.X * z.Z) + Math.Abs(y.Z * z.X)) + Math.Abs(x.Z) * (Math.Abs(y.X * z.Y) + Math.Abs(y.Y * z.X));
        if (double.IsFinite(det) && permanent > Math.ScaleB(1d, -900) && Math.Abs(det) > permanent * 1e-14) return Math.Sign(det);
        work.Charge(64); return ExactOrient(a, b, c, d).Sign;
    }
    private static int NormalDirection((Vec3 A, Vec3 B, Vec3 C) t, Vec3 d, GeometryWork work)
    {
        if (precision!.Contains(t.A) || precision.Contains(t.B) || precision.Contains(t.C))
        {
            work.Charge(8); var origin = IP.Of(t.A); var sign = IP.Dot(IP.Cross(IP.Of(t.B) - origin, IP.Of(t.C) - origin), IP.Of(d)).Sign;
            if (sign != 0) return sign; work.Charge(64); return EV.Dot(EV.Cross(EV.Of(t.B) - EV.Of(t.A), EV.Of(t.C) - EV.Of(t.A)), EV.Of(d)).Sign;
        }
        work.Charge(); var a = Subtract(t.B, t.A); var b = Subtract(t.C, t.A);
        var det = Dot(Cross(a, b), d);
        var permanent = Math.Abs(d.X) * (Math.Abs(a.Y * b.Z) + Math.Abs(a.Z * b.Y)) + Math.Abs(d.Y) * (Math.Abs(a.X * b.Z) + Math.Abs(a.Z * b.X)) + Math.Abs(d.Z) * (Math.Abs(a.X * b.Y) + Math.Abs(a.Y * b.X));
        if (double.IsFinite(det) && permanent > Math.ScaleB(1d, -900) && Math.Abs(det) > permanent * 1e-14) return Math.Sign(det);
        work.Charge(64);
        var ax = D(t.B.X) - D(t.A.X); var ay = D(t.B.Y) - D(t.A.Y); var az = D(t.B.Z) - D(t.A.Z);
        var bx = D(t.C.X) - D(t.A.X); var by = D(t.C.Y) - D(t.A.Y); var bz = D(t.C.Z) - D(t.A.Z);
        return ((ay * bz - az * by) * D(d.X) + (az * bx - ax * bz) * D(d.Y) + (ax * by - ay * bx) * D(d.Z)).Sign;
    }
    private static (double X, double Y) Project(Vec3 p, int drop) => drop == 0 ? (p.Y, p.Z) : drop == 1 ? (p.X, p.Z) : (p.X, p.Y);
    private static int Orient2(Vec3 a, Vec3 b, Vec3 c, int drop, GeometryWork work)
    {
        if (ReferenceEquals(a, b) || ReferenceEquals(a, c) || ReferenceEquals(b, c)) { work.Charge(); return 0; }
        var mapped = precision!.Contains(a) || precision.Contains(b) || precision.Contains(c);
        if (!mapped)
        {
            var pa = Project(a, drop); var pb = Project(b, drop); var pc = Project(c, drop);
            if (pa == pb || pa == pc || pb == pc || pa.X == pb.X && pa.X == pc.X || pa.Y == pb.Y && pa.Y == pc.Y) { work.Charge(); return 0; }
        }
        if (mapped)
        {
            work.Charge(4);
            (Interval X, Interval Y) P(Vec3 point) { var p = IP.Of(point); return drop == 0 ? (p.Y, p.Z) : drop == 1 ? (p.X, p.Z) : (p.X, p.Y); }
            var ix = P(a); var iy = P(b); var iz = P(c); var sign = ((ix.X - iz.X) * (iy.Y - iz.Y) - (ix.Y - iz.Y) * (iy.X - iz.X)).Sign;
            if (sign != 0) return sign; work.Charge(32); return ExactOrient2(a, b, c, drop).Sign;
        }
        work.Charge(); var x = Project(a, drop); var y = Project(b, drop); var z = Project(c, drop);
        var left = (x.X - z.X) * (y.Y - z.Y); var right = (x.Y - z.Y) * (y.X - z.X); var det = left - right;
        if (double.IsFinite(det) && Math.Abs(left) + Math.Abs(right) > Math.ScaleB(1d, -900) && Math.Abs(det) > (Math.Abs(left) + Math.Abs(right)) * 1e-14) return Math.Sign(det);
        work.Charge(32); return ExactOrient2(a, b, c, drop).Sign;
    }
    private static Dyadic ExactOrient2(Vec3 a, Vec3 b, Vec3 c, int drop)
    {
        (Dyadic X, Dyadic Y) P(Vec3 p) { var value = EV.Of(p); return drop == 0 ? (value.Y, value.Z) : drop == 1 ? (value.X, value.Z) : (value.X, value.Y); }
        var x = P(a); var y = P(b); var z = P(c);
        return (x.X - z.X) * (y.Y - z.Y) - (x.Y - z.Y) * (y.X - z.X);
    }
    private static Dyadic ExactOrient(Vec3 a, Vec3 b, Vec3 c, Vec3 d)
    {
        var x = EV.Of(a) - EV.Of(d); var y = EV.Of(b) - EV.Of(d); var z = EV.Of(c) - EV.Of(d);
        return EV.Dot(x, EV.Cross(y, z));
    }
    private static Dyadic D(double value)
    {
        if (!double.IsFinite(value)) throw new AssemblyGeometryException("Nonfinite exact predicate input.");
        var bits = BitConverter.DoubleToInt64Bits(value); var exponent = (int)((bits >> 52) & 2047); var fraction = bits & 0x000fffffffffffffL;
        var mantissa = exponent == 0 ? fraction : fraction | 0x0010000000000000L;
        return new(bits < 0 ? -mantissa : mantissa, exponent == 0 ? -1074 : exponent - 1075);
    }
    private readonly record struct Dyadic(BigInteger Numerator, int Exponent)
    {
        private const int MaximumBits = 16_384;
        public static Dyadic Zero => new(BigInteger.Zero, 0);
        public int Sign => Numerator.Sign;
        private static void Bound(long bits) { if (bits > MaximumBits) throw new AssemblyGeometryException("Exact geometry predicate exceeds its finite 16384-bit arithmetic limit."); }
        public static Dyadic operator -(Dyadic a) => new(-a.Numerator, a.Exponent);
        public static Dyadic operator +(Dyadic a, Dyadic b)
        {
            if (a.Sign == 0) return b; if (b.Sign == 0) return a;
            var exponent = Math.Min(a.Exponent, b.Exponent); var x = a.Exponent - exponent; var y = b.Exponent - exponent;
            Bound(Math.Max(a.Numerator.GetBitLength() + x, b.Numerator.GetBitLength() + y) + 1);
            return new((a.Numerator << x) + (b.Numerator << y), exponent);
        }
        public static Dyadic operator -(Dyadic a, Dyadic b) => a + -b;
        public static Dyadic operator *(Dyadic a, Dyadic b)
        {
            if (a.Sign == 0 || b.Sign == 0) return Zero;
            Bound(a.Numerator.GetBitLength() + b.Numerator.GetBitLength()); return new(a.Numerator * b.Numerator, checked(a.Exponent + b.Exponent));
        }
    }
    private readonly struct Fraction : IComparable<Fraction>
    {
        private readonly Dyadic numerator, denominator;
        public Fraction(Dyadic n, Dyadic d)
        {
            if (d.Sign == 0) throw new AssemblyGeometryException("Exact geometry division has a zero denominator.");
            numerator = d.Sign < 0 ? -n : n; denominator = d.Sign < 0 ? -d : d;
        }
        public static Fraction Zero => new(Dyadic.Zero, D(1)); public static Fraction One => new(D(1), D(1));
        public int CompareTo(Fraction other) => (numerator * other.denominator - other.numerator * denominator).Sign;
        public Fraction Interpolate(double a, double b) => new(D(a) * (denominator - numerator) + D(b) * numerator, denominator);
        public Fraction Interpolate(Dyadic a, Dyadic b) => new(a * (denominator - numerator) + b * numerator, denominator);
        public static Fraction operator -(Fraction a, Fraction b) => new(a.numerator * b.denominator - b.numerator * a.denominator, a.denominator * b.denominator);
        public static Fraction operator *(Fraction a, Fraction b) => new(a.numerator * b.numerator, a.denominator * b.denominator);
        public double Value
        {
            get
            {
                if (numerator.Sign == 0) return 0;
                var n = BigInteger.Abs(numerator.Numerator); var d = denominator.Numerator;
                var nShift = Math.Max(0, n.GetBitLength() - 54); var dShift = Math.Max(0, d.GetBitLength() - 54);
                var exponent = numerator.Exponent - denominator.Exponent + nShift - dShift;
                if (exponent is < -1074 or > 1024) return exponent < 0 ? 0 : double.PositiveInfinity;
                return numerator.Sign * Math.ScaleB((double)(n >> (int)nShift) / (double)(d >> (int)dShift), (int)exponent);
            }
        }
        public double UpperValue()
        {
            var value = Value; if (double.IsPositiveInfinity(value)) return value;
            for (var i = 0; i < 8; i++) { if (new Fraction(D(value), D(1)).CompareTo(this) >= 0) return value; value = Math.BitIncrement(value); }
            throw new AssemblyGeometryException("Exact distance could not be rounded upward within eight ULPs.");
        }
        public double SqrtApproximate()
        {
            if (numerator.Sign == 0) return 0;
            if (numerator.Sign < 0) throw new AssemblyGeometryException("Negative squared distance.");
            var n = numerator.Numerator; var d = denominator.Numerator; var ns = Math.Max(0, n.GetBitLength() - 54); var ds = Math.Max(0, d.GetBitLength() - 54);
            var exponent = numerator.Exponent - denominator.Exponent + ns - ds; var ratio = (double)(n >> (int)ns) / (double)(d >> (int)ds);
            if ((exponent & 1) != 0) { ratio *= 2; exponent--; }
            return Math.ScaleB(Math.Sqrt(ratio), checked((int)(exponent / 2)));
        }
        public double SqrtLowerBound()
        {
            var value = SqrtApproximate();
            for (var i = 0; i < 8; i++) { var v = D(value); if (new Fraction(v * v, D(1)).CompareTo(this) <= 0) return value; value = Math.BitDecrement(value); }
            throw new AssemblyGeometryException("Squared distance could not be conservatively rounded within eight ULPs.");
        }
    }
}
