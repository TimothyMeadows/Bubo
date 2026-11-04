using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bubo.Schematic.Application;
using Bubo.Schematic.Domain;
using Hephaera.AssetProduction;

namespace Bubo.Schematic.Infrastructure;

/// <summary>Immutable assembly and sampled diagnostic evidence, separate from export and asset acceptance.</summary>
public sealed class AssemblySession(IWorkspace workspace, DesignSession design, PartSession parts, INativeProcess process,
    string bundleDirectory, int timeoutSeconds, long maxLogBytes, long? maxPrivateBytes, string resourceCatalogSha256)
{
    const int MaximumJobBytes = 8_388_608;
    const int MaximumGeometry = 250_000;
    const int MaximumFiles = 16_384;
    static readonly JsonSerializerOptions CompactOptions = new(StrictDesignJson.Options) { WriteIndented = false };
    static readonly string[] Scripts = ["assembly_admission.py", "assembly_author.py", "assembly_geometry.py", "assembly_motion.py", "assembly_verify.py", "parts_admission.py", "parts_geometry.py"];
    readonly SchematicNativeRunner runner = new(workspace, process, bundleDirectory, resourceCatalogSha256, timeoutSeconds, maxLogBytes, maxPrivateBytes);

    sealed record Attempt
    {
        public required string Operation { get; init; }
        public required string Directory { get; init; }
        public required AssemblyBuildIdentity Identity { get; init; }
        public required string ApprovalSha256 { get; init; }
        public required string ToolPath { get; init; }
        public required SortedDictionary<string, string> Bundle { get; init; }
        public required DateTimeOffset StartedAt { get; init; }
        public required string? SourceAssembly { get; init; }
        public required string? SourceAssemblySha256 { get; init; }
        public required string? SourceAssemblyEvidenceSha256 { get; init; }
        public DateTimeOffset? CompletedAt { get; set; }
        public string Status { get; set; } = "running";
        public string? Error { get; set; }
        public string? JobSha256 { get; set; }
        public string? ReportSha256 { get; set; }
        public string? VerificationSha256 { get; set; }
        public string? NativeVerificationSha256 { get; set; }
        public string? NativeFailureSha256 { get; set; }
        public string? SceneSha256 { get; set; }
    }
    sealed record State
    {
        public string SchemaVersion { get; init; } = "bubo-schematic-assembly-state/v1";
        public required string Task { get; init; }
        public required string Asset { get; init; }
        public required string Version { get; init; }
        public required string DesignManifest { get; init; }
        public required string RuntimeBuildSha256 { get; init; }
        public List<Attempt> Attempts { get; init; } = [];
    }
    sealed record Compiled(DiagnosticPlan Plan, IReadOnlyList<FlexibleRigProgram> Rigs);
    sealed record CurrentAssembly(Attempt Attempt, AssemblyBuildJob Job, AssemblyNativeVerification Native, string Scene);
    internal sealed record VerifiedAssemblyStage(string Directory, string Scene, string ToolPath, string ToolSha256,
        string ApprovalSha256, string JobSha256, string ReportSha256, string VerificationSha256, string NativeVerificationSha256,
        string SceneSha256, SortedDictionary<string, string> Bundle, AssemblyBuildJob Job, AssemblyNativeVerification Native);
    internal sealed record VerifiedAssemblyInput(PartSession.VerifiedPartInput Parts, VerifiedAssemblyStage Assembly, VerifiedAssemblyStage Motion);
    static bool Hash(string? value) => SchematicNativeRunner.IsHash(value);
    static string Digest<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, StrictDesignJson.Options)));
    static byte[] Encode<T>(T value)
    {
        using var memory = new MemoryStream();
        using (var bounded = new BoundedWriter(memory)) JsonSerializer.Serialize(bounded,value,CompactOptions);
        return memory.ToArray();
    }
    sealed class BoundedWriter(Stream inner) : Stream
    {
        long written;
        public override bool CanRead=>false;public override bool CanSeek=>false;public override bool CanWrite=>true;
        public override long Length=>written;public override long Position{get=>written;set=>throw new NotSupportedException();}
        public override void Flush()=>inner.Flush();
        public override void Write(byte[] buffer,int offset,int count)=>Write(buffer.AsSpan(offset,count));
        public override void Write(ReadOnlySpan<byte> buffer){if(buffer.Length>MaximumJobBytes-written)throw new InvalidOperationException("Complete assembly job/native evidence exceeds the finite 8MiB JSON ceiling; no truncated evidence is accepted");inner.Write(buffer);written=checked(written+buffer.Length);}
        public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long value)=>throw new NotSupportedException();
    }
    string StateFile(string manifest) => workspace.Relative(manifest)[..^"manifest.json".Length] + "assembly.json";
    string LockPath(string manifest)
    {
        var directory = Path.GetDirectoryName(workspace.Resolve(manifest))!;
        return workspace.Relative(Path.Combine(Directory.GetParent(directory)!.FullName, "asset.lock"));
    }
    static string Stem(string operation) => operation == "assemble" ? "assembly" : operation == "prove-motion" ? "motion" : throw new InvalidOperationException("Unknown schematic assembly operation");
    static string SceneName(string operation) => operation == "assemble" ? "assembly.blend" : "diagnostic.blend";
    static string ReportName(string operation) => Stem(operation) + "-report.json";
    static string VerificationName(string operation) => Stem(operation) + "-verification.json";
    static string ScenePath(Attempt attempt) => attempt.Directory + "/output/" + SceneName(attempt.Operation);
    static Attempt? Latest(State state, string operation) => state.Attempts.LastOrDefault(a => a.Operation == operation);
    static AssemblyBuildIdentity Identity(PartSession.VerifiedPartInput input, string tool) => new(input.Input.Manifest.DesignSha256,
        input.Input.Package!.Fingerprint, input.Input.Manifest.RuntimeBuildSha256, tool, input.JobSha256, input.ReportSha256, input.VerificationSha256);
    static string SourceEvidence(Attempt source) => Digest(new { source.Directory,source.Identity,source.ApprovalSha256,source.ToolPath,source.Bundle,
        source.JobSha256,source.ReportSha256,source.VerificationSha256,source.NativeVerificationSha256,source.SceneSha256 });
    static AssemblyProfile RequireSpec(AssemblyDesign spec, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (spec.SchemaVersion != "bubo-assembly-design/v2" || spec.AssemblyProfile is not { SchemaVersion: "bubo-assembly-profile/v1" } profile)
            throw new InvalidOperationException("Assembly and motion proof require an explicit approved v2 design and bubo-assembly-profile/v1; v1 remains design and independent parts only");
        var validation = new AssemblyProfileValidator().Validate(spec, cancellation);
        if (!validation.IsValid) throw new DesignValidationException(validation);
        if (spec.Instances.Count > 4096 || profile.Contacts.Count > 32 || profile.MaxGeometryChecks is < 1 or > 50_000_000)
            throw new InvalidOperationException("Native assembly permits at most 4096 physical instances, 32 approved contact zones and 50000000 cumulative geometry checks");
        return profile;
    }
    static void RequireParts(PartSession.VerifiedPartInput input, string tool)
    {
        if (input.Report.Identity.RuntimeBuildSha256 != input.Input.Manifest.RuntimeBuildSha256 || input.Job.Identity.RuntimeBuildSha256 != input.Input.Manifest.RuntimeBuildSha256)
            throw new InvalidOperationException("Assembly requires parts built by the current runtime; preserve history and explicitly rebuild the parts");
        if (input.ToolSha256 != tool || input.Report.Identity.ToolSha256 != tool || input.Job.Identity.ToolSha256 != tool)
            throw new InvalidOperationException("Assembly requires the same exact Blender executable as its current verified parts; explicitly rebuild parts for a different selected tool");
        var artifacts = input.Report.Parts.ToDictionary(p => p.Id, StringComparer.Ordinal);
        long vertices = 0, triangles = 0; var maximum = Math.Min(MaximumGeometry, input.Input.Design.Budgets.MaxGeneratedTriangles);
        foreach (var instance in input.Input.Design.Instances)
        {
            var part = artifacts[instance.PartId]; vertices = checked(vertices + part.VertexCount); triangles = checked(triangles + part.TriangleCount);
            if (vertices > maximum || triangles > maximum) throw new InvalidOperationException("Actual occurrence-weighted part vertices/triangles exceed the approved or 250000-per-pose native assembly ceiling; detail is never reduced");
        }
    }
    static Compiled Compile(AssemblyDesign spec, CancellationToken cancellation)
    {
        var profile = RequireSpec(spec, cancellation);
        var plan = new AssemblyMotionCompiler().Compile(spec, profile.SamplesPerRange, profile.SamplesPerCycle, cancellation);
        var rigs = new FlexibleMotionCompiler().Compile(spec, plan, cancellation);
        cancellation.ThrowIfCancellationRequested(); return new(plan, rigs);
    }
    AssemblyBuildJob Job(PartSession.VerifiedPartInput input, Attempt attempt, Compiled compiled, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested(); var spec = input.Input.Design; var profile = spec.AssemblyProfile ?? throw new InvalidOperationException("Explicit assembly profile required");
        var count = attempt.Operation == "assemble" ? checked((long)spec.Instances.Count + 3) : checked((long)spec.Instances.Count * compiled.Plan.Sampling.TotalSamples + 3);
        if (count > MaximumFiles) throw new InvalidOperationException("Full physical-instance/rest-and-diagnostic geometry inventory exceeds the native 16384-file ceiling; no sample or instance omission is allowed");
        return new() { SchemaVersion = "bubo-schematic-assembly-job/v1", Operation = attempt.Operation, ProjectRoot = workspace.Root,
            Output = workspace.ResolveWrite(attempt.Directory + "/output"), Design = spec, PartsDirectory = workspace.Resolve(input.Directory), Parts = input.Report,
            PartsReportSha256 = input.ReportSha256, PartsVerificationSha256 = input.VerificationSha256, Identity = attempt.Identity, Bundle = attempt.Bundle,
            Limits = new(Math.Min(MaximumGeometry, spec.Budgets.MaxGeneratedTriangles), Math.Min(MaximumGeometry, spec.Budgets.MaxGeneratedTriangles), spec.Budgets.MaxExportBytes,
                MaximumJobBytes, checked((int)count), spec.Budgets.MaxMotionSamples, profile.MaxGeometryChecks), Plan = compiled.Plan, Rigs = compiled.Rigs,
            SourceAssembly = attempt.SourceAssembly is null ? null : workspace.Resolve(attempt.SourceAssembly), SourceAssemblySha256 = attempt.SourceAssemblySha256 };
    }
    State Load(string manifest, DesignSession.PartInput input, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var path = StateFile(manifest);
        if (!File.Exists(workspace.Resolve(path))) return new() { Task = input.Manifest.Task, Asset = input.Manifest.Asset, Version = input.Manifest.Version,
            DesignManifest = workspace.Relative(manifest), RuntimeBuildSha256 = input.Manifest.RuntimeBuildSha256 };
        var state = StrictDesignJson.Read<State>(workspace.Resolve(path));
        if (state.SchemaVersion != "bubo-schematic-assembly-state/v1" || state.Task != input.Manifest.Task || state.Asset != input.Manifest.Asset || state.Version != input.Manifest.Version
            || state.DesignManifest != workspace.Relative(manifest) || state.RuntimeBuildSha256 != input.Manifest.RuntimeBuildSha256 || state.Attempts.Count > 128
            || state.Attempts.Select(a=>a.Directory).Distinct(StringComparer.Ordinal).Count()!=state.Attempts.Count)
            throw new InvalidOperationException("Assembly state identity or retained bounded history differs");
        foreach (var attempt in state.Attempts)
        {
            cancellation.ThrowIfCancellationRequested();
            var prefix = input.Manifest.Staging + "/attempts/" + Stem(attempt.Operation) + "-";
            var identity = attempt.Identity;
            if (!attempt.Directory.StartsWith(prefix, StringComparison.Ordinal) || !Regex.IsMatch(attempt.Directory[prefix.Length..], "^[a-f0-9]{32}$", RegexOptions.CultureInvariant)
                || attempt.Status is not ("running" or "complete" or "failed") || !Path.IsPathFullyQualified(attempt.ToolPath) || !Hash(attempt.ApprovalSha256)
                || !Hash(identity.DesignSha256) || !Hash(identity.DesignFingerprint) || !Hash(identity.RuntimeBuildSha256) || !Hash(identity.ToolSha256) || !Hash(identity.PartsJobSha256)
                || !Hash(identity.PartsReportSha256) || !Hash(identity.PartsVerificationSha256) || attempt.Bundle.Count != Scripts.Length || attempt.Bundle.Values.Any(h => !Hash(h))
                || !attempt.Bundle.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(Scripts)) throw new InvalidOperationException("Invalid immutable assembly attempt identity");
            if (attempt.Operation == "assemble" && (attempt.SourceAssembly is not null || attempt.SourceAssemblySha256 is not null || attempt.SourceAssemblyEvidenceSha256 is not null)
                || attempt.Operation == "prove-motion" && (attempt.SourceAssembly is null || !Hash(attempt.SourceAssemblySha256) || !Hash(attempt.SourceAssemblyEvidenceSha256)
                    || !Regex.IsMatch(attempt.SourceAssembly, "^" + Regex.Escape(input.Manifest.Staging + "/attempts/assembly-") + "[a-f0-9]{32}/output/assembly\\.blend$", RegexOptions.CultureInvariant)))
                throw new InvalidOperationException("Invalid current-assembly ancestry on retained motion attempt");
            if (attempt.Status == "complete" && (attempt.CompletedAt is null || !Hash(attempt.JobSha256) || !Hash(attempt.ReportSha256) || !Hash(attempt.VerificationSha256)
                || !Hash(attempt.NativeVerificationSha256) || !Hash(attempt.SceneSha256))) throw new InvalidOperationException("Completed assembly evidence lacks independent and native identities");
            if (attempt.NativeFailureSha256 is not null && (!Hash(attempt.NativeFailureSha256)
                || PartOutputVerifier.FileHash(workspace,attempt.Directory+"/native-failure.json",cancellation,MaximumJobBytes)!=attempt.NativeFailureSha256))
                throw new InvalidOperationException("Retained native clearance failure evidence changed");
            workspace.Resolve(attempt.Directory);
        }
        return state;
    }
    void Save(string manifest, State state)
    {
        workspace.Save(StateFile(manifest), state);
        AuditJournal.Append(workspace, StateFile(manifest)[..^"assembly.json".Length] + "assembly-audit.jsonl", new
        { schemaVersion = "bubo-schematic-assembly-event/v1", state.Asset, state.Version, attempts = state.Attempts.Count, operation = state.Attempts.LastOrDefault()?.Operation,
            status = state.Attempts.LastOrDefault()?.Status, at = DateTimeOffset.UtcNow });
    }
    void Fresh(string manifest, Attempt attempt, CancellationToken cancellation)
    {
        workspace.HostContext?.ValidateFresh(); var input = parts.InspectOwned(manifest, cancellation);
        RequireParts(input, runner.ToolHash(attempt.ToolPath));
        if (Identity(input, attempt.Identity.ToolSha256) != attempt.Identity || input.Input.Manifest.Reviews[^1].Sha256 != attempt.ApprovalSha256)
            throw new InvalidOperationException("Current design approval, parts, runtime or selected tool changed during assembly or motion proof");
        runner.FreshResources(attempt.Bundle); runner.FreshCopy(attempt.Directory, attempt.Bundle);
        if (attempt.JobSha256 is not null && PartOutputVerifier.FileHash(workspace, attempt.Directory + "/job.json", cancellation, MaximumJobBytes) != attempt.JobSha256)
            throw new InvalidOperationException("Immutable assembly job changed during execution");
        if (attempt.SourceAssembly is not null)
        {
            var state = Load(manifest, input.Input,cancellation); var current = Latest(state, "assemble");
            if (current is not { Status: "complete" } || ScenePath(current) != attempt.SourceAssembly || current.SceneSha256 != attempt.SourceAssemblySha256
                || current.Identity != attempt.Identity || SourceEvidence(current) != attempt.SourceAssemblyEvidenceSha256
                || PartOutputVerifier.FileHash(workspace, attempt.SourceAssembly, cancellation, input.Input.Design.Budgets.MaxExportBytes) != attempt.SourceAssemblySha256)
                throw new InvalidOperationException("The latest independently verified source assembly changed during motion proof");
            ExactFiles(current,input.Input.Design.Budgets.MaxExportBytes,cancellation); runner.FreshResources(current.Bundle); runner.FreshCopy(current.Directory,current.Bundle);
            var sourceNative=StrictDesignJson.Read<AssemblyNativeVerification>(workspace.Resolve(current.Directory+"/native-verification.json"));
            VerifyMeasuredFiles(current,sourceNative.Report,input.Input.Design.Budgets.MaxExportBytes,cancellation);
        }
        cancellation.ThrowIfCancellationRequested();
    }
    void ExactFiles(Attempt attempt, long maximumOutputBytes, CancellationToken cancellation)
    {
        if (PartOutputVerifier.FileHash(workspace, attempt.Directory + "/job.json", cancellation, MaximumJobBytes) != attempt.JobSha256
            || PartOutputVerifier.FileHash(workspace, attempt.Directory + "/output/" + ReportName(attempt.Operation), cancellation, MaximumJobBytes) != attempt.ReportSha256
            || PartOutputVerifier.FileHash(workspace, attempt.Directory + "/output/" + VerificationName(attempt.Operation), cancellation, MaximumJobBytes) != attempt.VerificationSha256
            || PartOutputVerifier.FileHash(workspace, ScenePath(attempt), cancellation, maximumOutputBytes) != attempt.SceneSha256
            || PartOutputVerifier.FileHash(workspace, attempt.Directory + "/native-verification.json", cancellation, MaximumJobBytes) != attempt.NativeVerificationSha256)
            throw new InvalidOperationException("Retained assembly scene, job, independent reports or native verification changed");
    }
    void VerifyMeasuredFiles(Attempt attempt, AssemblyBuildReport report, long maximum, CancellationToken cancellation)
    {
        var measured = report.Instances.Concat(report.Clips.SelectMany(c => c.Samples.SelectMany(s => s.Instances))).ToArray();
        var expected=measured.Select(i=>i.GeometryFile).Concat([SceneName(attempt.Operation),ReportName(attempt.Operation),VerificationName(attempt.Operation)]).ToHashSet(StringComparer.Ordinal);
        if(expected.Count>MaximumFiles)throw new InvalidOperationException("Final assembly inventory exceeds the native file ceiling");
        foreach (var instance in measured)
        {
            cancellation.ThrowIfCancellationRequested();
            if (PartOutputVerifier.FileHash(workspace,attempt.Directory + "/output/" + instance.GeometryFile,cancellation,maximum) != instance.GeometrySha256)
                throw new InvalidOperationException("Measured assembly geometry changed during final source freshness checks");
        }
        if (PartOutputVerifier.FileHash(workspace,ScenePath(attempt),cancellation,maximum) != report.SourceSha256)
            throw new InvalidOperationException("Editable assembly changed during final source freshness checks");
        var directory=attempt.Directory+"/output";var entries=Directory.EnumerateFileSystemEntries(workspace.Resolve(directory)).Take(MaximumFiles+1).ToArray();
        if(entries.Length!=expected.Count||!entries.Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal).SetEquals(expected))
            throw new InvalidOperationException("Current assembly output inventory changed during source freshness checks");
        long total=0;foreach(var name in expected)
        {
            cancellation.ThrowIfCancellationRequested();if(Path.GetFileName(name)!=name||name.Contains('/')||name.Contains('\\'))throw new InvalidOperationException("Final assembly output filename escapes its inventory");
            var file=workspace.Resolve(directory+"/"+name);var attributes=File.GetAttributes(file);var length=new FileInfo(file).Length;
            if(!File.Exists(file)||(attributes&(FileAttributes.Directory|FileAttributes.ReparsePoint))!=0||length<=0||length>maximum-total
                ||name.EndsWith(".json",StringComparison.Ordinal)&&length>MaximumJobBytes)throw new InvalidOperationException("Current assembly output file or byte bounds changed");
            total=checked(total+length);
        }
    }
    AssemblyNativeVerification VerifyRetained(string manifest, PartSession.VerifiedPartInput input, Attempt attempt, Compiled compiled, CancellationToken cancellation)
    {
        Fresh(manifest, attempt, cancellation); var expected = Job(input, attempt, compiled, cancellation); ExactFiles(attempt, expected.Limits.MaxOutputBytes, cancellation);
        var job = StrictDesignJson.Read<AssemblyBuildJob>(workspace.Resolve(attempt.Directory + "/job.json"));
        if (Digest(job) != Digest(expected)) throw new InvalidOperationException("Retained assembly job differs from the complete current approved design, parts, plan, rigs or limits");
        var native = AssemblyOutputVerifier.Verify(workspace, attempt.Directory + "/output", job, attempt.JobSha256!, cancellation);
        var captured = StrictDesignJson.Read<AssemblyNativeVerification>(workspace.Resolve(attempt.Directory + "/native-verification.json"));
        if (Digest(native) != Digest(captured)) throw new InvalidOperationException("Fresh native geometry/clearance verification differs from its retained evidence");
        if (native.Report.SourceSha256 != attempt.SceneSha256) throw new InvalidOperationException("Native verified editable assembly identity differs");
        ExactFiles(attempt, expected.Limits.MaxOutputBytes, cancellation); Fresh(manifest, attempt, cancellation); VerifyMeasuredFiles(attempt, native.Report, expected.Limits.MaxOutputBytes, cancellation); return native;
    }
    CurrentAssembly InspectAssembly(string manifest, State state, PartSession.VerifiedPartInput input, Compiled compiled, CancellationToken cancellation)
    {
        var attempt = Latest(state, "assemble");
        if (attempt is not { Status: "complete" }) throw new InvalidOperationException("Motion proof requires the latest complete independently verified assembly; failed or interrupted assembly attempts cannot fall back to older success");
        if (attempt.Identity != Identity(input, runner.ToolHash(attempt.ToolPath)) || attempt.ApprovalSha256 != input.Input.Manifest.Reviews[^1].Sha256)
            throw new InvalidOperationException("Assembly ancestry is stale after current parts, tool or approval changed; explicitly assemble again");
        var native = VerifyRetained(manifest, input, attempt, compiled, cancellation);
        return new(attempt, Job(input, attempt, compiled, cancellation), native, ScenePath(attempt));
    }
    public Task<object> Assemble(string manifest, string blender, CancellationToken cancellation = default) => Execute(manifest, blender, "assemble", cancellation);
    public Task<object> ProveMotion(string manifest, string blender, CancellationToken cancellation = default) => Execute(manifest, blender, "prove-motion", cancellation);
    /// <summary>Read-only composition under the caller's existing asset lock; no older-success fallback or nested public-status lock.</summary>
    internal VerifiedAssemblyInput InspectOwned(string manifest, CancellationToken cancellation = default)
    {
        var input = parts.InspectOwned(manifest, cancellation);
        _ = RequireSpec(input.Input.Design, cancellation);
        RequireParts(input, input.ToolSha256);
        var state = Load(manifest, input.Input, cancellation);
        var compiled = Compile(input.Input.Design, cancellation);
        var source = InspectAssembly(manifest, state, input, compiled, cancellation);
        var proof = Latest(state, "prove-motion");
        if (proof is not { Status: "complete" } || proof.Identity != source.Attempt.Identity
            || proof.ApprovalSha256 != source.Attempt.ApprovalSha256 || proof.SourceAssembly != source.Scene
            || proof.SourceAssemblySha256 != source.Attempt.SceneSha256 || proof.SourceAssemblyEvidenceSha256 != SourceEvidence(source.Attempt))
            throw new InvalidOperationException("Export requires the latest complete current assembly and diagnostic proof, bound to their exact source ancestry; no older-success fallback");
        var motion = VerifyRetained(manifest, input, proof, compiled, cancellation);
        VerifiedAssemblyStage Stage(Attempt attempt, AssemblyBuildJob job, AssemblyNativeVerification measured) => new(attempt.Directory,
            ScenePath(attempt), attempt.ToolPath, attempt.Identity.ToolSha256, attempt.ApprovalSha256, attempt.JobSha256!, attempt.ReportSha256!,
            attempt.VerificationSha256!, attempt.NativeVerificationSha256!, attempt.SceneSha256!, new(attempt.Bundle, StringComparer.Ordinal), job, measured);
        return new(input, Stage(source.Attempt, source.Job, source.Native), Stage(proof, Job(input, proof, compiled, cancellation), motion));
    }
    async Task<object> Execute(string manifest, string blender, string operation, CancellationToken cancellation)
    {
        runner.RequireBudget(); using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds)); cancellation = deadline.Token;
        var budget = new SchematicNativeRunner.Budget(timeoutSeconds, maxLogBytes);
        _ = design.InspectParts(manifest, cancellation, requireApproved: true);
        using var ownership = workspace.Lock(LockPath(manifest));
        var input = parts.InspectOwned(manifest, cancellation); var profile = RequireSpec(input.Input.Design, cancellation);
        var tool = runner.ToolHash(blender); RequireParts(input, tool); var bundle = runner.Resources(Scripts); var state = Load(manifest, input.Input,cancellation);
        if (state.Attempts.Count >= 128) throw new InvalidOperationException("Assembly and motion attempt history is full; preserve every prior attempt");
        Compiled? compiled = null; Attempt? source = null;
        if (operation == "prove-motion")
        {
            source = Latest(state, "assemble");
            if (source is not {Status:"complete"}) throw new InvalidOperationException("Motion proof requires the latest complete independently verified assembly; no older-success fallback");
            if (source.Identity != Identity(input,tool) || source.ApprovalSha256 != input.Input.Manifest.Reviews[^1].Sha256)
                throw new InvalidOperationException("Current parts, selected tool or approval changed; explicitly assemble again before motion proof");
        }
        var attempt = new Attempt { Operation = operation, Directory = input.Input.Manifest.Staging + "/attempts/" + Stem(operation) + "-" + Guid.NewGuid().ToString("N"),
            Identity = Identity(input, tool), ApprovalSha256 = input.Input.Manifest.Reviews[^1].Sha256, ToolPath = Path.GetFullPath(blender), Bundle = bundle, StartedAt = DateTimeOffset.UtcNow,
            SourceAssembly = source is null ? null : ScenePath(source), SourceAssemblySha256 = source?.SceneSha256,SourceAssemblyEvidenceSha256=source is null?null:SourceEvidence(source) };
        state.Attempts.Add(attempt); Save(manifest, state);
        try
        {
            compiled ??= Compile(input.Input.Design, cancellation);
            if (operation == "prove-motion") _ = InspectAssembly(manifest,state,input,compiled,cancellation);
            var job = Job(input, attempt, compiled,cancellation);
            var bytes = Encode(job);
            runner.CopyBundle(attempt.Directory, bundle); Directory.CreateDirectory(workspace.ResolveWrite(attempt.Directory + "/output"));
            runner.WriteNew(attempt.Directory + "/job.json", bytes); attempt.JobSha256 = PartOutputVerifier.FileHash(workspace, attempt.Directory + "/job.json", cancellation, MaximumJobBytes); Save(manifest, state);
            Fresh(manifest, attempt, cancellation); _ = await runner.Version(blender, attempt.Directory, budget, cancellation); Fresh(manifest, attempt, cancellation);
            await runner.Run(blender, attempt.Directory, operation == "assemble" ? "assembly_author.py" : "assembly_motion.py", attempt.JobSha256, bundle, "author.log", budget, cancellation);
            Fresh(manifest, attempt, cancellation);
            await runner.Run(blender, attempt.Directory, "assembly_verify.py", attempt.JobSha256, bundle, "verification.log", budget, cancellation);
            Fresh(manifest, attempt, cancellation);
            var report = attempt.Directory + "/output/" + ReportName(operation); var verification = attempt.Directory + "/output/" + VerificationName(operation);
            var reportHash = PartOutputVerifier.FileHash(workspace, report, cancellation, MaximumJobBytes); var verificationHash = PartOutputVerifier.FileHash(workspace, verification, cancellation, MaximumJobBytes);
            var native = AssemblyOutputVerifier.Verify(workspace, attempt.Directory + "/output", job, attempt.JobSha256, cancellation);
            var nativeBytes = Encode(native);
            runner.WriteNew(attempt.Directory + "/native-verification.json", nativeBytes);
            Fresh(manifest, attempt, cancellation); _ = budget.Seconds();
            if (PartOutputVerifier.FileHash(workspace, report, cancellation, MaximumJobBytes) != reportHash || PartOutputVerifier.FileHash(workspace, verification, cancellation, MaximumJobBytes) != verificationHash)
                throw new InvalidOperationException("Assembly independent reports changed during native verification");
            var sceneHash = PartOutputVerifier.FileHash(workspace, ScenePath(attempt), cancellation, job.Limits.MaxOutputBytes);
            if (native.Report.SourceSha256 != sceneHash) throw new InvalidOperationException("Editable assembly changed during native verification");
            VerifyMeasuredFiles(attempt,native.Report,job.Limits.MaxOutputBytes,cancellation);
            attempt.ReportSha256 = reportHash; attempt.VerificationSha256 = verificationHash; attempt.SceneSha256 = sceneHash;
            attempt.NativeVerificationSha256 = PartOutputVerifier.FileHash(workspace, attempt.Directory + "/native-verification.json", cancellation, MaximumJobBytes);
            attempt.Status = "complete"; attempt.CompletedAt = DateTimeOffset.UtcNow; Save(manifest, state);
            cancellation.ThrowIfCancellationRequested(); return Describe(state, input.Input, true, operation == "prove-motion", null);
        }
        catch (Exception error)
        {
            attempt.Status = "failed"; attempt.Error = error.Message; attempt.CompletedAt = DateTimeOffset.UtcNow;
            if(error is AssemblyOutputVerificationException clearance)
            {
                try
                {
                    runner.WriteNew(attempt.Directory+"/native-failure.json",Encode(clearance.Report));
                    attempt.NativeFailureSha256=PartOutputVerifier.FileHash(workspace,attempt.Directory+"/native-failure.json",maximumBytes:MaximumJobBytes);
                }
                catch(Exception evidenceError) when(evidenceError is InvalidOperationException or IOException or JsonException)
                {attempt.Error+="; complete native failure evidence unavailable: "+evidenceError.Message;}
            }
            Save(manifest, state); throw;
        }
    }
    public object Status(string manifest, CancellationToken cancellation = default)
    {
        _ = design.InspectParts(manifest, cancellation); using var ownership = workspace.Lock(LockPath(manifest));
        var currentDesign = design.InspectParts(manifest, cancellation); var state = Load(manifest, currentDesign,cancellation);
        var assembly = Latest(state, "assemble"); var proof = Latest(state, "prove-motion"); var valid = false; var proved = false; string? unavailable = null;
        var input = currentDesign.Approved ? parts.TryInspectOwned(manifest, cancellation) : null;
        if (input is null) unavailable = currentDesign.Approved ? "latest current independently verified parts required" : "exact whole-assembly design approval required";
        else if (currentDesign.Design.SchemaVersion != "bubo-assembly-design/v2") unavailable = "v1 design remains design and independent parts only";
        else if (assembly is { Status: "complete" })
        {
            RequireParts(input, input.ToolSha256);
            if (assembly.Identity != Identity(input, input.ToolSha256) || assembly.ApprovalSha256 != currentDesign.Manifest.Reviews[^1].Sha256)
                unavailable = "new current parts or approval invalidated the prior assembly; explicitly assemble again";
            else
            {
                var compiled = Compile(currentDesign.Design, cancellation); _ = VerifyRetained(manifest, input, assembly, compiled, cancellation); valid = true;
                if (proof is { Status: "complete" } && proof.Identity == assembly.Identity && proof.ApprovalSha256 == assembly.ApprovalSha256
                    && proof.SourceAssembly == ScenePath(assembly) && proof.SourceAssemblySha256 == assembly.SceneSha256 && proof.SourceAssemblyEvidenceSha256==SourceEvidence(assembly))
                { _ = VerifyRetained(manifest, input, proof, compiled, cancellation); proved = true; }
            }
        }
        cancellation.ThrowIfCancellationRequested(); return Describe(state, currentDesign, valid, proved, unavailable);
    }
    public object Doctor(string manifest, CancellationToken cancellation = default)
    {
        _ = design.InspectParts(manifest, cancellation); using var ownership = workspace.Lock(LockPath(manifest));
        var input = design.InspectParts(manifest, cancellation); var unavailable = new List<string>();
        if (!input.Approved) unavailable.Add("exact fresh whole-assembly design approval required");
        try { runner.RequireBudget(); } catch (Exception error) when (error is InvalidOperationException or PlatformNotSupportedException) { unavailable.Add(error.Message); }
        SortedDictionary<string,string>? bundle=null;
        try { bundle = runner.Resources(Scripts); } catch (Exception error) when (error is InvalidOperationException or IOException or JsonException) { unavailable.Add("fixed audited assembly resources unavailable: " + error.Message); }
        PartSession.VerifiedPartInput? current = null;
        if (input.Approved) current = parts.TryInspectOwned(manifest, cancellation);
        if (current is null) unavailable.Add("latest independently verified current parts required");
        Compiled? compiled = null;
        try { _ = RequireSpec(input.Design, cancellation); compiled = Compile(input.Design, cancellation); if (current is not null) RequireParts(current, current.ToolSha256); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException) { unavailable.Add(error.Message); }
        var state = Load(manifest, input,cancellation); var assembly = Latest(state, "assemble"); var sourceReady = false;var motionUnavailable=new List<string>();
        if(state.Attempts.Count>=128)unavailable.Add("assembly/motion attempt history is full and must be preserved");
        Attempt Candidate(string operation,Attempt? source)=>new(){Operation=operation,Directory=input.Manifest.Staging+"/attempts/"+Stem(operation)+"-"+Guid.NewGuid().ToString("N"),
            Identity=Identity(current!,current!.ToolSha256),ApprovalSha256=input.Manifest.Reviews[^1].Sha256,ToolPath=current.ToolPath,Bundle=bundle!,StartedAt=DateTimeOffset.UtcNow,
            SourceAssembly=source is null?null:ScenePath(source),SourceAssemblySha256=source?.SceneSha256,SourceAssemblyEvidenceSha256=source is null?null:SourceEvidence(source)};
        if(unavailable.Count==0&&current is not null&&compiled is not null&&bundle is not null)
        {
            try{_ = Encode(Job(current,Candidate("assemble",null),compiled,cancellation));}
            catch(Exception error)when(error is ArgumentException or InvalidOperationException or OverflowException){unavailable.Add(error.Message);}
        }
        if (unavailable.Count == 0 && current is not null && compiled is not null && assembly is { Status: "complete" })
        {
            if (assembly.Identity == Identity(current, current.ToolSha256) && assembly.ApprovalSha256 == input.Manifest.Reviews[^1].Sha256)
            { _ = VerifyRetained(manifest, current, assembly, compiled, cancellation); sourceReady = true; }
        }
        if(!sourceReady)motionUnavailable.Add("latest complete current independently verified assembly required");
        else if(current is not null&&compiled is not null)
        {
            try{_ = Encode(Job(current,Candidate("prove-motion",assembly),compiled,cancellation));}
            catch(Exception error)when(error is ArgumentException or InvalidOperationException or OverflowException){motionUnavailable.Add(error.Message);}
        }
        workspace.HostContext?.ValidateFresh();var fresh=design.InspectParts(manifest,cancellation);
        if(fresh.Manifest.DesignSha256!=input.Manifest.DesignSha256||fresh.Package?.Fingerprint!=input.Package?.Fingerprint||fresh.Approved!=input.Approved||Digest(fresh.Manifest.Reviews)!=Digest(input.Manifest.Reviews))
            throw new InvalidOperationException("Design or approval changed during assembly prerequisite inspection");
        if(current is not null)
        {
            var refreshed=parts.TryInspectOwned(manifest,cancellation);
            if(refreshed is null||Identity(refreshed,current.ToolSha256)!=Identity(current,current.ToolSha256))throw new InvalidOperationException("Current verified parts changed during assembly prerequisite inspection");
        }
        if(bundle is not null)runner.FreshResources(bundle);cancellation.ThrowIfCancellationRequested();
        return new { schemaVersion = "bubo-schematic-assembly-doctor/v1", phase = "assembly-and-readiness", assemblyAvailable = unavailable.Count == 0,
            motionProofAvailable = unavailable.Count == 0 && sourceReady&&motionUnavailable.Count==0, latestAssemblyValid = sourceReady, nativeReadiness = unavailable.Count == 0 ? "verified-parts" : "unverified",
            unavailable,motionUnavailable, provisioningRequired = false, toolProbePerformed = false, modelExportAvailable = false, humanModelAccepted = false, runtimeAcceptance = false };
    }
    static object Describe(State state, DesignSession.PartInput input, bool assembled, bool proved, string? unavailable)
    {
        var assembly = Latest(state, "assemble"); var proof = Latest(state, "prove-motion");
        return new { schemaVersion = "bubo-schematic-assembly-status/v1", state.Asset, state.Version, phase = "assembly-and-readiness", wholeAssemblyApproved = input.Approved,
            currentAssemblyValid = assembled, currentMotionProofValid = proved, assemblySource = assembled ? ScenePath(assembly!) : null,
            assemblyReport = assembled ? assembly!.Directory + "/output/assembly-report.json" : null,
            assemblyVerification = assembled ? assembly!.Directory + "/output/assembly-verification.json" : null,
            assemblyNativeVerification = assembled ? assembly!.Directory + "/native-verification.json" : null,
            diagnosticSource = proved ? ScenePath(proof!) : null, motionReport = proved ? proof!.Directory + "/output/motion-report.json" : null,
            motionVerification = proved ? proof!.Directory + "/output/motion-verification.json" : null,
            motionNativeVerification = proved ? proof!.Directory + "/native-verification.json" : null, designFingerprint = input.Package?.Fingerprint,
            attempts = state.Attempts.Count, assemblyAttempts = state.Attempts.Count(a => a.Operation == "assemble"), motionAttempts = state.Attempts.Count(a => a.Operation == "prove-motion"),
            lastAssemblyStatus = assembly?.Status, lastAssemblyError = assembly?.Error, lastMotionStatus = proof?.Status, lastMotionError = proof?.Error,
            unavailable, modelExportAvailable = false, humanModelAccepted = false, runtimeAcceptance = false, continuousCollisionCertified = false };
    }
}
