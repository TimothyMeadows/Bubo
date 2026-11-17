using System.Buffers.Binary;
using Bubo.Splats.Application;
using Bubo.Splats.Domain;

namespace Bubo.Splats.Tests;

internal static class InspectionTests
{
    internal static void Run()
    {
        var bytes = Fixtures.Ply();
        var budget = Fixtures.Budgets();
        var result = Inspect(bytes);
        Check.That(result.SchemaVersion == "bubo-splat-inspection-result/v1" && result.Profile == "triposplat-ply-sh0/v1"
            && result.GaussianCount == 2 && result.SphericalHarmonicsDegree == 0, "binary SH0 input reports its exact representation");
        Check.That(result.Minimum.SequenceEqual([-4d, 2d, -6d]) && result.Maximum.SequenceEqual([1d, 5d, 3d]),
            "signed source-local coordinates produce actual bounds");
        Check.That(result.FileBytes == bytes.Length && result.DecodedBytes == 2 * 17 * 4 && result.Sha256 == Fixtures.Hash(bytes),
            "inspection reports exact bytes, decoded scalar footprint and full-file identity");
        Check.That(result.TechnicalOnly && result.Coordinates.Contains("calibration required", StringComparison.Ordinal)
            && result.Scale == "natural-log standard-deviation" && result.Opacity == "logit"
            && result.Rotation == "wxyz; normalized by receiver" && result.Color == "SH0 RGB coefficients",
            "inspection declares stored encodings and requires scale calibration without artistic acceptance");

        var reordered = Inspect(Fixtures.Ply(properties: Fixtures.Properties.Reverse().ToArray()));
        Check.That(reordered.Minimum.SequenceEqual(result.Minimum) && reordered.Maximum.SequenceEqual(result.Maximum),
            "property order does not change Gaussian coordinates");
        Check.That(Inspect(Fixtures.Ply(newline: "\r\n")).GaussianCount == 2, "CRLF headers are supported");
        Check.That(Inspect(Fixtures.Ply(rewriteHeader: h => h.Replace("property float ", "property float32 ", StringComparison.Ordinal))).GaussianCount == 2,
            "float32 scalar spelling is supported");
        Check.That(Inspect(Fixtures.Ply(rewriteHeader: h => h.Replace("end_header", "comment owned fixture\nend_header", StringComparison.Ordinal))).GaussianCount == 2,
            "bounded printable comments do not alter the payload");

        Reject(Fixtures.Ply(properties: [..Fixtures.Properties, "f_rest_0"]), "higher SH properties require a different explicit profile");
        Reject(Fixtures.Ply(properties: [..Fixtures.Properties, "red"]), "unregistered extra scalar is rejected");
        Reject(Fixtures.Ply(properties: [..Fixtures.Properties, "x"]), "duplicate scalar declaration is rejected");
        Reject(Fixtures.Ply(properties: Fixtures.Properties.Where(p => p != "rot_3").ToArray()), "missing required quaternion field is rejected");
        foreach (var (name, transform) in new (string, Func<string, string>)[]
        {
            ("ASCII", h => h.Replace("binary_little_endian", "ascii", StringComparison.Ordinal)),
            ("big-endian", h => h.Replace("binary_little_endian", "binary_big_endian", StringComparison.Ordinal)),
            ("unsupported version", h => h.Replace("1.0", "2.0", StringComparison.Ordinal)),
            ("list property", h => h.Replace("property float x", "property list uchar float x", StringComparison.Ordinal)),
            ("face element", h => h.Replace("end_header", "element face 1\nproperty list uchar int vertex_indices\nend_header", StringComparison.Ordinal)),
            ("unknown scalar type", h => h.Replace("property float x", "property double x", StringComparison.Ordinal)),
            ("repeated vertex element", h => h.Replace("end_header", "element vertex 2\nend_header", StringComparison.Ordinal)),
            ("property before element", h => h.Replace("element vertex 2\nproperty float x", "property float x\nelement vertex 2", StringComparison.Ordinal)),
            ("negative count", h => h.Replace("element vertex 2", "element vertex -1", StringComparison.Ordinal)),
            ("zero count", h => h.Replace("element vertex 2", "element vertex 0", StringComparison.Ordinal)),
            ("overflow count", h => h.Replace("element vertex 2", "element vertex 9223372036854775807", StringComparison.Ordinal)),
            ("count-payload mismatch", h => h.Replace("element vertex 2", "element vertex 3", StringComparison.Ordinal)),
            ("control in header", h => h.Replace("end_header", "comment bad\tvalue\nend_header", StringComparison.Ordinal)),
            ("embedded CR", h => h.Replace("end_header", "comment bad\rvalue\nend_header", StringComparison.Ordinal))
        }) Reject(Fixtures.Ply(rewriteHeader: transform), name + " header is rejected");
        Reject(bytes[..^1], "truncated payload is rejected even with its own correct hash");
        Reject([..bytes, 0], "trailing payload is rejected even with its own correct hash");
        Reject([], "empty input is rejected");

        var changed = bytes.ToArray();
        BinaryPrimitives.WriteSingleLittleEndian(changed.AsSpan(Fixtures.HeaderBytes(bytes), 4), .75f);
        Check.Throws<InvalidOperationException>(() => Inspect(changed, expectedHash: Fixtures.Hash(bytes)), "changed valid payload cannot retain old hash");
        foreach (var hash in new string?[] { null, "", new('A', 64), new('g', 64), new('a', 63) })
            Check.Throws<InvalidOperationException>(() => Inspect(bytes, expectedHash: hash!, useDefaultHash: false), "invalid expected identity is rejected");

        var header = Fixtures.HeaderBytes(bytes);
        Check.That(Inspect(bytes, budget with { MaxInputBytes = bytes.Length, MaxDecodedBytes = 136, MaxHeaderBytes = header }).GaussianCount == 2,
            "exact byte, decoded and header budgets are inclusive");
        Reject(bytes, "caller count bound is enforced", budget with { MaxGaussians = 1 });
        Reject(bytes, "caller header bound is enforced", budget with { MaxHeaderBytes = header - 1 });
        Reject(bytes, "caller file byte bound is enforced", budget with { MaxInputBytes = bytes.Length - 1 });
        Reject(bytes, "caller decoded byte bound is enforced", budget with { MaxDecodedBytes = 135 });
        Reject(Fixtures.Ply(rewriteHeader: h => h.Replace("end_header", "comment " + new string('a', 4096) + "\nend_header", StringComparison.Ordinal)),
            "an oversized header is rejected before unbounded accumulation");

        for (var i = 0; i < Fixtures.Properties.Length; i++)
        {
            var row = Fixtures.Row(); row[i] = float.NaN;
            Reject(Fixtures.Ply(rows: [row]), "nonfinite " + Fixtures.Properties[i] + " is rejected");
        }
        foreach (var (field, value, name) in new (int, float, string)[]
        {
            (0, float.PositiveInfinity, "infinite position"), (6, float.NegativeInfinity, "infinite colour"),
            (0, 1_000_001, "excessive source-local coordinate"), (6, 10_001, "excessive SH coefficient"),
            (9, 80.01f, "high opacity logit"), (9, -80.01f, "low opacity logit"),
            (10, 30.01f, "high log-scale"), (11, -30.01f, "low log-scale"),
            (13, 10_000_000, "unbounded quaternion")
        })
        {
            var row = Fixtures.Row(); row[field] = value;
            Reject(Fixtures.Ply(rows: [row]), name + " is rejected");
        }
        var zeroRotation = Fixtures.Row(); Array.Clear(zeroRotation, 13, 4);
        Reject(Fixtures.Ply(rows: [zeroRotation]), "zero quaternion is rejected");
        var tinyRotation = Fixtures.Row(); tinyRotation[13] = 1e-15f;
        Reject(Fixtures.Ply(rows: [tinyRotation]), "numerically degenerate quaternion is rejected");
        var limits = Fixtures.Row(1_000_000); limits[6] = -10_000; limits[9] = -80; limits[10] = -30; limits[11] = 30;
        Check.That(Inspect(Fixtures.Ply(rows: [limits])).GaussianCount == 1, "declared finite numeric boundaries are inclusive");
        var nonunitRotation = Fixtures.Row(); nonunitRotation[13] = 2;
        Check.That(Inspect(Fixtures.Ply(rows: [nonunitRotation])).GaussianCount == 1, "bounded nonunit stored rotation retains receiver-normalization semantics");

        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            Check.Throws<OperationCanceledException>(() => Inspect(bytes, cancellation: cancelled.Token), "pre-cancelled inspection stops");
        }
        using (var cancelled = new CancellationTokenSource())
        using (var stream = new CancelOnReadStream(bytes, cancelled, header + 68))
            Check.Throws<OperationCanceledException>(() => GaussianInspection.Inspect(stream, budget, Fixtures.Hash(bytes), cancelled.Token),
                "cancellation during payload reading stops before acceptance");
        using (var stream = new FragmentedStream(bytes))
        {
            var fragmented = GaussianInspection.Inspect(stream, budget, Fixtures.Hash(bytes));
            Check.That(fragmented.Minimum.SequenceEqual(result.Minimum), "short stream reads are completed without changing measured data");
            Check.That(stream.CanRead, "inspection leaves caller-owned stream open");
        }
        using (var stream = new NonseekableStream(bytes))
            Check.Throws<InvalidOperationException>(() => GaussianInspection.Inspect(stream, budget, Fixtures.Hash(bytes)), "unbounded nonseekable input is rejected");
        using (var stream = new MemoryStream(bytes, writable: false))
        {
            stream.Position = 1;
            Check.Throws<InvalidOperationException>(() => GaussianInspection.Inspect(stream, budget, Fixtures.Hash(bytes)), "nonzero input position is rejected");
        }
        using (var stream = new ChangedLengthStream(bytes))
            Check.Throws<InvalidOperationException>(() => GaussianInspection.Inspect(stream, budget, Fixtures.Hash(bytes)), "input length drift cannot be accepted");
    }

    static GaussianInspectionResult Inspect(byte[] bytes, SplatBudgets? budgets = null, string? expectedHash = null,
        CancellationToken cancellation = default, bool useDefaultHash = true)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return GaussianInspection.Inspect(stream, budgets ?? Fixtures.Budgets(),
            useDefaultHash ? expectedHash ?? Fixtures.Hash(bytes) : expectedHash!, cancellation);
    }

    static void Reject(byte[] bytes, string name, SplatBudgets? budgets = null) =>
        Check.Throws<InvalidOperationException>(() => Inspect(bytes, budgets), name);

    sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(3, buffer.Length)]);
    }

    sealed class NonseekableStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override bool CanSeek => false;
    }

    sealed class CancelOnReadStream(byte[] bytes, CancellationTokenSource source, int threshold) : MemoryStream(bytes, writable: false)
    {
        public override int Read(Span<byte> buffer)
        {
            var count = base.Read(buffer);
            if (Position >= threshold) source.Cancel();
            return count;
        }
    }

    sealed class ChangedLengthStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        bool read;
        public override long Length => base.Length + (read ? 1 : 0);
        public override int Read(Span<byte> buffer) { var count = base.Read(buffer); read = true; return count; }
    }
}
