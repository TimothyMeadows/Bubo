using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hephaera.AssetProduction;

namespace Bubo.Vfx.Tests;

/// <summary>Newly authored test pixels, encoded independently of the production codec.</summary>
internal static class PreparationFixtures
{
    internal sealed class Owned
    {
        internal const string RequestPath = "inputs/preparation/request.json";
        internal readonly Fixture Host;
        internal readonly JsonObject Request;
        internal readonly byte[][] Frames = [Pattern(2, 3), Pattern(2, 3, 1), Pattern(2, 3, 2)];
        internal string Root => Host.Root;

        internal Owned(string root, string profile = "sequence")
        {
            Host = new Fixture(root);
            for (var i = 0; i < Frames.Length; i++) Host.Write($"inputs/preparation/frame-{i}.png", Png(2, 3, Frames[i], i));
            Request = new JsonObject
            {
                ["schemaVersion"] = "bubo-vfx-preparation/v1", ["id"] = "owned-atlas", ["version"] = "v1",
                ["styleSha256"] = Host.Hash("STYLE.md"), ["engine"] = Host.Package["engine"]!.DeepClone(),
                ["profile"] = profile,
                ["sources"] = new JsonArray(Enumerable.Range(0, Frames.Length).Select(i => (JsonNode)Source($"inputs/preparation/frame-{i}.png")).ToArray()),
                ["timing"] = new JsonObject { ["firstFrame"] = 0, ["frameCount"] = 3, ["frameStep"] = 1, ["rateNumerator"] = 24, ["rateDenominator"] = 1, ["loop"] = "repeat" },
                ["atlas"] = new JsonObject { ["columns"] = 2, ["padding"] = 1, ["extrusion"] = 1, ["alpha"] = "straight", ["colorSpace"] = "srgb" },
                ["budgets"] = new JsonObject
                {
                    ["maxSourceBytes"] = 4096, ["maxTotalSourceBytes"] = 16384, ["maxChunkBytes"] = 4096,
                    ["maxCompressedBytes"] = 4096, ["maxFrames"] = 8, ["maxDecodedPixels"] = 4096,
                    ["maxAtlasPixels"] = 65536, ["maxPixelWork"] = 1048576, ["maxWorkingBytes"] = 33554432,
                    ["maxOutputBytes"] = 1048576, ["maxOutputFiles"] = 16, ["maxSeconds"] = 15, ["minimumFreeDiskBytes"] = 0
                }
            };
            if (profile == "sheet")
            {
                var sheet = new byte[7 * 9 * 4];
                Paste(sheet, 7, Frames[0], 2, 3, 1, 1);
                Paste(sheet, 7, Frames[1], 2, 3, 4, 1);
                Paste(sheet, 7, Frames[2], 2, 3, 1, 5);
                Paste(sheet, 7, Frames[0], 2, 3, 4, 5);
                Host.Write("inputs/preparation/sheet.png", Png(7, 9, sheet, 4));
                Request["sources"] = new JsonArray(Source("inputs/preparation/sheet.png"));
                Request["sheet"] = new JsonObject { ["columns"] = 2, ["rows"] = 2, ["cellWidth"] = 2, ["cellHeight"] = 3, ["originX"] = 1, ["originY"] = 1, ["strideX"] = 3, ["strideY"] = 4, ["frameIndices"] = new JsonArray(2, 0, 1) };
            }
            else if (profile == "math-mask")
            {
                Request["sources"] = new JsonArray();
                Request["timing"]!["frameCount"] = 1;
                Request["timing"]!["loop"] = "once";
                Request["atlas"]!["columns"] = 1;
                Request["mask"] = new JsonObject { ["kind"] = "disc", ["width"] = 9, ["height"] = 7, ["innerRadius"] = 0.0, ["outerRadius"] = 0.8, ["softness"] = 0.2, ["angleDegrees"] = 0.0 };
            }
            Save();
        }

        internal JsonObject Source(string path) => new() { ["path"] = path, ["sha256"] = Host.Hash(path), ["bytes"] = new FileInfo(Host.Full(path)).Length };
        internal void Save()
        {
            Host.Write(RequestPath, JsonSerializer.SerializeToUtf8Bytes(Request));
            var references = Host.Context["references"]!.AsArray();
            while (references.Count > 5) references.RemoveAt(references.Count - 1);
            references.Add(Host.Reference(RequestPath, "input"));
            foreach (var source in Request["sources"]!.AsArray())
            {
                var path = source!["path"]!.GetValue<string>();
                if (File.Exists(Host.Full(path))) references.Add(Host.Reference(path, "input"));
            }
            Host.SaveContext();
        }
        internal void RefreshSource(int index, byte[] bytes)
        {
            var path = Request["sources"]![index]!["path"]!.GetValue<string>();
            Host.Write(path, bytes); Request["sources"]![index] = Source(path); Save();
        }
        internal string Full(string path) => Host.Full(path);
        internal Workspace Workspace() => Host.Workspace();
    }

