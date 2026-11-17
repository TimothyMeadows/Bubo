using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Bubo.Splats.Domain;

namespace Bubo.Splats.Application;

public sealed record GaussianInspectionResult(string SchemaVersion, string Profile, int GaussianCount,
    int SphericalHarmonicsDegree, long FileBytes, long DecodedBytes, string Sha256,
    double[] Minimum, double[] Maximum, string Coordinates, string Scale, string Opacity,
    string Rotation, string Color, bool TechnicalOnly, string? RequestSha256 = null);

/// <summary>Streaming validation of the explicit TripoSplat SH0 PLY profile, not a mesh or renderer.</summary>
public static class GaussianInspection
{
    static readonly string[] Names = ["x", "y", "z", "nx", "ny", "nz", "f_dc_0", "f_dc_1", "f_dc_2",
        "opacity", "scale_0", "scale_1", "scale_2", "rot_0", "rot_1", "rot_2", "rot_3"];

    public static GaussianInspectionResult Inspect(Stream stream, SplatBudgets budgets, string expectedSha256,
        CancellationToken cancellation = default)
    {
        SplatRules.ValidateBudgets(budgets);
        if (expectedSha256 is null || expectedSha256.Length != 64 || expectedSha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidOperationException("Expected input SHA-256 must be lowercase hex");
        if (!stream.CanRead || !stream.CanSeek) throw new InvalidOperationException("Inspection requires a bounded seekable input");
        using var reader = new Reader(stream, budgets.MaxInputBytes, cancellation);
        var headerBytes = 0;
        string Line()
        {
            var text = new StringBuilder();
            while (true)
            {
                if (++headerBytes > budgets.MaxHeaderBytes) throw new InvalidOperationException("PLY header exceeds its bound");
                var value = reader.Byte();
                if (value == 10) return text.ToString().TrimEnd('\r');
                if (value is not (>= 32 and <= 126) && value != 13)
                    throw new InvalidOperationException("PLY header must be printable ASCII");
                if (text.Length > 0 && text[^1] == '\r') throw new InvalidOperationException("Invalid PLY line ending");
                text.Append((char)value);
            }
        }
        if (Line() != "ply" || Line() != "format binary_little_endian 1.0")
            throw new InvalidOperationException("Only binary little-endian PLY 1.0 is admitted");
        int? count = null;
        var properties = new List<string>();
        while (true)
        {
            var line = Line();
            if (line == "end_header") break;
            if (line.StartsWith("comment ", StringComparison.Ordinal)) continue;
            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words is ["element", "vertex", var number] && count is null && properties.Count == 0
                && int.TryParse(number, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                if (parsed < 1 || parsed > budgets.MaxGaussians) throw new InvalidOperationException("Gaussian count exceeds its bound");
                count = parsed;
            }
            else if (words is ["property", "float" or "float32", var name] && count is not null
                && Names.Contains(name, StringComparer.Ordinal) && !properties.Contains(name, StringComparer.Ordinal))
                properties.Add(name);
            else throw new InvalidOperationException("Unsupported, duplicate or misplaced PLY declaration");
        }
        if (count is null || properties.Count != Names.Length || Names.Any(n => !properties.Contains(n, StringComparer.Ordinal)))
            throw new InvalidOperationException("PLY does not contain the complete TripoSplat SH0 property set");
        var stride = checked(properties.Count * 4);
        var decoded = checked((long)count.Value * stride);
        if (decoded > budgets.MaxDecodedBytes || checked(decoded + headerBytes) != reader.Length)
            throw new InvalidOperationException("PLY payload length or decoded allocation exceeds its envelope");
        var indices = Names.Select(n => properties.IndexOf(n)).ToArray();
        var row = new byte[stride]; // Count-sized buffers are deliberately never allocated.
        var values = new float[Names.Length];
        var min = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        var max = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        for (var vertex = 0; vertex < count.Value; vertex++)
        {
            reader.Exact(row);
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = BinaryPrimitives.ReadSingleLittleEndian(row.AsSpan(indices[i] * 4, 4));
                if (!float.IsFinite(values[i])) throw new InvalidOperationException("Nonfinite Gaussian scalar");
            }
            for (var axis = 0; axis < 3; axis++)
            {
                if (Math.Abs(values[axis]) > 1_000_000) throw new InvalidOperationException("Position exceeds the admitted source-local bound");
                min[axis] = Math.Min(min[axis], values[axis]); max[axis] = Math.Max(max[axis], values[axis]);
            }
            if (values.Skip(3).Take(6).Any(v => Math.Abs(v) > 10_000) || Math.Abs(values[9]) > 80
                || values.Skip(10).Take(3).Any(v => Math.Abs(v) > 30))
                throw new InvalidOperationException("Gaussian appearance/log-scale/logit exceeds the supported numeric profile");
            var rotationNorm = values.Skip(13).Sum(v => (double)v * v);
            if (rotationNorm is < 1e-20 or > 1e12)
                throw new InvalidOperationException("Invalid Gaussian quaternion; nonzero bounded rotation required");
        }
        var digest = reader.Finish();
        if (!string.Equals(digest, expectedSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("Gaussian input hash differs from its declared identity");
        return new("bubo-splat-inspection-result/v1", "triposplat-ply-sh0/v1", count.Value, 0,
            reader.Length, decoded, digest, min, max, "source-local; scale/frame calibration required",
            "natural-log standard-deviation", "logit", "wxyz; normalized by receiver", "SH0 RGB coefficients", true);
    }

    sealed class Reader : IDisposable
    {
        readonly Stream stream;
        readonly CancellationToken cancellation;
        readonly IncrementalHash hash;
        long bytes;
        public long Length { get; }
        public Reader(Stream source, long maximumBytes, CancellationToken token)
        {
            stream = source; cancellation = token;
            cancellation.ThrowIfCancellationRequested();
            Length = stream.Length;
            if (Length < 1 || Length > maximumBytes || stream.Position != 0)
                throw new InvalidOperationException("Input length/position exceeds the declared envelope");
            hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        }
        public byte Byte() { Span<byte> one = stackalloc byte[1]; Exact(one); return one[0]; }
        public void Exact(Span<byte> target)
        {
            if (target.Length > Length - bytes) throw new InvalidOperationException("Truncated Gaussian input");
            while (!target.IsEmpty)
            {
                cancellation.ThrowIfCancellationRequested();
                var read = stream.Read(target);
                if (read == 0) throw new InvalidOperationException("Truncated Gaussian input");
                hash.AppendData(target[..read]); bytes += read; target = target[read..];
                cancellation.ThrowIfCancellationRequested();
            }
        }
        public string Finish()
        {
            cancellation.ThrowIfCancellationRequested();
            if (bytes != Length || stream.Length != Length || stream.ReadByte() != -1)
                throw new InvalidOperationException("Gaussian input changed or has trailing bytes");
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        public void Dispose() => hash.Dispose();
    }
}
