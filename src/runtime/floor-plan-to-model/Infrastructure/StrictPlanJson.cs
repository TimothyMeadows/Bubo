using System.Text.Json;
using System.Text.Json.Serialization;
using Bubo.FloorPlan.Domain;
namespace Bubo.FloorPlan.Infrastructure;

public static class StrictPlanJson
{
    public const int MaximumInputBytes = 8 * 1024 * 1024;
    public static readonly JsonSerializerOptions Options = CreateOptions();
    static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true,
            MaxDepth = 64, WriteIndented = true
        };
        options.Converters.Add(new Point2Converter());
        options.Converters.Add(new Point3Converter());
        options.Converters.Add(new ClosedEnumFactory());
        return options;
    }
    public static T Parse<T>(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 2 or > MaximumInputBytes) throw new InvalidOperationException("Missing or oversized native design JSON");
        using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 64 });
        Check(document.RootElement);
        return JsonSerializer.Deserialize<T>(bytes, Options) ?? throw new InvalidOperationException("Null native design JSON");
    }
    public static T Read<T>(string file)
    {
        var info = new FileInfo(file);
        if (!info.Exists || info.Length is < 2 or > MaximumInputBytes || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Missing, linked or oversized native design JSON");
        return Parse<T>(ReadBytes(file));
    }
    public static byte[] ReadBytes(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        var length = stream.Length;
        if (length is < 2 or > MaximumInputBytes) throw new InvalidOperationException("Missing or oversized native design JSON");
        var bytes = new byte[(int)length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new InvalidOperationException("Native design input grew while reading");
        return bytes;
    }
    static void Check(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidOperationException("Duplicate JSON property: " + property.Name);
                Check(property.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array) foreach (var item in node.EnumerateArray()) Check(item);
    }
    static double[] Coordinates(ref Utf8JsonReader reader, params string[] axes)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("Point coordinates require an object");
        var names = value.EnumerateObject().Select(x => x.Name).ToArray();
        if (names.Length != axes.Length || names.Distinct(StringComparer.Ordinal).Count() != axes.Length || !axes.All(x => names.Contains(x, StringComparer.Ordinal)))
            throw new JsonException("Every point axis must be explicit exactly once with canonical case");
        var result = new double[axes.Length];
        for (var i = 0; i < axes.Length; i++)
        {
            var coordinate = value.GetProperty(axes[i]);
            if (coordinate.ValueKind != JsonValueKind.Number || !coordinate.TryGetDouble(out result[i]) || !double.IsFinite(result[i])) throw new JsonException("Point coordinates must be finite numbers");
        }
        return result;
    }
    sealed class Point2Converter : JsonConverter<Point2>
    {
        public override Point2 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        { var p = Coordinates(ref reader, "x", "y"); return new(p[0], p[1]); }
        public override void Write(Utf8JsonWriter writer, Point2 value, JsonSerializerOptions options)
        { writer.WriteStartObject(); writer.WriteNumber("x", value.X); writer.WriteNumber("y", value.Y); writer.WriteEndObject(); }
    }
    sealed class Point3Converter : JsonConverter<Point3>
    {
        public override Point3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        { var p = Coordinates(ref reader, "x", "y", "z"); return new(p[0], p[1], p[2]); }
        public override void Write(Utf8JsonWriter writer, Point3 value, JsonSerializerOptions options)
        { writer.WriteStartObject(); writer.WriteNumber("x", value.X); writer.WriteNumber("y", value.Y); writer.WriteNumber("z", value.Z); writer.WriteEndObject(); }
    }
    sealed class ClosedEnumFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;
        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
            => (JsonConverter)Activator.CreateInstance(typeof(ClosedEnum<>).MakeGenericType(typeToConvert))!;
    }
    sealed class ClosedEnum<T> : JsonConverter<T> where T : struct, Enum
    {
        static readonly Dictionary<string, T> Names = Enum.GetValues<T>().ToDictionary(x => JsonNamingPolicy.CamelCase.ConvertName(x.ToString()), StringComparer.Ordinal);
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String || !Names.TryGetValue(reader.GetString()!, out var value)) throw new JsonException("Unknown or noncanonical enum value for " + typeof(T).Name);
            return value;
        }
        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            if (!Enum.IsDefined(value)) throw new JsonException("Undefined enum value");
            writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
        }
    }
}
