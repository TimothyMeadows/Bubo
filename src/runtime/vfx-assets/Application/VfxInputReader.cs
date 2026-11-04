using System.Security.Cryptography;

namespace Bubo.Vfx.Application;

/// <summary>All envelope bytes and digests pass through the same finite, cancellation-aware reader.</summary>
internal sealed class VfxInputReader : IDisposable
{
    const int BlockBytes = 64 * 1024;
    readonly FileStream stream;
    readonly long maximumBytes;
    readonly long initialLength;
    readonly long? expectedBytes;
    readonly CancellationToken cancellation;
    readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    readonly MemoryStream? content;

    internal VfxInputReader(FileStream stream, long maximumBytes, long? expectedBytes, bool retainContent, CancellationToken cancellation)
    {
        this.stream = stream;
        this.maximumBytes = maximumBytes;
        this.expectedBytes = expectedBytes;
        this.cancellation = cancellation;
        initialLength = stream.Length;
        if (initialLength < 1 || initialLength > maximumBytes || (expectedBytes is not null && initialLength != expectedBytes))
        {
            hash.Dispose();
            throw new InvalidOperationException("VFX input length is empty, changed or exceeds its byte bound");
        }
        content = retainContent ? new MemoryStream(checked((int)initialLength)) : null;
    }

    internal long Bytes { get; private set; }
    internal long Length => initialLength;
    internal long Remaining => initialLength - Bytes;
    internal byte[]? Content => content?.ToArray();

    internal int Read(Span<byte> buffer)
    {
        cancellation.ThrowIfCancellationRequested();
        // Read at most one byte beyond the allowance, including when the initial file grows.
        var count = stream.Read(buffer[..(int)Math.Min(buffer.Length, maximumBytes - Bytes + 1)]);
        Bytes += count;
        if (Bytes > maximumBytes || (expectedBytes is not null && Bytes > expectedBytes))
            throw new InvalidOperationException("VFX input grew beyond its declared byte bound");
        hash.AppendData(buffer[..count]);
        content?.Write(buffer[..count]);
        cancellation.ThrowIfCancellationRequested();
        return count;
    }

    internal void ReadExactly(Span<byte> target)
    {
        while (!target.IsEmpty)
        {
            var count = Read(target[..Math.Min(target.Length, BlockBytes)]);
            if (count == 0) throw new InvalidOperationException("Truncated VFX resource envelope");
            target = target[count..];
        }
    }

    internal void Skip(long bytes)
    {
        if (bytes < 0 || bytes > Remaining) throw new InvalidOperationException("VFX resource chunk exceeds its finite file envelope");
        var buffer = new byte[BlockBytes];
        while (bytes > 0)
        {
            var count = Read(buffer.AsSpan(0, (int)Math.Min(bytes, buffer.Length)));
            if (count == 0) throw new InvalidOperationException("Truncated VFX resource chunk");
            bytes -= count;
        }
    }

    internal void Drain()
    {
        var buffer = new byte[BlockBytes];
        while (Read(buffer) != 0) { }
    }

    internal void Complete()
    {
        Span<byte> trailing = stackalloc byte[1];
        if (Read(trailing) != 0) throw new InvalidOperationException("Unexpected trailing VFX resource bytes");
        if (Bytes != initialLength || stream.Length != initialLength || (expectedBytes is not null && Bytes != expectedBytes))
            throw new InvalidOperationException("VFX input length changed during bounded inspection");
    }

    internal string FinishHash() => Convert.ToHexStringLower(hash.GetHashAndReset());

    public void Dispose()
    {
        content?.Dispose();
        hash.Dispose();
    }
}
