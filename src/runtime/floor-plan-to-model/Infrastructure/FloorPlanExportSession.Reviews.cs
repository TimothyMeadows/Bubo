using System.Text.Json;
using System.Text.Json.Nodes;
using Bubo.FloorPlan.Domain;
using Hephaera.AssetProduction;

namespace Bubo.FloorPlan.Infrastructure;

internal sealed partial class FloorPlanExportSession
{
    // Keep the exact decision/view files readable but not replaceable while later
    // source/tool/payload freshness checks run. Approval booleans describe this
    // same read snapshot rather than files released before the expensive checks.
    sealed class ReviewReadLeases(IWorkspace workspace) : IDisposable
    {
        readonly Dictionary<string, FileStream> files = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        public void Add(string path, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            path = workspace.Resolve(path);
            if (files.ContainsKey(path)) return;
            ExportFileGuards.NoAlternateStreams(path, cancellation);
            files.Add(path, new(path, FileMode.Open, FileAccess.Read, FileShare.Read));
        }
        public void RequireCurrent(CancellationToken cancellation)
        {
            // NTFS can create a named stream despite a default-stream read lease.
            foreach (var path in files.Keys) ExportFileGuards.NoAlternateStreams(path, cancellation);
            cancellation.ThrowIfCancellationRequested();
        }
        public void Dispose() { foreach (var file in files.Values) file.Dispose(); }
    }

    static void HoldReviews(ReviewReadLeases leases, FloorPlanExportState state, ExportAttempt exported,
        ExportViewAttempt views, CancellationToken cancellation)
    {
        leases.Add(views.Directory + "/job.json", cancellation);
        leases.Add(views.Directory + "/output/views.json", cancellation);
        foreach (var artifact in views.Artifacts) leases.Add(views.Directory + "/output/" + artifact.Path, cancellation);
        foreach (var kind in new[] { "independent-model", "human-model" })
        {
            var decision = state.Reviews.LastOrDefault(r => r.Kind == kind && r.PackageSha256 == exported.PackageSha256 && r.ViewsSha256 == views.ViewsSha256);
            if (decision is null) continue;
            leases.Add(decision.Evidence, cancellation); leases.Add(decision.SourceEvidence, cancellation);
        }
    }
    static bool Matches(ExportAttempt attempt, ConstructionSession.CurrentConstruction source)
        => attempt.Status == "complete" && attempt.ConstructionDirectory == source.Attempt.Directory && attempt.Identities == Identities(source);

    CurrentExportPayload InspectExport(string manifest, ConstructionSession.CurrentConstruction source, ExportAttempt attempt, CancellationToken cancellation)
    {
        if (!Matches(attempt, source) || !Hash(attempt.PackageSha256)) throw new InvalidOperationException("Latest export does not belong to current construction; no older-success fallback");
        _ = Fresh(manifest, source, attempt.Bundle, attempt.Toolchain, cancellation);
        var package = payload.Inspect(source, attempt, attempt.PackageSha256!, cancellation);
        foreach (var (phase, operation, expectedReportHash) in new[]
        {
            ("produce", "produce", attempt.ProducerReportSha256!), ("verify-visual", "verify", attempt.VisualVerificationSha256!),
            ("verify-collision", "verify", attempt.CollisionVerificationSha256!)
        })
        {
            var jobPath = attempt.Directory + "/" + phase + "/job.json";
            if (!attempt.Jobs.TryGetValue(phase + "/job.json", out var jobHash) || ConstructionOutputVerifier.FileHash(workspace, jobPath, cancellation, 1_048_576) != jobHash)
                throw new InvalidOperationException("Saved export job changed");
            var job = ExportJson.Read<ExportNativeJob>(workspace.Resolve(jobPath), 1_048_576, cancellation);
            var expected = attempt.Identities with { PackageSha256 = operation == "produce" ? null : package.Sha256 };
            if (job.Operation != operation || job.Identities != expected || job.WorkspaceRoot != workspace.Root
                || job.OutputDirectory != workspace.Resolve(attempt.Directory + "/" + phase + "/output"))
                throw new InvalidOperationException("Saved export job disagrees with its current attempt");
            var reportPath = attempt.Directory + "/" + phase + "/output/" + (operation == "produce" ? "producer-report.json" : "verification.json");
            using var reportRead = new FileStream(workspace.Resolve(reportPath), FileMode.Open, FileAccess.Read, FileShare.Read);
            if (ConstructionOutputVerifier.FileHash(workspace, reportPath, cancellation, StrictPlanJson.MaximumInputBytes) != expectedReportHash)
                throw new InvalidOperationException("Saved export verification changed");
            var report = ExportOutputVerifier.Verify(workspace, attempt.Directory + "/" + phase + "/output", job, jobHash, cancellation);
            if (operation == "verify" && report.Representation != phase["verify-".Length..]) throw new InvalidOperationException("Saved verifier representation differs");
            if (ConstructionOutputVerifier.FileHash(workspace, reportPath, cancellation, StrictPlanJson.MaximumInputBytes) != expectedReportHash)
                throw new InvalidOperationException("Saved export verification changed during inspection");
        }
        _ = Fresh(manifest, source, attempt.Bundle, attempt.Toolchain, cancellation);
        return payload.Inspect(source, attempt, package.Sha256, cancellation);
    }

