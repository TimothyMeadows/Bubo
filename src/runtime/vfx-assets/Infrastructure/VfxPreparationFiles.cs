using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hephaera.AssetProduction;

namespace Bubo.Vfx.Infrastructure;

/// <summary>Cooperative finite file snapshots; these guards do not create an OS sandbox.</summary>
internal static class VfxPreparationFiles
{
    internal const int JsonLimit = 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 32
    };
    internal static readonly StringComparison Comparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static bool Within(string path, string root) => path.StartsWith(
        Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, Comparison);

    internal static void Canonical(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 1024 || path.Any(c => char.IsControl(c) || c is '\\' or ':' or '<' or '>' or '"' or '|' or '?' or '*'))
            throw new InvalidOperationException("Preparation paths must be canonical project-relative forward-slash paths");
        foreach (var part in path.Split('/'))
        {
            if (part is "" or "." or ".." || part.Equals(".git", StringComparison.OrdinalIgnoreCase)
                || part.EndsWith('.') || part.EndsWith(' ')) throw new InvalidOperationException("Unsafe preparation path component");
            var stem = part.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9'))
                throw new InvalidOperationException("Device names are not preparation paths");
        }
    }

    internal static string Resolve(IWorkspace workspace, string path, bool write = false)
    {
        Canonical(path);
        var expected = Path.GetFullPath(path, workspace.Root);
        var actual = write ? workspace.ResolveWrite(path) : workspace.Resolve(path);
        if (!actual.Equals(expected, Comparison) || !Within(actual, workspace.Root))
            throw new InvalidOperationException("Preparation workspace changed or escaped the requested path");
        CheckComponents(actual);
        return actual;
    }

    internal static void CheckComponents(string path)
    {
        var current = Path.GetPathRoot(path)!;
        foreach (var part in Path.GetRelativePath(current, path).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            if (File.Exists(current) || Directory.Exists(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Preparation refuses linked/reparse-point components");
        }
    }

    internal static FileStream Open(string path, long maximum, long? expected = null)
    {
        CheckComponents(path);
        // Reject stable FIFOs/devices before opening. The stream is independently checked below.
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < 1 || info.Length > maximum || (expected.HasValue && info.Length != expected.Value))
            throw new InvalidOperationException("Preparation input must have a matching positive bounded file length");
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        if (!stream.CanSeek || stream.Length != info.Length) { stream.Dispose(); throw new InvalidOperationException("Preparation requires a stable seekable file"); }
        return stream;
    }

    internal static (long Bytes, string Sha256) Digest(Stream stream, long maximum, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long total = 0;
        for (;;)
        {
            token.ThrowIfCancellationRequested();
            var count = stream.Read(buffer.AsSpan(0, (int)Math.Min(buffer.Length, maximum - total + 1)));
            if (count == 0) break;
            total = checked(total + count);
            if (total > maximum) throw new InvalidOperationException("Preparation file exceeds its declared byte budget");
            hash.AppendData(buffer, 0, count);
        }
        token.ThrowIfCancellationRequested();
        return (total, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    internal static byte[] Read(string path, int maximum, CancellationToken token)
    {
        using var stream = Open(path, maximum);
        var bytes = new byte[checked((int)stream.Length)];
        for (var offset = 0; offset < bytes.Length;)
        {
            token.ThrowIfCancellationRequested();
            var count = stream.Read(bytes.AsSpan(offset, Math.Min(65536, bytes.Length - offset)));
            if (count == 0) throw new InvalidOperationException("Preparation input was truncated");
            offset += count;
        }
        if (stream.ReadByte() != -1 || stream.Length != bytes.Length) throw new InvalidOperationException("Preparation input changed length");
        token.ThrowIfCancellationRequested();
        return bytes;
    }

    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static byte[] Encode<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Json);
    internal static T Parse<T>(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        Inspect(document.RootElement);
        return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidOperationException("Empty preparation receipt");
    }
    static void Inspect(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Null) throw new InvalidOperationException("Null preparation receipt field");
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidOperationException("Duplicate preparation receipt field");
                Inspect(property.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array) foreach (var item in node.EnumerateArray()) Inspect(item);
    }

    internal static void Create(string path, byte[] bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        CheckComponents(path);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        for (var offset = 0; offset < bytes.Length; offset += 65536)
        {
            token.ThrowIfCancellationRequested();
            stream.Write(bytes.AsSpan(offset, Math.Min(65536, bytes.Length - offset)));
        }
        stream.Flush(true);
    }
}
