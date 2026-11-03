using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bubo.FloorPlan.Domain;
using Hephaera.AssetProduction;
namespace Bubo.FloorPlan.Infrastructure;

public sealed class ConstructionSession(IWorkspace workspace, DesignSession design, INativeProcess process,
    string bundleDirectory, int timeoutSeconds, long maxLogBytes, long? maxPrivateBytes, string resourceCatalogSha256)
{
    static bool Hash(string? value) => value is not null && Regex.IsMatch(value, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant);
    string StateFile(string manifestPath) => workspace.Relative(manifestPath)[..^"manifest.json".Length] + "construction.json";
    string LockPath(string manifestPath) => workspace.Relative(manifestPath)[..^"manifest.json".Length] + "asset.lock";
    SortedDictionary<string, string> Bundle()
    {
        var resourceRoot = Directory.GetParent(bundleDirectory)?.Parent?.FullName ?? throw new InvalidOperationException("Invalid fixed authoring bundle layout");
        var catalog = Path.Combine(resourceRoot, "catalog.json");
        if (!Hash(resourceCatalogSha256) || !File.Exists(catalog) || new FileInfo(catalog).Length > 1_048_576 || workspace.HashFile(catalog) != resourceCatalogSha256)
            throw new InvalidOperationException("The construction resource catalog differs from the compiled reviewed identity");
        using var inventory = JsonDocument.Parse(File.ReadAllBytes(catalog));
        var files = inventory.RootElement.GetProperty("files");
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in new[] { "author.py", "admission.py" })
        {
            var path = Path.Combine(bundleDirectory, name);
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Fixed floor-plan authoring bundle is missing or linked");
            var digest = workspace.HashFile(path);
            if (!files.TryGetProperty("resources/floor-plan-to-model/blender/" + name, out var declared) || declared.GetString() != digest)
                throw new InvalidOperationException("Fixed construction script is not registered with exact bytes in the reviewed catalog");
            result.Add(name, digest);
        }
        return result;
    }
    void FreshBundle(SortedDictionary<string, string> expected)
    {
        var actual = Bundle();
        if (actual.Count != expected.Count || expected.Any(p => actual.GetValueOrDefault(p.Key) != p.Value)) throw new InvalidOperationException("Audited construction bundle changed during execution");
    }
    string ToolHash(string executable)
    {
        if (!Path.IsPathFullyQualified(executable)) throw new InvalidOperationException("Explicit absolute registered Blender executable is required");
        if (!File.Exists(executable) || (File.GetAttributes(executable) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Selected Blender executable is missing or linked");
        return workspace.HostContext?.HashToolFile(executable) ?? workspace.HashFile(executable);
    }
    void NativeBudget()
    {
        if (maxPrivateBytes is null) throw new InvalidOperationException("Construction requires an explicit host aggregate committed-memory ceiling");
        if (!process.SupportsPrivateMemoryLimit) throw new PlatformNotSupportedException("The selected native runner cannot enforce the construction memory ceiling; no unbounded launch");
        if (maxPrivateBytes is < 67_108_864 or > 68_719_476_736 || timeoutSeconds is < 1 or > 86400 || maxLogBytes is < 1024 or > 1073741824)
            throw new InvalidOperationException("Invalid explicit construction native resource budget");
    }
    ConstructionState Load(string manifestPath, DesignSession.ConstructionInput input)
    {
        var path = StateFile(manifestPath);
        if (!File.Exists(workspace.Resolve(path))) return new() { Task = input.Manifest.Task, Asset = input.Manifest.Asset, Version = input.Manifest.Version, DesignManifest = workspace.Relative(manifestPath), RuntimeSha256 = input.Manifest.RuntimeBuildSha256 };
        var state = StrictPlanJson.Read<ConstructionState>(workspace.Resolve(path));
        if (state.SchemaVersion != "bubo-floor-plan-construction/v1" || state.Task != input.Manifest.Task || state.Asset != input.Manifest.Asset || state.Version != input.Manifest.Version || state.DesignManifest != workspace.Relative(manifestPath) || state.RuntimeSha256 != input.Manifest.RuntimeBuildSha256 || state.Attempts.Count > 128)
            throw new InvalidOperationException("Construction state identity or bounded history differs");
        foreach (var attempt in state.Attempts)
        {
            var prefix = input.Manifest.Staging + "/attempts/build-";
            if (!attempt.Directory.StartsWith(prefix, StringComparison.Ordinal) || !Regex.IsMatch(attempt.Directory[prefix.Length..], "^[a-f0-9]{32}$") || attempt.Status is not ("running" or "complete" or "failed") || !Hash(attempt.PlanSha256) || !Hash(attempt.DesignFingerprint) || !Hash(attempt.ApprovalSha256) || !Hash(attempt.ToolSha256) || attempt.Bundle.Count != 2 || !attempt.Bundle.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(new[] { "author.py", "admission.py" }) || attempt.Bundle.Values.Any(v => !Hash(v)))
                throw new InvalidOperationException("Construction attempt identity is invalid");
            if (attempt.Status == "complete" && (!Hash(attempt.JobSha256) || !Hash(attempt.ReportSha256) || !Hash(attempt.BlendSha256) || attempt.CompletedAt is null))
                throw new InvalidOperationException("Completed construction attempt has incomplete identities");
            workspace.Resolve(attempt.Directory);
        }
        return state;
    }
    void Save(string manifestPath, ConstructionState state)
    {
        workspace.Save(StateFile(manifestPath), state);
        AuditJournal.Append(workspace, StateFile(manifestPath)[..^"construction.json".Length] + "construction-audit.jsonl", new { schemaVersion = "bubo-construction-event/v1", state.Asset, state.Version, attempts = state.Attempts.Count, last = state.Attempts.LastOrDefault()?.Status, at = DateTimeOffset.UtcNow });
    }
    static Dictionary<string, string> EnvironmentFor(string directory)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal) { ["TEMP"] = directory, ["TMP"] = directory, ["TMPDIR"] = directory, ["PYTHONDONTWRITEBYTECODE"] = "1", ["PYTHONNOUSERSITE"] = "1", ["XDG_CACHE_HOME"] = Path.Combine(directory, "cache"), ["BLENDER_USER_CONFIG"] = Path.Combine(directory, "config"), ["BLENDER_USER_SCRIPTS"] = Path.Combine(directory, "scripts"), ["BLENDER_USER_DATAFILES"] = Path.Combine(directory, "data"), ["BLENDER_USER_EXTENSIONS"] = Path.Combine(directory, "extensions") };
        return result;
    }
    public async Task<object> Doctor(string manifestPath, string? blender, CancellationToken cancellation = default)
    {
        var input = design.InspectConstruction(manifestPath, cancellation);
        var unavailable = new List<string>(); string? version = null; string? toolSha = null;
        if (input.Plan.Construction is null) unavailable.Add("construction-ready approved v2 plan required");
        if (!input.Approved) unavailable.Add("exact complete whole-building approval required");
        if (maxPrivateBytes is null) unavailable.Add("explicit native aggregate committed-memory ceiling required");
        if (!process.SupportsPrivateMemoryLimit) unavailable.Add("selected platform has no supported native construction memory enforcement");
        try { _ = Bundle(); }
        catch (Exception error) when (error is InvalidOperationException or IOException or JsonException) { unavailable.Add("fixed audited authoring bundle unavailable: " + error.Message); }
        if (blender is null) unavailable.Add("select an existing registered Blender 4.5 LTS executable");
        else
        {
            toolSha = ToolHash(blender);
            if (maxPrivateBytes is not null && process.SupportsPrivateMemoryLimit)
            {
            NativeBudget();
            var directory = input.Manifest.Staging + "/attempts/doctor-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(workspace.ResolveWrite(directory + "/temp"));
            var log = workspace.ResolveWrite(directory + "/version.log");
            await process.RunWithLimits(blender, ["--version"], workspace.Root, log, Math.Min(timeoutSeconds, 30), maxPrivateBytes!.Value, cancellation, Math.Min(maxLogBytes, 16384), EnvironmentFor(workspace.Resolve(directory + "/temp")));
            var content = File.ReadAllText(log); var match = Regex.Match(content, "Blender (4\\.5\\.[0-9]+(?: LTS)?)");
            if (!match.Success) throw new InvalidOperationException("Selected executable did not report Blender 4.5 LTS");
            version = match.Groups[1].Value;
            if (ToolHash(blender) != toolSha) throw new InvalidOperationException("Selected Blender changed during prerequisite inspection");
            }
        }
        workspace.HostContext?.ValidateFresh(); input = design.InspectConstruction(manifestPath, cancellation);
        try { _ = Bundle(); }
        catch (Exception error) when (error is InvalidOperationException or IOException or JsonException) { unavailable.Add("fixed audited authoring bundle changed/unavailable after inspection: " + error.Message); }
        if (!input.Approved && !unavailable.Contains("exact complete whole-building approval required")) unavailable.Add("exact complete whole-building approval required");
        cancellation.ThrowIfCancellationRequested();
        return new { schemaVersion = "bubo-floor-plan-doctor/v1", phase = input.Plan.Construction is null ? "design-foundation" : "architectural-construction", vectorPreviewAvailable = true, modelingAvailable = input.Plan.Construction is not null && input.Approved, modelExportAvailable = false, provisioningRequired = false, nativeReadiness = blender is not null && unavailable.Count == 0 ? "verified" : "unverified", blenderVersion = version, toolSha256 = toolSha, unavailable, note = "No model generation, installation or model/runtime acceptance occurs during prerequisite inspection." };
    }
    public async Task<object> Build(string manifestPath, string blender, CancellationToken cancellation = default)
    {
        var initial = design.InspectConstruction(manifestPath, cancellation, requireApproved: true);
        using var ownership = workspace.Lock(LockPath(manifestPath));
        var input = design.InspectConstruction(manifestPath, cancellation, requireApproved: true);
        NativeBudget(); var toolSha = ToolHash(blender); var bundle = Bundle();
        var state = Load(manifestPath, input);
        if (state.Attempts.Count >= 128) throw new InvalidOperationException("Construction attempt history is full; preserve it");
        var attempt = new ConstructionAttempt { Directory = input.Manifest.Staging + "/attempts/build-" + Guid.NewGuid().ToString("N"), StartedAt = DateTimeOffset.UtcNow, PlanSha256 = input.Manifest.PlanSha256, DesignFingerprint = input.Package!.Fingerprint, ApprovalSha256 = input.Manifest.Reviews[^1].EvidenceSha256, ToolPath = Path.GetFullPath(blender), ToolSha256 = toolSha, Bundle = bundle };
        state.Attempts.Add(attempt); Save(manifestPath, state);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            var scene = new ConstructionGeometryCompiler().Compile(input.Plan, cancellation);
            if (scene.LinearTolerance != input.Plan.Numerics.LinearTolerance) throw new InvalidOperationException("Compiled geometry changed the approved tolerance");
            var profile = input.Plan.Construction!;
            var output = attempt.Directory + "/output";
            var limits = new ConstructionLimits(input.Plan.Budgets.MaxGeneratedTriangles, profile.MaxConstructionVertices, checked(scene.Meshes.Count + scene.Anchors.Count), input.Plan.Budgets.MaxExportBytes, profile.MaxJobBytes);
            var job = new ConstructionBlenderJob("bubo-floor-plan-blender-job/v1", "construct", workspace.Root, workspace.ResolveWrite(output), scene, new(attempt.PlanSha256, attempt.DesignFingerprint, input.Manifest.RuntimeBuildSha256), bundle, limits);
            Directory.CreateDirectory(workspace.ResolveWrite(attempt.Directory + "/temp"));
            var jobPath = attempt.Directory + "/job.json";
            using (var file = new FileStream(workspace.ResolveWrite(jobPath), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var bounded = new LimitedWriteStream(file, profile.MaxJobBytes, cancellation)) JsonSerializer.Serialize(bounded, job, StrictPlanJson.Options);
            attempt.JobSha256 = ConstructionOutputVerifier.FileHash(workspace, jobPath, cancellation, profile.MaxJobBytes); Save(manifestPath, state);
            Refresh(manifestPath, attempt, cancellation); FreshBundle(bundle);
            await process.RunWithLimits(blender, ["--background", "--factory-startup", "--disable-autoexec", "--offline-mode", "--python-exit-code", "1", "--python", Path.Combine(bundleDirectory, "author.py"), "--", workspace.Resolve(jobPath), attempt.JobSha256], workspace.Root, workspace.ResolveWrite(attempt.Directory + "/author.log"), timeoutSeconds, maxPrivateBytes!.Value, cancellation, maxLogBytes, EnvironmentFor(workspace.Resolve(attempt.Directory + "/temp")));
            Refresh(manifestPath, attempt, cancellation); FreshBundle(bundle);
            using var jobRead = new FileStream(workspace.Resolve(jobPath), FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reportRead = new FileStream(workspace.Resolve(output + "/construction-report.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
            using var blendRead = new FileStream(workspace.Resolve(output + "/building.blend"), FileMode.Open, FileAccess.Read, FileShare.Read);
            if (ConstructionOutputVerifier.FileHash(workspace, jobPath, cancellation, profile.MaxJobBytes) != attempt.JobSha256) throw new InvalidOperationException("Construction job changed during authoring");
            var reportPath = output + "/construction-report.json";
            var reportHash = ConstructionOutputVerifier.FileHash(workspace, reportPath, cancellation, StrictPlanJson.MaximumInputBytes);
            var report = ConstructionOutputVerifier.Verify(workspace, output, job, attempt.JobSha256, cancellation);
            if (ConstructionOutputVerifier.FileHash(workspace, reportPath, cancellation, StrictPlanJson.MaximumInputBytes) != reportHash) throw new InvalidOperationException("Construction report changed during verification");
            Refresh(manifestPath, attempt, cancellation); FreshBundle(bundle);
            if (ConstructionOutputVerifier.FileHash(workspace, jobPath, cancellation, profile.MaxJobBytes) != attempt.JobSha256) throw new InvalidOperationException("Construction job changed during final verification");
            ConstructionOutputVerifier.VerifyInventory(workspace, output, input.Plan.Budgets.MaxExportBytes);
            cancellation.ThrowIfCancellationRequested();
            attempt.ReportSha256 = reportHash; attempt.BlendSha256 = report.Blend.Sha256;
            attempt.CompletedAt = DateTimeOffset.UtcNow; attempt.Status = "complete"; Save(manifestPath, state);
            cancellation.ThrowIfCancellationRequested();
            return Describe(state, input, true);
        }
        catch (Exception error)
        {
            attempt.Status = "failed"; attempt.Error = error.Message; attempt.CompletedAt = DateTimeOffset.UtcNow; Save(manifestPath, state); throw;
        }
    }
    void Refresh(string manifestPath, ConstructionAttempt attempt, CancellationToken cancellation)
    {
        workspace.HostContext?.ValidateFresh();
        var current = design.InspectConstruction(manifestPath, cancellation, requireApproved: true);
        if (current.Manifest.PlanSha256 != attempt.PlanSha256 || current.Package!.Fingerprint != attempt.DesignFingerprint || current.Manifest.Reviews[^1].EvidenceSha256 != attempt.ApprovalSha256 || ToolHash(attempt.ToolPath) != attempt.ToolSha256)
            throw new InvalidOperationException("Design approval, input or selected tool changed during construction");
    }
    internal sealed record CurrentConstruction(DesignSession.ConstructionInput Design, ConstructionAttempt Attempt,
        ConstructionBlenderJob Job, ConstructionReport Report, string JobPath, string ReportPath, string BlendPath);

    // Caller owns the canonical asset.lock for the whole export/review operation.
    // Keep this typed inspection separate from public Status to avoid recursive locking.
    internal CurrentConstruction InspectCurrentBuildUnderOwnership(string manifestPath, CancellationToken cancellation)
    {
        var input = design.InspectConstruction(manifestPath, cancellation, requireApproved: true);
        var state = Load(manifestPath, input);
        var latest = state.Attempts.LastOrDefault();
        if (input.Plan.Construction is null || latest?.Status != "complete")
            throw new InvalidOperationException("Export requires the latest current successful v2 construction; no older-success fallback");
        Refresh(manifestPath, latest, cancellation); FreshBundle(latest.Bundle);
        var jobPath = latest.Directory + "/job.json";
        var reportPath = latest.Directory + "/output/construction-report.json";
        var blendPath = latest.Directory + "/output/building.blend";
        using var jobRead = new FileStream(workspace.Resolve(jobPath), FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reportRead = new FileStream(workspace.Resolve(reportPath), FileMode.Open, FileAccess.Read, FileShare.Read);
        using var blendRead = new FileStream(workspace.Resolve(blendPath), FileMode.Open, FileAccess.Read, FileShare.Read);
        ConstructionOutputVerifier.VerifyInventory(workspace, latest.Directory + "/output", input.Plan.Budgets.MaxExportBytes);
        if (ConstructionOutputVerifier.FileHash(workspace, jobPath, cancellation, input.Plan.Construction.MaxJobBytes) != latest.JobSha256
            || ConstructionOutputVerifier.FileHash(workspace, reportPath, cancellation, StrictPlanJson.MaximumInputBytes) != latest.ReportSha256
            || ConstructionOutputVerifier.FileHash(workspace, blendPath, cancellation, input.Plan.Budgets.MaxExportBytes) != latest.BlendSha256)
            throw new InvalidOperationException("Current construction input/output identities changed before export");
        var job = ExportJson.Read<ConstructionBlenderJob>(workspace.Resolve(jobPath), input.Plan.Construction.MaxJobBytes, cancellation);
        if (job.SchemaVersion != "bubo-floor-plan-blender-job/v1" || job.Operation != "construct"
            || job.WorkspaceRoot != workspace.Root || job.OutputDirectory != workspace.Resolve(latest.Directory + "/output")
            || job.Identities.PlanSha256 != latest.PlanSha256 || job.Identities.DesignFingerprint != latest.DesignFingerprint
            || job.Identities.RuntimeSha256 != state.RuntimeSha256 || job.Bundle.Count != latest.Bundle.Count
            || latest.Bundle.Any(p => job.Bundle.GetValueOrDefault(p.Key) != p.Value))
            throw new InvalidOperationException("Current construction job disagrees with its immutable attempt");
        var report = ConstructionOutputVerifier.Verify(workspace, latest.Directory + "/output", job, latest.JobSha256!, cancellation);
        Refresh(manifestPath, latest, cancellation); FreshBundle(latest.Bundle);
        if (ConstructionOutputVerifier.FileHash(workspace, jobPath, cancellation, input.Plan.Construction.MaxJobBytes) != latest.JobSha256
            || ConstructionOutputVerifier.FileHash(workspace, reportPath, cancellation, StrictPlanJson.MaximumInputBytes) != latest.ReportSha256)
            throw new InvalidOperationException("Construction references changed during export admission");
        ConstructionOutputVerifier.VerifyInventory(workspace, latest.Directory + "/output", input.Plan.Budgets.MaxExportBytes);
        cancellation.ThrowIfCancellationRequested();
        return new(input, latest, job, report, jobPath, reportPath, blendPath);
    }
    public object Status(string manifestPath, CancellationToken cancellation = default)
    {
        var initial = design.InspectConstruction(manifestPath, cancellation);
        using var ownership = workspace.Lock(LockPath(manifestPath));
        var input = design.InspectConstruction(manifestPath, cancellation); var state = Load(manifestPath, input); var latest = state.Attempts.LastOrDefault();
        var current = latest?.Status == "complete" && input.Plan.Construction is not null && input.Approved && input.Package?.Fingerprint == latest.DesignFingerprint && input.Manifest.PlanSha256 == latest.PlanSha256 && input.Manifest.Reviews[^1].EvidenceSha256 == latest.ApprovalSha256;
        if (current)
        {
            FreshBundle(latest!.Bundle);
            var job = latest.Directory + "/job.json"; var report = latest.Directory + "/output/construction-report.json"; var blend = latest.Directory + "/output/building.blend";
            using var jobRead = new FileStream(workspace.Resolve(job), FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reportRead = new FileStream(workspace.Resolve(report), FileMode.Open, FileAccess.Read, FileShare.Read);
            using var blendRead = new FileStream(workspace.Resolve(blend), FileMode.Open, FileAccess.Read, FileShare.Read);
            ConstructionOutputVerifier.VerifyInventory(workspace, latest.Directory + "/output", input.Plan.Budgets.MaxExportBytes);
            if (ConstructionOutputVerifier.FileHash(workspace, job, cancellation, input.Plan.Construction!.MaxJobBytes) != latest.JobSha256 || ConstructionOutputVerifier.FileHash(workspace, report, cancellation, StrictPlanJson.MaximumInputBytes) != latest.ReportSha256 || ConstructionOutputVerifier.FileHash(workspace, blend, cancellation, input.Plan.Budgets.MaxExportBytes) != latest.BlendSha256 || ToolHash(latest.ToolPath) != latest.ToolSha256)
                throw new InvalidOperationException("Construction output/job/tool changed; preserve evidence and rebuild in a fresh attempt");
            workspace.HostContext?.ValidateFresh(); input = design.InspectConstruction(manifestPath, cancellation);
            current = input.Plan.Construction is not null && input.Approved && input.Package?.Fingerprint == latest.DesignFingerprint && input.Manifest.PlanSha256 == latest.PlanSha256 && input.Manifest.Reviews[^1].EvidenceSha256 == latest.ApprovalSha256;
            FreshBundle(latest.Bundle); ConstructionOutputVerifier.VerifyInventory(workspace, latest.Directory + "/output", input.Plan.Budgets.MaxExportBytes);
        }
        cancellation.ThrowIfCancellationRequested(); return Describe(state, input, current);
    }
    static object Describe(ConstructionState state, DesignSession.ConstructionInput input, bool current)
    {
        var last = state.Attempts.LastOrDefault();
        return new { schemaVersion = "bubo-floor-plan-construction-status/v1", state.Asset, state.Version, phase = "architectural-construction", wholeBuildingApproved = input.Approved, currentBuildValid = current, editableSource = current ? last!.Directory + "/output/building.blend" : null, report = current ? last!.Directory + "/output/construction-report.json" : null, designFingerprint = input.Package?.Fingerprint, attempts = state.Attempts.Count, lastStatus = last?.Status, lastError = last?.Error, modelExportAvailable = false, independentReimport = false, humanModelAccepted = false, runtimeAcceptance = false };
    }
    sealed class LimitedWriteStream(Stream inner, long maximum, CancellationToken cancellation) : Stream
    {
        long written;
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => written; public override long Position { get => written; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer) { cancellation.ThrowIfCancellationRequested(); if (buffer.Length > maximum - written) throw new InvalidOperationException("Construction job exceeds approved byte budget"); inner.Write(buffer); written = checked(written + buffer.Length); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
