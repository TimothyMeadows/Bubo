using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Hephaera.ProofToModel.Domain;

public sealed record CanvasFile(string Path, string Sha256, long? Bytes = null);
public sealed record CanvasRuntimeFile(string AbsolutePath, string Sha256, long Bytes);

/// <summary>Explicit executable selection; not an installer or inference fallback.</summary>
public sealed record CanvasMattingProfile(CanvasFile BaseToolchain, CanvasFile Executable, IReadOnlyList<CanvasFile> Files, IReadOnlyList<CanvasRuntimeFile> ExistingRuntimeDependencies)
{
    public const string Id = "birefnet-source-canvas-v1";
    public const int MaximumBytes = 65536, MaximumChildren = 4;
    public const long MaximumBundleBytes = 536870912;
    public static CanvasMattingProfile Parse(byte[] bytes)
    {
        var root = CanvasJson.Parse(bytes);
        string[] required = ["schemaVersion", "id", "mode", "baseToolchain", "executable", "files", "maxMattingChildren"];
        CanvasJson.Fields(root, root.ContainsKey("existingRuntimeDependencies") ? [.. required, "existingRuntimeDependencies"] : required);
        if (root["schemaVersion"]!.GetValue<int>() != 1 || root["id"]!.GetValue<string>() != Id || root["mode"]!.GetValue<string>() != "multi-view-four" || root["maxMattingChildren"]!.GetValue<int>() != MaximumChildren)
            throw new InvalidOperationException("Explicit four-view source-canvas matting profile required");
        var baseToolchain = CanvasJson.Reference(root["baseToolchain"]!, false);
        var executable = CanvasJson.Reference(root["executable"]!, true);
        var files = root["files"] as JsonArray ?? throw new InvalidOperationException("Matting bundle files required");
        if (files.Count is < 1 or > 32) throw new InvalidOperationException("Matting bundle requires1..32 exact files");
        var entries = files.Select(file => CanvasJson.Reference(file!, true)).ToArray();
        if (entries.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Length || entries.Sum(file => file.Bytes!.Value) > MaximumBundleBytes || entries.Count(file => file == executable) != 1)
            throw new InvalidOperationException("Matting bundle must contain its executable exactly once and remain within512MiB");
        var bundleParent = executable.Path[..executable.Path.LastIndexOf('/')];
        if (entries.Any(file => !file.Path.StartsWith(bundleParent + "/", StringComparison.Ordinal))) throw new InvalidOperationException("Matting files must remain within the sealed executable bundle");
        var runtimes = new List<CanvasRuntimeFile>();
        if (root["existingRuntimeDependencies"] is JsonArray runtimeArray)
        {
            if (runtimeArray.Count is < 1 or > 3) throw new InvalidOperationException("Existing CUDA prerequisites require1..3 explicit DLLs");
            foreach (var node in runtimeArray)
            {
                var runtime = node as JsonObject ?? throw new InvalidOperationException("Existing CUDA dependency object required");
                CanvasJson.Fields(runtime, "absolutePath", "sha256", "bytes");
                var path = runtime["absolutePath"]!.GetValue<string>(); var hash = runtime["sha256"]!.GetValue<string>(); var bytesCount = runtime["bytes"]!.GetValue<long>();
                if (!Regex.IsMatch(path, @"^[A-Za-z]:/[^:\\]+/CUDA/v13\.2/bin/x64/(cublas64_13|cublasLt64_13|cudart64_13)\.dll\z") || path.Split('/').Skip(1).Any(part => part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') || part.Any(char.IsControl)) || hash.Length != 64 || !Regex.IsMatch(hash, "^[a-f0-9]{64}\\z") || bytesCount is < 1 or > MaximumBundleBytes)
                    throw new InvalidOperationException("Exact existing CUDA13.2 absolute DLL identity required");
                runtimes.Add(new(path, hash, bytesCount));
            }
            if (runtimes.Select(file => file.AbsolutePath[..file.AbsolutePath.LastIndexOf('/')]).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1 || runtimes.Select(file => file.AbsolutePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != runtimes.Count)
                throw new InvalidOperationException("Existing CUDA DLLs must share one exact parent without aliases");
            var names = entries.Select(file => file.Path[(file.Path.LastIndexOf('/') + 1)..]).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (runtimes.Any(file => names.Contains(file.AbsolutePath[(file.AbsolutePath.LastIndexOf('/') + 1)..]))) throw new InvalidOperationException("Bundle cannot shadow declared existing CUDA DLL basenames");
        }
        else if (root.ContainsKey("existingRuntimeDependencies")) throw new InvalidOperationException("Existing CUDA dependencies must be an explicit array");
        return new(baseToolchain, executable, entries, runtimes);
    }
    public static string[] PreserveCanvasArguments(string[] arguments)
    {
        if (arguments.Length != 8 || arguments[0] != "--models" || arguments[2] != "--bg-removal" || arguments[3] != "birefnet" || arguments[4] != "--bg-only" || arguments[5] != "--dump-bg" || new[] { 1, 6, 7 }.Any(index => string.IsNullOrWhiteSpace(arguments[index]) || arguments[index].StartsWith("--", StringComparison.Ordinal)))
            throw new InvalidOperationException("Canvas executable only accepts the existing explicit BiRefNet/bg-only template; no inference flags");
        // CUDA_VISIBLE_DEVICES binds the owner-selected physical device; the
        // matting child sees it at local ordinal0. Refuse CPU fallback explicitly.
        return arguments.Take(6).Concat(["--bg-preserve-canvas", "--require-gpu", "--gpu", "0"]).Concat(arguments.Skip(6)).ToArray();
    }
}

/// <summary>Exact retained expenditure. This assertion does not authenticate a human.</summary>
public sealed record CanvasStageLineage(string Task, string Asset, string SourceVersion, string TargetVersion,
    CanvasFile Authorization, CanvasFile InputApproval, CanvasFile? PriorManifest, string PriorStateRoot, string PriorStaging,
    IReadOnlyDictionary<string, string[]> PriorAttempts, IReadOnlyDictionary<string, string[]> PriorDirectories, IReadOnlyDictionary<string, int> PriorHistoryCounts,
    IReadOnlyDictionary<string, int> StageLimits)
{
    public static readonly string[] Stages = ["classify", "prepare-proofs", "preprocess", "reconstruct", "finish", "guided", "review"];
    public CanvasFile? InheritedLineage { get; init; }
    public static CanvasStageLineage Parse(byte[] bytes)
    {
        var root = CanvasJson.Parse(bytes);
        if (root["schemaVersion"]?.GetValue<int>() == 2) return CanvasChainedLineage.Parse(root);
        CanvasJson.Fields(root, "schemaVersion", "kind", "decision", "task", "asset", "sourceVersion", "targetVersion", "authorization", "inputApproval", "priorManifest", "priorStateRoot", "priorStaging", "priorAttempts", "priorDirectories", "priorHistoryCounts", "stageLimits");
        if (root["schemaVersion"]!.GetValue<int>() != 1 || root["kind"]!.GetValue<string>() != "proof-to-model-stage-lineage" || root["decision"]!.GetValue<string>() != "approved") throw new InvalidOperationException("Explicit approved stage lineage required");
        var task = CanvasJson.Id(root, "task"); var asset = CanvasJson.Id(root, "asset");
        var source = CanvasJson.Id(root, "sourceVersion"); var target = CanvasJson.Id(root, "targetVersion");
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Canvas migration requires a fresh explicit version");
        var attempts = root["priorAttempts"] as JsonObject ?? throw new InvalidOperationException("Prior attempt lists required");
        var history = root["priorHistoryCounts"] as JsonObject ?? throw new InvalidOperationException("Prior history counts required");
        var directories = root["priorDirectories"] as JsonObject ?? throw new InvalidOperationException("Prior directory snapshots required");
        var limits = root["stageLimits"] as JsonObject ?? throw new InvalidOperationException("Stage limits required");
        CanvasJson.Fields(attempts, Stages); CanvasJson.Fields(directories, Stages); CanvasJson.Fields(history, Stages); CanvasJson.Fields(limits, Stages);
        var entries = new Dictionary<string, string[]>(); var directoryEntries = new Dictionary<string, string[]>(); var counts = new Dictionary<string, int>(); var bounds = new Dictionary<string, int>();
        foreach (var stage in Stages)
        {
            var list = attempts[stage] as JsonArray ?? throw new InvalidOperationException("Attempt list required for " + stage);
            if (list.Count > 4) throw new InvalidOperationException("Retained attempt list exceeds the existing bounded stage allowance");
            var paths = list.Select(value => CanvasJson.Path(value!.GetValue<string>())).ToArray();
            if (!paths.SequenceEqual(paths.Order(StringComparer.Ordinal), StringComparer.Ordinal) || paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length) throw new InvalidOperationException("Attempt paths must be exact sorted unique paths");
            var directoryList = directories[stage] as JsonArray ?? throw new InvalidOperationException("Prior directory list required");
            if (directoryList.Count > 4) throw new InvalidOperationException("Prior directory count exceeded");
            var directoryPaths = directoryList.Select(value => CanvasJson.Path(value!.GetValue<string>())).ToArray();
            if (!directoryPaths.SequenceEqual(directoryPaths.Order(StringComparer.Ordinal), StringComparer.Ordinal) || directoryPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != directoryPaths.Length || directoryPaths.Any(path => !paths.Contains(path, StringComparer.Ordinal))) throw new InvalidOperationException("Prior directory snapshot must be sorted/unique and included in spent attempts");
            var persisted = history[stage]!.GetValue<int>(); var maximum = limits[stage]!.GetValue<int>();
            var expected = stage is "classify" or "prepare-proofs" ? 4 : stage == "preprocess" ? maximum : 1;
            if (maximum != expected || maximum is < 1 or > 4 || stage == "preprocess" && maximum is not (1 or 2) || persisted < 0 || persisted > paths.Length || paths.Length > maximum) throw new InvalidOperationException("Stage lineage cannot expand metadata/model/finish budgets");
            entries[stage] = paths; directoryEntries[stage] = directoryPaths; counts[stage] = persisted; bounds[stage] = maximum;
        }
        if (bounds["preprocess"] == 2 && entries["preprocess"].Length != 1) throw new InvalidOperationException("Extra preprocessing requires exactly one retained prior failure");
        var prior = root["priorManifest"] is null ? null : CanvasJson.Reference(root["priorManifest"]!, false);
        if (prior is null && (entries.Values.Any(paths => paths.Length != 0) || counts.Values.Any(value => value != 0))) throw new InvalidOperationException("Absent predecessor cannot erase retained attempts");
        return new(task, asset, source, target, CanvasJson.Reference(root["authorization"]!, false), CanvasJson.Reference(root["inputApproval"]!, false), prior,
            CanvasJson.Path(root["priorStateRoot"]!.GetValue<string>()), CanvasJson.Path(root["priorStaging"]!.GetValue<string>()), entries, directoryEntries, counts, bounds);
    }
    public void ValidateScope(string task, string asset, string version)
    {
        if (Task != task || Asset != asset || TargetVersion != version) throw new InvalidOperationException("Stage lineage belongs to another task/class/version");
    }
    public int Limit(string stage, int configured)
    {
        var limit = StageLimits[stage];
        if (InheritedLineage is not null && stage == "preprocess" && configured == 1 && limit == 3 && PriorAttempts[stage].Length == 1) return limit;
        if (stage != "preprocess" && limit > configured || stage == "preprocess" && limit != configured && !(configured == 1 && limit == 2 && PriorAttempts[stage].Length == 1)) throw new InvalidOperationException("Stage lineage cannot widen the selected configured budget");
        return limit;
    }
    public void ValidatePrior(string stage, IEnumerable<string> attempts, IEnumerable<string> directories, int historyCount)
    {
        if (!attempts.SequenceEqual(PriorAttempts[stage], StringComparer.Ordinal) || !directories.SequenceEqual(PriorDirectories[stage], StringComparer.Ordinal) || historyCount != PriorHistoryCounts[stage])
            throw new InvalidOperationException("Prior attempt directories/history changed, including empty failed starts");
    }
}

static class CanvasJson
{
    public static JsonObject Parse(byte[] bytes)
    {
        if (bytes.Length is < 2 or > CanvasMattingProfile.MaximumBytes) throw new InvalidOperationException("Canvas metadata must be2..65536 UTF-8 bytes");
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 });
        void Check(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object) { var seen = new HashSet<string>(StringComparer.Ordinal); foreach (var field in value.EnumerateObject()) { if (!seen.Add(field.Name)) throw new InvalidOperationException("Duplicate canvas metadata key"); Check(field.Value); } }
            else if (value.ValueKind == JsonValueKind.Array) { if (value.GetArrayLength() > 128) throw new InvalidOperationException("Canvas metadata array exceeded"); foreach (var item in value.EnumerateArray()) Check(item); }
        }
        Check(document.RootElement);
        return JsonNode.Parse(bytes) as JsonObject ?? throw new InvalidOperationException("Canvas metadata object required");
    }
    public static void Fields(JsonObject value, params string[] expected)
    { if (value.Count != expected.Length || value.Any(field => !expected.Contains(field.Key, StringComparer.Ordinal))) throw new InvalidOperationException("Unknown/missing canvas metadata fields"); }
    public static string Id(JsonObject value, string key) => Regex.IsMatch(value[key]!.GetValue<string>(), "^[A-Za-z0-9][A-Za-z0-9._-]{0,79}\\z") && value[key]!.GetValue<string>() != ".." ? value[key]!.GetValue<string>() : throw new InvalidOperationException("Safe canvas identity required");
    public static string Path(string path)
    { if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.Contains(':') || path.StartsWith('/') || path.Split('/').Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.Any(char.IsControl))) throw new InvalidOperationException("Exact relative canvas path required"); return path; }
    public static CanvasFile Reference(JsonNode value, bool sized)
    {
        var item = value as JsonObject ?? throw new InvalidOperationException("Canvas reference required"); Fields(item, sized ? ["path", "sha256", "bytes"] : ["path", "sha256"]);
        var hash = item["sha256"]!.GetValue<string>(); if (hash.Length != 64 || !Regex.IsMatch(hash, "^[a-f0-9]{64}\\z")) throw new InvalidOperationException("Exact canvas SHA required");
        long? bytes = sized ? item["bytes"]!.GetValue<long>() : null; if (sized && bytes is < 1 or > CanvasMattingProfile.MaximumBundleBytes) throw new InvalidOperationException("Bounded canvas bundle file required");
        return new(Path(item["path"]!.GetValue<string>()), hash, bytes);
    }
}
