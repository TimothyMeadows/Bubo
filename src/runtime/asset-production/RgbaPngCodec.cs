using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Hephaera.AssetProduction;

/// <summary>
/// Shared lossless, bounded noninterlaced 8-bit RGBA/gray-alpha PNG codec.
/// Explicit colour declarations label unchanged samples; this codec performs no conversion.
/// Allocation estimates bound owned image buffers, not the process or an operating-system sandbox.
/// </summary>
public static class RgbaPngCodec
{
    public sealed record Image(int Width, int Height, byte[] Pixels);
    public sealed record Limits
    {
        public long MaxEncodedBytes { get; init; } = 134217728;
        public long MaxChunkBytes { get; init; } = 134217728;
        public long MaxCompressedBytes { get; init; } = 134217728;
        public long MaxPixels { get; init; } = 16777216;
        public long MaxWorkingBytes { get; init; } = 268435456;
        public long MaxOutputBytes { get; init; } = 134217728;
        public string? ColorSpace { get; init; }
    }
    public const int MaximumDimension = 8192;
    public const int MaximumPixels = 16777216;
    public const int MaximumChunks = 4096;
    public const int BufferBytes = 65536;
    // Includes the worst bounded descriptor-list capacity and codec object/row overhead.
    public const long FixedWorkingBytes = BufferBytes * 2L + MaximumChunks * 32L + 16384;
    static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(value =>
    {
        var crc = (uint)value;
        for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320 : crc >> 1;
        return crc;
    }).ToArray();

