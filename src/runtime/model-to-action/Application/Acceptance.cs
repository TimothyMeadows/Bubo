using System.Text.Json.Nodes;
using Hephaera.ModelAction.Domain;
using static Hephaera.ModelAction.Application.ActionValidation;
namespace Hephaera.ModelAction.Application;

public sealed partial class Pipeline
{
    void Validate(CancellationToken cancellation = default)
    {
        foreach (var (name, stage) in m.Stages)
        {
            if (stage.Status != "complete") throw new InvalidOperationException("Incomplete/interrupted action stage: " + name);
            foreach (var previous in Order.Take(System.Array.IndexOf(Order, name))) Require(previous);
        }
        if (m.Stages.TryGetValue("export", out var export)) ValidatePackage(LoadObject(export.Artifacts["package"]), export, cancellation);
        if (m.Stages.TryGetValue("review", out var review)) ValidateReview(LoadObject(review.Artifacts["reviewReport"]), review, Plan(cancellation));
        foreach (var approval in m.Approvals)
        {
            if (approval.Fingerprint != Fingerprint() || HashFile(approval.Evidence) != approval.EvidenceHash) throw new InvalidOperationException("Stale action approval");
            if (approval.Kind == "human" || m.SchemaVersion == "hephaera-model-action/v2" && approval.Kind == "independent") ValidateRuntime(LoadObject(approval.Evidence), cancellation);
        }
    }
    void Record(CancellationToken cancellation = default)
    {
        var kind = Need("kind"); var reviewer = Need("reviewer");
        if (kind is not ("action" or "independent" or "human") || string.IsNullOrWhiteSpace(reviewer)) throw new InvalidOperationException("Select action, independent or human review with a reviewer");
        var evidence = Relative(w, Need("evidence")); RequireEvidence(evidence);
        var receipt = m.SchemaVersion == "hephaera-model-action/v2"
            ? ActionEffectDependencies.ParseRuntimeData<JsonObject>(ActionEffectDependencies.Read(w, evidence, ActionEffectDependencies.MaximumJsonBytes, cancellation))
            : LoadObject(evidence);
        Require("review"); Validate(cancellation);
        if (Text(receipt, "kind") != kind || Text(receipt, "reviewer") != reviewer || Text(receipt, "decision") != "approved" || Text(receipt, "reason").Length < 8 || Text(receipt, "fingerprint") != Fingerprint()) throw new InvalidOperationException("Review receipt must bind current kind/reviewer/fingerprint/reason");
        if (kind is "action" or "human" && receipt["human"]?.GetValue<bool>() != true) throw new InvalidOperationException("Explicit human action/runtime assertion required");
        if (kind == "independent")
        {
            RequireApproval("action");
            if (reviewer.Equals(m.Actor, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Independent reviewer must differ from creator");
            if (m.SchemaVersion == "hephaera-model-action/v2") ValidateRuntime(receipt, cancellation);
        }
        if (kind == "human") { RequireApproval("action"); RequireApproval("independent"); ValidateRuntime(receipt, cancellation); }
        m.Approvals.RemoveAll(approval => approval.Kind == kind);
        m.Approvals.Add(new Approval(kind, reviewer, evidence, HashFile(evidence), Fingerprint(), DateTimeOffset.UtcNow));
        m.History.Add(new JsonObject { ["event"] = "approval", ["kind"] = kind, ["reviewer"] = reviewer, ["evidence"] = evidence, ["timestamp"] = DateTimeOffset.UtcNow.ToString("O") }); Save();
    }
    void ValidateRuntime(JsonObject receipt, CancellationToken cancellation = default)
    {
        if (m.SchemaVersion == "hephaera-model-action/v2")
        {
            var package = LoadObject(Artifact("export", "package"));
            ActionEffectDependencies.ValidateRuntimeClosure(w, package, cancellation: cancellation);
            RuntimeEffectEvidence.Validate(w, receipt, package, m.Engine, HashFile(Artifact("export", "glb")), HashFile(Artifact("export", "package")),
                m.RuntimeBuildSha256 ?? throw new InvalidOperationException("V2 runtime evidence requires current native build identity"), TaskPrefix, cancellation);
            return;
        }
        var runtime = receipt["runtime"]?.AsObject() ?? throw new InvalidOperationException("Human runtime review requires actual selected-engine evidence");
        if (Text(runtime, "status") != "passed" || Text(runtime, "engine") != Text(m.Engine, "effectiveEngine") || Text(runtime, "modelSha256") != HashFile(Artifact("export", "glb")) || Text(runtime, "packageSha256") != HashFile(Artifact("export", "package"))) throw new InvalidOperationException("Runtime review conflicts with current engine/model/action package");
        var records = Array(runtime, "evidence").Select(node => node!.AsObject()).ToArray();
        foreach (var kind in new[] { "runtime-test", "runtime-capture", "performance-profile" })
            if (records.Count(item => Text(item, "kind") == kind) != 1) throw new InvalidOperationException("Runtime review requires exactly one " + kind);
        foreach (var record in records)
        {
            var file = Relative(w, Text(record, "path")); RequireEvidence(file); var expected = Text(record, "sha256"); Hash(expected);
            if (!File.Exists(w.Resolve(file)) || new FileInfo(w.Resolve(file)).Length == 0 || HashFile(file) != expected) throw new InvalidOperationException("Missing/stale runtime evidence");
        }
    }
    void Promote(CancellationToken cancellation = default)
    {
        Validate(cancellation); foreach (var stage in Order) Require(stage);
        foreach (var kind in new[] { "action", "independent", "human" }) RequireApproval(kind);
        var accepted = m.Approvals.Last(approval => approval.Kind == "human"); ValidateRuntime(LoadObject(accepted.Evidence), cancellation);
        var version2 = m.SchemaVersion == "hephaera-model-action/v2";
        if (version2) ValidatePromotedEffectSources(LoadObject(Artifact("export", "package")), EffectCatalog(cancellation));
        var destination = Relative(w, Need("destination"));
        if (!destination.StartsWith("assets/models/", StringComparison.Ordinal) || Directory.Exists(w.Resolve(destination)) || File.Exists(w.Resolve(destination))) throw new InvalidOperationException("Promotion requires a new assets/models/... destination");
        var temporary = destination + ".pending-" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(w.ResolveWrite(temporary));
        var package = LoadObject(Artifact("export", "package")); var originalHash = HashFile(Artifact("export", "package"));
        var originalClosure = version2 ? Text(package, "effectClosureSha256") : null;
        var originalModelReference = package["model"]!.DeepClone();
        var copies = new List<JsonObject>();
        var promotedFiles = new List<string>();
        string? pendingPackageHash = null, pendingPromotionHash = null;
        m.History.Add(new JsonObject { ["event"] = "promotion-start", ["temporary"] = temporary, ["destination"] = destination, ["sourcePackageSha256"] = originalHash }); Save();
        try
        {
            foreach (var (field, source, filename) in new[] { ("model", Artifact("export", "glb"), "model.glb"), ("source", Artifact("export", "blend"), "source.blend"), ("motion", m.Motion, "motion-source.json") })
            {
                var expected = HashFile(source);
                if (version2)
                {
                    var length = new FileInfo(w.Resolve(source)).Length;
                    if (length < 1 || length > ActionEffectDependencies.MaximumFileBytes) throw new InvalidOperationException("V2 promoted source exceeds finite copy bound");
                    ActionEffectDependencies.Copy(w, source, temporary + "/" + filename, length, expected, cancellation);
                    copies.Add(new JsonObject { ["role"] = field, ["source"] = source, ["destination"] = destination + "/" + filename, ["sha256"] = expected, ["bytes"] = length });
                    promotedFiles.Add(temporary + "/" + filename);
                }
                else File.Copy(w.Resolve(source), w.ResolveWrite(temporary + "/" + filename), false);
                if (HashFile(temporary + "/" + filename) != expected) throw new InvalidOperationException("Promoted action file changed during copy");
                package[field] = new JsonObject { ["path"] = destination + "/" + filename, ["sha256"] = expected };
            }
            if (version2)
            {
                // CopyAndRebind first revalidates the original model/effect union,
                // then binds the already copied pending model and complete effect closure.
                package["model"] = originalModelReference;
                var runtimeFiles = ActionEffectDependencies.RuntimeFiles(Array(package, "effectSources"), w, cancellation);
                var moved = ActionEffectDependencies.CopyAndRebind(w, package, temporary, destination, cancellation);
                foreach (var file in moved)
                {
                    var suffix = file.Path[temporary.Length..];
                    var original = runtimeFiles.Single(item => item.SourceId == file.SourceId && item.Role == file.Role && item.ResourceId == file.ResourceId);
                    copies.Add(new JsonObject { ["role"] = file.Role, ["sourceId"] = file.SourceId, ["resourceId"] = file.ResourceId, ["source"] = original.Path, ["destination"] = destination + suffix, ["sha256"] = file.Sha256, ["bytes"] = file.Bytes });
                    promotedFiles.Add(file.Path);
                }
                if (Text(package, "effectClosureSha256") != originalClosure) throw new InvalidOperationException("Runtime effect identity changed during promotion rebinding");
            }
            package["verification"] = new JsonObject { ["status"] = "accepted", ["evidence"] = accepted.Evidence, ["evidenceSha256"] = accepted.EvidenceHash, ["reviewer"] = accepted.Reviewer };
            w.Save(temporary + "/action-package.json", package);
            if (version2)
            {
                w.Save(temporary + "/promotion.json", new { schemaVersion = 2, sourceManifest = path, sourcePackage = Artifact("export", "package"), sourcePackageSha256 = originalHash, promotedPackageSha256 = HashFile(temporary + "/action-package.json"), sourceEffectClosureSha256 = originalClosure, promotedEffectClosureSha256 = Text(package, "effectClosureSha256"), files = copies, approvals = m.Approvals, note = "Complete runtime effect closure rebound; private source media/preparation/rights/review payloads remain local non-runtime ancestry. Supplied review assertions are not authenticated by Bubo." });
                promotedFiles.Add(temporary + "/action-package.json"); promotedFiles.Add(temporary + "/promotion.json");
                pendingPackageHash = HashFile(temporary + "/action-package.json"); pendingPromotionHash = HashFile(temporary + "/promotion.json");
                ActionEffectDependencies.ValidateRuntimeClosure(w, package, temporary, cancellation);
                ActionEffectDependencies.VerifyInventory(w, temporary, promotedFiles, cancellation);
            }
            else w.Save(temporary + "/promotion.json", new { schemaVersion = 1, sourceManifest = path, sourcePackage = Artifact("export", "package"), sourcePackageSha256 = originalHash, promotedPackageSha256 = HashFile(temporary + "/action-package.json"), approvals = m.Approvals, note = "Model/source/motion paths explicitly rebound; source ancestry and human runtime evidence retained" });
            VerifyHashes(m.Originals); VerifyHashes(m.SourceInputs); VerifyHashes(m.BaselineInputs, true);
            foreach (var stage in m.Stages.Values) { VerifyHashes(stage.Inputs); VerifyHashes(stage.Outputs); VerifyHashes(stage.ExternalInputs, true); }
            if (version2)
            {
                ValidatePromotedEffectSources(LoadObject(Artifact("export", "package")), EffectCatalog(cancellation));
                foreach (var approval in m.Approvals)
                    if (HashFile(approval.Evidence) != approval.EvidenceHash || approval.Fingerprint != Fingerprint()) throw new InvalidOperationException("Current approval changed during V2 promotion");
                ValidateRuntime(LoadObject(accepted.Evidence), cancellation);
                foreach (var copy in copies)
                {
                    var target = temporary + Text(copy, "destination")[destination.Length..];
                    var observed = ActionEffectDependencies.Observe(w, target, copy["bytes"]!.GetValue<long>(), cancellation);
                    if (observed.Sha256 != Text(copy, "sha256") || observed.Bytes != copy["bytes"]!.GetValue<long>()) throw new InvalidOperationException("Pending V2 promotion file changed before commit");
                }
                if (HashFile(temporary + "/action-package.json") != pendingPackageHash || HashFile(temporary + "/promotion.json") != pendingPromotionHash)
                    throw new InvalidOperationException("Pending V2 package/promotion ledger changed before commit");
                ActionEffectDependencies.VerifyInventory(w, temporary, promotedFiles, cancellation);
                VerifyHashes(m.Originals); VerifyHashes(m.SourceInputs); VerifyHashes(m.BaselineInputs, true);
                foreach (var stage in m.Stages.Values) { VerifyHashes(stage.Inputs); VerifyHashes(stage.Outputs); VerifyHashes(stage.ExternalInputs, true); }
            }
            cancellation.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(w.ResolveWrite(destination))!); Directory.Move(w.ResolveWrite(temporary), w.ResolveWrite(destination));
            m.History.Add(new JsonObject { ["event"] = "promoted", ["destination"] = destination, ["sourcePackageSha256"] = originalHash, ["promotedPackageSha256"] = HashFile(destination + "/action-package.json"), ["timestamp"] = DateTimeOffset.UtcNow.ToString("O") }); Save();
        }
        catch (Exception error) { m.History.Add(new JsonObject { ["event"] = "promotion-failed", ["destination"] = destination, ["temporary"] = temporary, ["error"] = error.Message }); Save(); throw; }
    }

    static void ValidatePromotedEffectSources(JsonObject package, ActionEffectCatalog catalog)
    {
        var sources = ActionEffectDependencies.RuntimeSources(Array(package, "effectSources"));
        if (sources.Count != catalog.Sources.Count) throw new InvalidOperationException("Exported effect source closure differs from current original inputs");
        foreach (var source in sources)
        {
            var original = catalog.Sources.SingleOrDefault(item => item.Source.Id == source.Id) ?? throw new InvalidOperationException("Export contains an unknown original effect source");
            if (source.EffectId != original.Source.EffectId || source.Package.Sha256 != original.Source.Package.Sha256 || source.Profile.Sha256 != original.Source.Profile.Sha256 || source.Ancestry.SourceClosureSha256 != original.SourceInventory.SourceClosureSha256 || source.Ancestry.ProductionReviewSha256 != original.Source.ProductionReview.Sha256 || source.Ancestry.RightsSha256 != original.Source.Rights.Sha256 || source.Ancestry.NativePreparationSha256 != original.Source.Preparation?.Sha256)
                throw new InvalidOperationException("Current source/package/profile/preparation/review ancestry differs from exported runtime effects");
        }
    }
}