    ExportNativeReport InspectViews(string manifest, ConstructionSession.CurrentConstruction source, ExportAttempt exported,
        ExportViewAttempt views, CancellationToken cancellation)
    {
        if (views.Status != "complete" || views.ExportDirectory != exported.Directory || views.PackageSha256 != exported.PackageSha256
            || views.VisualVerificationSha256 != exported.VisualVerificationSha256 || views.CollisionVerificationSha256 != exported.CollisionVerificationSha256)
            throw new InvalidOperationException("Latest view attempt is not a current complete package review");
        _ = Fresh(manifest, source, views.Bundle, views.Toolchain, cancellation);
        var jobPath = views.Directory + "/job.json";
        if (ConstructionOutputVerifier.FileHash(workspace, jobPath, cancellation, 1_048_576) != views.JobSha256)
            throw new InvalidOperationException("Saved package-view job changed");
        var job = ExportJson.Read<ExportNativeJob>(workspace.Resolve(jobPath), 1_048_576, cancellation);
        if (job.Operation != "views" || job.Identities != (exported.Identities with { PackageSha256 = exported.PackageSha256 })
            || job.WorkspaceRoot != workspace.Root || job.OutputDirectory != workspace.Resolve(views.Directory + "/output"))
            throw new InvalidOperationException("Saved view job belongs to another package");
        var reportPath = views.Directory + "/output/views.json";
        using var reportRead = new FileStream(workspace.Resolve(reportPath), FileMode.Open, FileAccess.Read, FileShare.Read);
        if (ConstructionOutputVerifier.FileHash(workspace, reportPath, cancellation, StrictPlanJson.MaximumInputBytes) != views.ViewsSha256)
            throw new InvalidOperationException("Saved model-view report changed");
        var report = ExportOutputVerifier.Verify(workspace, views.Directory + "/output", job, views.JobSha256!, cancellation);
        var actual = report.Outputs.Select(f => new ExportFileEntry(f.Path, f.Sha256, f.Bytes)).OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        if (!actual.SequenceEqual(views.Artifacts)) throw new InvalidOperationException("Saved model-view artifact inventory differs");
        _ = Fresh(manifest, source, views.Bundle, views.Toolchain, cancellation);
        if (ConstructionOutputVerifier.FileHash(workspace, reportPath, cancellation, StrictPlanJson.MaximumInputBytes) != views.ViewsSha256)
            throw new InvalidOperationException("Saved model views changed during inspection");
        return report;
    }