    public static Image Read(string path, Limits? limits = null, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var bound = limits ?? new Limits();
        Validate(bound);
        // A finite length snapshot precedes opening so ordinary FIFO/device entries fail closed.
        var length = new FileInfo(path).Length;
        if (length < 1 || length > bound.MaxEncodedBytes) throw new InvalidOperationException("Encoded PNG exceeds byte budget");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferBytes, FileOptions.RandomAccess);
        return Read(file, bound, cancellation);
    }

    public static Image Read(Stream file, Limits? limits = null, CancellationToken cancellation = default)
    {
        var bound = limits ?? new Limits();
        Validate(bound);
        cancellation.ThrowIfCancellationRequested();
        if (!file.CanRead || !file.CanSeek || file.Position != 0 || file.Length < 1 || file.Length > bound.MaxEncodedBytes)
            throw new InvalidOperationException("PNG requires a finite bounded seekable input at its beginning");
        var initialLength = file.Length;
        var scratch = new byte[BufferBytes];
        Span<byte> signature = stackalloc byte[8];
        ReadExactly(file, signature, cancellation);
        if (!signature.SequenceEqual(Signature)) throw new InvalidOperationException("PNG requires actual alpha-bearing PNG data");
        var segments = new List<Segment>();
        long compressedBytes = 0;
        int width = 0, height = 0, channels = 0, chunks = 0;
        bool header = false, data = false, dataEnded = false, palette = false, end = false, srgb = false, gamma = false;
        Span<byte> integer = stackalloc byte[4];
        Span<byte> tag = stackalloc byte[4];
        Span<byte> small = stackalloc byte[13];
        while (file.Position < initialLength)
        {
            cancellation.ThrowIfCancellationRequested();
            if (++chunks > MaximumChunks) throw new InvalidOperationException("PNG chunk count exceeds bounded work");
            ReadExactly(file, integer, cancellation);
            var length = BinaryPrimitives.ReadInt32BigEndian(integer);
            if (length < 0 || length > bound.MaxChunkBytes || length > initialLength - file.Position - 8)
                throw new InvalidOperationException("Invalid or oversized PNG chunk");
            ReadExactly(file, tag, cancellation);
            for (var i = 0; i < tag.Length; i++)
                if (tag[i] is not (>= (byte)'A' and <= (byte)'Z') and not (>= (byte)'a' and <= (byte)'z'))
                    throw new InvalidOperationException("Invalid PNG chunk type");
            if ((tag[2] & 32) != 0) throw new InvalidOperationException("Invalid PNG chunk reserved bit");
            var type = Encoding.ASCII.GetString(tag);
            if (!header && type != "IHDR") throw new InvalidOperationException("PNG must start with IHDR");
            if (data && type != "IDAT") dataEnded = true;
            if (bound.ColorSpace is not null && type is not ("IHDR" or "PLTE" or "IDAT" or "IEND" or "sRGB" or "gAMA"))
                throw new InvalidOperationException("PNG ancillary profile is unavailable for fixed VFX processing: " + type);
            var offset = file.Position;
            var crc = UpdateCrc(0xffffffffu, tag, cancellation);
            var remaining = length;
            var copied = 0;
            while (remaining > 0)
            {
                var count = Math.Min(remaining, scratch.Length);
                ReadExactly(file, scratch.AsSpan(0, count), cancellation);
                crc = UpdateCrc(crc, scratch.AsSpan(0, count), cancellation);
                if (copied < small.Length)
                {
                    var retain = Math.Min(count, small.Length - copied);
                    scratch.AsSpan(0, retain).CopyTo(small[copied..]);
                    copied += retain;
                }
                remaining -= count;
            }
            ReadExactly(file, integer, cancellation);
            if (BinaryPrimitives.ReadUInt32BigEndian(integer) != ~crc) throw new InvalidOperationException("PNG chunk checksum mismatch");
            switch (type)
            {
                case "IHDR":
                    if (header || length != 13) throw new InvalidOperationException("PNG needs exactly one valid IHDR");
                    width = BinaryPrimitives.ReadInt32BigEndian(small);
                    height = BinaryPrimitives.ReadInt32BigEndian(small[4..]);
                    Dimensions(width, height, bound.MaxPixels);
                    if (small[8] != 8 || small[9] is not (4 or 6) || small[10] != 0 || small[11] != 0 || small[12] != 0)
                        throw new InvalidOperationException("Only noninterlaced 8-bit RGBA or gray-alpha PNG is available; normalize explicitly first");
                    channels = small[9] == 6 ? 4 : 2;
                    var working = checked((long)width * height * 4 + (long)width * channels * 2 + FixedWorkingBytes);
                    if (working > bound.MaxWorkingBytes) throw new InvalidOperationException("PNG owned working-allocation estimate exceeds budget");
                    header = true;
                    break;
                case "PLTE":
                    if (channels != 4 || palette || data || length < 3 || length > 768 || length % 3 != 0)
                        throw new InvalidOperationException("Invalid optional RGBA PNG palette");
                    palette = true;
                    break;
                case "IDAT":
                    if (dataEnded || compressedBytes > bound.MaxCompressedBytes - length)
                        throw new InvalidOperationException("PNG data must be contiguous and within compressed byte budget");
                    compressedBytes += length;
                    segments.Add(new(offset, length));
                    data = true;
                    break;
                case "IEND":
                    if (!data || length != 0 || file.Position != initialLength) throw new InvalidOperationException("PNG needs final empty IEND without trailing data");
                    end = true;
                    break;
                case "sRGB" when bound.ColorSpace is not null:
                    if (srgb || palette || data || length != 1 || small[0] > 3 || bound.ColorSpace != "srgb")
                        throw new InvalidOperationException("PNG sRGB profile conflicts with explicit color space or chunk order");
                    srgb = true;
                    break;
                case "gAMA" when bound.ColorSpace is not null:
                    if (gamma || palette || data || length != 4 || BinaryPrimitives.ReadUInt32BigEndian(small) != (bound.ColorSpace == "srgb" ? 45455u : 100000u))
                        throw new InvalidOperationException("PNG gamma conflicts with explicit color space or chunk order");
                    gamma = true;
                    break;
                default:
                    if ((tag[0] & 32) == 0) throw new InvalidOperationException("Unsupported critical PNG chunk: " + type);
                    break;
            }
            if (end) break;
        }
        if (!header || !data || !end || compressedBytes == 0 || file.Length != initialLength)
            throw new InvalidOperationException("Incomplete or changed PNG");
        uint? expectedAdler = null;
        if (bound.ColorSpace is not null)
        {
            using var framing = new SegmentStream(file, segments, compressedBytes, cancellation);
            expectedAdler = RgbaPngDeflate.Validate(framing, checked(((long)width * channels + 1) * height), cancellation);
        }
        var pixels = new byte[checked(width * height * 4)];
        var stride = checked(width * channels);
        var previous = new byte[stride];
        var row = new byte[stride];
        using var compressed = new SegmentStream(file, segments, compressedBytes, cancellation);
        using var zip = new ZLibStream(compressed, CompressionMode.Decompress, true);
        uint adlerA = 1, adlerB = 0;
        for (var y = 0; y < height; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            var filter = zip.ReadByte();
            if (filter is < 0 or > 4) throw new InvalidOperationException("Invalid or missing PNG scanline filter");
            ReadExactly(zip, row, cancellation);
            if (expectedAdler is not null)
            {
                adlerA = (adlerA + (uint)filter) % 65521; adlerB = (adlerB + adlerA) % 65521;
                UpdateAdler(row, ref adlerA, ref adlerB, cancellation);
            }
            for (var x = 0; x < stride; x++)
            {
                if ((x & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                int left = x >= channels ? row[x - channels] : 0, up = previous[x], corner = x >= channels ? previous[x - channels] : 0;
                var predictor = filter switch { 0 => 0, 1 => left, 2 => up, 3 => (left + up) / 2, 4 => Paeth(left, up, corner), _ => 0 };
                row[x] = unchecked((byte)(row[x] + predictor));
            }
            for (var x = 0; x < width; x++)
            {
                var target = (y * width + x) * 4;
                var source = x * channels;
                pixels[target] = row[source];
                pixels[target + 1] = channels == 4 ? row[source + 1] : row[source];
                pixels[target + 2] = channels == 4 ? row[source + 2] : row[source];
                pixels[target + 3] = row[source + channels - 1];
            }
            (row, previous) = (previous, row);
        }
        cancellation.ThrowIfCancellationRequested();
        if (zip.ReadByte() != -1) throw new InvalidOperationException("PNG decompression exceeds declared scanlines");
        if (expectedAdler is not null && expectedAdler != ((adlerB << 16) | adlerA))
            throw new InvalidOperationException("PNG zlib Adler checksum differs from actual scanline bytes");
        if (file.Length != initialLength) throw new InvalidOperationException("PNG changed during decoding");
        return new(width, height, pixels);
    }

    public static void Write(string path, Image image, Limits? limits = null, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        ValidateImage(image, limits ?? new Limits());
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        Write(file, image, limits, cancellation);
    }

    public static void Write(Stream output, Image image, Limits? limits = null, CancellationToken cancellation = default)
    {
        var bound = limits ?? new Limits();
        ValidateImage(image, bound);
        cancellation.ThrowIfCancellationRequested();
        if (!output.CanWrite || !output.CanSeek || output.Position != 0 || output.Length != 0)
            throw new InvalidOperationException("PNG output requires an empty exclusive seekable stream");
        using var limited = new OutputStream(output, bound.MaxOutputBytes, cancellation);
        limited.Write(Signature);
        Span<byte> header = stackalloc byte[13];
        header.Clear();
        BinaryPrimitives.WriteInt32BigEndian(header, image.Width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], image.Height);
        header[8] = 8; header[9] = 6;
        Chunk(limited, "IHDR", header, cancellation);
        if (bound.ColorSpace == "srgb") Chunk(limited, "sRGB", [0], cancellation);
        if (bound.ColorSpace == "linear")
        {
            Span<byte> gamma = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(gamma, 100000);
            Chunk(limited, "gAMA", gamma, cancellation);
        }
        using (var chunks = new IdatStream(limited, checked((int)Math.Min(bound.MaxChunkBytes, BufferBytes)), bound.MaxCompressedBytes, cancellation))
        {
            using (var zip = new ZLibStream(chunks, CompressionLevel.Optimal, true))
                for (var y = 0; y < image.Height; y++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    zip.WriteByte(0);
                    zip.Write(image.Pixels.AsSpan(y * image.Width * 4, image.Width * 4));
                }
            chunks.Complete();
        }
        Chunk(limited, "IEND", [], cancellation);
        cancellation.ThrowIfCancellationRequested();
    }

    static void ValidateImage(Image image, Limits bound)
    {
        Validate(bound);
        Dimensions(image.Width, image.Height, bound.MaxPixels);
        if (bound.MaxChunkBytes < 13) throw new InvalidOperationException("PNG chunk budget cannot hold its required header");
        if (image.Pixels is null || image.Pixels.Length != checked(image.Width * image.Height * 4)) throw new InvalidOperationException("RGBA byte count differs from dimensions");
        if (checked((long)image.Pixels.Length + FixedWorkingBytes + BufferBytes) > bound.MaxWorkingBytes)
            throw new InvalidOperationException("PNG encoder owned working-allocation estimate exceeds budget");
    }
    static void Validate(Limits bound)
    {
        if (bound.MaxEncodedBytes is < 1 or > 134217728 || bound.MaxChunkBytes is < 1 or > 134217728
            || bound.MaxCompressedBytes is < 1 or > 134217728 || bound.MaxPixels is < 1 or > MaximumPixels
            || bound.MaxWorkingBytes is < FixedWorkingBytes or > 1073741824 || bound.MaxOutputBytes is < 1 or > 134217728
            || bound.ColorSpace is not (null or "srgb" or "linear"))
            throw new InvalidOperationException("Invalid PNG processing limits or explicit color space");
    }
    static void Dimensions(int width, int height, long maxPixels)
    {
        if (width < 1 || height < 1 || width > MaximumDimension || height > MaximumDimension || (long)width * height > maxPixels)
            throw new InvalidOperationException("PNG dimensions exceed bounded pixel budget");
    }
    static void ReadExactly(Stream input, Span<byte> bytes, CancellationToken cancellation)
    {
        while (!bytes.IsEmpty)
        {
            cancellation.ThrowIfCancellationRequested();
            var read = input.Read(bytes[..Math.Min(bytes.Length, BufferBytes)]);
            if (read == 0) throw new InvalidOperationException("Truncated PNG");
            bytes = bytes[read..];
        }
    }
    static int Paeth(int a, int b, int c)
    {
        var p = a + b - c; var pa = Math.Abs(p - a); var pb = Math.Abs(p - b); var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
    static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data, CancellationToken cancellation)
    {
        while (!data.IsEmpty)
        {
            cancellation.ThrowIfCancellationRequested();
            var count = Math.Min(data.Length, 4096);
            foreach (var value in data[..count]) crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8);
            data = data[count..];
        }
        return crc;
    }
    static void UpdateAdler(ReadOnlySpan<byte> data, ref uint a, ref uint b, CancellationToken cancellation)
    {
        // 5552 bytes keep both accumulators within uint without changing exact Adler arithmetic.
        while (!data.IsEmpty)
        {
            cancellation.ThrowIfCancellationRequested();
            var count = Math.Min(data.Length, 5552);
            foreach (var value in data[..count]) { a += value; b += a; }
            a %= 65521; b %= 65521; data = data[count..];
        }
    }
    static void Chunk(Stream output, string type, ReadOnlySpan<byte> bytes, CancellationToken cancellation)
    {
        var tag = Encoding.ASCII.GetBytes(type);
        Span<byte> integer = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(integer, bytes.Length);
        output.Write(integer); output.Write(tag); output.Write(bytes);
        BinaryPrimitives.WriteUInt32BigEndian(integer, ~UpdateCrc(UpdateCrc(0xffffffffu, tag, cancellation), bytes, cancellation));
        output.Write(integer);
    }
    readonly record struct Segment(long Offset, int Length);
    sealed class SegmentStream(Stream input, IReadOnlyList<Segment> segments, long length, CancellationToken cancellation) : Stream
    {
        int index, offset; long position;
        public override int Read(Span<byte> buffer)
        {
            cancellation.ThrowIfCancellationRequested();
            while (index < segments.Count && offset == segments[index].Length) { index++; offset = 0; }
            if (index == segments.Count || buffer.IsEmpty) return 0;
            var segment = segments[index];
            var count = Math.Min(Math.Min(buffer.Length, BufferBytes), segment.Length - offset);
            input.Position = segment.Offset + offset;
            var read = input.Read(buffer[..count]);
            if (read == 0) throw new InvalidOperationException("PNG IDAT changed or was truncated");
            offset += read; position += read; return read;
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => length; public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    sealed class OutputStream(Stream output, long maximum, CancellationToken cancellation) : Stream
    {
        long bytes;
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            cancellation.ThrowIfCancellationRequested();
            if (buffer.Length > maximum - bytes) throw new InvalidOperationException("PNG encoded output exceeds byte budget");
            output.Write(buffer); bytes += buffer.Length;
        }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => bytes; public override long Position { get => bytes; set => throw new NotSupportedException(); }
        public override void Flush() => output.Flush(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    sealed class IdatStream(Stream output, int capacity, long compressedMaximum, CancellationToken cancellation) : Stream
    {
        readonly byte[] buffer = new byte[capacity]; int used; long total;
        public void Complete() { if (used > 0) { Chunk(output, "IDAT", buffer.AsSpan(0, used), cancellation); used = 0; } }
        public override void Write(ReadOnlySpan<byte> bytes)
        {
            while (!bytes.IsEmpty)
            {
                cancellation.ThrowIfCancellationRequested();
                var count = Math.Min(bytes.Length, buffer.Length - used);
                if (count > compressedMaximum - total) throw new InvalidOperationException("PNG compressed output exceeds byte budget");
                total += count;
                bytes[..count].CopyTo(buffer.AsSpan(used)); used += count; bytes = bytes[count..];
                if (used == buffer.Length) Complete();
            }
        }
        public override void Write(byte[] bytes, int offset, int count) => Write(bytes.AsSpan(offset, count));
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override int Read(byte[] bytes, int offset, int count) => throw new NotSupportedException();
    }
}
