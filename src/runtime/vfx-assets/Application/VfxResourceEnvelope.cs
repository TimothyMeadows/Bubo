using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Bubo.Vfx.Application;

/// <summary>Finite container checks only. These do not decode pixels, meshes, materials or animations.</summary>
internal static class VfxResourceEnvelope
{
    const int MaximumGlbJsonBytes = 1024 * 1024;
    const int MaximumPngChunks = 65536;
    static readonly uint[] CrcTable = MakeCrcTable();

    internal static void Validate(VfxInputReader input, string kind, CancellationToken cancellation)
    {
        switch (kind)
        {
            case "texture-png": Png(input, cancellation); break;
            case "mesh-glb": Glb(input, cancellation); break;
            default: throw new InvalidOperationException("Unsupported VFX resource envelope");
        }
    }

    static void Png(VfxInputReader input, CancellationToken cancellation)
    {
        Span<byte> signature = stackalloc byte[8];
        input.ReadExactly(signature);
        if (!signature.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new InvalidOperationException("VFX texture is not a PNG resource");
        Span<byte> header = stackalloc byte[8];
        Span<byte> crcBytes = stackalloc byte[4];
        var data = new byte[64 * 1024];
        var ihdr = false;
        var palette = false;
        var idat = false;
        var idatEnded = false;
        long compressedBytes = 0;
        byte colorType = 0;
        for (var index = 0; index < MaximumPngChunks; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            input.ReadExactly(header);
            var length = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
            var typeBytes = header[4..];
            foreach (var value in typeBytes)
                if (value is not (>= (byte)'A' and <= (byte)'Z') and not (>= (byte)'a' and <= (byte)'z'))
                    throw new InvalidOperationException("PNG chunk has an invalid type");
            if (typeBytes[2] is >= (byte)'a' and <= (byte)'z')
                throw new InvalidOperationException("PNG chunk uses the reserved type bit");
            var type = Encoding.ASCII.GetString(typeBytes);
            if (length > int.MaxValue || length > input.Remaining - 4)
                throw new InvalidOperationException("PNG chunk exceeds its resource envelope");
            if (!ihdr && type != "IHDR") throw new InvalidOperationException("PNG must start with IHDR");
            if (type is not ("IHDR" or "PLTE" or "IDAT" or "IEND") && typeBytes[0] < (byte)'a')
                throw new InvalidOperationException("Unsupported critical PNG chunk");
            if (type == "IHDR" && (ihdr || length != 13))
                throw new InvalidOperationException("PNG must contain exactly one 13-byte IHDR");
            if (type == "PLTE" && (palette || idat || length is < 3 or > 768 || length % 3 != 0 || colorType is 0 or 4))
                throw new InvalidOperationException("PNG palette envelope is invalid");
            if (type == "IDAT" && (idatEnded || (colorType == 3 && !palette)))
                throw new InvalidOperationException("PNG IDAT order or palette is invalid");
            if (idat && type != "IDAT") idatEnded = true;
            if (type == "IEND" && (length != 0 || !idat || compressedBytes < 1))
                throw new InvalidOperationException("PNG IEND requires finite image data and no payload");

            var crc = UpdateCrc(uint.MaxValue, typeBytes);
            long remaining = length;
            while (remaining > 0)
            {
                var count = (int)Math.Min(remaining, data.Length);
                input.ReadExactly(data.AsSpan(0, count));
                crc = UpdateCrc(crc, data.AsSpan(0, count));
                if (type == "IHDR") colorType = ValidateIhdr(data.AsSpan(0, count));
                remaining -= count;
            }
            input.ReadExactly(crcBytes);
            if (BinaryPrimitives.ReadUInt32BigEndian(crcBytes) != (crc ^ uint.MaxValue))
                throw new InvalidOperationException("PNG chunk CRC differs from its bytes");
            if (type == "IHDR") ihdr = true;
            if (type == "PLTE") palette = true;
            if (type == "IDAT") { idat = true; compressedBytes += length; }
            if (type == "IEND") return;
        }
        throw new InvalidOperationException("PNG chunk count exceeds the finite envelope profile");
    }

    static byte ValidateIhdr(ReadOnlySpan<byte> data)
    {
        var width = BinaryPrimitives.ReadUInt32BigEndian(data[..4]);
        var height = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4));
        var depth = data[8];
        var color = data[9];
        var supportedDepth = color switch
        {
            0 => depth is 1 or 2 or 4 or 8 or 16,
            2 or 4 or 6 => depth is 8 or 16,
            3 => depth is 1 or 2 or 4 or 8,
            _ => false
        };
        if (width is < 1 or > int.MaxValue || height is < 1 or > int.MaxValue || !supportedDepth
            || data[10] != 0 || data[11] != 0 || data[12] > 1)
            throw new InvalidOperationException("PNG IHDR dimensions or encoding envelope is invalid");
        return color;
    }

    static void Glb(VfxInputReader input, CancellationToken cancellation)
    {
        Span<byte> header = stackalloc byte[12];
        input.ReadExactly(header);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header[..4]) != 0x46546c67
            || BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(4, 4)) != 2
            || BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(8, 4)) != input.Length)
            throw new InvalidOperationException("VFX mesh must be an exact GLB v2 resource envelope");
        Span<byte> chunk = stackalloc byte[8];
        input.ReadExactly(chunk);
        var jsonBytes = BinaryPrimitives.ReadUInt32LittleEndian(chunk[..4]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]) != 0x4e4f534a || jsonBytes < 2
            || jsonBytes > MaximumGlbJsonBytes || jsonBytes % 4 != 0 || jsonBytes > input.Remaining)
            throw new InvalidOperationException("GLB must start with a bounded aligned JSON chunk");
        var json = new byte[(int)jsonBytes];
        input.ReadExactly(json);
        long? declaredBuffer;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            InspectJson(root, cancellation);
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("asset", out var asset)
                || asset.ValueKind != JsonValueKind.Object || !asset.TryGetProperty("version", out var version)
                || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0")
                throw new InvalidOperationException("GLB JSON requires asset version 2.0");
            declaredBuffer = InternalBuffer(root, cancellation);
        }
        catch (JsonException error) { throw new InvalidOperationException("Invalid strict GLB JSON envelope", error); }

        if (input.Remaining == 0)
        {
            if (declaredBuffer is not null) throw new InvalidOperationException("GLB declares a missing internal binary buffer");
            return;
        }
        input.ReadExactly(chunk);
        var binaryBytes = BinaryPrimitives.ReadUInt32LittleEndian(chunk[..4]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]) != 0x004e4942 || binaryBytes % 4 != 0
            || binaryBytes != input.Remaining || declaredBuffer is null || binaryBytes < declaredBuffer
            || binaryBytes - declaredBuffer > 3)
            throw new InvalidOperationException("GLB binary chunk differs from its sole declared internal buffer");
        input.Skip(binaryBytes);
    }

    static long? InternalBuffer(JsonElement root, CancellationToken cancellation)
    {
        long? length = null;
        if (root.TryGetProperty("buffers", out var buffers))
        {
            if (buffers.ValueKind != JsonValueKind.Array || buffers.GetArrayLength() > 1)
                throw new InvalidOperationException("GLB envelope supports at most one internal buffer");
            if (buffers.GetArrayLength() == 1)
            {
                var buffer = buffers[0];
                if (buffer.ValueKind != JsonValueKind.Object || !buffer.TryGetProperty("byteLength", out var bytes)
                    || !bytes.TryGetInt64(out var value) || value < 1)
                    throw new InvalidOperationException("GLB internal buffer requires a positive byteLength");
                length = value;
            }
        }
        if (root.TryGetProperty("bufferViews", out var views))
        {
            if (views.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("GLB bufferViews must be an array");
            foreach (var view in views.EnumerateArray())
            {
                cancellation.ThrowIfCancellationRequested();
                if (view.ValueKind != JsonValueKind.Object || length is null
                    || !view.TryGetProperty("buffer", out var buffer) || !buffer.TryGetInt32(out var index) || index != 0
                    || !view.TryGetProperty("byteLength", out var bytes) || !bytes.TryGetInt64(out var count) || count < 1)
                    throw new InvalidOperationException("GLB bufferView does not select the internal buffer");
                long offset = 0;
                if (view.TryGetProperty("byteOffset", out var start) && (!start.TryGetInt64(out offset) || offset < 0))
                    throw new InvalidOperationException("GLB bufferView byteOffset is invalid");
                if (offset > length || count > length - offset)
                    throw new InvalidOperationException("GLB bufferView exceeds its internal buffer");
            }
        }
        return length;
    }

    static void InspectJson(JsonElement element, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                cancellation.ThrowIfCancellationRequested();
                if (!seen.Add(property.Name)) throw new InvalidOperationException("Duplicate GLB JSON property");
                if (property.Name.Equals("uri", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("GLB URI dependencies are unsupported; only internal bufferViews are admitted");
                if (property.Name is "extensions" or "extensionsUsed" or "extensionsRequired")
                    throw new InvalidOperationException("GLB extensions are unavailable in the closed resource envelope profile");
                InspectJson(property.Value, cancellation);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) InspectJson(item, cancellation);
    }

    static uint UpdateCrc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes) crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8);
        return crc;
    }

    static uint[] MakeCrcTable()
    {
        var values = new uint[256];
        for (uint index = 0; index < values.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xedb88320 ^ (value >> 1) : value >> 1;
            values[index] = value;
        }
        return values;
    }
}