    List<ExportFileEntry> ReviewArtifacts(CurrentExportPayload package, ExportAttempt exported, ExportViewAttempt views, CancellationToken cancellation)
    {
        var result = package.Package.Files.Select(f => f with { Path = package.Directory + "/" + f.Path }).ToList();
        void Add(string path, string hash, long maximum)
        {
            var file = Reference(path, hash, maximum, cancellation); result.Add(new(workspace.Relative(file.Path), hash, file.Bytes));
        }
        Add(package.Manifest, package.Sha256, StrictPlanJson.MaximumInputBytes);
        Add(exported.Directory + "/verify-visual/output/verification.json", exported.VisualVerificationSha256!, StrictPlanJson.MaximumInputBytes);
        Add(exported.Directory + "/verify-collision/output/verification.json", exported.CollisionVerificationSha256!, StrictPlanJson.MaximumInputBytes);
        Add(views.Directory + "/output/views.json", views.ViewsSha256!, StrictPlanJson.MaximumInputBytes);
        foreach (var view in views.Artifacts) Add(views.Directory + "/output/" + view.Path, view.Sha256, view.Bytes);
        result.Sort((a, b) => StringComparer.Ordinal.Compare(a.Path, b.Path));
        cancellation.ThrowIfCancellationRequested(); return result;
    }
    string EvidenceRoot(DesignSession.ConstructionInput input, string manifest)
    {
        var suffix = "/floor-plan-to-model/" + input.Manifest.Asset + "/" + input.Manifest.Version + "/manifest.json";
        var path = workspace.Relative(manifest);
        if (!path.EndsWith(suffix, StringComparison.Ordinal) || path.Length <= suffix.Length)
            throw new InvalidOperationException("Cannot resolve canonical task state root");
        return path[..^suffix.Length];
    }
    FloorPlanModelReview ReadDecision(ExportModelReviewRecord record, ModelReviewBinding binding, CancellationToken cancellation)
    {
        foreach (var path in new[] { record.Evidence, record.SourceEvidence })
            if (ConstructionOutputVerifier.FileHash(workspace, path, cancellation, StrictPlanJson.MaximumInputBytes) != record.EvidenceSha256)
                throw new InvalidOperationException("Submitted model review or immutable snapshot changed");
        var review = ExportJson.Read<FloorPlanModelReview>(workspace.Resolve(record.Evidence), StrictPlanJson.MaximumInputBytes, cancellation);
        ExportPackageValidator.RequireValidModelReview(review, binding);
        if (review.Kind != record.Kind || review.Decision != record.Decision || review.Reviewer != record.Reviewer || review.Role != record.Role
            || review.Human != record.Human || review.PackageSha256 != record.PackageSha256 || review.ViewsSha256 != record.ViewsSha256
            || review.IndependentReviewSha256 != record.IndependentReviewSha256)
            throw new InvalidOperationException("Saved model decision disagrees with its exact submitted evidence");
        cancellation.ThrowIfCancellationRequested(); return review;
    }
    (bool Independent, bool Human, string? IndependentHash) Decisions(FloorPlanExportState state, CurrentExportPayload package,
        ExportAttempt exported, ExportViewAttempt views, List<ExportFileEntry> artifacts, CancellationToken cancellation)
    {
        ModelReviewBinding Binding(string kind, string? independent) => new()
        {
            Kind = kind, Author = package.Package.Ancestry.ModelAuthor, PackageSha256 = package.Sha256,
            VisualVerificationSha256 = exported.VisualVerificationSha256!, CollisionVerificationSha256 = exported.CollisionVerificationSha256!,
            ViewsSha256 = views.ViewsSha256!, IndependentReviewSha256 = independent, Artifacts = artifacts
        };
        var independent = state.Reviews.LastOrDefault(r => r.Kind == "independent-model" && r.PackageSha256 == package.Sha256 && r.ViewsSha256 == views.ViewsSha256);
        var independentApproved = independent is not null && ReadDecision(independent, Binding("independent-model", null), cancellation).Decision == "approved";
        var human = state.Reviews.LastOrDefault(r => r.Kind == "human-model" && r.PackageSha256 == package.Sha256 && r.ViewsSha256 == views.ViewsSha256);
        var humanApproved = false;
        if (independentApproved && human is not null && human.IndependentReviewSha256 == independent!.EvidenceSha256)
            humanApproved = ReadDecision(human, Binding("human-model", independent.EvidenceSha256), cancellation).Decision == "approved";
        return (independentApproved, humanApproved, independentApproved ? independent!.EvidenceSha256 : null);
    }

