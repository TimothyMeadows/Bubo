using System.Text.Json;
using Hephaera.AssetProduction;

namespace Bubo.FloorPlan.Infrastructure;

/// <summary>Strict bounded JSON for native construction references, which may exceed the design-input ceiling.</summary>
internal static class ExportJson
{
    internal const long HardMaximumBytes = 268_435_456;

    internal static void RequireSavedSize<T>(T value, long maximum)
    {
        if (maximum is < 2 or > HardMaximumBytes) throw new InvalidOperationException("Invalid bounded export state allowance");
        using var counter = new CountingWriteStream(maximum);
        // These are exactly the options used by IWorkspace.Save's atomic writer.
        JsonSerializer.Serialize(counter, value, Workspace.Json);
    }

    internal static void SaveBounded<T>(IWorkspace workspace, string path, T value, long maximum)
    {
        RequireSavedSize(value, maximum);
        workspace.Save(path, value);
    }

    sealed class CountingWriteStream(long maximum) : Stream
    {
        long written;
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => written; public override long Position { get => written; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length > maximum - written) throw new InvalidOperationException("Export state would exceed its readable byte ceiling; preserve prior state and start no new native work");
            written = checked(written + buffer.Length);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    internal static T Read<T>(string path, long maximum, CancellationToken cancellation)
    {
        if (maximum is < 2 or > HardMaximumBytes) throw new InvalidOperationException("Invalid bounded export JSON allowance");
        cancellation.ThrowIfCancellationRequested();
        var info = new FileInfo(path);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length < 2 || info.Length > maximum)
            throw new InvalidOperationException("Missing, linked or oversized export reference JSON");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var length = stream.Length;
        if (length < 2 || length > maximum) throw new InvalidOperationException("Export reference JSON changed size");
        var bytes = new byte[checked((int)length)];
        for (var offset = 0; offset < bytes.Length;)
        {
            cancellation.ThrowIfCancellationRequested();
            var read = stream.Read(bytes, offset, Math.Min(65_536, bytes.Length - offset));
            if (read == 0) throw new InvalidOperationException("Export reference JSON shrank during reading");
            offset += read;
        }
        if (stream.ReadByte() != -1) throw new InvalidOperationException("Export reference JSON grew during reading");
        Check(bytes, cancellation);
        var result = JsonSerializer.Deserialize<T>(bytes, StrictPlanJson.Options) ?? throw new InvalidOperationException("Null export JSON");
        cancellation.ThrowIfCancellationRequested();
        return result;
    }

    static void Check(ReadOnlySpan<byte> bytes, CancellationToken cancellation)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 64 });
        var containers = new Stack<HashSet<string>?>();
        long tokens = 0;
        while (reader.Read())
        {
            if ((++tokens & 127) == 0) cancellation.ThrowIfCancellationRequested();
            if (tokens > 100_000_000) throw new InvalidOperationException("Export JSON token ceiling exceeded");
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject: containers.Push(new(StringComparer.Ordinal)); break;
                case JsonTokenType.StartArray: containers.Push(null); break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray: containers.Pop(); break;
                case JsonTokenType.PropertyName:
                    if (reader.ValueSpan.Length > 4096) throw new InvalidOperationException("Export JSON property name exceeds its fixed ceiling");
                    if (containers.Peek() is not { } names || !names.Add(reader.GetString()!)) throw new InvalidOperationException("Duplicate export JSON property");
                    break;
                case JsonTokenType.Number:
                    if (!reader.TryGetDouble(out var number) || !double.IsFinite(number)) throw new InvalidOperationException("Nonfinite export JSON number");
                    break;
            }
        }
        cancellation.ThrowIfCancellationRequested();
    }
}
