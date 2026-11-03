using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bubo.Schematic.Application;
using Bubo.Schematic.Domain;
using Hephaera.AssetProduction;

namespace Bubo.Schematic.Infrastructure;

public sealed record DesignPackage
{
    public required string SchemaVersion { get; init; }
    public required string DesignSha256 { get; init; }
    public required string StyleSha256 { get; init; }
    public required string HostPolicySha256 { get; init; }
    public required string RuntimeBuildSha256 { get; init; }
    public required SortedDictionary<string, string> Artifacts { get; init; }
    public required long TotalBytes { get; init; }
    public required string Fingerprint { get; init; }
}
public sealed record WholeAssemblyReview
{
    public required string SchemaVersion { get; init; }
    public required string Kind { get; init; }
    public required string Decision { get; init; }
    public required bool Human { get; init; }
    public required string Reviewer { get; init; }
    public required string Reason { get; init; }
    public required string Asset { get; init; }
    public required string Version { get; init; }
    public required string DesignSha256 { get; init; }
    public required string DesignFingerprint { get; init; }
    public required Dictionary<string, string> ReviewedArtifacts { get; init; }
}
public sealed record PreviewAttempt
{
    public required string Directory { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public string Status { get; set; } = "running";
    public string? Error { get; set; }
    public string? IndexSha256 { get; set; }
}
public sealed record DesignReviewRecord(string Decision, string Reviewer, string Source, string Snapshot, string Sha256, string Fingerprint);
public sealed record DesignManifest
{
    public required string SchemaVersion { get; init; }
    public required string Task { get; init; }
    public required string Asset { get; init; }
    public required string Version { get; init; }
    public required string SourceDesign { get; init; }
    public required string OriginalDesign { get; init; }
    public required string DesignSha256 { get; init; }
    public required string HostPolicySha256 { get; init; }
    public required string RuntimeBuildSha256 { get; init; }
    public required string StyleSha256 { get; init; }
    public required string Staging { get; init; }
    public required string? ParentSnapshot { get; init; }
    public required string? ParentSha256 { get; init; }
    public required Dictionary<string, string> PolicyInputs { get; init; }
    public required List<PreviewAttempt> Previews { get; init; }
    public required List<DesignReviewRecord> Reviews { get; init; }
}

/// <summary>Task-owned immutable originals, attempts and complete-design assertions. Never grants model acceptance.</summary>
public sealed class DesignSession
{
    readonly IWorkspace workspace;
    readonly string task, stateRoot, stagingRoot, policySha, runtimeSha, styleSha;
    readonly IAssemblyDesignValidator validator;
    readonly PreviewCompiler compiler;
    static readonly UTF8Encoding Utf8 = new(false, true);
    public DesignSession(IWorkspace workspace, string task, string stateRoot, string stagingRoot, string policySha,
        string runtimeSha, string styleSha, IAssemblyDesignValidator? validator = null, ISchematicPreviewRenderer? renderer = null)
    {
        this.workspace = workspace; Id(task); Hash(policySha); Hash(runtimeSha); Hash(styleSha);
        this.task = task; this.stateRoot = workspace.Relative(stateRoot); this.stagingRoot = workspace.Relative(stagingRoot);
        if (this.stateRoot == "." || this.stagingRoot == "." || Under(this.stateRoot, this.stagingRoot) || Under(this.stagingRoot, this.stateRoot))
            throw new InvalidOperationException("Schematic state and staging require separate confined roots");
        this.policySha = policySha; this.runtimeSha = runtimeSha; this.styleSha = styleSha;
        this.validator = validator ?? new AssemblyDesignValidator();
        compiler = new(this.validator, renderer ?? new SvgSchematicRenderer());
    }
    static bool Under(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return path.Equals(root, comparison) || path.StartsWith(root + "/", comparison);
    }
    static void Id(string value)
    { if (value is null || value == ".." || !Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$", RegexOptions.CultureInvariant)) throw new InvalidOperationException("Invalid schematic asset identity"); }
    static void Hash(string value)
    { if (value is null || !Regex.IsMatch(value, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant)) throw new InvalidOperationException("Exact schematic SHA-256 required"); }
    static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    static string JsonDigest<T>(T value) => Digest(JsonSerializer.SerializeToUtf8Bytes(value, StrictDesignJson.Options));
    string JsonHash(string path) => Digest(StrictDesignJson.ReadBytes(workspace.Resolve(path)));
    string State(string asset, string version) => stateRoot + "/schematic-to-model/" + asset + "/" + version;
    FileStream Ownership(string asset) => workspace.Lock(stateRoot + "/schematic-to-model/" + asset + "/asset.lock");
    string Stage(string asset, string version) => stagingRoot + "/schematic-to-model/" + asset + "/" + version;
    public string ManifestPath(string asset, string version) => State(asset, version) + "/manifest.json";
    static string Fingerprint(DesignManifest manifest, SortedDictionary<string, string> artifacts)
        => JsonDigest(new { schemaVersion = "bubo-schematic-design-identity/v1", manifest.DesignSha256, manifest.StyleSha256, manifest.HostPolicySha256, manifest.RuntimeBuildSha256, artifacts });
    void Save(DesignManifest manifest)
    {
        workspace.Save(ManifestPath(manifest.Asset, manifest.Version), manifest);
        AuditJournal.Append(workspace, State(manifest.Asset, manifest.Version) + "/audit.jsonl", new
        { type = "schematic-design-state", manifest.Asset, manifest.Version, manifest.DesignSha256, previews = manifest.Previews.Count, reviews = manifest.Reviews.Count, at = DateTimeOffset.UtcNow });
    }
    DesignManifest Load(string path)
    {
        path = workspace.Relative(path);
        var manifest = StrictDesignJson.Read<DesignManifest>(workspace.Resolve(path));
        Id(manifest.Task); Id(manifest.Asset); Id(manifest.Version);
        if (manifest.SchemaVersion != "bubo-schematic-design/v1" || manifest.Task != task || path != ManifestPath(manifest.Asset, manifest.Version)
            || manifest.Staging != Stage(manifest.Asset, manifest.Version) || manifest.OriginalDesign != manifest.Staging + "/original/assembly-design.json")
            throw new InvalidOperationException("Schematic manifest identity or canonical layout differs");
        Hash(manifest.DesignSha256); Hash(manifest.HostPolicySha256); Hash(manifest.RuntimeBuildSha256); Hash(manifest.StyleSha256);
        if ((manifest.ParentSnapshot is null) != (manifest.ParentSha256 is null) || manifest.ParentSnapshot is not null && manifest.ParentSnapshot != manifest.Staging + "/original/parent-manifest.json")
            throw new InvalidOperationException("Schematic parent ancestry layout differs");
        if (manifest.Previews.Count > 256 || manifest.Reviews.Count > 1024 || manifest.PolicyInputs.Count > 128)
            throw new InvalidOperationException("Schematic state history exceeds bounds");
        foreach (var attempt in manifest.Previews)
            if (!Regex.IsMatch(attempt.Directory, "^" + Regex.Escape(manifest.Staging + "/attempts/preview-") + "[a-f0-9]{32}$") || attempt.Status is not ("running" or "complete" or "failed"))
                throw new InvalidOperationException("Invalid immutable schematic attempt");
        foreach (var review in manifest.Reviews)
        {
            Hash(review.Sha256);
            if (!Under(workspace.Relative(review.Source), stateRoot) || review.Snapshot != State(manifest.Asset, manifest.Version) + "/review-evidence/" + review.Sha256 + ".json"
                || JsonHash(review.Snapshot) != review.Sha256)
                throw new InvalidOperationException("Recorded schematic review ancestry changed");
        }
        return manifest;
    }
    AssemblyDesign Fresh(DesignManifest manifest, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (manifest.HostPolicySha256 != policySha || manifest.RuntimeBuildSha256 != runtimeSha || manifest.StyleSha256 != styleSha)
            throw new InvalidOperationException("Host/style/runtime changed; preserve history and initialize a new version");
        if (JsonHash(manifest.SourceDesign) != manifest.DesignSha256 || JsonHash(manifest.OriginalDesign) != manifest.DesignSha256)
            throw new InvalidOperationException("Schematic changed; preserve history and initialize a new version");
        foreach (var (path, hash) in manifest.PolicyInputs)
            if (workspace.HashProjectFile(path) != hash) throw new InvalidOperationException("Schematic policy evidence changed");
        if (manifest.ParentSnapshot is not null && JsonHash(manifest.ParentSnapshot) != manifest.ParentSha256) throw new InvalidOperationException("Parent snapshot changed");
        var design = StrictDesignJson.Read<AssemblyDesign>(workspace.Resolve(manifest.OriginalDesign));
        validator.RequireValid(design, cancellation);
        if (design.StyleSha256 != styleSha) throw new InvalidOperationException("Schematic design conflicts with selected style");
        return design;
    }
    public object Initialize(string designPath, string asset, string version, string? parentManifest = null, CancellationToken cancellation = default)
    {
        Id(asset); Id(version); designPath = workspace.Relative(designPath);
        if (Under(designPath, stateRoot) || Under(designPath, stagingRoot)) throw new InvalidOperationException("Design original must be a separately declared input");
        var state = State(asset, version); var stage = Stage(asset, version);
        using var ownership = Ownership(asset);
        if (File.Exists(workspace.Resolve(ManifestPath(asset, version))) || Directory.Exists(workspace.Resolve(stage))) throw new InvalidOperationException("Schematic version already exists; preserve history");
        cancellation.ThrowIfCancellationRequested(); workspace.HostContext?.RequireReference(designPath);
        var bytes = StrictDesignJson.ReadBytes(workspace.Resolve(designPath)); var hash = Digest(bytes);
        RequireRegisteredHash(designPath, hash);
        var design = StrictDesignJson.Parse<AssemblyDesign>(bytes); validator.RequireValid(design, cancellation);
        if (design.StyleSha256 != styleSha) throw new InvalidOperationException("Schematic design must bind selected style hash");
        byte[]? parent = null;
        if (parentManifest is not null)
        {
            workspace.HostContext?.RequireReference(parentManifest);
            var prior = Load(parentManifest);
            if (prior.Asset != asset || prior.Version == version || JsonHash(prior.OriginalDesign) != prior.DesignSha256) throw new InvalidOperationException("Schematic parent identity differs");
            parent = StrictDesignJson.ReadBytes(workspace.Resolve(parentManifest));
            RequireRegisteredHash(workspace.Relative(parentManifest), Digest(parent));
        }
        else
        {
            var directory = workspace.Resolve(stateRoot + "/schematic-to-model/" + asset);
            if (Directory.Exists(directory) && Directory.EnumerateDirectories(directory).Any(x => File.Exists(workspace.Resolve(workspace.Relative(x) + "/manifest.json"))))
                throw new InvalidOperationException("An existing asset requires explicit parent-manifest ancestry");
        }
        var original = stage + "/original/assembly-design.json";
        WriteNew(original, bytes);
        if (JsonHash(designPath) != hash || JsonHash(original) != hash) throw new InvalidOperationException("Schematic input changed while preserving original");
        var snapshot = parent is null ? null : stage + "/original/parent-manifest.json";
        if (snapshot is not null) WriteNew(snapshot, parent!);
        var manifest = new DesignManifest
        {
            SchemaVersion = "bubo-schematic-design/v1", Task = task, Asset = asset, Version = version, SourceDesign = designPath,
            OriginalDesign = original, DesignSha256 = hash, HostPolicySha256 = policySha, RuntimeBuildSha256 = runtimeSha, StyleSha256 = styleSha,
            Staging = stage, ParentSnapshot = snapshot, ParentSha256 = parent is null ? null : Digest(parent), Previews = [], Reviews = [],
            PolicyInputs = workspace.HostContext?.References.Where(x => x.Kind == "policy").ToDictionary(x => x.Path, x => x.Sha256, StringComparer.Ordinal) ?? new(StringComparer.Ordinal)
        };
        Save(manifest); return Describe(manifest, null, design, cancellation);
    }
    void WriteNew(string path, byte[] bytes)
    {
        var file = workspace.ResolveWrite(path); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes); stream.Flush(true);
    }
    void RequireRegisteredHash(string path, string hash)
    {
        if (workspace.HostContext is not { } host) return;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var reference = host.References.SingleOrDefault(x => x.Path.Equals(path, comparison));
        if (reference is null || reference.Sha256 != hash) throw new InvalidOperationException("Schematic input changed after registered reference admission");
    }
    public object Preview(string path, CancellationToken cancellation = default)
    {
        var first = Load(path);
        using var ownership = Ownership(first.Asset);
        var manifest = Load(path); var design = Fresh(manifest, cancellation);
        if (manifest.Previews.Count >= 256) throw new InvalidOperationException("Schematic preview history is full");
        var attempt = new PreviewAttempt { Directory = manifest.Staging + "/attempts/preview-" + Guid.NewGuid().ToString("N"), StartedAt = DateTimeOffset.UtcNow };
        manifest.Previews.Add(attempt); Save(manifest);
        try
        {
            var files = compiler.Compile(design, cancellation);
            var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal); long total = 0;
            foreach (var (name, text) in files.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                cancellation.ThrowIfCancellationRequested();
                if (!Regex.IsMatch(name, "^[A-Za-z0-9][A-Za-z0-9._-]{0,159}\\.(svg|json)$")) throw new InvalidOperationException("Unsafe schematic preview filename");
                var bytes = Utf8.GetBytes(text);
                if (bytes.LongLength > design.Budgets.MaxPreviewBytes - total) throw new InvalidOperationException("Schematic preview exceeds approved bytes");
                WriteNew(attempt.Directory + "/" + name, bytes); total += bytes.LongLength; hashes.Add(name, Digest(bytes));
            }
            var package = new DesignPackage { SchemaVersion = "bubo-schematic-preview/v1", DesignSha256 = manifest.DesignSha256,
                StyleSha256 = styleSha, HostPolicySha256 = policySha, RuntimeBuildSha256 = runtimeSha, Artifacts = hashes, TotalBytes = total, Fingerprint = Fingerprint(manifest, hashes) };
            WriteNew(attempt.Directory + "/preview-index.json", JsonSerializer.SerializeToUtf8Bytes(package, StrictDesignJson.Options));
            attempt.IndexSha256 = workspace.HashProjectFile(attempt.Directory + "/preview-index.json"); attempt.Status = "complete";
            Fresh(manifest, cancellation); VerifyPreview(manifest, design); Save(manifest);
            return Describe(manifest, package, design, cancellation);
        }
        catch (Exception error)
        { attempt.Status = "failed"; attempt.Error = error.Message; Save(manifest); throw; }
    }
    DesignPackage VerifyPreview(DesignManifest manifest, AssemblyDesign design)
    {
        var attempt = manifest.Previews.LastOrDefault();
        if (attempt?.Status != "complete") throw new InvalidOperationException("Complete all current schematic previews first");
        var index = attempt.Directory + "/preview-index.json";
        if (workspace.HashProjectFile(index) != attempt.IndexSha256) throw new InvalidOperationException("Schematic preview index changed");
        var package = StrictDesignJson.Read<DesignPackage>(workspace.Resolve(index));
        if (package.SchemaVersion != "bubo-schematic-preview/v1" || package.DesignSha256 != manifest.DesignSha256 || package.StyleSha256 != styleSha
            || package.HostPolicySha256 != policySha || package.RuntimeBuildSha256 != runtimeSha || package.Fingerprint != Fingerprint(manifest, package.Artifacts)
            || !new HashSet<string>(PreviewCompiler.RequiredFiles(design), StringComparer.Ordinal).SetEquals(package.Artifacts.Keys))
            throw new InvalidOperationException("Schematic preview identity or complete artifact inventory differs");
        long bytes = 0;
        foreach (var (name, hash) in package.Artifacts)
        {
            Hash(hash); var file = attempt.Directory + "/" + name;
            var size = new FileInfo(workspace.Resolve(file)).Length;
            if (size > design.Budgets.MaxPreviewBytes - bytes || workspace.HashProjectFile(file) != hash) throw new InvalidOperationException("Schematic preview changed or exceeds budget");
            bytes += size;
        }
        if (bytes != package.TotalBytes) throw new InvalidOperationException("Schematic preview size changed");
        return package;
    }
    WholeAssemblyReview ReviewEvidence(DesignManifest manifest, DesignPackage package, string path, byte[]? captured = null)
    {
        if (!Under(workspace.Relative(path), stateRoot)) throw new InvalidOperationException("Schematic review evidence must stay in task state");
        var review = captured is null ? StrictDesignJson.Read<WholeAssemblyReview>(workspace.Resolve(path)) : StrictDesignJson.Parse<WholeAssemblyReview>(captured);
        if (review.SchemaVersion != "bubo-schematic-review/v1" || review.Kind != "whole-assembly-design" || review.Decision is not ("approved" or "rejected")
            || !review.Human || string.IsNullOrWhiteSpace(review.Reviewer) || review.Reviewer.Length > 160 || review.Reason.Trim().Length is < 8 or > 4096
            || review.Asset != manifest.Asset || review.Version != manifest.Version || review.DesignSha256 != manifest.DesignSha256
            || review.DesignFingerprint != package.Fingerprint || review.ReviewedArtifacts.Count != package.Artifacts.Count
            || package.Artifacts.Any(x => review.ReviewedArtifacts.GetValueOrDefault(x.Key) != x.Value))
            throw new InvalidOperationException("Explicit human review of the exact complete schematic package is required");
        return review;
    }
    bool Approved(DesignManifest manifest, DesignPackage? package)
    {
        var latest = manifest.Reviews.LastOrDefault();
        if (package is null || latest is null || latest.Fingerprint != package.Fingerprint) return false;
        if (JsonHash(latest.Source) != latest.Sha256 || JsonHash(latest.Snapshot) != latest.Sha256) throw new InvalidOperationException("Recorded human schematic evidence changed");
        var review = ReviewEvidence(manifest, package, latest.Snapshot);
        if (review.Reviewer != latest.Reviewer || review.Decision != latest.Decision) throw new InvalidOperationException("Recorded schematic review identity differs");
        return review.Decision == "approved";
    }
    public object Record(string path, string kind, string reviewer, string evidence, CancellationToken cancellation = default)
    {
        var first = Load(path);
        using var ownership = Ownership(first.Asset);
        var manifest = Load(path); var design = Fresh(manifest, cancellation); var package = VerifyPreview(manifest, design);
        evidence = workspace.Relative(evidence); workspace.HostContext?.RequireReference(evidence);
        var bytes = StrictDesignJson.ReadBytes(workspace.Resolve(evidence)); var hash = Digest(bytes);
        var review = ReviewEvidence(manifest, package, evidence, bytes);
        if (kind != review.Kind || reviewer != review.Reviewer || JsonHash(evidence) != hash) throw new InvalidOperationException("Schematic review request/evidence differs");
        var registered = workspace.HostContext?.References.FirstOrDefault(x => x.Path.Equals(evidence, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        if (registered is not null && registered.Sha256 != hash) throw new InvalidOperationException("Schematic review changed after admission");
        if (manifest.Reviews.LastOrDefault() is { } prior && prior.Sha256 == hash && prior.Source == evidence && prior.Fingerprint == package.Fingerprint
            && prior.Decision == review.Decision && prior.Reviewer == reviewer) return Describe(manifest, package, design, cancellation);
        if (manifest.Reviews.Count >= 1024) throw new InvalidOperationException("Schematic review history is full");
        var snapshot = State(manifest.Asset, manifest.Version) + "/review-evidence/" + hash + ".json";
        if (File.Exists(workspace.Resolve(snapshot)))
        { if (JsonHash(snapshot) != hash) throw new InvalidOperationException("Existing schematic review snapshot differs"); }
        else WriteNew(snapshot, bytes);
        manifest.Reviews.Add(new(review.Decision, reviewer, evidence, snapshot, hash, package.Fingerprint));
        Fresh(manifest, cancellation); Save(manifest); return Describe(manifest, package, design, cancellation);
    }
    public object Status(string path, CancellationToken cancellation = default)
    {
        var first = Load(path);
        using var ownership = Ownership(first.Asset);
        var manifest = Load(path); var design = Fresh(manifest, cancellation);
        return Describe(manifest, manifest.Previews.LastOrDefault()?.Status == "complete" ? VerifyPreview(manifest, design) : null, design, cancellation);
    }
    public ValidationReport Validate(string path, CancellationToken cancellation = default)
    {
        var first = Load(path);
        using var ownership = Ownership(first.Asset);
        var manifest = Load(path); var design = Fresh(manifest, cancellation);
        if (manifest.Previews.Count > 0) Approved(manifest, VerifyPreview(manifest, design));
        return validator.Validate(design, cancellation);
    }
    internal sealed record PartInput(DesignManifest Manifest, AssemblyDesign Design, DesignPackage? Package, bool Approved);
    internal PartInput InspectParts(string path, CancellationToken cancellation = default, bool requireApproved = false)
    {
        var manifest = Load(path); var spec = Fresh(manifest, cancellation);
        var package = manifest.Previews.LastOrDefault()?.Status == "complete" ? VerifyPreview(manifest, spec) : null;
        var approved = Approved(manifest, package);
        if (requireApproved && (!approved || package is null)) throw new InvalidOperationException("Part construction requires human approval of the exact complete schematic design package");
        return new(manifest, spec, package, approved);
    }
    object Describe(DesignManifest manifest, DesignPackage? package, AssemblyDesign design, CancellationToken cancellation) => new
    {
        schemaVersion = "bubo-schematic-status/v1", task, manifest.Asset, manifest.Version,
        manifest = ManifestPath(manifest.Asset, manifest.Version), phase = "part-construction", manifest.DesignSha256,
        completeSchematicPreview = package is not null, designFingerprint = package?.Fingerprint,
        drawingDirectory = package is null ? null : manifest.Previews[^1].Directory, artifacts = package?.Artifacts,
        wholeAssemblyApproved = Approved(manifest, package), partConstructionAvailable = Approved(manifest, package) && new PartConstructionReadiness().Validate(design, cancellation).IsReady, assemblyAvailable = false,
        motionProofAvailable = false, modelExportAvailable = false, runtimeAcceptance = false,
        lastPreviewStatus = manifest.Previews.LastOrDefault()?.Status, lastPreviewError = manifest.Previews.LastOrDefault()?.Error,
        attempts = manifest.Previews.Count, reviews = manifest.Reviews.Count
    };
}
