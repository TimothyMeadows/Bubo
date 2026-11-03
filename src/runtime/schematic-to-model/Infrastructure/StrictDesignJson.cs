using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bubo.Schematic.Infrastructure;

public static class StrictDesignJson
{
    public const int MaximumInputBytes = 8 * 1024 * 1024;
    public static readonly JsonSerializerOptions Options = CreateOptions();
    static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true,
            MaxDepth = 64, WriteIndented = true, NewLine = "\n"
        };
        options.Converters.Add(new ClosedEnumFactory());
        return options;
    }
    public static T Parse<T>(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 2 or > MaximumInputBytes) throw new InvalidOperationException("Missing or oversized schematic JSON");
        using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 64 });
        Check(document.RootElement);
        return JsonSerializer.Deserialize<T>(bytes, Options) ?? throw new InvalidOperationException("Null schematic JSON");
    }
    public static T Read<T>(string file) => Parse<T>(ReadBytes(file));
    public static byte[] ReadBytes(string file)
    {
        var info = new FileInfo(file);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Missing or linked schematic JSON");
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < 2 or > MaximumInputBytes) throw new InvalidOperationException("Missing or oversized schematic JSON");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new InvalidOperationException("Schematic input grew while reading");
        return bytes;
    }
    static void Check(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate schematic JSON property: " + property.Name);
                Check(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Check(item);
    }
    sealed class ClosedEnumFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type type) => type.IsEnum;
        public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options)
            => (JsonConverter)Activator.CreateInstance(typeof(ClosedEnum<>).MakeGenericType(type))!;
    }
    sealed class ClosedEnum<T> : JsonConverter<T> where T : struct, Enum
    {
        static readonly Dictionary<string, T> Values = Enum.GetValues<T>().ToDictionary(x => JsonNamingPolicy.CamelCase.ConvertName(x.ToString()), StringComparer.Ordinal);
        public override T Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
            => reader.TokenType == JsonTokenType.String && Values.TryGetValue(reader.GetString()!, out var value)
                ? value : throw new JsonException("Explicit canonical schematic enum name required");
        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            if (!Enum.IsDefined(value)) throw new JsonException("Unknown schematic enum value");
            writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
        }
    }
}