    internal static byte[] Pattern(int width, int height, int frame = 0)
    {
        var pixels = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var offset = (y * width + x) * 4;
            pixels[offset] = (byte)(17 + (x * 37 + y * 13 + frame * 53) % 223);
            pixels[offset + 1] = (byte)(9 + (x * 11 + y * 61 + frame * 29) % 239);
            pixels[offset + 2] = (byte)(21 + (x * 71 + y * 19 + frame * 7) % 211);
            pixels[offset + 3] = (byte)(((x + y + frame) % 4) switch { 0 => 0, 1 => 63, 2 => 173, _ => 255 });
        }
        return pixels;
    }

    internal static byte[] Png(int width, int height, byte[] pixels, int filter = 0,
        bool grayAlpha = false, IReadOnlyList<(string Type, byte[] Data)>? ancillary = null,
        bool extraInflatedByte = false, bool invalidFilter = false, byte[]? compressedTail = null, int truncateCompressedBytes = 0,
        string deflateProfile = "default")
    {
        if (pixels.Length != checked(width * height * 4)) throw new InvalidOperationException("Invalid test pixels");
        var channels = grayAlpha ? 2 : 4;
        var raw = new byte[checked(height * (width * channels + 1) + (extraInflatedByte ? 1 : 0))];
        var previous = new byte[width * channels];
        for (var y = 0; y < height; y++)
        {
            var row = new byte[width * channels];
            for (var x = 0; x < width; x++)
            {
                var source = (y * width + x) * 4;
                row[x * channels] = pixels[source];
                if (!grayAlpha)
                {
                    row[x * channels + 1] = pixels[source + 1];
                    row[x * channels + 2] = pixels[source + 2];
                }
                row[x * channels + channels - 1] = pixels[source + 3];
            }
            var start = y * (width * channels + 1);
            raw[start] = (byte)(invalidFilter ? 5 : filter);
            for (var x = 0; x < row.Length; x++)
            {
                var left = x >= channels ? row[x - channels] : 0;
                var up = previous[x];
                var corner = x >= channels ? previous[x - channels] : 0;
                var predictor = filter switch
                {
                    0 => 0, 1 => left, 2 => up, 3 => (left + up) / 2,
                    4 => Paeth(left, up, corner), _ => throw new InvalidOperationException("Invalid test filter")
                };
                raw[start + 1 + x] = unchecked((byte)(row[x] - predictor));
            }
            previous = row;
        }
        using var result = new MemoryStream();
        result.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; header[9] = (byte)(grayAlpha ? 4 : 6);
        Chunk(result, "IHDR", header);
        if (ancillary is not null) foreach (var (type, data) in ancillary) Chunk(result, type, data);
        using var compressed = new MemoryStream();
        using (var zip = new ZLibStream(compressed, deflateProfile == "stored" ? CompressionLevel.NoCompression : CompressionLevel.SmallestSize, true)) zip.Write(raw);
        var encoded = deflateProfile == "fixed" ? FixedZlib(raw) : compressed.ToArray();
        if (truncateCompressedBytes > 0) encoded = encoded[..^truncateCompressedBytes];
        Chunk(result, "IDAT", compressedTail is null ? encoded : encoded.Concat(compressedTail).ToArray());
        Chunk(result, "IEND", []);
        return result.ToArray();
    }

    internal static byte[] GrayPixels(byte[] pixels)
    {
        var result = (byte[])pixels.Clone();
        for (var i = 0; i < result.Length; i += 4) result[i + 1] = result[i + 2] = result[i];
        return result;
    }

    internal static void Paste(byte[] canvas, int canvasWidth, byte[] pixels, int width, int height, int left, int top)
    {
        for (var y = 0; y < height; y++)
            pixels.AsSpan(y * width * 4, width * 4).CopyTo(canvas.AsSpan(((top + y) * canvasWidth + left) * 4));
    }

    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal static int FirstDeflateBlockType(byte[] png)
    {
        for (var offset = 8; offset < png.Length;)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
            if (Encoding.ASCII.GetString(png, offset + 4, 4) == "IDAT") return (png[offset + 10] >> 1) & 3;
            offset += length + 12;
        }
        throw new InvalidOperationException("No test IDAT");
    }

    // A literal-only fixed-code DEFLATE stream authored independently of the production token walker.
    static byte[] FixedZlib(byte[] raw)
    {
        using var result = new MemoryStream(); result.Write(new byte[] { 0x78, 0x01 });
        uint pending = 0; var count = 0;
        void Bits(uint value, int bits)
        {
            pending |= value << count; count += bits;
            while (count >= 8) { result.WriteByte((byte)pending); pending >>= 8; count -= 8; }
        }
        static uint Reverse(uint code, int bits)
        {
            uint result = 0;
            for (var i = 0; i < bits; i++) { result = (result << 1) | (code & 1); code >>= 1; }
            return result;
        }
        Bits(3, 3); // BFINAL=1, BTYPE=01, least significant bit first.
        foreach (var value in raw)
        {
            var width = value < 144 ? 8 : 9;
            var code = value < 144 ? 0x30U + value : 0x190U + value - 144;
            Bits(Reverse(code, width), width);
        }
        Bits(0, 7); // Fixed symbol256 marks the end of this sole block.
        if (count > 0) result.WriteByte((byte)pending);
        uint a = 1, b = 0;
        foreach (var value in raw) { a = (a + value) % 65521; b = (b + a) % 65521; }
        Span<byte> checksum = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(checksum, b * 65536 + a); result.Write(checksum);
        return result.ToArray();
    }

    internal static JsonObject Object(object value) => JsonSerializer.SerializeToNode(value, Workspace.Json)!.AsObject();

    static void Chunk(Stream stream, string type, byte[] data)
    {
        Span<byte> integer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(integer, data.Length); stream.Write(integer);
        var tag = Encoding.ASCII.GetBytes(type); stream.Write(tag); stream.Write(data);
        var crc = uint.MaxValue;
        foreach (var value in tag.Concat(data))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xedb88320U & (uint)-(int)(crc & 1));
        }
        BinaryPrimitives.WriteUInt32BigEndian(integer, ~crc); stream.Write(integer);
    }

    static int Paeth(int a, int b, int c)
    {
        var estimate = a + b - c;
        var da = Math.Abs(estimate - a); var db = Math.Abs(estimate - b); var dc = Math.Abs(estimate - c);
        return da <= db && da <= dc ? a : db <= dc ? b : c;
    }
}
