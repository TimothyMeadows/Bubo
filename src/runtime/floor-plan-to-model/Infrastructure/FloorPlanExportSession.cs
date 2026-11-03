using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Bubo.FloorPlan.Domain;
using Hephaera.AssetProduction;

namespace Bubo.FloorPlan.Infrastructure;

internal sealed partial class FloorPlanExportSession
{
    readonly IWorkspace workspace;
    readonly DesignSession design;
    readonly ConstructionSession construction;
    readonly INativeProcess process;
    readonly string bundleDirectory, catalogSha256;
    readonly int timeoutSeconds;
    readonly long maxLogBytes;
    readonly long? maxPrivateBytes;
    readonly ExportResources resources;
    readonly ExportPayload payload;

    internal FloorPlanExportSession(IWorkspace workspace, DesignSession design, ConstructionSession construction,
        INativeProcess process, string bundleDirectory, int timeoutSeconds, long maxLogBytes, long? maxPrivateBytes, string catalogSha256)
    {
        this.workspace = workspace; this.design = design; this.construction = construction; this.process = process;
        this.bundleDirectory = bundleDirectory; this.timeoutSeconds = timeoutSeconds; this.maxLogBytes = maxLogBytes;
        this.maxPrivateBytes = maxPrivateBytes; this.catalogSha256 = catalogSha256;
        resources = new(workspace, bundleDirectory, catalogSha256); payload = new(workspace, catalogSha256);
    }

    static bool Hash(string? value) => value is not null && Regex.IsMatch(value, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant);
    string Parent(string manifest) => workspace.Relative(manifest)[..^"manifest.json".Length];
    string StatePath(string manifest) => Parent(manifest) + "exports.json";
    string LockPath(string manifest) => Parent(manifest) + "asset.lock";
    void NativeBudget()
    {
        if (!process.SupportsPrivateMemoryLimit || maxPrivateBytes is null)
            throw new PlatformNotSupportedException("Export/review requires an explicit supported native committed-memory ceiling; no unbounded fallback");
        if (maxPrivateBytes is < 67_108_864 or > 68_719_476_736 || timeoutSeconds is < 1 or > 86400 || maxLogBytes is < 1024 or > 1073741824)
            throw new InvalidOperationException("Invalid finite export process budgets");
    }
    FloorPlanExportState Load(string manifest, DesignSession.ConstructionInput input)
    {
        var path = StatePath(manifest);
        if (!File.Exists(workspace.Resolve(path))) return new()
        {
            Task = input.Manifest.Task, Asset = input.Manifest.Asset, Version = input.Manifest.Version,
            DesignManifest = workspace.Relative(manifest), RuntimeSha256 = input.Manifest.RuntimeBuildSha256
        };
        var state = StrictPlanJson.Read<FloorPlanExportState>(workspace.Resolve(path));
        if (state.SchemaVersion != "bubo-floor-plan-export-state/v1" || state.Task != input.Manifest.Task || state.Asset != input.Manifest.Asset
            || state.Version != input.Manifest.Version || state.DesignManifest != workspace.Relative(manifest)
            || state.RuntimeSha256 != input.Manifest.RuntimeBuildSha256 || state.Exports.Count > 128 || state.Views.Count > 128 || state.Reviews.Count > 1024)
            throw new InvalidOperationException("Export state identity or bounded history differs");
        bool Attempt(string directory, string kind)
        {
            var prefix = input.Manifest.Staging + "/attempts/" + kind + "-";
            return directory.StartsWith(prefix, StringComparison.Ordinal) && Regex.IsMatch(directory[prefix.Length..], "^[a-f0-9]{32}$", RegexOptions.CultureInvariant);
        }
        foreach (var attempt in state.Exports)
        {
            if (!Attempt(attempt.Directory, "export") || attempt.Status is not ("running" or "complete" or "failed")
                || !Hash(attempt.Identities.PlanSha256) || !Hash(attempt.Identities.DesignFingerprint) || !Hash(attempt.Identities.ApprovalSha256)
                || !Hash(attempt.Identities.RuntimeSha256) || !Hash(attempt.Identities.ConstructionJobSha256) || !Hash(attempt.Identities.ConstructionReportSha256)
                || !Hash(attempt.Identities.SourceSha256) || attempt.Bundle.Count != ExportResources.Names.Length || attempt.Bundle.Values.Any(h => !Hash(h))
                || string.IsNullOrWhiteSpace(attempt.ModelAuthor) || attempt.ModelAuthor.Length > 128 || attempt.ModelAuthor.Any(char.IsControl)
                || attempt.Jobs.Count > 3 || attempt.Jobs.Any(p => p.Key is not ("produce/job.json" or "verify-visual/job.json" or "verify-collision/job.json") || !Hash(p.Value)))
                throw new InvalidOperationException("Invalid export attempt state");
            if (attempt.Status == "complete" && (attempt.CompletedAt is null || attempt.Jobs.Count != 3 || !Hash(attempt.PackageSha256)
                || !Hash(attempt.ProducerReportSha256) || !Hash(attempt.VisualVerificationSha256) || !Hash(attempt.CollisionVerificationSha256)))
                throw new InvalidOperationException("Completed export state has incomplete evidence");
            workspace.Resolve(attempt.Directory); workspace.Resolve(attempt.ConstructionDirectory);
        }
        foreach (var view in state.Views)
        {
            if (!Attempt(view.Directory, "export-views") || view.Status is not ("running" or "complete" or "failed")
                || !Hash(view.PackageSha256) || !Hash(view.VisualVerificationSha256) || !Hash(view.CollisionVerificationSha256)
                || view.Bundle.Count != ExportResources.Names.Length || view.Bundle.Values.Any(h => !Hash(h)) || view.Artifacts.Count > 1158)
                throw new InvalidOperationException("Invalid exported-model view state");
            if (view.Status == "complete" && (view.CompletedAt is null || !Hash(view.JobSha256) || !Hash(view.ViewsSha256) || view.Artifacts.Count == 0))
                throw new InvalidOperationException("Completed view state has incomplete evidence");
            workspace.Resolve(view.Directory); workspace.Resolve(view.ExportDirectory);
        }
        foreach (var review in state.Reviews)
            if (review.Kind is not ("independent-model" or "human-model") || review.Decision is not ("approved" or "rejected")
                || !Hash(review.EvidenceSha256) || !Hash(review.PackageSha256) || !Hash(review.ViewsSha256))
                throw new InvalidOperationException("Invalid model review state");
        return state;
    }
    void Save(string manifest, FloorPlanExportState state)
    {
        ExportJson.SaveBounded(workspace, StatePath(manifest), state, StrictPlanJson.MaximumInputBytes);
        AuditJournal.Append(workspace, Parent(manifest) + "export-audit.jsonl", new
        {
            schemaVersion = "bubo-floor-plan-export-event/v1", state.Asset, state.Version,
            exports = state.Exports.Count, views = state.Views.Count, reviews = state.Reviews.Count,
            lastExport = state.Exports.LastOrDefault()?.Status, lastViews = state.Views.LastOrDefault()?.Status, at = DateTimeOffset.UtcNow
        });
    }
    static ExportNativeIdentities Identities(ConstructionSession.CurrentConstruction source, string? package = null)
        => new(source.Attempt.PlanSha256, source.Attempt.DesignFingerprint, source.Attempt.ApprovalSha256,
            source.Job.Identities.RuntimeSha256, source.Attempt.JobSha256!, source.Attempt.ReportSha256!, source.Attempt.BlendSha256!, package);

