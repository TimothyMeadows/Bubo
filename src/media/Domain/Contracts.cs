using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Bubo.Media.Domain;

/// <summary>Versioned production input, independent of any host instruction format.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MediaRequest
{
    public required string SchemaVersion { get; init; }
    public required string RequestId { get; init; }
    public required string Backend { get; init; }
    public required string Task { get; init; }
    public required string OutputDirectory { get; init; }
    public required FileIdentity Rights { get; init; }
    public required FileIdentity Installation { get; init; }
    public required ExecutionSettings Settings { get; init; }
    public required OutputBudget Budget { get; init; }
    public required GenerationItem[] Items { get; init; }
    public AcceptanceAttempt? Acceptance { get; init; }

    public void Validate(BackendCapability capability)
    {
        if (SchemaVersion != "bubo-media-request/v1") throw Invalid("Unsupported media request schema");
        Identity(RequestId, "requestId"); Identity(Task, "task");
        if (Backend != capability.Id) throw Invalid("Selected backend does not match the capability");
        if (string.IsNullOrWhiteSpace(OutputDirectory)) throw Invalid("Explicit staging output directory is required");
        if (Rights is null || Installation is null || Settings is null || Budget is null)
            throw Invalid("Rights, installation, settings and budget objects are required");
        Rights.Validate(); Installation.Validate(); Settings.Validate(capability); Budget.Validate();
        if (Items is null || Items.Length < 1 || Items.Length > Math.Min(4, capability.MaximumBatch))
            throw Invalid("Media batch must contain 1..4 supported items");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Items)
        {
            if (item is null) throw Invalid("Null batch item");
            item.Validate(capability.Modality, Budget);
            if (!ids.Add(item.Id)) throw Invalid("Duplicate batch item identity");
        }
        if (Items.Length > Budget.MaximumOutputs) throw Invalid("Batch exceeds declared output count");
        var acceptanceCase=capability.Modality=="music"?Items[0].Music!.Mode:capability.Modality;
        Acceptance?.Validate(acceptanceCase, Items.Length);
    }

    public static void Identity(string value, string name)
    {
        if (value is null || value.Length > 80 || !Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant) || value is "." or "..")
            throw Invalid("Invalid " + name);
    }
    static InvalidOperationException Invalid(string message) => new(message);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FileIdentity(string Path, string Sha256)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Path) || Sha256 is null || !Regex.IsMatch(Sha256, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("Explicit path and lowercase SHA-256 are required");
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExecutionSettings(string Device, string Precision, string Offload)
{
    public void Validate(BackendCapability capability)
    {
        if (!capability.Devices.Contains(Device, StringComparer.Ordinal) || !capability.Precisions.Contains(Precision, StringComparer.Ordinal)
            || !capability.OffloadModes.Contains(Offload, StringComparer.Ordinal))
            throw new InvalidOperationException("Unsupported explicit device, precision or offload; no fallback is permitted");
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OutputBudget(int MaximumOutputs, long MaximumBytesPerOutput, long MaximumTotalBytes, long MaximumPixels, double MaximumAudioSeconds)
{
    public void Validate()
    {
        if (MaximumOutputs is < 1 or > 4 || MaximumBytesPerOutput is < 1024 or > 268435456
            || MaximumTotalBytes < MaximumBytesPerOutput || MaximumTotalBytes > 1073741824
            || MaximumPixels is < 4096 or > 4194304 || !double.IsFinite(MaximumAudioSeconds) || MaximumAudioSeconds is < 0.25 or > 600)
            throw new InvalidOperationException("Finite explicit output budgets exceed supported limits");
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GenerationItem
{
    public required string Id { get; init; }
    public required string Prompt { get; init; }
    public string NegativePrompt { get; init; } = "";
    public required long Seed { get; init; }
    public required int Steps { get; init; }
    public required double Guidance { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public double? Seconds { get; init; }
    public bool LoopCandidate { get; init; }
    public MusicOptions? Music { get; init; }

    public void Validate(string modality, OutputBudget budget)
    {
        MediaRequest.Identity(Id, "item id");
        if (string.IsNullOrWhiteSpace(Prompt) || Prompt.Length > 4096 || NegativePrompt is null || NegativePrompt.Length > 4096
            || Prompt.Contains('\0') || NegativePrompt.Contains('\0') || Seed < 0 || !double.IsFinite(Guidance))
            throw new InvalidOperationException("Prompt, negative prompt, explicit nonnegative seed and finite guidance are required");
        if(modality!="music" && Music is not null)
            throw new InvalidOperationException("Music options cannot be silently ignored by another modality");
        switch (modality)
        {
            case "image":
                if (System.Text.Encoding.UTF8.GetByteCount(Prompt)>4096 || Width is not { } width || Height is not { } height || width is < 64 or > 2048 || height is < 64 or > 2048
                    || width % 64 != 0 || height % 64 != 0 || checked((long)width * height) > budget.MaximumPixels
                    || Seconds is not null || LoopCandidate || NegativePrompt.Length != 0 || Steps is < 1 or > 4 || Guidance != 1)
                    throw new InvalidOperationException("FLUX schnell requires 64-aligned bounded dimensions,1..4steps and guidance1; audio/loop options are unsupported");
                break;
            case "sfx":
                if (Seconds is not { } seconds || !double.IsFinite(seconds) || seconds is < 0.25 or > 47 || seconds > budget.MaximumAudioSeconds
                    || Width is not null || Height is not null || Steps is < 1 or > 200 || Guidance is < 1 or > 12 || (Guidance<=1 && NegativePrompt.Length!=0))
                    throw new InvalidOperationException("Stable Audio requires bounded duration,1..200steps and guidance1..12; image dimensions are unsupported");
                break;
            case "music":
                if(Music is null || Prompt.Length>512 || Seconds is not { } duration || !double.IsFinite(duration) || duration is <10 or >600 || duration!=Math.Truncate(duration)
                    || duration>budget.MaximumAudioSeconds || Width is not null || Height is not null || NegativePrompt.Length!=0
                    || Steps is <1 or >8 || Guidance!=1 || Seed>int.MaxValue)
                    throw new InvalidOperationException("ACE-Step Turbo requires explicit music mode, bounded caption/whole-second duration,1..8steps,guidance1 and a nonnegative int32 seed; image/negative-prompt controls are unsupported");
                Music.Validate();
                break;
            default: throw new InvalidOperationException("This backend modality is not implemented");
        }
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MusicOptions(string Mode,string Lyrics,string VocalLanguage,int? Bpm,string KeyScale,string TimeSignature,double Shift)
{
    static readonly HashSet<string> Languages=new("ar az bg bn ca cs da de el en es fa fi fr he hi hr ht hu id is it ja ko la lt ms ne nl no pa pl pt ro ru sa sk sr sv sw ta te th tl tr uk ur vi yue zh unknown".Split(' '),StringComparer.Ordinal);
    public void Validate()
    {
        if(Mode is not ("instrumental" or "supplied-lyrics") || Lyrics is null || Lyrics.Length>4096 || Lyrics.Contains('\0')
            || VocalLanguage is null || KeyScale is null || TimeSignature is null || !double.IsFinite(Shift) || Shift is <1 or >5
            || Bpm is { } bpm && bpm is <30 or >300)
            throw new InvalidOperationException("Explicit bounded music mode,lyrics,language,metadata and shift are required");
        if(Mode=="instrumental" ? Lyrics.Length!=0 || VocalLanguage!="unknown" : string.IsNullOrWhiteSpace(Lyrics) || VocalLanguage=="unknown" || !Languages.Contains(VocalLanguage))
            throw new InvalidOperationException("Instrumental requires empty supplied lyrics and unknown language; singing requires actual supplied lyrics and an explicit language code");
        if(KeyScale!="" && !Regex.IsMatch(KeyScale,"^[A-G](#|b)? (major|minor)$",RegexOptions.CultureInvariant|RegexOptions.IgnoreCase)
            || TimeSignature is not ("" or "2" or "3" or "4" or "6"))
            throw new InvalidOperationException("Unsupported explicit music key or time signature");
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AcceptanceAttempt(string Case, string Kind, FileIdentity Approval)
{
    public static readonly string[] Cases = ["image", "sfx", "instrumental", "supplied-lyrics", "preset-speech", "consented-cloning"];
    public void Validate(string modality, int outputs)
    {
        Approval.Validate();
        if (!Cases.Contains(Case, StringComparer.Ordinal) || Kind is not ("candidate" or "revision") || outputs != 1 || Case != modality)
            throw new InvalidOperationException("Acceptance must select the matching case, one output and candidate/revision");
    }
}

public sealed record BackendCapability(string Id, string Modality, string[] Devices, string[] Precisions, string[] OffloadModes, int MaximumBatch, string WorkerProtocol);

public static class MediaCapabilities
{
    public static IReadOnlyList<BackendCapability> Implemented { get; } = Array.AsReadOnly(new[]
    {
        new BackendCapability("flux-schnell", "image", ["cuda:0"], ["q8_0"], ["text-encoder-cpu"], 4, "bubo-flux-worker/v1"),
        new BackendCapability("stable-audio-open", "sfx", ["cuda:0"], ["float16", "float32"], ["none"], 4, "bubo-sfx-worker/v1"),
        new BackendCapability("ace-step-turbo", "music", ["cuda:0"], ["bfloat16"], ["none"], 4, "bubo-music-worker/v1")
    });
    public static BackendCapability Require(string id) => Implemented.SingleOrDefault(x => x.Id == id)
        ?? throw new InvalidOperationException("Unknown or unimplemented media backend; no provider fallback");
}

public static class MediaJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        WriteIndented = true,
        MaxDepth = 32
    };
    public static T Read<T>(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 2 or > 1048576) throw new InvalidOperationException("Media JSON must contain 2bytes..1MiB");
        using var doc = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        Duplicates(doc.RootElement);
        return JsonSerializer.Deserialize<T>(bytes, Options) ?? throw new InvalidOperationException("Media JSON is null");
    }
    static void Duplicates(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in node.EnumerateObject())
            {
                if (!keys.Add(p.Name)) throw new InvalidOperationException("Duplicate media JSON property");
                Duplicates(p.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var item in node.EnumerateArray()) Duplicates(item);
    }
}