    internal object Record(string manifest, string kind, string reviewer, string evidence, CancellationToken cancellation)
    {
        if (kind is not ("independent-model" or "human-model")) throw new InvalidOperationException("Unsupported model review kind");
        _ = design.InspectConstruction(manifest, cancellation, requireApproved: true);
        using var ownership = workspace.Lock(LockPath(manifest));
        var source = construction.InspectCurrentBuildUnderOwnership(manifest, cancellation); var state = Load(manifest, source.Design);
        var exported = state.Exports.LastOrDefault() ?? throw new InvalidOperationException("Current export required before model decisions");
        var package = InspectExport(manifest, source, exported, cancellation);
        var views = state.Views.LastOrDefault() ?? throw new InvalidOperationException("Complete current model review views first");
        using var reviewRead = new ReviewReadLeases(workspace);
        HoldReviews(reviewRead, state, exported, views, cancellation);
        _ = InspectViews(manifest, source, exported, views, cancellation);
        var artifacts = ReviewArtifacts(package, exported, views, cancellation);
        var decisions = Decisions(state, package, exported, views, artifacts, cancellation);
        var binding = new ModelReviewBinding
        {
            Kind = kind, Author = package.Package.Ancestry.ModelAuthor, PackageSha256 = package.Sha256,
            VisualVerificationSha256 = exported.VisualVerificationSha256!, CollisionVerificationSha256 = exported.CollisionVerificationSha256!,
            ViewsSha256 = views.ViewsSha256!, IndependentReviewSha256 = kind == "human-model" ? decisions.IndependentHash : null, Artifacts = artifacts
        };
        evidence = workspace.Relative(evidence); var root = EvidenceRoot(source.Design, manifest);
        if (!evidence.StartsWith(root + "/", StringComparison.Ordinal)) throw new InvalidOperationException("Model decisions must be explicit evidence within the active task state");
        workspace.HostContext?.RequireReference(evidence);
        reviewRead.Add(evidence, cancellation);
        var hash = ConstructionOutputVerifier.FileHash(workspace, evidence, cancellation, StrictPlanJson.MaximumInputBytes);
        var decision = ExportJson.Read<FloorPlanModelReview>(workspace.Resolve(evidence), StrictPlanJson.MaximumInputBytes, cancellation);
        ExportPackageValidator.RequireValidModelReview(decision, binding);
        if (decision.Reviewer != reviewer || decision.Kind != kind) throw new InvalidOperationException("Requested reviewer/kind conflicts with submitted evidence");
        if (state.Reviews.Count >= 1024) throw new InvalidOperationException("Model decision history is full");
        var snapshot = Parent(manifest) + "model-review-evidence/" + hash + ".json";
        if (File.Exists(workspace.Resolve(snapshot)))
        {
            if (ConstructionOutputVerifier.FileHash(workspace, snapshot, cancellation, StrictPlanJson.MaximumInputBytes) != hash)
                throw new InvalidOperationException("Existing immutable model review snapshot differs");
        }
        else _ = payload.CopyExact(evidence, snapshot, hash, StrictPlanJson.MaximumInputBytes, cancellation);
        reviewRead.Add(snapshot, cancellation);
        _ = Fresh(manifest, source, exported.Bundle, exported.Toolchain, cancellation);
        _ = InspectExport(manifest, source, exported, cancellation); _ = InspectViews(manifest, source, exported, views, cancellation);
        if (ConstructionOutputVerifier.FileHash(workspace, evidence, cancellation, StrictPlanJson.MaximumInputBytes) != hash)
            throw new InvalidOperationException("Model review input changed during admission");
        state.Reviews.Add(new(kind, decision.Decision, decision.Reviewer, decision.Role, decision.Human, snapshot, hash, evidence,
            package.Sha256, views.ViewsSha256!, decision.IndependentReviewSha256, DateTimeOffset.UtcNow));
        Save(manifest, state); cancellation.ThrowIfCancellationRequested();
        var current = Decisions(state, package, exported, views, artifacts, cancellation);
        reviewRead.RequireCurrent(cancellation);
        return Describe(state, source.Design, true, true, current.Independent, current.Human);
    }

