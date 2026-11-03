using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bubo.FloorPlan.Application;
using Bubo.FloorPlan.Domain;
using Hephaera.AssetProduction;
namespace Bubo.FloorPlan.Infrastructure;

public sealed record DesignPackage
{
    public string SchemaVersion { get; init; } = "bubo-architectural-preview/v1";
    public required string PlanSha256 { get; init; }
    public required string StyleSha256 { get; init; }
    public required string HostPolicySha256 { get; init; }
    public required string RuntimeBuildSha256 { get; init; }
    public required SortedDictionary<string, string> Drawings { get; init; }
    public required long TotalBytes { get; init; }
    public required string Fingerprint { get; init; }
}
public sealed record WholeBuildingReview
{
    public required string SchemaVersion { get; init; }
    public required string Kind { get; init; }
    public required string Decision { get; init; }
    public required bool Human { get; init; }
    public required string Reviewer { get; init; }
    public required string Reason { get; init; }
    public required string Asset { get; init; }
    public required string Version { get; init; }
    public required string PlanSha256 { get; init; }
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
public sealed record DesignReviewRecord(string Kind, string Decision, string Reviewer, string Evidence, string EvidenceSha256, string SourceEvidence, string Fingerprint, DateTimeOffset RecordedAt);
public sealed record DesignEvent(string Event, DateTimeOffset At, string? Detail = null);
public sealed record DesignManifest
{
    public string SchemaVersion { get; init; } = "bubo-floor-plan-design/v1";
    public required string Task { get; init; }
    public required string Asset { get; init; }
    public required string Version { get; init; }
    public required string SourcePlan { get; init; }
    public required string OriginalPlan { get; init; }
    public required string PlanSha256 { get; init; }
    public required string HostPolicySha256 { get; init; }
    public required string RuntimeBuildSha256 { get; init; }
    public required string StyleSha256 { get; init; }
    public required string Staging { get; init; }
    public string? ParentSnapshot { get; init; }
    public string? ParentSha256 { get; init; }
    public Dictionary<string, string> PolicyInputs { get; init; } = new(StringComparer.Ordinal);
    public List<PreviewAttempt> Previews { get; init; } = [];
    public List<DesignReviewRecord> Reviews { get; init; } = [];
    public List<DesignEvent> History { get; init; } = [];
}

// Filesystem policy is supplied by the caller; no host instructions or engine resolver are read.
public sealed class DesignSession
{
    readonly IWorkspace workspace;
    readonly string task, stateRoot, stagingRoot, policySha, runtimeSha, styleSha;
    readonly IBuildingPlanValidator validator;
    readonly PreviewCompiler compiler;
    static readonly UTF8Encoding Utf8 = new(false, true);
    public DesignSession(IWorkspace workspace, string task, string stateRoot, string stagingRoot,
        string policySha, string runtimeSha, string styleSha, IBuildingPlanValidator? validator = null,
        IArchitecturalPreviewRenderer? renderer = null)
    {
        this.workspace = workspace; Id(task); Hash(policySha); Hash(runtimeSha); Hash(styleSha);
        this.task = task; this.stateRoot = Relative(stateRoot); this.stagingRoot = Relative(stagingRoot);
        if (this.stateRoot == "." || this.stagingRoot == "." || Under(this.stateRoot, this.stagingRoot) || Under(this.stagingRoot, this.stateRoot))
            throw new InvalidOperationException("Design state and staging require separate confined roots");
        this.policySha = policySha; this.runtimeSha = runtimeSha; this.styleSha = styleSha;
        this.validator = validator ?? new BuildingPlanValidator();
        compiler = new(this.validator, renderer ?? new SvgPlanRenderer());
    }
    string Relative(string value) => workspace.Relative(value);
    string JsonHash(string value) => Digest(StrictPlanJson.ReadBytes(workspace.Resolve(value)));
    static bool Under(string path, string root) => path == root || path.StartsWith(root + "/", StringComparison.Ordinal);
    static void Id(string value) { if (value is null || !Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$", RegexOptions.CultureInvariant) || value == "..") throw new InvalidOperationException("Invalid design asset identity"); }
    static void Hash(string value) { if (value is null || !Regex.IsMatch(value, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant)) throw new InvalidOperationException("Missing exact design hash identity"); }
    static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    static string JsonDigest<T>(T value) => Digest(JsonSerializer.SerializeToUtf8Bytes(value, StrictPlanJson.Options));
    string State(string asset, string version) => $"{stateRoot}/floor-plan-to-model/{asset}/{version}";
    string ManifestPath(string asset, string version) => State(asset, version) + "/manifest.json";
    string Stage(string asset, string version) => $"{stagingRoot}/floor-plan-to-model/{asset}/{version}";
    static string Fingerprint(string plan, string style, string policy, string runtime, SortedDictionary<string, string> drawings)
        => JsonDigest(new { schemaVersion = "bubo-design-identity/v1", plan, style, policy, runtime, drawings });
    void Save(DesignManifest manifest)
    {
        workspace.Save(ManifestPath(manifest.Asset, manifest.Version), manifest);
        var audit = State(manifest.Asset, manifest.Version) + "/audit.jsonl";
        workspace.ResolveWrite(audit);
        AuditJournal.Append(workspace, audit, new { type = "design-state", manifest.Asset, manifest.Version, manifest.PlanSha256, previews = manifest.Previews.Count, reviews = manifest.Reviews.Count, timestamp = DateTimeOffset.UtcNow });
    }
    DesignManifest Load(string path)
    {
        path = Relative(path);
        var manifest = StrictPlanJson.Read<DesignManifest>(workspace.Resolve(path));
        Id(manifest.Task); Id(manifest.Asset); Id(manifest.Version);
        if (manifest.SchemaVersion != "bubo-floor-plan-design/v1" || manifest.Task != task || path != ManifestPath(manifest.Asset, manifest.Version) || manifest.Staging != Stage(manifest.Asset, manifest.Version))
            throw new InvalidOperationException("Design manifest identity or canonical layout differs");
        if (manifest.OriginalPlan != manifest.Staging + "/original/building-plan.json") throw new InvalidOperationException("Design original violates immutable layout");
        Hash(manifest.PlanSha256); Hash(manifest.HostPolicySha256); Hash(manifest.RuntimeBuildSha256); Hash(manifest.StyleSha256);
        if ((manifest.ParentSnapshot is null) != (manifest.ParentSha256 is null)) throw new InvalidOperationException("Incomplete parent design ancestry");
        if (manifest.ParentSnapshot is not null && manifest.ParentSnapshot != manifest.Staging + "/original/parent-manifest.json") throw new InvalidOperationException("Parent snapshot violates immutable layout");
        if (manifest.Previews.Count > 256 || manifest.Reviews.Count > 1024 || manifest.History.Count > 2048) throw new InvalidOperationException("Design state exceeds bounded history");
        foreach (var attempt in manifest.Previews)
        {
            if (!Under(Relative(attempt.Directory), manifest.Staging + "/attempts") || !Regex.IsMatch(attempt.Directory[(manifest.Staging + "/attempts/").Length..], "^preview-[a-f0-9]{32}$") || attempt.Status is not ("running" or "complete" or "failed"))
                throw new InvalidOperationException("Invalid immutable preview attempt");
        }
        foreach (var review in manifest.Reviews)
        {
            Hash(review.EvidenceSha256);
            if (Relative(review.Evidence) != State(manifest.Asset, manifest.Version) + "/review-evidence/" + review.EvidenceSha256 + ".json" || !Under(Relative(review.SourceEvidence), stateRoot))
                throw new InvalidOperationException("Review evidence must retain its immutable task-local snapshot");
            if (JsonHash(review.Evidence) != review.EvidenceSha256) throw new InvalidOperationException("Historical human review snapshot changed");
        }
        return manifest;
    }
    BuildingPlan Fresh(DesignManifest manifest, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (manifest.HostPolicySha256 != policySha || manifest.RuntimeBuildSha256 != runtimeSha || manifest.StyleSha256 != styleSha)
            throw new InvalidOperationException("Host/style/runtime identity changed; preserve history and initialize a new design version");
        if (JsonHash(manifest.SourcePlan) != manifest.PlanSha256 || JsonHash(manifest.OriginalPlan) != manifest.PlanSha256)
            throw new InvalidOperationException("Design input changed; preserve the approved version and initialize a new design version");
        foreach (var (path, hash) in manifest.PolicyInputs) if (workspace.HashProjectFile(path) != hash) throw new InvalidOperationException("Design policy evidence changed");
        if (manifest.ParentSnapshot is not null && JsonHash(manifest.ParentSnapshot) != manifest.ParentSha256) throw new InvalidOperationException("Parent design snapshot changed");
        var plan = StrictPlanJson.Read<BuildingPlan>(workspace.Resolve(manifest.OriginalPlan));
        validator.RequireValid(plan, cancellation);
        if (plan.StyleSha256 != styleSha) throw new InvalidOperationException("Building design conflicts with the selected host style");
        return plan;
    }
    public object Initialize(string sourcePlan, string asset, string version, string? parentManifest = null, CancellationToken cancellation = default)
    {
        Id(asset); Id(version); sourcePlan = Relative(sourcePlan);
        if (Under(sourcePlan, State(asset, version)) || Under(sourcePlan, Stage(asset, version))) throw new InvalidOperationException("Original design must be an explicit input, not this workflow's state/output");
        var state = State(asset, version); var stage = Stage(asset, version); var path = ManifestPath(asset, version);
        if (File.Exists(workspace.Resolve(path)) || Directory.Exists(workspace.Resolve(stage))) throw new InvalidOperationException("Design version already exists; preserve its evidence");
        using var ownership = workspace.Lock(state + "/asset.lock");
        if (File.Exists(workspace.Resolve(path)) || Directory.Exists(workspace.Resolve(stage))) throw new InvalidOperationException("Another initialization owns this version");
        cancellation.ThrowIfCancellationRequested();
        workspace.HostContext?.RequireReference(sourcePlan);
        var sourceHash = JsonHash(sourcePlan);
        var plan = StrictPlanJson.Read<BuildingPlan>(workspace.Resolve(sourcePlan)); validator.RequireValid(plan, cancellation);
        if (plan.StyleSha256 != styleSha) throw new InvalidOperationException("Building design must bind the selected host style hash");
        byte[]? parentBytes = null;
        if (parentManifest is not null)
        {
            var parent = Load(parentManifest);
            if (parent.Asset != asset || parent.Version == version || JsonHash(parent.OriginalPlan) != parent.PlanSha256) throw new InvalidOperationException("Parent version ancestry differs");
            parentBytes = StrictPlanJson.ReadBytes(workspace.Resolve(parentManifest));
        }
        else
        {
            var assetDirectory = workspace.Resolve(stateRoot + "/floor-plan-to-model/" + asset);
            if (Directory.Exists(assetDirectory) && Directory.EnumerateDirectories(assetDirectory).Any(directory => File.Exists(workspace.Resolve(Relative(directory) + "/manifest.json"))))
                throw new InvalidOperationException("An existing asset needs explicit --parent-manifest ancestry for its next version");
        }
        var original = stage + "/original/building-plan.json";
        Directory.CreateDirectory(workspace.ResolveWrite(stage + "/original"));
        File.Copy(workspace.Resolve(sourcePlan), workspace.ResolveWrite(original), false);
        if (JsonHash(sourcePlan) != sourceHash || JsonHash(original) != sourceHash) throw new InvalidOperationException("Source changed while preserving immutable original");
        var snapshot = parentBytes is null ? null : stage + "/original/parent-manifest.json";
        if (snapshot is not null) File.WriteAllBytes(workspace.ResolveWrite(snapshot), parentBytes!);
        var manifest = new DesignManifest
        {
            Task = task, Asset = asset, Version = version, SourcePlan = sourcePlan, OriginalPlan = original,
            PlanSha256 = sourceHash, HostPolicySha256 = policySha, RuntimeBuildSha256 = runtimeSha, StyleSha256 = styleSha, Staging = stage,
            ParentSnapshot = snapshot, ParentSha256 = parentBytes is null ? null : Digest(parentBytes),
            PolicyInputs = workspace.HostContext?.References.Where(x => x.Kind == "policy").ToDictionary(x => x.Path, x => x.Sha256, StringComparer.Ordinal) ?? new(StringComparer.Ordinal)
        };
        manifest.History.Add(new("initialized", DateTimeOffset.UtcNow)); Save(manifest);
        return Describe(manifest, null);
    }
    public object Preview(string manifestPath, CancellationToken cancellation = default)
    {
        var initial = Load(manifestPath);
        using var ownership = workspace.Lock(State(initial.Asset, initial.Version) + "/asset.lock");
        var manifest = Load(manifestPath); var plan = Fresh(manifest, cancellation);
        if (manifest.Previews.Count >= 256) throw new InvalidOperationException("Immutable preview attempt history is full");
        var attempt = new PreviewAttempt { Directory = manifest.Staging + "/attempts/preview-" + Guid.NewGuid().ToString("N"), StartedAt = DateTimeOffset.UtcNow };
        manifest.Previews.Add(attempt); manifest.History.Add(new("preview-started", DateTimeOffset.UtcNow, attempt.Directory)); Save(manifest);
        try
        {
            var compiled = compiler.Compile(plan, cancellation);
            Directory.CreateDirectory(workspace.ResolveWrite(attempt.Directory));
            foreach (var (name, text) in compiled.Sheets)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!Regex.IsMatch(name, "^[A-Za-z0-9][A-Za-z0-9._-]{0,159}\\.svg$")) throw new InvalidOperationException("Unsafe preview filename");
                var file = workspace.ResolveWrite(attempt.Directory + "/" + name);
                using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(Utf8.GetBytes(text));
            }
            var drawings = new SortedDictionary<string, string>(compiled.DrawingHashes.ToDictionary(x => x.Key, x => x.Value), StringComparer.Ordinal);
            var package = new DesignPackage { PlanSha256 = manifest.PlanSha256, StyleSha256 = styleSha, HostPolicySha256 = policySha, RuntimeBuildSha256 = runtimeSha, Drawings = drawings, TotalBytes = compiled.TotalBytes, Fingerprint = Fingerprint(manifest.PlanSha256, styleSha, policySha, runtimeSha, drawings) };
            workspace.Save(attempt.Directory + "/preview-index.json", package);
            attempt.IndexSha256 = workspace.HashProjectFile(attempt.Directory + "/preview-index.json"); attempt.Status = "complete";
            Fresh(manifest, cancellation); VerifyPreview(manifest, plan);
            manifest.History.Add(new("preview-complete", DateTimeOffset.UtcNow, package.Fingerprint)); Save(manifest);
            return Describe(manifest, package);
        }
        catch (Exception error)
        {
            attempt.Status = "failed"; attempt.Error = error.Message; manifest.History.Add(new("preview-failed", DateTimeOffset.UtcNow, error.Message)); Save(manifest); throw;
        }
    }
    DesignPackage VerifyPreview(DesignManifest manifest, BuildingPlan plan)
    {
        var attempt = manifest.Previews.LastOrDefault();
        if (attempt?.Status != "complete") throw new InvalidOperationException("Complete current architectural previews first");
        var indexPath = attempt.Directory + "/preview-index.json";
        if (workspace.HashProjectFile(indexPath) != attempt.IndexSha256) throw new InvalidOperationException("Architectural preview index changed");
        var package = StrictPlanJson.Read<DesignPackage>(workspace.Resolve(indexPath));
        var names = Enumerable.Range(1, plan.Storeys.Count).Select(i => $"floor-{i:D2}.svg")
            .Concat(new[] { "roof-plan.svg", "elevation-north.svg", "elevation-south.svg", "elevation-east.svg", "elevation-west.svg", "section-principal.svg" })
            .Concat(Enumerable.Range(1, plan.Stairs.Count).Select(i => $"section-stair-{i:D2}.svg")).ToHashSet(StringComparer.Ordinal);
        if (package.SchemaVersion != "bubo-architectural-preview/v1" || !names.SetEquals(package.Drawings.Keys) || package.PlanSha256 != manifest.PlanSha256 || package.StyleSha256 != styleSha || package.HostPolicySha256 != policySha || package.RuntimeBuildSha256 != runtimeSha || package.Fingerprint != Fingerprint(manifest.PlanSha256, styleSha, policySha, runtimeSha, package.Drawings))
            throw new InvalidOperationException("Architectural preview identity or full sheet inventory differs");
        long bytes = 0;
        foreach (var (name, hash) in package.Drawings)
        {
            Hash(hash); var file = attempt.Directory + "/" + name;
            var length = new FileInfo(workspace.Resolve(file)).Length;
            if (length > plan.Budgets.MaxPreviewBytes - bytes) throw new InvalidOperationException("Architectural drawing exceeds declared byte budget");
            if (workspace.HashProjectFile(file) != hash) throw new InvalidOperationException("Architectural drawing changed: " + name);
            bytes = checked(bytes + length);
        }
        if (bytes != package.TotalBytes || bytes > plan.Budgets.MaxPreviewBytes) throw new InvalidOperationException("Architectural preview output exceeds budget or changed size");
        return package;
    }
    WholeBuildingReview ReviewEvidence(DesignManifest manifest, DesignPackage package, string evidence)
    {
        evidence = Relative(evidence);
        if (!Under(evidence, stateRoot)) throw new InvalidOperationException("Human review evidence must stay in the active task state");
        var review = StrictPlanJson.Read<WholeBuildingReview>(workspace.Resolve(evidence));
        if (review.SchemaVersion != "bubo-floor-plan-review/v1" || review.Kind != "whole-building-design" || review.Decision is not ("approved" or "rejected") || !review.Human || string.IsNullOrWhiteSpace(review.Reviewer) || review.Reviewer.Length > 160 || review.Reason.Trim().Length is < 8 or > 4096 || review.Asset != manifest.Asset || review.Version != manifest.Version || review.PlanSha256 != manifest.PlanSha256 || review.DesignFingerprint != package.Fingerprint || review.ReviewedArtifacts.Count != package.Drawings.Count || package.Drawings.Any(pair => review.ReviewedArtifacts.GetValueOrDefault(pair.Key) != pair.Value))
            throw new InvalidOperationException("Explicit human review of the exact complete architectural package is required");
        return review;
    }
    public object Record(string manifestPath, string kind, string reviewer, string evidence, CancellationToken cancellation = default)
    {
        var initial = Load(manifestPath);
        using var ownership = workspace.Lock(State(initial.Asset, initial.Version) + "/asset.lock");
        var manifest = Load(manifestPath); var plan = Fresh(manifest, cancellation); var package = VerifyPreview(manifest, plan);
        var sourceEvidence = Relative(evidence);
        workspace.HostContext?.RequireReference(evidence);
        var registered = workspace.HostContext?.References.FirstOrDefault(x => x.Path.Equals(sourceEvidence, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        var evidenceBytes = StrictPlanJson.ReadBytes(workspace.Resolve(evidence));
        var hash = Digest(evidenceBytes);
        if (registered is not null && registered.Sha256 != hash) throw new InvalidOperationException("Human evidence changed after registered reference admission");
        var review = ReviewEvidence(manifest, package, evidence);
        if (JsonHash(evidence) != hash) throw new InvalidOperationException("Human evidence changed during review admission");
        if (kind != review.Kind || reviewer != review.Reviewer) throw new InvalidOperationException("Requested review identity conflicts with supplied human evidence");
        if (manifest.Reviews.LastOrDefault() is { } prior && prior.EvidenceSha256 == hash && prior.Fingerprint == package.Fingerprint) return Describe(manifest, package);
        if (manifest.Reviews.Count >= 1024) throw new InvalidOperationException("Design review history is full");
        var snapshot = State(manifest.Asset, manifest.Version) + "/review-evidence/" + hash + ".json";
        var snapshotFile = workspace.ResolveWrite(snapshot);
        Directory.CreateDirectory(Path.GetDirectoryName(snapshotFile)!);
        if (File.Exists(snapshotFile))
        {
            if (JsonHash(snapshot) != hash) throw new InvalidOperationException("Existing human review snapshot differs; preserve and reconcile it");
        }
        else
        {
            using var stream = new FileStream(snapshotFile, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(evidenceBytes);
        }
        manifest.Reviews.Add(new(review.Kind, review.Decision, review.Reviewer, snapshot, hash, sourceEvidence, package.Fingerprint, DateTimeOffset.UtcNow));
        manifest.History.Add(new("human-design-" + review.Decision, DateTimeOffset.UtcNow, package.Fingerprint)); Save(manifest);
        return Describe(manifest, package);
    }
    public object Status(string manifestPath, CancellationToken cancellation = default)
    {
        var initial = Load(manifestPath);
        using var ownership = workspace.Lock(State(initial.Asset, initial.Version) + "/asset.lock");
        var manifest = Load(manifestPath); var plan = Fresh(manifest, cancellation);
        return Describe(manifest, manifest.Previews.LastOrDefault()?.Status == "complete" ? VerifyPreview(manifest, plan) : null);
    }
    public ValidationReport Validate(string manifestPath, CancellationToken cancellation = default)
    {
        var initial = Load(manifestPath);
        using var ownership = workspace.Lock(State(initial.Asset, initial.Version) + "/asset.lock");
        var manifest = Load(manifestPath); var plan = Fresh(manifest, cancellation);
        if (manifest.Previews.Count > 0) VerifyPreview(manifest, plan);
        return validator.Validate(plan, cancellation);
    }
    object Describe(DesignManifest manifest, DesignPackage? package)
    {
        var latest = manifest.Reviews.LastOrDefault(); var approved = false;
        if (package is not null && latest is not null && latest.Fingerprint == package.Fingerprint)
        {
            if (JsonHash(latest.SourceEvidence) != latest.EvidenceSha256 || JsonHash(latest.Evidence) != latest.EvidenceSha256) throw new InvalidOperationException("Recorded human review evidence changed");
            var review = ReviewEvidence(manifest, package, latest.Evidence);
            if (review.Reviewer != latest.Reviewer || review.Decision != latest.Decision || review.Kind != latest.Kind) throw new InvalidOperationException("Recorded human review identity differs");
            approved = review.Decision == "approved";
        }
        return new { schemaVersion = "bubo-floor-plan-status/v1", task, manifest.Asset, manifest.Version,
            manifest = ManifestPath(manifest.Asset, manifest.Version), phase = "design-foundation", manifest.PlanSha256,
            completeArchitecturalPreview = package is not null, designFingerprint = package?.Fingerprint,
            drawingDirectory = package is null ? null : manifest.Previews[^1].Directory, drawings = package?.Drawings,
            wholeBuildingApproved = approved, modelingAvailable = false, modelExportAvailable = false,
            runtimeAcceptance = false, lastPreviewStatus = manifest.Previews.LastOrDefault()?.Status, lastPreviewError = manifest.Previews.LastOrDefault()?.Error, attempts = manifest.Previews.Count, reviews = manifest.Reviews.Count };
    }
}
