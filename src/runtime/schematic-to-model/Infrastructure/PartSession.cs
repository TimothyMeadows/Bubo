using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bubo.Schematic.Domain;
using Hephaera.AssetProduction;

namespace Bubo.Schematic.Infrastructure;

/// <summary>Owns bounded, immutable individual definition prototypes; it does not assemble or promote them.</summary>
public sealed class PartSession(IWorkspace workspace, DesignSession design, INativeProcess process,
    string bundleDirectory, int timeoutSeconds, long maxLogBytes, long? maxPrivateBytes, string resourceCatalogSha256)
{
    static readonly string[] Scripts = ["parts_author.py", "parts_verify.py", "parts_geometry.py", "parts_admission.py"];
    sealed record PartAttempt
    {
        public required string Directory { get; init; }
        public required string DesignSha256 { get; init; }
        public required string DesignFingerprint { get; init; }
        public required string ApprovalSha256 { get; init; }
        public required string ToolPath { get; init; }
        public required string ToolSha256 { get; init; }
        public required SortedDictionary<string, string> Bundle { get; init; }
        public required DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset? CompletedAt { get; set; }
        public string Status { get; set; } = "running";
        public string? Error { get; set; }
        public string? JobSha256 { get; set; }
        public string? ReportSha256 { get; set; }
        public string? VerificationSha256 { get; set; }
    }
    sealed record PartState
    {
        public string SchemaVersion { get; init; } = "bubo-schematic-parts-state/v1";
        public required string Task { get; init; }
        public required string Asset { get; init; }
        public required string Version { get; init; }
        public required string DesignManifest { get; init; }
        public required string RuntimeSha256 { get; init; }
        public List<PartAttempt> Attempts { get; init; } = [];
    }
    static bool Hash(string? value) => value is not null && Regex.IsMatch(value, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant);
    static string Digest<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, StrictDesignJson.Options)));
    string StateFile(string manifest) => workspace.Relative(manifest)[..^"manifest.json".Length] + "parts.json";
    string LockPath(string manifest)
    {
        var version = Path.GetDirectoryName(workspace.Resolve(manifest))!;
        return workspace.Relative(Path.Combine(Directory.GetParent(version)!.FullName, "asset.lock"));
    }
    SortedDictionary<string, string> Bundle()
    {
        var catalog = Path.Combine(Directory.GetParent(bundleDirectory)?.Parent?.FullName ?? throw new InvalidOperationException("Invalid schematic bundle layout"), "catalog.json");
        var catalogBytes = SchematicNativeRunner.Capture(catalog, 1_048_576);
        if (!Hash(resourceCatalogSha256) || Convert.ToHexStringLower(SHA256.HashData(catalogBytes)) != resourceCatalogSha256)
            throw new InvalidOperationException("Schematic resource catalog differs from its compiled reviewed identity");
        using var document = JsonDocument.Parse(catalogBytes);
        var files = document.RootElement.GetProperty("files"); var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in Scripts)
        {
            var path = Path.Combine(bundleDirectory, name);
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || (File.GetAttributes(bundleDirectory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Fixed schematic part bundle is missing or linked");
            var hash = Convert.ToHexStringLower(SHA256.HashData(SchematicNativeRunner.Capture(path, 2_097_152)));
            if (!files.TryGetProperty("resources/schematic-to-model/blender/" + name, out var expected) || expected.GetString() != hash)
                throw new InvalidOperationException("Schematic script differs from the exact reviewed resource catalog");
            result.Add(name, hash);
        }
        return result;
    }
    void FreshBundle(SortedDictionary<string, string> expected)
    {
        var actual = Bundle();
        if (actual.Count != expected.Count || expected.Any(p => actual.GetValueOrDefault(p.Key) != p.Value)) throw new InvalidOperationException("Audited schematic bundle changed during execution");
    }
    void FreshCopy(PartAttempt attempt)
    {
        foreach (var (name, hash) in attempt.Bundle)
            if (PartOutputVerifier.FileHash(workspace, attempt.Directory + "/bundle/" + name, maximumBytes: 2_097_152) != hash)
                throw new InvalidOperationException("Immutable schematic working bundle changed");
        var files = Directory.EnumerateFileSystemEntries(workspace.Resolve(attempt.Directory + "/bundle")).Take(Scripts.Length + 1).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        if (!files.SetEquals(Scripts)) throw new InvalidOperationException("Immutable schematic bundle inventory changed");
    }
    string ToolHash(string executable)
    {
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable) || (File.GetAttributes(executable) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Explicit absolute existing registered Blender executable is required");
        return workspace.HostContext?.HashToolFile(executable) ?? workspace.HashFile(executable);
    }
    void NativeBudget()
    {
        if (maxPrivateBytes is null) throw new InvalidOperationException("Part construction requires an explicit aggregate committed-memory ceiling");
        if (!process.SupportsPrivateMemoryLimit) throw new PlatformNotSupportedException("Part construction requires supported 64-bit Windows owned-job aggregate committed-memory enforcement; no unbounded launch");
        if (maxPrivateBytes is < 67_108_864 or > 68_719_476_736 || timeoutSeconds is < 1 or > 86400 || maxLogBytes is < 1024 or > 1073741824)
            throw new InvalidOperationException("Invalid explicit schematic native process budget");
    }
    PartState Load(string path, DesignSession.PartInput input)
    {
        if (!File.Exists(workspace.Resolve(StateFile(path)))) return new() { Task = input.Manifest.Task, Asset = input.Manifest.Asset, Version = input.Manifest.Version,
            DesignManifest = workspace.Relative(path), RuntimeSha256 = input.Manifest.RuntimeBuildSha256 };
        var state = StrictDesignJson.Read<PartState>(workspace.Resolve(StateFile(path)));
        if (state.SchemaVersion != "bubo-schematic-parts-state/v1" || state.Task != input.Manifest.Task || state.Asset != input.Manifest.Asset || state.Version != input.Manifest.Version
            || state.DesignManifest != workspace.Relative(path) || state.RuntimeSha256 != input.Manifest.RuntimeBuildSha256 || state.Attempts.Count > 128)
            throw new InvalidOperationException("Part state identity or retained history differs");
        foreach (var attempt in state.Attempts)
        {
            var prefix = input.Manifest.Staging + "/attempts/parts-";
            if (!attempt.Directory.StartsWith(prefix, StringComparison.Ordinal) || !Regex.IsMatch(attempt.Directory[prefix.Length..], "^[a-f0-9]{32}$")
                || attempt.Status is not ("running" or "complete" or "failed") || !Hash(attempt.DesignSha256) || !Hash(attempt.DesignFingerprint) || !Hash(attempt.ApprovalSha256)
                || !Hash(attempt.ToolSha256) || !Path.IsPathFullyQualified(attempt.ToolPath) || attempt.Bundle.Count != Scripts.Length || !attempt.Bundle.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(Scripts)
                || attempt.Bundle.Values.Any(h => !Hash(h))) throw new InvalidOperationException("Invalid retained part attempt identity");
            if (attempt.Status == "complete" && (!Hash(attempt.JobSha256) || !Hash(attempt.ReportSha256) || !Hash(attempt.VerificationSha256) || attempt.CompletedAt is null))
                throw new InvalidOperationException("Successful part attempt lacks complete independent verification identities");
            workspace.Resolve(attempt.Directory);
        }
        return state;
    }
    void Save(string path, PartState state)
    {
        workspace.Save(StateFile(path), state);
        AuditJournal.Append(workspace, StateFile(path)[..^"parts.json".Length] + "parts-audit.jsonl", new
        { schemaVersion = "bubo-schematic-parts-event/v1", state.Asset, state.Version, attempts = state.Attempts.Count, last = state.Attempts.LastOrDefault()?.Status, at = DateTimeOffset.UtcNow });
    }
    static Dictionary<string, string> EnvironmentFor(string path) => new(StringComparer.Ordinal)
    {
        ["TEMP"] = path, ["TMP"] = path, ["TMPDIR"] = path, ["PYTHONDONTWRITEBYTECODE"] = "1", ["PYTHONNOUSERSITE"] = "1", ["XDG_CACHE_HOME"] = Path.Combine(path, "cache"),
        ["BLENDER_USER_CONFIG"] = Path.Combine(path, "config"), ["BLENDER_USER_SCRIPTS"] = Path.Combine(path, "scripts"), ["BLENDER_USER_DATAFILES"] = Path.Combine(path, "data"), ["BLENDER_USER_EXTENSIONS"] = Path.Combine(path, "extensions")
    };
    sealed class ProcessBudget(int seconds, long logBytes)
    {
        readonly Stopwatch elapsed = Stopwatch.StartNew();
        long remainingLog = logBytes;
        public int Seconds()
        {
            var remaining = Math.Ceiling(seconds - elapsed.Elapsed.TotalSeconds);
            if (remaining < 1) throw new TimeoutException("Cumulative part prerequisite/author/independent verification deadline exhausted");
            return checked((int)remaining);
        }
        public long LogBytes()
        {
            if (remainingLog < 1024) throw new InvalidOperationException("Cumulative part process log budget exhausted");
            return remainingLog;
        }
        public void DebitLog(string file) { remainingLog = checked(remainingLog - new FileInfo(file).Length); if (remainingLog < 0) throw new InvalidOperationException("Cumulative part process logs exceeded approved bytes"); }
    }
    async Task<string> Version(string blender, string directory, ProcessBudget budget, CancellationToken cancellation)
    {
        Directory.CreateDirectory(workspace.ResolveWrite(directory + "/temp"));
        var log = workspace.ResolveWrite(directory + "/version.log");
        await process.RunWithLimits(blender, ["--version"], workspace.Root, log, Math.Min(30, budget.Seconds()), maxPrivateBytes!.Value, cancellation,
            Math.Min(16384, budget.LogBytes()), EnvironmentFor(workspace.Resolve(directory + "/temp")));
        budget.DebitLog(log); _ = budget.Seconds();
        var match = Regex.Match(File.ReadAllText(log), "Blender (4\\.5\\.[0-9]+(?: LTS)?)");
        if (!match.Success) throw new InvalidOperationException("Selected executable did not report Blender 4.5 LTS");
        return match.Groups[1].Value;
    }
    public async Task<object> Doctor(string manifest, string? blender, CancellationToken cancellation = default)
    {
        var initial = design.InspectParts(manifest, cancellation);
        using var ownership = workspace.Lock(LockPath(manifest));
        var input = design.InspectParts(manifest, cancellation); var unavailable = new List<string>();
        var readiness = new PartConstructionReadiness().Validate(input.Design, cancellation);
        unavailable.AddRange(readiness.Issues.Select(i => i.Code + ": " + i.Message));
        if (!input.Approved) unavailable.Add("exact complete whole-assembly human design approval required");
        try { NativeBudget(); } catch (Exception error) when (error is InvalidOperationException or PlatformNotSupportedException) { unavailable.Add(error.Message); }
        SortedDictionary<string, string>? bundle = null;
        try { bundle = Bundle(); } catch (Exception error) when (error is InvalidOperationException or IOException or JsonException) { unavailable.Add("fixed audited part bundle unavailable: " + error.Message); }
        string? version = null, tool = null;
        if (blender is null) unavailable.Add("select an existing registered Blender 4.5 LTS executable");
        else
        {
            try
            {
                tool = ToolHash(blender);
                if (maxPrivateBytes is not null && process.SupportsPrivateMemoryLimit)
                {
                    NativeBudget(); var budget = new ProcessBudget(timeoutSeconds, maxLogBytes);
                    version = await Version(blender, input.Manifest.Staging + "/attempts/parts-doctor-" + Guid.NewGuid().ToString("N"), budget, cancellation);
                    if (ToolHash(blender) != tool) throw new InvalidOperationException("Selected Blender changed during part prerequisite inspection");
                }
            }
            catch (Exception error) when (error is InvalidOperationException or IOException or PlatformNotSupportedException or TimeoutException) { unavailable.Add(error.Message); }
        }
        workspace.HostContext?.ValidateFresh(); var current = design.InspectParts(manifest, cancellation);
        if (current.Manifest.DesignSha256 != input.Manifest.DesignSha256 || current.Package?.Fingerprint != input.Package?.Fingerprint
            || current.Approved != input.Approved || Digest(current.Manifest.Reviews) != Digest(input.Manifest.Reviews))
            throw new InvalidOperationException("Schematic input or approval changed during prerequisite inspection");
        if (bundle is not null) FreshBundle(bundle);
        cancellation.ThrowIfCancellationRequested();
        return new { schemaVersion = "bubo-schematic-parts-doctor/v1", phase = "individual-part-construction", vectorPreviewAvailable = true,
            partConstructionAvailable = readiness.IsReady && current.Approved, nativeReadiness = version is not null && unavailable.Count == 0 ? "verified" : "unverified",
            blenderVersion = version, toolSha256 = tool, unavailable, provisioningRequired = false, assemblyAvailable = false, modelExportAvailable = false,
            humanModelAccepted = false, runtimeAcceptance = false };
    }
    PartBuildJob Job(DesignSession.PartInput input, PartAttempt attempt) => new()
    {
        SchemaVersion = "bubo-schematic-part-job/v1", Operation = "build-parts", ProjectRoot = workspace.Root, Output = workspace.ResolveWrite(attempt.Directory + "/output"),
        Design = input.Design, PartIdentities = PartOutputVerifier.PartIdentities(input.Design), Identity = new(input.Manifest.DesignSha256, input.Package!.Fingerprint, input.Manifest.RuntimeBuildSha256, attempt.ToolSha256),
        Bundle = attempt.Bundle, Limits = new(PartConstructionReadiness.MaximumVertices, Math.Min(PartConstructionReadiness.MaximumTriangles, input.Design.Budgets.MaxGeneratedTriangles),
            input.Design.Budgets.MaxExportBytes, PartConstructionReadiness.MaximumJobBytes, checked(input.Design.Parts.Count * 2 + 2))
    };
    void WriteNew(string path, byte[] bytes)
    {
        var file = workspace.ResolveWrite(path); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None); output.Write(bytes); output.Flush(true);
    }
    void CopyBundle(PartAttempt attempt)
    {
        foreach (var (name, hash) in attempt.Bundle)
        {
            var path = Path.Combine(bundleDirectory, name);
            if (new FileInfo(path).Length is <= 0 or > 2_097_152) throw new InvalidOperationException("Part script exceeds bounded immutable-copy bytes");
            var bytes = SchematicNativeRunner.Capture(path, 2_097_152);
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != hash) throw new InvalidOperationException("Part script changed during immutable capture");
            WriteNew(attempt.Directory + "/bundle/" + name, bytes);
        }
        FreshBundle(attempt.Bundle); FreshCopy(attempt);
    }
    public async Task<object> BuildParts(string manifest, string blender, CancellationToken cancellation = default)
    {
        _ = design.InspectParts(manifest, cancellation, requireApproved: true);
        using var ownership = workspace.Lock(LockPath(manifest));
        var input = design.InspectParts(manifest, cancellation, requireApproved: true);
        new PartConstructionReadiness().RequireReady(input.Design, cancellation); NativeBudget();
        var tool = ToolHash(blender); var bundle = Bundle(); var state = Load(manifest, input);
        if (state.Attempts.Count >= 128) throw new InvalidOperationException("Part attempt history is full; preserve it");
        var attempt = new PartAttempt { Directory = input.Manifest.Staging + "/attempts/parts-" + Guid.NewGuid().ToString("N"), DesignSha256 = input.Manifest.DesignSha256,
            DesignFingerprint = input.Package!.Fingerprint, ApprovalSha256 = input.Manifest.Reviews[^1].Sha256, ToolPath = Path.GetFullPath(blender), ToolSha256 = tool, Bundle = bundle, StartedAt = DateTimeOffset.UtcNow };
        state.Attempts.Add(attempt); Save(manifest, state);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds)); cancellation = deadline.Token;
        var budget = new ProcessBudget(timeoutSeconds, maxLogBytes);
        try
        {
            CopyBundle(attempt); var job = Job(input, attempt); var jobPath = attempt.Directory + "/job.json";
            Directory.CreateDirectory(workspace.ResolveWrite(attempt.Directory + "/output"));
            var bytes = JsonSerializer.SerializeToUtf8Bytes(job, StrictDesignJson.Options);
            if (bytes.Length > job.Limits.MaxJobBytes) throw new InvalidOperationException("Part job exceeds its finite 8MiB limit");
            WriteNew(jobPath, bytes); attempt.JobSha256 = PartOutputVerifier.FileHash(workspace, jobPath, cancellation, job.Limits.MaxJobBytes); Save(manifest, state);
            Refresh(manifest, attempt, cancellation);
            _ = await Version(blender, attempt.Directory, budget, cancellation);
            Refresh(manifest, attempt, cancellation);
            foreach (var (script, logName) in new[] { ("parts_author.py", "author.log"), ("parts_verify.py", "verification.log") })
            {
                var log = workspace.ResolveWrite(attempt.Directory + "/" + logName);
                await process.RunWithLimits(blender, ["--background", "--factory-startup", "--disable-autoexec", "--offline-mode", "--python-exit-code", "1", "--python",
                    workspace.Resolve(attempt.Directory + "/bundle/" + script), "--", workspace.Resolve(jobPath), attempt.JobSha256], workspace.Root, log,
                    budget.Seconds(), maxPrivateBytes!.Value, cancellation, budget.LogBytes(), EnvironmentFor(workspace.Resolve(attempt.Directory + "/temp")));
                budget.DebitLog(log); _ = budget.Seconds(); Refresh(manifest, attempt, cancellation);
                if (PartOutputVerifier.FileHash(workspace, jobPath, cancellation, job.Limits.MaxJobBytes) != attempt.JobSha256) throw new InvalidOperationException("Immutable part job changed during native execution");
            }
            var output = attempt.Directory + "/output";
            var reportHash = PartOutputVerifier.FileHash(workspace, output + "/parts-report.json", cancellation, StrictDesignJson.MaximumInputBytes);
            var verifiedHash = PartOutputVerifier.FileHash(workspace, output + "/parts-verification.json", cancellation, StrictDesignJson.MaximumInputBytes);
            _ = PartOutputVerifier.Verify(workspace, output, job, attempt.JobSha256, cancellation);
            Refresh(manifest, attempt, cancellation); _ = budget.Seconds();
            if (PartOutputVerifier.FileHash(workspace, output + "/parts-report.json", cancellation, StrictDesignJson.MaximumInputBytes) != reportHash
                || PartOutputVerifier.FileHash(workspace, output + "/parts-verification.json", cancellation, StrictDesignJson.MaximumInputBytes) != verifiedHash
                || PartOutputVerifier.FileHash(workspace, jobPath, cancellation, job.Limits.MaxJobBytes) != attempt.JobSha256)
                throw new InvalidOperationException("Part output or immutable job changed during native verification");
            attempt.ReportSha256 = reportHash; attempt.VerificationSha256 = verifiedHash; attempt.CompletedAt = DateTimeOffset.UtcNow; attempt.Status = "complete"; Save(manifest, state);
            cancellation.ThrowIfCancellationRequested(); return Describe(state, input, true, cancellation);
        }
        catch (Exception error)
        {
            attempt.Status = "failed"; attempt.Error = error.Message; attempt.CompletedAt = DateTimeOffset.UtcNow; Save(manifest, state); throw;
        }
    }
    void Refresh(string manifest, PartAttempt attempt, CancellationToken cancellation)
    {
        workspace.HostContext?.ValidateFresh(); var input = design.InspectParts(manifest, cancellation, requireApproved: true);
        if (input.Manifest.DesignSha256 != attempt.DesignSha256 || input.Package!.Fingerprint != attempt.DesignFingerprint
            || input.Manifest.Reviews[^1].Sha256 != attempt.ApprovalSha256 || ToolHash(attempt.ToolPath) != attempt.ToolSha256)
            throw new InvalidOperationException("Schematic input, approval or selected tool changed during part construction");
        FreshBundle(attempt.Bundle); FreshCopy(attempt);
    }
    /// <summary>Internal composition under the caller's existing shared asset lock, never a second lock.</summary>
    internal sealed record VerifiedPartInput(DesignSession.PartInput Input, string Directory, string ToolPath, string ToolSha256,
        string JobSha256, string ReportSha256, string VerificationSha256, PartBuildJob Job, PartsReport Report);
    internal VerifiedPartInput? TryInspectOwned(string manifest, CancellationToken cancellation = default)
    {
        var input = design.InspectParts(manifest, cancellation);
        var state = Load(manifest, input);
        if (state.Attempts.LastOrDefault() is not { Status: "complete" }) return null;
        return InspectOwned(manifest, cancellation);
    }
    internal VerifiedPartInput InspectOwned(string manifest, CancellationToken cancellation = default)
    {
        var input = design.InspectParts(manifest, cancellation, requireApproved: true);
        var state = Load(manifest, input);
        var attempt = state.Attempts.LastOrDefault();
        if (attempt is not { Status: "complete" }) throw new InvalidOperationException("Assembly requires the latest completed independently verified part build; failed or interrupted attempts remain authoritative");
        Refresh(manifest, attempt, cancellation);
        var expected = Job(input, attempt); var path = attempt.Directory + "/job.json";
        if (PartOutputVerifier.FileHash(workspace, path, cancellation, PartConstructionReadiness.MaximumJobBytes) != attempt.JobSha256
            || PartOutputVerifier.FileHash(workspace, attempt.Directory + "/output/parts-report.json", cancellation, StrictDesignJson.MaximumInputBytes) != attempt.ReportSha256
            || PartOutputVerifier.FileHash(workspace, attempt.Directory + "/output/parts-verification.json", cancellation, StrictDesignJson.MaximumInputBytes) != attempt.VerificationSha256)
            throw new InvalidOperationException("Retained part job or independent report identity changed");
        var job = StrictDesignJson.Read<PartBuildJob>(workspace.Resolve(path));
        if (Digest(job) != Digest(expected)) throw new InvalidOperationException("Retained part job differs from current approved inputs/budgets");
        var report = PartOutputVerifier.Verify(workspace, attempt.Directory + "/output", job, attempt.JobSha256!, cancellation);
        Refresh(manifest, attempt, cancellation);
        return new(input, attempt.Directory + "/output", attempt.ToolPath, attempt.ToolSha256, attempt.JobSha256!, attempt.ReportSha256!, attempt.VerificationSha256!, job, report);
    }
    public object Status(string manifest, CancellationToken cancellation = default)
    {
        _ = design.InspectParts(manifest, cancellation);
        using var ownership = workspace.Lock(LockPath(manifest));
        var input = design.InspectParts(manifest, cancellation); var state = Load(manifest, input); var current = false;
        if (state.Attempts.LastOrDefault() is { Status: "complete" } attempt)
        {
            Refresh(manifest, attempt, cancellation); var expected = Job(input, attempt); var path = attempt.Directory + "/job.json";
            if (PartOutputVerifier.FileHash(workspace, path, cancellation, PartConstructionReadiness.MaximumJobBytes) != attempt.JobSha256
                || PartOutputVerifier.FileHash(workspace, attempt.Directory + "/output/parts-report.json", cancellation, StrictDesignJson.MaximumInputBytes) != attempt.ReportSha256
                || PartOutputVerifier.FileHash(workspace, attempt.Directory + "/output/parts-verification.json", cancellation, StrictDesignJson.MaximumInputBytes) != attempt.VerificationSha256)
                throw new InvalidOperationException("Retained part job or independent report identity changed");
            var job = StrictDesignJson.Read<PartBuildJob>(workspace.Resolve(path));
            if (Digest(job) != Digest(expected)) throw new InvalidOperationException("Retained part job differs from current approved inputs/budgets");
            _ = PartOutputVerifier.Verify(workspace, attempt.Directory + "/output", job, attempt.JobSha256!, cancellation);
            Refresh(manifest, attempt, cancellation); current = true;
        }
        cancellation.ThrowIfCancellationRequested(); return Describe(state, input, current, cancellation);
    }
    static object Describe(PartState state, DesignSession.PartInput input, bool current, CancellationToken cancellation)
    {
        var last = state.Attempts.LastOrDefault();
        return new { schemaVersion = "bubo-schematic-parts-status/v1", state.Asset, state.Version, phase = "individual-part-construction", wholeAssemblyApproved = input.Approved,
            partConstructionAvailable = input.Approved && new PartConstructionReadiness().Validate(input.Design, cancellation).IsReady, currentBuildValid = current,
            directory = current ? last!.Directory + "/output" : null, report = current ? last!.Directory + "/output/parts-report.json" : null,
            verification = current ? last!.Directory + "/output/parts-verification.json" : null, definitions = current ? input.Design.Parts.Count : 0,
            physicalOccurrences = input.Design.Instances.Count, designFingerprint = input.Package?.Fingerprint, attempts = state.Attempts.Count, lastStatus = last?.Status, lastError = last?.Error,
            independentReimport = current, assemblyAvailable = false, motionProofAvailable = false, modelExportAvailable = false, humanModelAccepted = false, runtimeAcceptance = false };
    }
}