    internal object Status(string manifest, CancellationToken cancellation)
    {
        var input = design.InspectConstruction(manifest, cancellation);
        using var ownership = workspace.Lock(LockPath(manifest));
        input = design.InspectConstruction(manifest, cancellation); var state = Load(manifest, input);
        var exported = state.Exports.LastOrDefault(); var views = state.Views.LastOrDefault();
        using var reviewRead = new ReviewReadLeases(workspace);
        var exportCurrent = false; var viewCurrent = false; var independent = false; var human = false;
        List<ExportFileEntry>? artifacts = null;
        if (input.Approved && input.Plan.Construction is not null && exported?.Status == "complete")
        {
            var source = construction.InspectCurrentBuildUnderOwnership(manifest, cancellation);
            if (Matches(exported, source))
            {
                var package = InspectExport(manifest, source, exported, cancellation); exportCurrent = true;
                if (views?.Status == "complete" && views.ExportDirectory == exported.Directory && views.PackageSha256 == package.Sha256
                    && views.VisualVerificationSha256 == exported.VisualVerificationSha256 && views.CollisionVerificationSha256 == exported.CollisionVerificationSha256)
                {
                    HoldReviews(reviewRead, state, exported, views, cancellation);
                    _ = InspectViews(manifest, source, exported, views, cancellation); viewCurrent = true;
                    artifacts = ReviewArtifacts(package, exported, views, cancellation);
                    var decisions = Decisions(state, package, exported, views, artifacts, cancellation);
                    independent = decisions.Independent; human = decisions.Human;
                }
                _ = Fresh(manifest, source, exported.Bundle, exported.Toolchain, cancellation);
                _ = payload.Inspect(source, exported, package.Sha256, cancellation);
            }
        }
        cancellation.ThrowIfCancellationRequested();
        var result = JsonSerializer.SerializeToNode(Describe(state, input, exportCurrent, viewCurrent, independent, human), StrictPlanJson.Options)!.AsObject();
        result["reviewArtifacts"] = artifacts is null ? null : JsonSerializer.SerializeToNode(artifacts, StrictPlanJson.Options);
        reviewRead.RequireCurrent(cancellation);
        return result;
    }

    internal object Doctor(string manifest, string? blender, CancellationToken cancellation)
    {
        var input = design.InspectConstruction(manifest, cancellation); var unavailable = new List<string>(); GltfToolchain? toolchain = null;
        if (input.Plan.Construction is null || !input.Approved) unavailable.Add("exact current approved construction-ready v2 design required");
        if (!process.SupportsPrivateMemoryLimit || maxPrivateBytes is null) unavailable.Add("supported explicit native committed-memory ceiling required");
        try { _ = resources.Capture(); }
        catch (Exception error) when (error is InvalidOperationException or IOException or JsonException) { unavailable.Add("fixed reviewed export bundle unavailable: " + error.Message); }
        if (blender is null) unavailable.Add("explicit existing registered Blender 4.5 LTS required");
        else
        {
            try
            {
                workspace.HostContext?.EnsureToolAllowed(blender);
                var hash = workspace.HostContext?.HashToolFile(blender) ?? workspace.HashFile(blender);
                toolchain = GltfToolchainIdentity.Capture(blender, hash, cancellation);
            }
            catch (Exception error) when (error is InvalidOperationException or IOException or PlatformNotSupportedException) { unavailable.Add("bundled glTF dependency unavailable: " + error.Message); }
        }
        workspace.HostContext?.ValidateFresh(); cancellation.ThrowIfCancellationRequested();
        return new { schemaVersion = "bubo-floor-plan-export-doctor/v1", declared = true, nativeReadiness = "unverified", installedToolchainIdentity = toolchain?.Fingerprint,
            unavailable, reason = "Read-only exact dependency inspection; actual export/import/render operations still establish executable readiness", modelExportAvailable = input.Plan.Construction is not null };
    }

