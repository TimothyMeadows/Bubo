using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bubo.Vfx.Application;
using Bubo.Vfx.Domain;
using Hephaera.AssetProduction;
using Hephaera.ModelAction.Domain;
using static Hephaera.ModelAction.Application.ActionValidation;

namespace Hephaera.ModelAction.Application;

public sealed record ActionEffectAllocation(long Vertices, long Indices, long BufferBytes, long TextureBytes);

public sealed record ActionEffectInspection(ActionEffectSource Source, VfxPackage Package,
    ActionEffectProfile Profile, ActionEffectSourceInventory SourceInventory, ActionRuntimeEffectSource RuntimeSource)
{
    public IReadOnlyDictionary<string, ActionEffectAllocation> LayerAllocations { get; init; }
        = new Dictionary<string, ActionEffectAllocation>(StringComparer.Ordinal);
}

public sealed record ActionEffectReviewSubject(string SourceId, string SubjectSha256,
    IReadOnlyList<ActionEffectFile> Inventory, bool Accepted = false);

public enum ActionRuntimeDataKind { EffectSources, ActionReceipt, RuntimeAcceptanceReceipt, ReceivingPacket }

/// <summary>Created only by current native inspection; it is not a caller-authored compiler facts object.</summary>
public sealed class ActionEffectCatalog
{
    internal ActionEffectCatalog(IReadOnlyList<ActionEffectInspection> sources) => Sources = sources;
    public IReadOnlyList<ActionEffectInspection> Sources { get; }
}

/// <summary>Complete private source ancestry and a separate, data-only portable runtime closure.</summary>
public static class ActionEffectDependencies
{
    public const int MaximumJsonBytes = 4 * 1024 * 1024;
    public const long MaximumFileBytes = 1024L * 1024 * 1024;
    static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static IReadOnlyList<ActionEffectSource> ReadSources(JsonObject recipe)
    {
        if (recipe["schemaVersion"]?.GetValue<int>() == 1)
        {
            if (recipe.ContainsKey("effectSources")) throw new InvalidOperationException("V1 cannot acquire effect source semantics");
            return [];
        }
        if (recipe["schemaVersion"]?.GetValue<int>() != 2 || recipe["effectSources"] is not JsonArray sources)
            throw new InvalidOperationException("V2 requires an explicit effect source inventory");
        _ = sources;
        return ActionCompilerV2.Sources(recipe);
    }

    public static ActionEffectCatalog Inspect(IWorkspace workspace, JsonObject recipe,
        ActionEffectInspectionContext context, IActionEffectDependencyInspector inspector, CancellationToken cancellation)
    {
        var declarations = ReadSources(recipe);
        if (declarations.Count == 0 && recipe["schemaVersion"]?.GetValue<int>() == 1) return new([]);
        var maximumSources = Integer(context.Budgets, "maxEffectSources", 1, 64);
        if (declarations.Count is < 1 || declarations.Count > maximumSources) throw new InvalidOperationException("Effect source count exceeds its finite budget");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sources = new List<ActionEffectInspection>();
        long bytes = 0, jsonBytes = 0; var files = 0;
        var maxFiles = Integer(context.Budgets, "maxEffectFiles", 1, 16384);
        var maxBytes = LongBudget(context.Budgets, "maxEffectDependencyBytes", 1, 4L * 1024 * 1024 * 1024);
        var maxJson = LongBudget(context.Budgets, "maxEffectJsonBytes", 1, 256L * 1024 * 1024);
        foreach (var source in declarations)
        {
            cancellation.ThrowIfCancellationRequested();
            Id(source.Id); Id(source.EffectId);
            if (!names.Add(source.Id)) throw new InvalidOperationException("Duplicate or case-aliased effect source ID");
            var remaining = context.Budgets.DeepClone().AsObject();
            remaining["maxEffectFiles"] = maxFiles - files;
            remaining["maxEffectDependencyBytes"] = maxBytes - bytes;
            remaining["maxEffectJsonBytes"] = maxJson - jsonBytes;
            if (maxFiles <= files || maxBytes <= bytes || maxJson <= jsonBytes) throw new InvalidOperationException("Effect source closure budget exhausted");
            var observed = inspector.Inspect(workspace, source, context with { Budgets = remaining }, cancellation);
            if (observed.Source != source) throw new InvalidOperationException("Effect inspector changed the source declaration");
            files = checked(files + observed.SourceInventory.Files.Count);
            bytes = checked(bytes + observed.SourceInventory.Files.Sum(file => file.Bytes));
            jsonBytes = checked(jsonBytes + observed.SourceInventory.Files.Where(IsJsonFile).Sum(file => file.Bytes));
            if (files > maxFiles || bytes > maxBytes || jsonBytes > maxJson) throw new InvalidOperationException("Aggregate private effect source closure exceeds its finite budget");
            sources.Add(observed);
        }
        return new(sources.AsReadOnly());
    }