    GltfToolchain Toolchain(ConstructionSession.CurrentConstruction source, string blender, CancellationToken cancellation)
    {
        workspace.HostContext?.EnsureToolAllowed(blender);
        if (!Path.IsPathFullyQualified(blender) || !Path.GetFullPath(blender).Equals(Path.GetFullPath(source.Attempt.ToolPath), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Export must use the exact registered Blender selected for current construction");
        return GltfToolchainIdentity.Capture(source.Attempt.ToolPath, source.Attempt.ToolSha256, cancellation);
    }
    ConstructionSession.CurrentConstruction Fresh(string manifest, ConstructionSession.CurrentConstruction expected,
        SortedDictionary<string, string> bundle, GltfToolchain toolchain, CancellationToken cancellation)
    {
        workspace.HostContext?.ValidateFresh();
        var current = construction.InspectCurrentBuildUnderOwnership(manifest, cancellation);
        if (current.Attempt.Directory != expected.Attempt.Directory || Identities(current) != Identities(expected))
            throw new InvalidOperationException("Construction or complete design approval changed during export/review");
        resources.RequireCurrent(bundle); GltfToolchainIdentity.RequireCurrent(toolchain, cancellation);
        cancellation.ThrowIfCancellationRequested(); return current;
    }
    ExportNativeFile Reference(string path, string expected, long maximum, CancellationToken cancellation)
    {
        var reference = payload.Describe(path, maximum, cancellation);
        if (reference.Sha256 != expected) throw new InvalidOperationException("Export input changed; expected hashes are never rebound automatically");
        return reference;
    }
    ExportNativeInputs SourceInputs(ConstructionSession.CurrentConstruction source, bool producer, CancellationToken cancellation)
        => new(producer ? Reference(source.BlendPath, source.Attempt.BlendSha256!, source.Design.Plan.Budgets.MaxExportBytes, cancellation) : null,
            Reference(source.JobPath, source.Attempt.JobSha256!, source.Design.Plan.Construction!.MaxJobBytes, cancellation),
            producer ? Reference(source.ReportPath, source.Attempt.ReportSha256!, StrictPlanJson.MaximumInputBytes, cancellation) : null,
            Reference(source.Design.Manifest.OriginalPlan, source.Attempt.PlanSha256, StrictPlanJson.MaximumInputBytes, cancellation), null, null, null, null);

    ExportNativeLimits Limits(ConstructionSession.CurrentConstruction source, ExportInvocationBudget budget, CancellationToken cancellation)
    {
        var plan = source.Design.Plan; var profile = plan.Construction!;
        var remaining = budget.RemainingBytes(cancellation) - Math.Min(profile.MaxJobBytes, 1_048_576);
        if (remaining < 1024 || budget.RemainingGeometry <= 0) throw new InvalidOperationException("Cumulative export allowance exhausted before native work");
        return new(Math.Min(plan.Budgets.MaxExportBytes, ExportPackageValidator.MaxPayloadBytes), profile.MaxJobBytes, remaining,
            profile.MaxConstructionVertices, plan.Budgets.MaxGeneratedTriangles, Math.Min(40_000, source.Job.Scene.Meshes.Count + source.Job.Scene.Anchors.Count + 3 * plan.Storeys.Count + 1),
            Math.Min(20_000, profile.MaxMeshes), 4096, 100_000, 100_000, budget.RemainingGeometry, plan.Storeys.Count + plan.Stairs.Count + 6, 768);
    }
    static Dictionary<string, string> EnvironmentFor(string directory) => new(StringComparer.Ordinal)
    {
        ["TEMP"] = directory, ["TMP"] = directory, ["TMPDIR"] = directory, ["PYTHONDONTWRITEBYTECODE"] = "1", ["PYTHONNOUSERSITE"] = "1",
        ["XDG_CACHE_HOME"] = Path.Combine(directory, "cache"), ["BLENDER_USER_CONFIG"] = Path.Combine(directory, "config"),
        ["BLENDER_USER_SCRIPTS"] = Path.Combine(directory, "scripts"), ["BLENDER_USER_DATAFILES"] = Path.Combine(directory, "data"),
        ["BLENDER_USER_EXTENSIONS"] = Path.Combine(directory, "extensions")
    };
    sealed record NativeResult(ExportNativeReport Report, string ReportPath, string ReportSha256, string JobSha256);
    async Task<NativeResult> Run(string child, ExportNativeJob job, ExportInvocationBudget budget,
        Action<CancellationToken> refresh, Action<string> jobSaved, CancellationToken cancellation)
    {
        refresh(cancellation);
        Directory.CreateDirectory(workspace.ResolveWrite(child));
        var jobPath = child + "/job.json";
        payload.WriteJson(jobPath, job, Math.Min(1_048_576, job.Limits.MaxJobBytes), cancellation);
        var jobSha = ConstructionOutputVerifier.FileHash(workspace, jobPath, cancellation, Math.Min(1_048_576, job.Limits.MaxJobBytes));
        jobSaved(jobSha);
        refresh(cancellation);
        var temporary = workspace.ResolveWrite(child + "/temp"); Directory.CreateDirectory(temporary);
        var script = job.Operation == "views" ? "export_views.py" : "export.py";
        var log = child + "/native.log";
        await process.RunWithLimits(job.Toolchain.BlenderPath,
            ["--background", "--factory-startup", "--offline-mode", "--disable-autoexec", "--python-exit-code", "1", "--python", Path.Combine(bundleDirectory, script), "--", workspace.Resolve(jobPath), jobSha],
            workspace.Root, workspace.ResolveWrite(log), timeoutSeconds, maxPrivateBytes!.Value, cancellation, budget.RemainingLog, EnvironmentFor(temporary));
        refresh(cancellation);
        var report = ExportOutputVerifier.Verify(workspace, child + "/output", job, jobSha, cancellation);
        budget.Debit(report, log, cancellation);
        refresh(cancellation);
        var reportPath = child + "/output/" + (job.Operation == "produce" ? "producer-report.json" : job.Operation == "verify" ? "verification.json" : "views.json");
        var hash = ConstructionOutputVerifier.FileHash(workspace, reportPath, cancellation, StrictPlanJson.MaximumInputBytes);
        cancellation.ThrowIfCancellationRequested(); return new(report, reportPath, hash, jobSha);
    }

    internal async Task<object> Export(string manifest, string blender, string author, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds)); cancellation = deadline.Token;
        if (string.IsNullOrWhiteSpace(author) || author != author.Trim() || author.Length > 128 || author.Any(char.IsControl)) throw new InvalidOperationException("Explicit canonical model author identity is required for independent review separation");
        _ = design.InspectConstruction(manifest, cancellation, requireApproved: true);
        using var ownership = workspace.Lock(LockPath(manifest));
        var source = construction.InspectCurrentBuildUnderOwnership(manifest, cancellation);
        NativeBudget(); var bundle = resources.Capture(); var toolchain = Toolchain(source, blender, cancellation);
        var state = Load(manifest, source.Design);
        if (state.Exports.Count >= 128) throw new InvalidOperationException("Export attempt history is full");
        var attempt = new ExportAttempt
        {
            Directory = source.Design.Manifest.Staging + "/attempts/export-" + Guid.NewGuid().ToString("N"), StartedAt = DateTimeOffset.UtcNow,
            ModelAuthor = author, ConstructionDirectory = source.Attempt.Directory, Identities = Identities(source), Bundle = bundle, Toolchain = toolchain
        };
        state.Exports.Add(attempt);
        ExportJson.RequireSavedSize(state, StrictPlanJson.MaximumInputBytes - 65_536);
        Save(manifest, state);
        try
        {
            Directory.CreateDirectory(workspace.ResolveWrite(attempt.Directory));
            var budget = new ExportInvocationBudget(workspace, attempt.Directory, source.Design.Plan, maxLogBytes);
            void Refresh(CancellationToken token) => _ = Fresh(manifest, source, bundle, toolchain, token);
            var producerJob = new ExportNativeJob("bubo-floor-plan-export-job/v1", "produce", workspace.Root,
                workspace.ResolveWrite(attempt.Directory + "/produce/output"), bundle, toolchain, attempt.Identities,
                Limits(source, budget, cancellation), SourceInputs(source, true, cancellation));
            var produced = await Run(attempt.Directory + "/produce", producerJob, budget, Refresh,
                hash => { attempt.Jobs.Add("produce/job.json", hash); Save(manifest, state); }, cancellation);
            attempt.ProducerReportSha256 = produced.ReportSha256; Save(manifest, state);
            var sealedPayload = payload.Seal(source, attempt, produced.Report, catalogSha256, cancellation);
            attempt.PackageSha256 = sealedPayload.Sha256; Save(manifest, state);
            _ = budget.RemainingBytes(cancellation); Refresh(cancellation);
            foreach (var representation in new[] { "visual", "collision" })
            {
                var input = SourceInputs(source, false, cancellation) with
                {
                    Package = Reference(sealedPayload.Manifest, sealedPayload.Sha256, StrictPlanJson.MaximumInputBytes, cancellation),
                    Glb = Reference(sealedPayload.Directory + "/" + representation + ".glb", sealedPayload.Package.Files.Single(f => f.Path == representation + ".glb").Sha256, source.Design.Plan.Budgets.MaxExportBytes, cancellation)
                };
                var child = attempt.Directory + "/verify-" + representation;
                var job = new ExportNativeJob("bubo-floor-plan-export-job/v1", "verify", workspace.Root,
                    workspace.ResolveWrite(child + "/output"), bundle, toolchain, attempt.Identities with { PackageSha256 = sealedPayload.Sha256 },
                    Limits(source, budget, cancellation), input);
                var verified = await Run(child, job, budget, Refresh,
                    hash => { attempt.Jobs.Add("verify-" + representation + "/job.json", hash); Save(manifest, state); }, cancellation);
                if (verified.Report.Representation != representation) throw new InvalidOperationException("Verifier observed a different portable representation");
                if (representation == "visual") attempt.VisualVerificationSha256 = verified.ReportSha256;
                else attempt.CollisionVerificationSha256 = verified.ReportSha256;
                Save(manifest, state); _ = payload.Inspect(source, attempt, sealedPayload.Sha256, cancellation);
            }
            Refresh(cancellation); _ = payload.Inspect(source, attempt, attempt.PackageSha256, cancellation);
            _ = budget.RemainingBytes(cancellation); cancellation.ThrowIfCancellationRequested();
            attempt.Status = "complete"; attempt.CompletedAt = DateTimeOffset.UtcNow; Save(manifest, state);
            cancellation.ThrowIfCancellationRequested();
            return Describe(state, source.Design, true, false, false, false);
        }
        catch (Exception error)
        {
            attempt.Status = "failed"; attempt.Error = error.Message[..Math.Min(error.Message.Length, 4096)]; attempt.CompletedAt = DateTimeOffset.UtcNow; Save(manifest, state); throw;
        }
    }
}