    internal async Task<object> Review(string manifest, string blender, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds)); cancellation = deadline.Token;
        _ = design.InspectConstruction(manifest, cancellation, requireApproved: true);
        using var ownership = workspace.Lock(LockPath(manifest));
        var source = construction.InspectCurrentBuildUnderOwnership(manifest, cancellation);
        NativeBudget(); var state = Load(manifest, source.Design);
        var exported = state.Exports.LastOrDefault() ?? throw new InvalidOperationException("Export a current building package before model review views");
        var package = InspectExport(manifest, source, exported, cancellation);
        var toolchain = Toolchain(source, blender, cancellation); var bundle = resources.Capture();
        if (toolchain.Fingerprint != exported.Toolchain.Fingerprint || bundle.Any(p => exported.Bundle.GetValueOrDefault(p.Key) != p.Value))
            throw new InvalidOperationException("View tool/bundle differs from the sealed export; preserve history and create a new export");
        if (state.Views.Count >= 128) throw new InvalidOperationException("Model-view attempt history is full");
        var views = new ExportViewAttempt
        {
            Directory = source.Design.Manifest.Staging + "/attempts/export-views-" + Guid.NewGuid().ToString("N"), StartedAt = DateTimeOffset.UtcNow,
            ExportDirectory = exported.Directory, PackageSha256 = package.Sha256,
            VisualVerificationSha256 = exported.VisualVerificationSha256!, CollisionVerificationSha256 = exported.CollisionVerificationSha256!, Bundle = bundle, Toolchain = toolchain
        };
        state.Views.Add(views);
        // Fixed view filenames/hashes/lengths add at most 512 JSON bytes each;
        // also reserve escaped bounded error text and terminal identities.
        var terminalReserve = 65_536 + 512 * (source.Design.Plan.Storeys.Count + source.Design.Plan.Stairs.Count + 6);
        ExportJson.RequireSavedSize(state, StrictPlanJson.MaximumInputBytes - terminalReserve);
        Save(manifest, state);
        try
        {
            Directory.CreateDirectory(workspace.ResolveWrite(views.Directory));
            var budget = new ExportInvocationBudget(workspace, views.Directory, source.Design.Plan, maxLogBytes);
            void Refresh(CancellationToken token)
            {
                _ = Fresh(manifest, source, bundle, toolchain, token);
                _ = payload.Inspect(source, exported, package.Sha256, token);
                foreach (var (kind, hash) in new[] { ("visual", views.VisualVerificationSha256), ("collision", views.CollisionVerificationSha256) })
                    if (ConstructionOutputVerifier.FileHash(workspace, exported.Directory + "/verify-" + kind + "/output/verification.json", token, StrictPlanJson.MaximumInputBytes) != hash)
                        throw new InvalidOperationException("Prior representation verification changed during model view generation");
            }
            var inputs = SourceInputs(source, false, cancellation) with
            {
                Package = Reference(package.Manifest, package.Sha256, StrictPlanJson.MaximumInputBytes, cancellation),
                VisualVerification = Reference(exported.Directory + "/verify-visual/output/verification.json", views.VisualVerificationSha256, StrictPlanJson.MaximumInputBytes, cancellation),
                CollisionVerification = Reference(exported.Directory + "/verify-collision/output/verification.json", views.CollisionVerificationSha256, StrictPlanJson.MaximumInputBytes, cancellation)
            };
            var job = new ExportNativeJob("bubo-floor-plan-export-job/v1", "views", workspace.Root,
                workspace.ResolveWrite(views.Directory + "/output"), bundle, toolchain, exported.Identities with { PackageSha256 = package.Sha256 },
                Limits(source, budget, cancellation), inputs);
            var rendered = await Run(views.Directory, job, budget, Refresh, hash => { views.JobSha256 = hash; Save(manifest, state); }, cancellation);
            views.ViewsSha256 = rendered.ReportSha256;
            views.Artifacts = rendered.Report.Outputs.Select(f => new ExportFileEntry(f.Path, f.Sha256, f.Bytes)).OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
            Refresh(cancellation); _ = budget.RemainingBytes(cancellation); cancellation.ThrowIfCancellationRequested();
            views.Status = "complete"; views.CompletedAt = DateTimeOffset.UtcNow; Save(manifest, state);
            _ = InspectViews(manifest, source, exported, views, cancellation);
            cancellation.ThrowIfCancellationRequested(); return Describe(state, source.Design, true, true, false, false);
        }
        catch (Exception error)
        {
            views.Status = "failed"; views.Error = error.Message[..Math.Min(error.Message.Length, 4096)]; views.CompletedAt = DateTimeOffset.UtcNow; Save(manifest, state); throw;
        }
    }

    static object Describe(FloorPlanExportState state, DesignSession.ConstructionInput input, bool exported, bool viewed, bool independent, bool human)
    {
        var package = state.Exports.LastOrDefault(); var views = state.Views.LastOrDefault();
        return new
        {
            schemaVersion = "bubo-floor-plan-export-status/v1", state.Asset, state.Version, phase = "architectural-export",
            wholeBuildingApproved = input.Approved, currentExportValid = exported, portableVerified = exported,
            packageManifest = exported ? package!.Directory + "/package/package.json" : null,
            packageSha256 = exported ? package!.PackageSha256 : null,
            currentViewsValid = viewed, views = viewed ? views!.Directory + "/output/views.json" : null,
            independentModelReviewed = independent, humanModelAccepted = human, runtimeAcceptance = false,
            modelExportAvailable = input.Approved && input.Plan.Construction is not null, exports = state.Exports.Count, viewAttempts = state.Views.Count,
            lastExportStatus = package?.Status, lastExportError = package?.Error, lastViewStatus = views?.Status, lastViewError = views?.Error
        };
    }
}
