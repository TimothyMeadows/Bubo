using System.Text.Json;
using System.Text.Json.Nodes;
using Hephaera.ProofToModel.Domain;

namespace Hephaera.ProofToModel.Application;

public sealed partial class Pipeline
{
    int canvasMattingChildren;
    byte[] CanvasBytes(string path)
    {
        RequireTaskEvidence(path);
        var full = w.Resolve(path);
        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || !File.Exists(full) || new FileInfo(full).Length is < 2 or > CanvasMattingProfile.MaximumBytes)
            throw new InvalidOperationException("Canvas selection requires bounded current task-local JSON");
        var bytes = File.ReadAllBytes(full);
        if (bytes.Length > CanvasMattingProfile.MaximumBytes) throw new InvalidOperationException("Canvas metadata grew during read");
        return bytes;
    }
    CanvasMattingProfile? CanvasProfile() => m.PreprocessingProfileFile is null ? null : CanvasMattingProfile.Parse(CanvasBytes(m.PreprocessingProfileFile));
    CanvasStageLineage? CanvasLineage() => m.StageLineageFile is null ? null : CanvasStageLineage.Parse(CanvasBytes(m.StageLineageFile));
    void CanvasReference(CanvasFile file, bool tool = false)
    {
        var full = w.Resolve(file.Path);
        var length = File.Exists(full) ? new FileInfo(full).Length : 0;
        if (file.Bytes is null && (length is < 2 or > 1048576)) throw new InvalidOperationException("Canvas JSON reference exceeds1MiB before hashing");
        if (!File.Exists(full) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0 || file.Bytes is { } expected && new FileInfo(full).Length != expected || w.HashFile(full) != file.Sha256)
            throw new InvalidOperationException("Canvas tool/input bytes changed");
        if (tool) Host.EnsureToolAllowed(full);
        CanvasFileIdentity.NoHardLinks(full);
    }
    void RequireCanvasReadOnlyRoot(string root)
    {
        CanvasFileIdentity.RequireReadOnlyRoot(Host, root);
    }
    void ValidateCanvasRecovery()
    {
        if (m.PreprocessingProfileFile is null && m.StageLineageFile is null) return;
        if (!IsFourView || m.PreprocessingProfileFile is null || m.StageLineageFile is null)
            throw new InvalidOperationException("Canvas recovery is paired and only available to explicit four-view state");
        var profile = CanvasProfile()!; var lineage = CanvasLineage()!;
        lineage.ValidateScope(m.Task, m.Asset, m.Version);
        if (profile.BaseToolchain.Path != m.Toolchain || profile.BaseToolchain.Sha256 != Hash(m.Toolchain))
            throw new InvalidOperationException("Canvas profile must preserve the exact accepted base toolchain");
        CanvasReference(profile.BaseToolchain);
        if (string.Equals(w.Resolve(profile.Executable.Path), Executable("engine"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Matting executable must be separate; reconstruction engine cannot be replaced");
        foreach (var file in profile.Files) CanvasReference(file, file.Path == profile.Executable.Path);
        var bundleRelative = Path.GetDirectoryName(profile.Executable.Path)!.Replace('\\', '/');
        RequireCanvasReadOnlyRoot(bundleRelative);
        var bundle = w.Resolve(bundleRelative);
        var expectedFiles = profile.Files.Select(file => Path.GetFullPath(w.Resolve(file.Path))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(); pending.Push(bundle); var entries = 0; var observedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.TryPop(out var directory)) foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if (++entries > 64 || (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Sealed matting bundle contains unbounded/linked entries");
            if (Directory.Exists(entry)) pending.Push(entry); else if (!expectedFiles.Contains(Path.GetFullPath(entry)) || !observedFiles.Add(Path.GetFullPath(entry))) throw new InvalidOperationException("Unregistered file/shadow in sealed matting bundle");
        }
        if (!expectedFiles.SetEquals(observedFiles)) throw new InvalidOperationException("Sealed matting bundle inventory changed");
        foreach (var runtime in profile.ExistingRuntimeDependencies) VerifyCanvasRuntime(runtime);
        Host.RequireReference(lineage.Authorization.Path); Host.RequireReference(lineage.InputApproval.Path);
        CanvasReference(lineage.Authorization); CanvasReference(lineage.InputApproval);
        var expectedState = TaskRoot(m.Task) + "/proof-to-model/" + m.Asset + "/" + lineage.SourceVersion;
        var expectedStaging = StagingRoot + "/proof-to-model/" + m.Asset + "/" + lineage.SourceVersion;
        if (lineage.PriorStateRoot != expectedState || lineage.PriorStaging != expectedStaging || lineage.PriorStateRoot == State || lineage.PriorStaging == m.Staging)
            throw new InvalidOperationException("Lineage roots must name one exact prior version of this same task/class");
        RequireCanvasReadOnlyRoot(expectedState); RequireCanvasReadOnlyRoot(expectedStaging);
        var priorPath = expectedState + "/manifest.json";
        Manifest? prior = null;
        if (lineage.PriorManifest is { } reference)
        {
            if (reference.Path != priorPath) throw new InvalidOperationException("Exact prior manifest required");
            Host.RequireReference(priorPath); CanvasReference(reference);
            prior = w.Load<Manifest>(priorPath);
            if (prior.SchemaVersion != 3 || prior.ConditioningProfile != m.ConditioningProfile || prior.Task != m.Task || prior.Asset != m.Asset || prior.Version != lineage.SourceVersion || prior.Staging != expectedStaging || lineage.InheritedLineage is null && (prior.PreprocessingProfileFile is not null || prior.StageLineageFile is not null) || Hash(prior.Proof) != Hash(m.Proof) ||
                Hash(prior.Toolchain) != Hash(m.Toolchain) || Hash(prior.Rights) != Hash(m.Rights) || Hash(prior.Recipe) != Hash(m.Recipe))
                throw new InvalidOperationException("Only explicit direct lineage from unchanged four-view originals/toolchain/recipe is supported; no chained budget reset");
        }
        else if (File.Exists(w.Resolve(priorPath))) throw new InvalidOperationException("An existing prior manifest cannot be omitted");
        var inherited = ValidateCanvasInheritedLineage(lineage, prior);
        foreach (var stage in CanvasStageLineage.Stages)
        {
            var observed = CanvasAttemptInventory.Scan(w, expectedStaging, prior?.History ?? [], stage);
            var persisted = prior?.History.Count(item => item["event"]?.GetValue<string>() == "stage-start" && item["stage"]?.GetValue<string>() == stage) ?? 0;
            lineage.ValidatePrior(stage, observed, CanvasAttemptInventory.Directories(w, expectedStaging, stage), persisted);
            var configured = stage is "classify" or "prepare-proofs" ? w.Load<JsonObject>(m.Recipe)["proofPreparation"]!["maxSetRevisions"]!.GetValue<int>() : Toolchain["limits"]!["maxAttempts"]!.GetValue<int>();
            var maximum = lineage.Limit(stage, configured);
            if (observed.Length + (inherited?.PriorAttempts[stage].Length ?? 0) + CurrentCanvasAttempts(stage).Length > maximum) throw new InvalidOperationException("Cumulative old/current stage allowance exceeded");
        }
        // No attempt/approval/toolchain data is copied or rewritten here. Fresh
        // native build and ordinary proof/preprocessing acceptance stay required.
    }
    string[] CurrentCanvasAttempts(string stage) => CanvasAttemptInventory.Scan(w, m.Staging, m.History, stage);
    void VerifyCanvasRuntime(CanvasRuntimeFile runtime)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Existing CUDA13.2 runtime dependencies require their qualified Windows host");
        var file = Path.GetFullPath(runtime.AbsolutePath);
        if (!File.Exists(file) || new FileInfo(file).Length != runtime.Bytes || Host.HashToolFile(file) != runtime.Sha256) throw new InvalidOperationException("Existing CUDA runtime changed");
        CanvasFileIdentity.NoHardLinks(file);
    }
    string CanvasStatus()
    {
        var lineage = CanvasLineage()!;
        return JsonSerializer.Serialize(new
        {
            manifest = m, manifestSha256 = Hash(manifestPath),
            finishBudget = new { consumedFinishAttempts = ConsumedAttempts("finish") },
            approvalFingerprints = new { proof = ApprovalFingerprint("proof"), proofSet = ApprovalFingerprint("proof-set"), preprocessing = ApprovalFingerprint("preprocessing"), rigging = ApprovalFingerprint("rigging"), animation = ApprovalFingerprint("animation"), independent = ApprovalFingerprint("independent"), human = ApprovalFingerprint("human") },
            stageLineage = new { path = m.StageLineageFile, sha256 = Hash(m.StageLineageFile!), consumed = CanvasStageLineage.Stages.ToDictionary(stage => stage, ConsumedAttempts), limits = lineage.StageLimits, maximumMattingChildren = CanvasMattingProfile.MaximumChildren }
        }, Serialization.Json);
    }
}

/// <summary>Counts real empty starts and retained history without touching outputs.</summary>
public static class CanvasAttemptInventory
{
    public static string[] Directories(IWorkspace workspace, string staging, string stage)
    {
        if (!CanvasStageLineage.Stages.Contains(stage, StringComparer.Ordinal)) throw new InvalidOperationException("Unknown canvas stage");
        var directory = staging + "/attempts"; var full = workspace.Resolve(directory);
        var entries = new HashSet<string>(StringComparer.Ordinal);
        if (Directory.Exists(full))
        {
            if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Reparse attempt root refused");
            // Enumerate only this explicit prior/current asset's attempts root.
            var scanned = 0;
            foreach (var path in Directory.EnumerateDirectories(full, "*", SearchOption.TopDirectoryOnly))
            {
                if (++scanned > 100 || !CanvasStageLineage.Stages.Any(known => Path.GetFileName(path).StartsWith(known + "-", StringComparison.Ordinal))) throw new InvalidOperationException("Unknown/unbounded entry in exact attempt root");
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Reparse attempt directory refused");
                if (!Path.GetFileName(path).StartsWith(stage + "-", StringComparison.Ordinal)) continue;
                if (entries.Count >= 100) throw new InvalidOperationException("Bounded attempt inventory exceeded");
                entries.Add(directory + "/" + Path.GetFileName(path));
            }
        }
        return entries.Order(StringComparer.Ordinal).ToArray();
    }
    public static string[] Scan(IWorkspace workspace, string staging, IEnumerable<JsonObject> history, string stage)
    {
        var directory = staging + "/attempts";
        var entries = Directories(workspace, staging, stage).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in history.Where(item => item["event"]?.GetValue<string>() == "stage-start" && item["stage"]?.GetValue<string>() == stage))
        {
            var attempt = entry["attempt"]?.GetValue<string>() ?? throw new InvalidOperationException("Retained stage start lacks attempt identity");
            if (!attempt.StartsWith(directory + "/" + stage + "-", StringComparison.Ordinal) || attempt[(directory.Length + 1)..].Contains('/') || entries.Count >= 100 && !entries.Contains(attempt))
                throw new InvalidOperationException("Retained attempt history escaped exact staging or bounded count");
            entries.Add(attempt);
        }
        return entries.Order(StringComparer.Ordinal).ToArray();
    }
}