    public static IReadOnlyList<ActionEffectReviewSubject> ReviewSubjects(IWorkspace workspace, JsonObject recipe,
        ActionEffectInspectionContext context, IActionEffectDependencyInspector inspector, CancellationToken cancellation)
    {
        if (recipe["schemaVersion"]?.GetValue<int>() != 2) throw new InvalidOperationException("Effect review subjects require explicit action recipe v2");
        var declarations = ReadSources(recipe);
        if (declarations.Count is < 1 || declarations.Count > Integer(context.Budgets, "maxEffectSources", 1, 64)) throw new InvalidOperationException("Effect review subject source count exceeds budget");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var result = new List<ActionEffectReviewSubject>();
        var maxFiles = Integer(context.Budgets, "maxEffectFiles", 1, 16384);
        var maxBytes = LongBudget(context.Budgets, "maxEffectDependencyBytes", 1, 4L * 1024 * 1024 * 1024);
        var maxJson = LongBudget(context.Budgets, "maxEffectJsonBytes", 1, 256L * 1024 * 1024);
        long bytes = 0, json = 0; var count = 0;
        foreach (var declaration in declarations)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!names.Add(declaration.Id)) throw new InvalidOperationException("Duplicate effect subject source ID");
            var remaining = context.Budgets.DeepClone().AsObject();
            remaining["maxEffectFiles"] = maxFiles - count; remaining["maxEffectDependencyBytes"] = maxBytes - bytes; remaining["maxEffectJsonBytes"] = maxJson - json;
            if (count >= maxFiles || bytes >= maxBytes || json >= maxJson) throw new InvalidOperationException("Aggregate review subject allowance exhausted");
            var subject = inspector.ReviewSubject(workspace, declaration, context with { Budgets = remaining }, cancellation);
            if (subject.Accepted || subject.SourceId != declaration.Id) throw new InvalidOperationException("Read-only subject inspection cannot assert asset acceptance");
            count = checked(count + subject.Inventory.Count); bytes = checked(bytes + subject.Inventory.Sum(file => file.Bytes));
            json = checked(json + subject.Inventory.Where(IsJsonFile).Sum(file => file.Bytes));
            result.Add(subject);
        }
        return result.AsReadOnly();
    }

    public static void Snapshot(IWorkspace workspace, ActionEffectCatalog catalog, string originalRoot,
        IDictionary<string, string> originals, IDictionary<string, string> sourceInputs, CancellationToken cancellation)
    {
        foreach (var source in catalog.Sources)
        {
            var index = 0;
            foreach (var file in source.SourceInventory.Files)
            {
                cancellation.ThrowIfCancellationRequested();
                if (sourceInputs.TryGetValue(file.Path, out var previous) && previous != file.Sha256)
                    throw new InvalidOperationException("Shared effect source path has conflicting byte identities");
                sourceInputs[file.Path] = file.Sha256;
                var extension = Path.GetExtension(file.Path).ToLowerInvariant();
                var target = originalRoot.TrimEnd('/') + "/effects/" + source.Source.Id + "/" + (index++).ToString("D5", System.Globalization.CultureInfo.InvariantCulture) + extension;
                Copy(workspace, file.Path, target, file.Bytes, file.Sha256, cancellation);
                originals[target] = file.Sha256;
            }
        }
    }

    public static JsonArray Export(IWorkspace workspace, ActionEffectCatalog catalog, string attemptRoot,
        Stage stage, CancellationToken cancellation)
    {
        var result = new List<ActionRuntimeEffectSource>();
        foreach (var source in catalog.Sources)
        {
            var prefix = attemptRoot + "/effects/" + source.Source.Id;
            var runtime = source.RuntimeSource;
            var package = prefix + "/package/package.json";
            var profile = prefix + "/profile.json";
            var nativePackage = Observe(workspace, runtime.Package.Path, MaximumJsonBytes, cancellation);
            Copy(workspace, nativePackage.Path, package, nativePackage.Bytes, runtime.Package.Sha256, cancellation);
            var nativeProfile = Observe(workspace, runtime.Profile.Path, MaximumJsonBytes, cancellation);
            Copy(workspace, nativeProfile.Path, profile, nativeProfile.Bytes, runtime.Profile.Sha256, cancellation);
            var resources = new List<ActionEffectResource>();
            foreach (var resource in runtime.Resources)
            {
                var declaration = source.Package.Resources.Single(item => item.Id == resource.Id);
                var target = prefix + "/package/" + declaration.Path;
                Copy(workspace, resource.Path, target, resource.Bytes, resource.Sha256, cancellation);
                resources.Add(resource with { Path = target });
            }
            var correspondences = new List<ActionEffectCorrespondence>();
            foreach (var correspondence in runtime.Correspondences)
            {
                var target = prefix + "/correspondences/" + correspondence.ResourceId + ".json";
                Copy(workspace, correspondence.Report.Path, target, correspondence.Report.Bytes, correspondence.Report.Sha256, cancellation);
                correspondences.Add(correspondence with { Report = correspondence.Report with { Path = target } });
            }
            result.Add(runtime with { Package = runtime.Package with { Path = package }, Profile = runtime.Profile with { Path = profile }, Resources = resources, Correspondences = correspondences });
        }
        var sources = ToJson(result);
        foreach (var file in RuntimeFiles(sources, workspace, cancellation)) stage.Outputs[file.Path] = file.Sha256;
        return sources;
    }

    public static JsonArray ExpectedRuntimeSources(ActionEffectCatalog catalog, string runtimeRoot)
    {
        var result = new List<ActionRuntimeEffectSource>();
        foreach (var inspected in catalog.Sources)
        {
            var source = inspected.RuntimeSource; var prefix = runtimeRoot.TrimEnd('/') + "/effects/" + source.Id;
            result.Add(source with
            {
                Package = source.Package with { Path = prefix + "/package/package.json" },
                Profile = source.Profile with { Path = prefix + "/profile.json" },
                Resources = source.Resources.Select(resource => resource with { Path = prefix + "/package/" + inspected.Package.Resources.Single(item => item.Id == resource.Id).Path }).ToArray(),
                Correspondences = source.Correspondences.Select(item => item with { Report = item.Report with { Path = prefix + "/correspondences/" + item.ResourceId + ".json" } }).ToArray()
            });
        }
        return ToJson(result);
    }

    public static string ExpectedRuntimeFingerprint(ActionEffectCatalog catalog)
        => RuntimeClosureFingerprint(ExpectedRuntimeSources(catalog, "runtime"));

    public static void ValidateRuntimeClosure(IWorkspace workspace, JsonObject package,
        string? physicalRootOverride = null, CancellationToken cancellation = default)
    {
        if (Text(package, "schemaVersion") != "hephaera-action-package/v2") return;
        var sources = RuntimeSources(package["effectSources"] as JsonArray ?? throw new InvalidOperationException("V2 package omits runtime effect sources"));
        var budget = package["budgets"]?.AsObject() ?? throw new InvalidOperationException("Runtime effect budgets missing");
        if (sources.Count is < 1 || sources.Count > Integer(budget, "maxEffectSources", 1, 64)) throw new InvalidOperationException("Runtime effect source inventory exceeds budget");
        var array = ToJson(sources);
        if (Text(package, "effectClosureSha256") != RuntimeClosureFingerprint(array)) throw new InvalidOperationException("Runtime effect closure fingerprint is stale");
        var expectedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? logicalRoot = null;
        long bytes = 0, jsonBytes = 0; var count = 0;
        foreach (var source in sources)
        {
            cancellation.ThrowIfCancellationRequested();
            Id(source.Id); Id(source.EffectId);
            var suffix = "/effects/" + source.Id + "/package/package.json";
            if (!source.Package.Path.EndsWith(suffix, StringComparison.Ordinal)) throw new InvalidOperationException("Runtime effect package must use its owned portable layout");
            var root = source.Package.Path[..^suffix.Length];
            if (logicalRoot is not null && root != logicalRoot) throw new InvalidOperationException("Runtime effect sources have competing owners");
            logicalRoot = root;
            string Physical(string path)
            {
                if (!path.StartsWith(root + "/effects/" + source.Id + "/", StringComparison.Ordinal)) throw new InvalidOperationException("Runtime effect file escapes its source owner");
                return physicalRootOverride is null ? path : physicalRootOverride + path[root.Length..];
            }
            void Check(string path, string sha, long? length, long maximum)
            {
                Hash(sha);
                var physical = Physical(path);
                if (!expectedFiles.TryAdd(physical, sha)) throw new InvalidOperationException("Runtime effect file destination is aliased or duplicated");
                var lengthBeforeRead = new FileInfo(Resolve(workspace, physical)).Length;
                if (count >= Integer(budget, "maxEffectFiles", 1, 16384) || lengthBeforeRead < 1 || lengthBeforeRead > LongBudget(budget, "maxEffectDependencyBytes", 1, 4L * 1024 * 1024 * 1024) - bytes || path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && lengthBeforeRead > LongBudget(budget, "maxEffectJsonBytes", 1, 256L * 1024 * 1024) - jsonBytes)
                    throw new InvalidOperationException("Runtime effect file exceeds remaining allowance before read");
                var observed = Observe(workspace, physical, maximum, cancellation);
                if (observed.Sha256 != sha || length.HasValue && observed.Bytes != length) throw new InvalidOperationException("Runtime effect file identity changed");
                bytes = checked(bytes + observed.Bytes); count++;
                if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) jsonBytes = checked(jsonBytes + observed.Bytes);
                if (count > Integer(budget, "maxEffectFiles", 1, 16384) || bytes > LongBudget(budget, "maxEffectDependencyBytes", 1, 4L * 1024 * 1024 * 1024) || jsonBytes > LongBudget(budget, "maxEffectJsonBytes", 1, 256L * 1024 * 1024))
                    throw new InvalidOperationException("Runtime effect closure exceeds cumulative file/byte/JSON budget");
            }
            Check(source.Package.Path, source.Package.Sha256, null, MaximumJsonBytes);
            if (source.Profile.Path != root + "/effects/" + source.Id + "/profile.json") throw new InvalidOperationException("Runtime profile escapes its exact portable role");
            Check(source.Profile.Path, source.Profile.Sha256, null, MaximumJsonBytes);
            var declaration = VfxJson.Parse(Read(workspace, Physical(source.Package.Path), MaximumJsonBytes, cancellation));
            if (declaration.Resources.Count > Integer(budget, "maxEffectFiles", 1, 16384) - count || declaration.Resources.Sum(resource => resource.Bytes) > LongBudget(budget, "maxEffectDependencyBytes", 1, 4L * 1024 * 1024 * 1024) - bytes)
                throw new InvalidOperationException("Runtime native resources exceed remaining allowance before inspection");
            var inspected = VfxInspection.Inspect(workspace, Physical(source.Package.Path), cancellation);
            if (!inspected.IsValid || inspected.PackageSha256 != source.Package.Sha256) throw new InvalidOperationException("Runtime native effect package is invalid or changed");
            RequireEngine(inspected.Package, package["engine"]!.AsObject(), workspace.HostContext?.StyleSha256);
            var profile = EffectProfileV2.Parse(Read(workspace, Physical(source.Profile.Path), MaximumJsonBytes, cancellation));
            EffectProfileV2.Validate(profile, inspected.Package, source.EffectId);
            if (source.Profile.SchemaVersion != "hephaera-three-vfx-profile/v1" || profile.NativePackageSha256 != source.Package.Sha256 || profile.PreparationSha256 != source.Ancestry.NativePreparationSha256)
                throw new InvalidOperationException("Runtime profile package/preparation ancestry differs");
            Hash(source.Ancestry.SourceClosureSha256); Hash(source.Ancestry.ProductionReviewSha256); Hash(source.Ancestry.RightsSha256);
            if (source.Ancestry.NativePreparationSha256 is { } prep) Hash(prep);
            if (source.Resources.Count != inspected.ObservedResources.Count || source.Correspondences.Count != source.Resources.Count) throw new InvalidOperationException("Runtime effect resource/correspondence closure is incomplete");
            var resourceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var correspondenceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var measurements = new Dictionary<string, ActionEffectCorrespondenceReport>(StringComparer.Ordinal);
            foreach (var resource in source.Resources)
            {
                if (!resourceNames.Add(resource.Id)) throw new InvalidOperationException("Duplicate runtime effect resource");
                var expected = inspected.ObservedResources.SingleOrDefault(item => item.Id == resource.Id) ?? throw new InvalidOperationException("Unexpected runtime effect resource");
                if (resource.Kind != expected.Kind || resource.Sha256 != expected.Sha256 || resource.Bytes != expected.Bytes || resource.Path != root + "/effects/" + source.Id + "/package/" + expected.Path)
                    throw new InvalidOperationException("Runtime effect resource differs from native package");
                Check(resource.Path, resource.Sha256, resource.Bytes, MaximumFileBytes);
            }
            foreach (var correspondence in source.Correspondences)
            {
                if (!correspondenceNames.Add(correspondence.ResourceId)) throw new InvalidOperationException("Duplicate runtime correspondence");
                var resource = source.Resources.SingleOrDefault(item => item.Id == correspondence.ResourceId) ?? throw new InvalidOperationException("Unknown runtime correspondence resource");
                if (correspondence.Report.Path != root + "/effects/" + source.Id + "/correspondences/" + resource.Id + ".json") throw new InvalidOperationException("Runtime correspondence escapes its exact portable role");
                Check(correspondence.Report.Path, correspondence.Report.Sha256, correspondence.Report.Bytes, MaximumJsonBytes);
                var report = EffectProfileV2.ParseData<ActionEffectCorrespondenceReport>(Read(workspace, Physical(correspondence.Report.Path), MaximumJsonBytes, cancellation));
                ValidateCorrespondence(correspondence, resource, report);
                ValidateCorrespondenceContent(workspace, resource with { Path = Physical(resource.Path) }, report, cancellation);
                measurements.Add(resource.Id, report);
            }
            ValidateResourceMeasurements(profile, inspected.Package, source.EffectId, measurements);
        }
        if (logicalRoot is null) throw new InvalidOperationException("Missing runtime effect owner");
        var model = package["model"]?.AsObject() ?? throw new InvalidOperationException("Runtime effect package must bind the delivered action model");
        Fields(model, "path", "sha256");
        var modelPath = Relative(workspace, Text(model, "path")); var modelHash = Text(model, "sha256"); Hash(modelHash);
        if (!modelPath.StartsWith(logicalRoot + "/", StringComparison.Ordinal)) throw new InvalidOperationException("Runtime model escapes the effect package owner");
        var physicalModel = physicalRootOverride is null ? modelPath : physicalRootOverride + modelPath[logicalRoot.Length..];
        var maximumRuntime = LongBudget(budget, "maxRuntimeBytes", 1024, MaximumFileBytes);
        var descriptorBytes = JsonSerializer.SerializeToUtf8Bytes(package, Workspace.Json).LongLength;
        var modelBytes = new FileInfo(Resolve(workspace, physicalModel)).Length;
        if (modelBytes < 1 || modelBytes > maximumRuntime - bytes - descriptorBytes)
            throw new InvalidOperationException("Complete runtime model/effect/package payload exceeds maxRuntimeBytes");
        var deliveredModel = Observe(workspace, physicalModel, maximumRuntime - bytes - descriptorBytes, cancellation);
        if (deliveredModel.Bytes != modelBytes || deliveredModel.Sha256 != modelHash) throw new InvalidOperationException("Delivered action model identity changed during effect closure verification");
        VerifyInventory(workspace, (physicalRootOverride ?? logicalRoot) + "/effects", expectedFiles.Keys, cancellation);
    }

    public static IReadOnlyList<ActionEffectFile> CopyAndRebind(IWorkspace workspace, JsonObject package,
        string pendingRoot, string destination, CancellationToken cancellation)
    {
        ValidateRuntimeClosure(workspace, package, cancellation: cancellation);
        var sources = RuntimeSources(Array(package, "effectSources"));
        var output = new List<ActionRuntimeEffectSource>();
        var copied = new List<ActionEffectFile>();
        foreach (var source in sources)
        {
            var suffix = "/effects/" + source.Id + "/package/package.json";
            var previousRoot = source.Package.Path[..^suffix.Length];
            string Rebind(string path) => destination + path[previousRoot.Length..];
            foreach (var file in RuntimeFiles(ToJson([source]), workspace, cancellation))
            {
                var target = pendingRoot + file.Path[previousRoot.Length..];
                Copy(workspace, file.Path, target, file.Bytes, file.Sha256, cancellation);
                copied.Add(file with { Path = target });
            }
            output.Add(source with
            {
                Package = source.Package with { Path = Rebind(source.Package.Path) },
                Profile = source.Profile with { Path = Rebind(source.Profile.Path) },
                Resources = source.Resources.Select(resource => resource with { Path = Rebind(resource.Path) }).ToArray(),
                Correspondences = source.Correspondences.Select(item => item with { Report = item.Report with { Path = Rebind(item.Report.Path) } }).ToArray()
            });
        }
        package["effectSources"] = ToJson(output);
        // The existing native acceptance owner copies the delivered model first.
        // Rebind its metadata together with effect paths before validating pending bytes.
        package["model"]!["path"] = destination + "/model.glb";
        package["effectClosureSha256"] = RuntimeClosureFingerprint(Array(package, "effectSources"));
        ValidateRuntimeClosure(workspace, package, pendingRoot, cancellation);
        return copied;
    }

    public static string RuntimeClosureFingerprint(JsonArray sources)
    {
        var rows = new List<string>();
        foreach (var source in RuntimeSources(sources).OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            var suffix = "/effects/" + source.Id + "/package/package.json";
            if (!source.Package.Path.EndsWith(suffix, StringComparison.Ordinal)) throw new InvalidOperationException("Effect closure has an invalid portable package path");
            var root = source.Package.Path[..^suffix.Length];
            string Local(string value) => value.StartsWith(root + "/", StringComparison.Ordinal) ? value[(root.Length + 1)..] : throw new InvalidOperationException("Effect runtime file is outside its portable owner");
            rows.Add(JsonSerializer.Serialize(new
            {
                source.Id, source.EffectId,
                package = new { path = Local(source.Package.Path), source.Package.Sha256 },
                profile = new { source.Profile.SchemaVersion, path = Local(source.Profile.Path), source.Profile.Sha256 },
                resources = source.Resources.OrderBy(item => item.Id, StringComparer.Ordinal).Select(item => new { item.Id, item.Kind, path = Local(item.Path), item.Sha256, item.Bytes }),
                correspondences = source.Correspondences.OrderBy(item => item.ResourceId, StringComparer.Ordinal).Select(item => new { item.ResourceId, item.ResourceSha256, item.Kind, item.DecodedSha256, report = new { path = Local(item.Report.Path), item.Report.Sha256, item.Report.Bytes } }),
                source.Ancestry
            }, Json));
        }
        return Digest(Encoding.UTF8.GetBytes(string.Join("\n", rows)));
    }

    public static string SourceFingerprint(IReadOnlyList<ActionEffectFile> files, ActionEffectInspectionContext context, string? selectedEffectId = null)
    {
        var value = JsonSerializer.Serialize(new
        {
            engine = Canonical(context.Engine), context.StyleSha256, context.RuntimeBuildSha256, selectedEffectId,
            files = files.OrderBy(item => item.Role, StringComparer.Ordinal).ThenBy(item => item.ResourceId, StringComparer.Ordinal).ThenBy(item => item.Path, StringComparer.Ordinal)
                .Select(item => new { item.Role, item.ResourceId, item.Path, item.Bytes, item.Sha256 })
        }, Json);
        return Digest(Encoding.UTF8.GetBytes(value));
    }

    public static void RequireEngine(VfxPackage package, JsonObject engine, string? styleSha256)
    {
        if (package.Engine.Id != "THREEJS" || Text(engine, "effectiveEngine") != "THREEJS") throw new InvalidOperationException("Action v2 effects require explicitly selected THREEJS");
        foreach (var (field, expected) in new[] { ("contractSha256", package.Engine.ContractSha256), ("definitionSha256", package.Engine.DefinitionSha256), ("registrySha256", package.Engine.RegistrySha256) })
            if (Text(engine, field) != expected) throw new InvalidOperationException("Action effect package engine identity conflicts with the current selection");
        if (styleSha256 is not null && package.StyleSha256 != styleSha256) throw new InvalidOperationException("Action effect package STYLE differs from current policy");
    }

    public static void ValidateCorrespondence(ActionEffectCorrespondence correspondence, ActionEffectResource resource, ActionEffectCorrespondenceReport report)
    {
        Hash(correspondence.DecodedSha256); Hash(report.SourceEvidenceSha256);
        var kind = resource.Kind == "texture-png" ? "texture" : resource.Kind == "mesh-glb" ? "geometry" : throw new InvalidOperationException("Unsupported correspondence resource kind");
        if (report.SchemaVersion != "bubo-vfx-resource-correspondence/v1" || !report.Independent || correspondence.ResourceId != resource.Id || correspondence.ResourceSha256 != resource.Sha256 || correspondence.Kind != kind || report.ResourceId != resource.Id || report.ResourceSha256 != resource.Sha256 || report.Kind != kind || report.DecodedSha256 != correspondence.DecodedSha256)
            throw new InvalidOperationException("Canonical effect resource correspondence does not bind the actual resource/decoded identities");
        var m = report.Measurements;
        if (m.Bytes is < 1 or > 268435456) throw new InvalidOperationException("Decoded correspondence bytes exceed bounded runtime profile");
        if (kind == "texture")
        {
            if (m.Width is not (>= 1 and <= 8192) || m.Height is not (>= 1 and <= 8192) || m.Vertices.HasValue || m.Indices.HasValue || (long)m.Width * m.Height * 4 != m.Bytes || (long)m.Width * m.Height > 16777216)
                throw new InvalidOperationException("Invalid bounded RGBA correspondence measurements");
        }
        else if (m.Vertices is not (>= 1 and <= 1048576) || m.Indices is not (>= 0 and <= 3145728) || m.Width.HasValue || m.Height.HasValue || (m.Indices == 0 ? m.Vertices : m.Indices) % 3 != 0)
            throw new InvalidOperationException("Invalid bounded static geometry correspondence measurements");
    }

    public static void ValidateCorrespondenceContent(IWorkspace workspace, ActionEffectResource resource,
        ActionEffectCorrespondenceReport report, CancellationToken cancellation)
    {
        if (resource.Kind != "texture-png") return; // Mesh correspondence remains separately reviewed; no new decoder is selected.
        cancellation.ThrowIfCancellationRequested();
        using var stream = new FileStream(Resolve(workspace, resource.Path), FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[24];
        stream.ReadExactly(header);
        if (!header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) || System.Text.Encoding.ASCII.GetString(header[12..16]) != "IHDR"
            || System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header[16..20]) != report.Measurements.Width
            || System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header[20..24]) != report.Measurements.Height)
            throw new InvalidOperationException("Texture correspondence dimensions differ from actual bounded PNG header");
        cancellation.ThrowIfCancellationRequested();
    }

    public static IReadOnlyDictionary<string, ActionEffectAllocation> ValidateResourceMeasurements(ActionEffectProfile profile, VfxPackage package,
        string effectId, IReadOnlyDictionary<string, ActionEffectCorrespondenceReport> reports)
    {
        if (reports.Count != package.Resources.Count || package.Resources.Any(resource => !reports.ContainsKey(resource.Id)))
            throw new InvalidOperationException("Decoded correspondence inventory must cover every native package resource exactly once");
        var decodedTextureBytes = package.Resources.Where(resource => resource.Kind == "texture-png").Sum(resource => reports[resource.Id].Measurements.Bytes);
        var decodedBufferBytes = package.Resources.Where(resource => resource.Kind == "mesh-glb").Sum(resource => reports[resource.Id].Measurements.Bytes);
        if (decodedTextureBytes > profile.Budgets.MaxTextureBytes || decodedBufferBytes > profile.Budgets.MaxBufferBytes)
            throw new InvalidOperationException("Complete borrowed decoded resource inventory exceeds production texture/buffer bounds");
        var result = new Dictionary<string, ActionEffectAllocation>(StringComparer.Ordinal);
        long verticesSum = 0, indicesSum = 0, bufferSum = 0, textureSum = 0;
        foreach (var layer in profile.Layers.Where(layer => layer.EffectId == effectId))
        {
            var native = package.Effects.Single(effect => effect.Id == effectId).Layers.Single(item => item.Id == layer.LayerId);
            var trail = layer.Family == "trail"; var beam = layer.Family == "beam";
            var geometry = layer.Geometry?.Kind == "binding" ? reports[layer.Geometry.ResourceId!].Measurements : null;
            long geometryVertices = beam ? 18 : geometry is not null ? geometry.Vertices!.Value : layer.Geometry?.Kind == "ring" ? checked((layer.Geometry.Segments!.Value + 1L) * 2) : 4;
            long geometryIndices = beam ? 48 : geometry is not null ? geometry.Indices!.Value : layer.Geometry?.Kind == "ring" ? checked(layer.Geometry.Segments!.Value * 6L) : 6;
            var history = layer.Trail?.HistoryPoints ?? 0;
            long vertices = trail ? checked((long)layer.Emission.Capacity * history * 2) : checked(geometryVertices * layer.Emission.Capacity);
            long indices = trail ? checked((long)layer.Emission.Capacity * history * 6) : checked(geometryIndices * layer.Emission.Capacity);
            if (vertices > native.MaxVertices || native.MaxDrawCalls < 1) throw new InvalidOperationException("Actual effect geometry exceeds native layer reservation");
            // Exact bounded allocation policy of the selected Three.js/Quarks production helper.
            // Native declarations bound logical geometry; profile/scene reservations include batch floors.
            long grown = trail ? 10000 : 1000;
            var demand = trail ? checked((long)layer.Emission.Capacity * history * 2) : layer.Emission.Capacity;
            if (demand > grown) while (demand >= grown) grown = checked(grown * 2);
            var ownedBufferBytes = checked(geometryVertices * 32 + geometryIndices * 2);
            var estimatedBufferBytes = beam ? ownedBufferBytes : checked(grown * (trail ? 160 : geometry is not null || layer.Geometry?.Kind == "ring" ? 64 : 52) + geometryVertices * 32 + geometryIndices * 4);
            var allocatedVertices = Math.Max(vertices, trail ? checked(grown * 2) : geometryVertices);
            var allocatedIndices = Math.Max(indices, trail ? checked(grown * 6) : geometryIndices);
            var textureBytes = layer.ResourceId is null ? 0 : reports[layer.ResourceId].Measurements.Bytes;
            var allocation = new ActionEffectAllocation(allocatedVertices, allocatedIndices, estimatedBufferBytes, textureBytes);
            result.Add(layer.LayerId, allocation);
            verticesSum = checked(verticesSum + allocation.Vertices); indicesSum = checked(indicesSum + allocation.Indices);
            bufferSum = checked(bufferSum + allocation.BufferBytes); textureSum = checked(textureSum + allocation.TextureBytes);
        }
        if (verticesSum > profile.Budgets.MaxVertices || indicesSum > profile.Budgets.MaxIndices || bufferSum > profile.Budgets.MaxBufferBytes || textureSum > profile.Budgets.MaxTextureBytes)
            throw new InvalidOperationException("Complete selected effect allocation exceeds production vertex/index/buffer/texture bounds");
        return new System.Collections.ObjectModel.ReadOnlyDictionary<string, ActionEffectAllocation>(result);
    }

    public static IReadOnlyList<ActionRuntimeEffectSource> RuntimeSources(JsonArray value)
    {
        var result = ParseRuntimeData<ActionRuntimeEffectSource[]>(Encoding.UTF8.GetBytes(value.ToJsonString()), ActionRuntimeDataKind.EffectSources);
        if (result.Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Length) throw new InvalidOperationException("Duplicate runtime effect source ID");
        return result;
    }

    public static JsonArray ToJson<T>(IEnumerable<T> values) => JsonSerializer.SerializeToNode(values, Json)!.AsArray();

    enum RuntimeDataPosition { Root, Other, ReceiptRuntime, ReceivingSources, ReceivingSource, RuntimeSource, Ancestry, OptionalPreparation, EngineIdentity }

    public static T ParseRuntimeData<T>(byte[] bytes, ActionRuntimeDataKind kind)
    {
        if (!Enum.IsDefined(kind)) throw new InvalidOperationException("Explicit runtime effect parsing context required");
        if (bytes.Length is < 2 or > MaximumJsonBytes) throw new InvalidOperationException("Runtime effect JSON exceeds its finite byte envelope");
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            var count = 0;
            RuntimeDataPosition PropertyPosition(RuntimeDataPosition position, JsonProperty property)
            {
                if (position == RuntimeDataPosition.EngineIdentity) return RuntimeDataPosition.EngineIdentity;
                // These exact identities remain opaque host data. Their complete
                // structure, including nulls, is compared with the current engine
                // by RuntimeEffectEvidence; never strip or rebind those fields.
                if (property.Value.ValueKind == JsonValueKind.Object &&
                    (kind == ActionRuntimeDataKind.ReceivingPacket && position == RuntimeDataPosition.Root && property.Name == "engine" ||
                     kind == ActionRuntimeDataKind.RuntimeAcceptanceReceipt && position == RuntimeDataPosition.ReceiptRuntime && property.Name == "engineIdentity"))
                    return RuntimeDataPosition.EngineIdentity;
                if (kind == ActionRuntimeDataKind.RuntimeAcceptanceReceipt && position == RuntimeDataPosition.Root && property.Name == "runtime")
                    return RuntimeDataPosition.ReceiptRuntime;
                if (kind == ActionRuntimeDataKind.ReceivingPacket && position == RuntimeDataPosition.Root && property.Name == "effectSources")
                    return RuntimeDataPosition.ReceivingSources;
                if (position == RuntimeDataPosition.RuntimeSource && property.Name == "ancestry") return RuntimeDataPosition.Ancestry;
                if ((position is RuntimeDataPosition.Ancestry or RuntimeDataPosition.ReceivingSource) && property.Name == "nativePreparationSha256")
                    return RuntimeDataPosition.OptionalPreparation;
                return RuntimeDataPosition.Other;
            }
            void Check(JsonElement node, RuntimeDataPosition position)
            {
                if (++count > 262144) throw new InvalidOperationException("Runtime effect JSON exceeds finite node inventory");
                if (node.ValueKind == JsonValueKind.Null && position is not (RuntimeDataPosition.EngineIdentity or RuntimeDataPosition.OptionalPreparation))
                    throw new InvalidOperationException("Runtime effect JSON contains an unsupported null");
                if (node.ValueKind == JsonValueKind.String && node.GetString()!.Length > 4096) throw new InvalidOperationException("Runtime effect text exceeds finite bound");
                if (node.ValueKind == JsonValueKind.Object)
                {
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in node.EnumerateObject())
                    {
                        if (!names.Add(property.Name)) throw new InvalidOperationException("Duplicate runtime effect JSON field");
                        Check(property.Value, PropertyPosition(position, property));
                    }
                }
                else if (node.ValueKind == JsonValueKind.Array)
                {
                    if (node.GetArrayLength() > 16384) throw new InvalidOperationException("Runtime effect array exceeds finite bound");
                    var child = position == RuntimeDataPosition.EngineIdentity ? RuntimeDataPosition.EngineIdentity
                        : position == RuntimeDataPosition.ReceivingSources ? RuntimeDataPosition.ReceivingSource
                        : kind == ActionRuntimeDataKind.EffectSources && position == RuntimeDataPosition.Root ? RuntimeDataPosition.RuntimeSource
                        : RuntimeDataPosition.Other;
                    foreach (var item in node.EnumerateArray()) Check(item, child);
                }
            }
            Check(document.RootElement, RuntimeDataPosition.Root);
            return JsonSerializer.Deserialize<T>(bytes, EffectProfileV2.Json) ?? throw new InvalidOperationException("Missing runtime effect data");
        }
        catch (JsonException error) { throw new InvalidOperationException("Invalid strict runtime effect JSON", error); }
    }

    public static IReadOnlyList<ActionEffectFile> RuntimeFiles(JsonArray value, IWorkspace workspace, CancellationToken cancellation)
    {
        var result = new List<ActionEffectFile>();
        foreach (var source in RuntimeSources(value))
        {
            void Add(string role, string path, string sha, string? resourceId = null, long? length = null)
            {
                var actual = Observe(workspace, path, role == "resource" ? MaximumFileBytes : MaximumJsonBytes, cancellation);
                if (actual.Sha256 != sha || length.HasValue && actual.Bytes != length) throw new InvalidOperationException("Runtime effect file changed");
                result.Add(actual with { Role = role, SourceId = source.Id, ResourceId = resourceId });
            }
            Add("native-package", source.Package.Path, source.Package.Sha256);
            Add("production-profile", source.Profile.Path, source.Profile.Sha256);
            foreach (var resource in source.Resources) Add("resource", resource.Path, resource.Sha256, resource.Id, resource.Bytes);
            foreach (var correspondence in source.Correspondences) Add("source-correspondence", correspondence.Report.Path, correspondence.Report.Sha256, correspondence.ResourceId, correspondence.Report.Bytes);
        }
        return result;
    }

    public static long LongBudget(JsonObject budget, string name, long minimum, long maximum)
    {
        if (budget[name] is not JsonValue value || value.GetValueKind() != JsonValueKind.Number || !long.TryParse(value.ToJsonString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var result) || result < minimum || result > maximum) throw new InvalidOperationException("Invalid finite effect budget: " + name);
        return result;
    }

    public static bool IsJsonFile(ActionEffectFile file) => IsJsonFile(file.Role, file.Path);

    public static bool IsJsonFile(string role, string path) => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
        || role is "native-package" or "production-profile" or "rights" or "production-review" or "source-correspondence"
            or "native-preparation-seal" or "native-preparation-request" or "native-preparation-claim" or "native-preparation-context";

    public static ActionEffectFile Observe(IWorkspace workspace, string path, long maximum, CancellationToken cancellation)
    {
        var full = Resolve(workspace, path);
        var snapshot = new FileInfo(full).Length;
        if (snapshot < 1 || snapshot > maximum) throw new InvalidOperationException("Effect closure file has no positive bounded length: " + path);
        using var input = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        if (!input.CanSeek || input.Length != snapshot) throw new InvalidOperationException("Effect closure file is not a stable regular input");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536]; long length = 0;
        for (;;)
        {
            cancellation.ThrowIfCancellationRequested();
            var count = input.Read(buffer.AsSpan(0, (int)Math.Min(buffer.Length, snapshot - length + 1)));
            if (count == 0) break;
            length = checked(length + count);
            if (length > snapshot) throw new InvalidOperationException("Effect closure input grew during hashing");
            hash.AppendData(buffer, 0, count);
        }
        if (length != snapshot || input.Length != snapshot || Resolve(workspace, path) != full) throw new InvalidOperationException("Effect closure input changed during hashing");
        return new() { Role = "observed", SourceId = "observed", Path = path, Bytes = length, Sha256 = Convert.ToHexStringLower(hash.GetHashAndReset()) };
    }

    public static byte[] Read(IWorkspace workspace, string path, int maximum, CancellationToken cancellation)
    {
        var observed = Observe(workspace, path, maximum, cancellation);
        var result = new byte[checked((int)observed.Bytes)];
        using var stream = new FileStream(Resolve(workspace, path), FileMode.Open, FileAccess.Read, FileShare.Read);
        for (var offset = 0; offset < result.Length;)
        {
            cancellation.ThrowIfCancellationRequested();
            var count = stream.Read(result.AsSpan(offset, Math.Min(65536, result.Length - offset)));
            if (count == 0) throw new InvalidOperationException("Effect JSON was truncated");
            offset += count;
        }
        if (stream.ReadByte() != -1 || Digest(result) != observed.Sha256) throw new InvalidOperationException("Effect JSON changed during read");
        return result;
    }

    public static string Resolve(IWorkspace workspace, string path, bool write = false)
    {
        Relative(workspace, path);
        if (path.Length > 1024 || path.Any(c => char.IsControl(c) || c is ':' or '<' or '>' or '"' or '|' or '?' or '*')) throw new InvalidOperationException("Unsafe effect closure path");
        foreach (var part in path.Split('/'))
        {
            var stem = part.Split('.')[0].ToUpperInvariant();
            if (part.EndsWith('.') || part.EndsWith(' ') || part.Equals(".git", StringComparison.OrdinalIgnoreCase) || stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9')
                throw new InvalidOperationException("Effect closure path contains a reserved or aliased component");
        }
        var expected = Path.GetFullPath(path, workspace.Root);
        var actual = write ? workspace.ResolveWrite(path) : workspace.Resolve(path);
        if (!actual.Equals(expected, PathComparison)) throw new InvalidOperationException("Workspace changed the effect closure path identity");
        var current = Path.GetPathRoot(actual)!;
        foreach (var part in Path.GetRelativePath(current, actual).Split(Path.DirectorySeparatorChar))
        {
            if (Directory.Exists(current))
            {
                var matches = Directory.EnumerateFileSystemEntries(current, part, new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, MatchType = MatchType.Simple, AttributesToSkip = 0 }).Take(2).ToArray();
                if (matches.Length > 1 || matches.Length == 1 && Path.GetFileName(matches[0]) != part) throw new InvalidOperationException("Effect closure path contains a case alias/collision");
            }
            current = Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Effect closure paths cannot traverse links/reparse points");
        }
        return actual;
    }

    public static void Copy(IWorkspace workspace, string source, string destination, long bytes, string sha256, CancellationToken cancellation)
    {
        Hash(sha256);
        var original = Observe(workspace, source, bytes, cancellation);
        if (original.Bytes != bytes || original.Sha256 != sha256) throw new InvalidOperationException("Effect source changed before copy");
        var target = Resolve(workspace, destination, true);
        if (Path.GetFullPath(workspace.Resolve(source)).Equals(target, PathComparison) || File.Exists(target) || Directory.Exists(target)) throw new InvalidOperationException("Effect copy requires a new, nonoverlapping destination");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Resolve(workspace, destination, true);
        using var reader = new FileStream(Resolve(workspace, source), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        using (var writer = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536))
        {
            var buffer = new byte[65536]; long total = 0;
            for (;;)
            {
                cancellation.ThrowIfCancellationRequested();
                var count = reader.Read(buffer.AsSpan(0, (int)Math.Min(buffer.Length, bytes - total + 1)));
                if (count == 0) break;
                total = checked(total + count);
                if (total > bytes) throw new InvalidOperationException("Effect source grew while copying");
                writer.Write(buffer, 0, count);
            }
            if (total != bytes || reader.Length != bytes) throw new InvalidOperationException("Effect source length changed while copying");
            writer.Flush(true);
        }
        var copied = Observe(workspace, destination, bytes, cancellation);
        if (copied.Bytes != bytes || copied.Sha256 != sha256) throw new InvalidOperationException("Effect copy hash/length mismatch");
        var after = Observe(workspace, source, bytes, cancellation);
        if (after.Sha256 != sha256 || after.Bytes != bytes) throw new InvalidOperationException("Effect original changed during copy");
    }

    public static void VerifyInventory(IWorkspace workspace, string root, IEnumerable<string> expectedPaths, CancellationToken cancellation)
    {
        var expected = new HashSet<string>(expectedPaths, StringComparer.Ordinal);
        var allowedDirectories = new HashSet<string>(StringComparer.Ordinal) { root };
        foreach (var file in expected)
        {
            var directory = file;
            while ((directory = Path.GetDirectoryName(directory)?.Replace('\\', '/') ?? "").StartsWith(root, StringComparison.Ordinal) && directory.Length >= root.Length) allowedDirectories.Add(directory);
        }
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(Resolve(workspace, directory)))
            {
                cancellation.ThrowIfCancellationRequested();
                var path = workspace.Relative(entry); Resolve(workspace, path);
                if (Directory.Exists(entry))
                {
                    if (!allowedDirectories.Contains(path)) throw new InvalidOperationException("Unowned directory in runtime effect closure");
                    pending.Push(path);
                }
                else if (!expected.Remove(path)) throw new InvalidOperationException("Unowned/aliased file in runtime effect closure");
            }
        }
        if (expected.Count != 0) throw new InvalidOperationException("Runtime effect closure is missing files");
    }

    public static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    static JsonNode? Canonical(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, Canonical(pair.Value)))),
        JsonArray array => new JsonArray(array.Select(Canonical).ToArray()),
        _ => node?.DeepClone()
    };
}
