namespace Hephaera.AssetProduction;

/// <summary>
/// Strict PNG zlib member framing, not a second image decoder. Walks bounded DEFLATE tokens to locate
/// the actual final block, count its exact output and require one complete Adler trailer and EOF.
/// The BCL still reconstructs all pixels; the caller independently checks Adler on raw scanline bytes.
/// </summary>
internal static class RgbaPngDeflate
{
    const int MaximumBlocks = 65536;
    static readonly int[] LengthBase = [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258];
    static readonly int[] LengthExtra = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];
    static readonly int[] DistanceBase = [1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577];
    static readonly int[] DistanceExtra = [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13];
    static readonly int[] CodeOrder = [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];
    static readonly Huffman FixedLiteral = new(Enumerable.Range(0, 288).Select(i => i <= 143 ? 8 : i <= 255 ? 9 : i <= 279 ? 7 : 8).ToArray());
    static readonly Huffman FixedDistance = new(Enumerable.Repeat(5, 32).ToArray());

    internal static uint Validate(Stream compressed, long expectedBytes, CancellationToken cancellation)
    {
        var bits = new BitInput(compressed, cancellation);
        var cmf = bits.ReadBits(8); var flg = bits.ReadBits(8);
        if ((cmf & 15) != 8 || (cmf >> 4) > 7 || ((cmf << 8) + flg) % 31 != 0 || (flg & 32) != 0)
            throw new InvalidOperationException("PNG needs one complete dictionary-free zlib/DEFLATE member");
        var window = 1 << (8 + (cmf >> 4));
        long produced = 0, tokens = 0;
        void Produce(long count)
        {
            if (count < 0 || count > expectedBytes - produced) throw new InvalidOperationException("PNG DEFLATE exceeds exact declared scanline bytes");
            produced += count;
        }
        var blocks = 0; var final = false;
        while (!final)
        {
            cancellation.ThrowIfCancellationRequested();
            if (++blocks > MaximumBlocks) throw new InvalidOperationException("PNG DEFLATE block count exceeds bounded work");
            final = bits.ReadBits(1) != 0;
            var kind = bits.ReadBits(2);
            if (kind == 0)
            {
                bits.Align();
                var count = bits.ReadBits(16); var inverse = bits.ReadBits(16);
                if ((count ^ inverse) != 65535) throw new InvalidOperationException("Invalid PNG stored DEFLATE length");
                Produce(count); bits.SkipBytes(count); continue;
            }
            if (kind == 3) throw new InvalidOperationException("Reserved PNG DEFLATE block kind");
            var literal = FixedLiteral; var distance = FixedDistance;
            if (kind == 2)
            {
                var literalCount = bits.ReadBits(5) + 257; var distanceCount = bits.ReadBits(5) + 1; var codeCount = bits.ReadBits(4) + 4;
                if (literalCount > 286) throw new InvalidOperationException("Invalid PNG DEFLATE literal alphabet");
                var codeLengths = new int[19];
                for (var i = 0; i < codeCount; i++) codeLengths[CodeOrder[i]] = bits.ReadBits(3);
                var codeTree = new Huffman(codeLengths);
                var lengths = new int[literalCount + distanceCount];
                var offset = 0;
                while (offset < lengths.Length)
                {
                    var symbol = codeTree.Decode(bits);
                    if (symbol < 16) { lengths[offset++] = symbol; continue; }
                    var count = symbol switch { 16 => bits.ReadBits(2) + 3, 17 => bits.ReadBits(3) + 3, 18 => bits.ReadBits(7) + 11, _ => throw new InvalidOperationException("Invalid PNG DEFLATE code repeat") };
                    if (symbol == 16 && offset == 0 || count > lengths.Length - offset) throw new InvalidOperationException("PNG DEFLATE code repeat exceeds its exact alphabet");
                    var value = symbol == 16 ? lengths[offset - 1] : 0;
                    Array.Fill(lengths, value, offset, count); offset += count;
                }
                if (lengths[256] == 0) throw new InvalidOperationException("PNG DEFLATE alphabet omits end-of-block");
                literal = new Huffman(lengths[..literalCount]);
                distance = new Huffman(lengths[literalCount..], allowEmpty: true);
            }
            while (true)
            {
                if ((++tokens & 1023) == 0) cancellation.ThrowIfCancellationRequested();
                var symbol = literal.Decode(bits);
                if (symbol < 256) { Produce(1); continue; }
                if (symbol == 256) break;
                if (symbol > 285) throw new InvalidOperationException("Reserved PNG DEFLATE length symbol");
                var lengthIndex = symbol - 257;
                var length = LengthBase[lengthIndex] + bits.ReadBits(LengthExtra[lengthIndex]);
                var distanceIndex = distance.Decode(bits);
                if (distanceIndex > 29) throw new InvalidOperationException("Reserved PNG DEFLATE distance symbol");
                var back = DistanceBase[distanceIndex] + bits.ReadBits(DistanceExtra[distanceIndex]);
                if (back > produced || back > window) throw new InvalidOperationException("PNG DEFLATE references unavailable or out-of-window history");
                Produce(length);
            }
        }
        if (produced != expectedBytes) throw new InvalidOperationException("PNG DEFLATE has incomplete declared scanlines");
        bits.Align();
        uint adler = 0;
        for (var i = 0; i < 4; i++) adler = (adler << 8) | (uint)bits.ReadBits(8);
        if (!bits.AtEnd()) throw new InvalidOperationException("PNG IDAT contains trailing data or multiple zlib members");
        cancellation.ThrowIfCancellationRequested();
        return adler;
    }

    sealed class Huffman
    {
        readonly int[] counts = new int[16];
        readonly int[] symbols;
        readonly int maximum;
        internal Huffman(IReadOnlyList<int> lengths, bool allowEmpty = false)
        {
            if (lengths.Count > 288) throw new InvalidOperationException("Unbounded PNG Huffman alphabet");
            foreach (var length in lengths)
            {
                if (length is < 0 or > 15) throw new InvalidOperationException("Invalid PNG Huffman code length");
                counts[length]++; maximum = Math.Max(maximum, length);
            }
            if (maximum == 0 && !allowEmpty) throw new InvalidOperationException("Empty PNG Huffman alphabet");
            var left = 1;
            for (var i = 1; i <= 15; i++) { left = (left << 1) - counts[i]; if (left < 0) throw new InvalidOperationException("Oversubscribed PNG Huffman alphabet"); }
            // The BCL validates remaining RFC profile constraints on the independently decoded stream.
            var offsets = new int[16];
            for (var i = 1; i < 15; i++) offsets[i + 1] = offsets[i] + counts[i];
            symbols = new int[lengths.Count - counts[0]];
            for (var i = 0; i < lengths.Count; i++) if (lengths[i] != 0) symbols[offsets[lengths[i]]++] = i;
        }
        internal int Decode(BitInput bits)
        {
            var code = 0; var first = 0; var index = 0;
            for (var length = 1; length <= maximum; length++)
            {
                code |= bits.ReadBits(1);
                var count = counts[length];
                if (code >= first && code < first + count) return symbols[index + code - first];
                index += count; first = (first + count) << 1; code <<= 1;
            }
            throw new InvalidOperationException("Invalid or empty PNG Huffman code");
        }
    }
    sealed class BitInput(Stream input, CancellationToken cancellation)
    {
        readonly byte[] buffer = new byte[RgbaPngCodec.BufferBytes];
        int offset, count, available; uint bits;
        int Byte()
        {
            if (offset == count)
            {
                cancellation.ThrowIfCancellationRequested();
                count = input.Read(buffer); offset = 0;
                if (count == 0) throw new InvalidOperationException("Truncated PNG zlib member");
            }
            return buffer[offset++];
        }
        internal int ReadBits(int number)
        {
            while (available < number) { bits |= (uint)Byte() << available; available += 8; }
            var value = (int)(bits & ((1u << number) - 1)); bits >>= number; available -= number; return value;
        }
        internal void Align() { var discard = available % 8; bits >>= discard; available -= discard; }
        internal void SkipBytes(int bytes)
        {
            while (bytes > 0 && available >= 8) { ReadBits(8); bytes--; }
            while (bytes > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                if (offset == count) { Byte(); offset--; }
                var used = Math.Min(bytes, count - offset); offset += used; bytes -= used;
            }
        }
        internal bool AtEnd()
        {
            cancellation.ThrowIfCancellationRequested();
            if (available != 0 || offset != count) return false;
            return input.Read(buffer.AsSpan(0, 1)) == 0;
        }
    }
}
