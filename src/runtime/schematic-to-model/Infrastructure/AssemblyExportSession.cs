using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Bubo.Schematic.Domain;
using Hephaera.AssetProduction;

namespace Bubo.Schematic.Infrastructure;

/// <summary>Immutable portable deliveries and distinct model decisions. Caller never promotes an asset.</summary>
public sealed partial class AssemblyExportSession(IWorkspace workspace, DesignSession design, AssemblySession assembly,
    INativeProcess process, string bundleDirectory, int timeoutSeconds, long maxLogBytes, long? maxPrivateBytes, string resourceCatalogSha256)
{
    const int JsonLimit = 8_388_608;
    static readonly JsonSerializerOptions Compact = new(StrictDesignJson.Options) { WriteIndented = false };
    static readonly string[] Scripts = ["export_admission.py", "export_geometry.py", "export_author.py", "export_verify.py", "export_review.py",
        "assembly_admission.py", "assembly_author.py", "assembly_geometry.py", "assembly_motion.py", "assembly_verify.py", "parts_admission.py", "parts_geometry.py"];
    readonly SchematicNativeRunner runner = new(workspace, process, bundleDirectory, resourceCatalogSha256, timeoutSeconds, maxLogBytes, maxPrivateBytes);
    sealed record RunRecord(string JobSha256, string ReportSha256, string NativeSha256);
    sealed record Attempt
    {
        public required string Operation { get; init; }
        public required string Directory { get; init; }
        public required string Author { get; init; }
        public required AssemblyExportIdentity Identity { get; init; }
        public required SortedDictionary<string,string> Bundle { get; init; }
        public required AssemblyExportToolchain Toolchain { get; init; }
        public required DateTimeOffset StartedAt { get; init; }
        public string? ExportDirectory { get; init; }
        public string? PackageSha256 { get; set; }
        public string Status { get; set; } = "running";
        public string? Error { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public SortedDictionary<string,RunRecord> Runs { get; init; } = new(StringComparer.Ordinal);
    }
    sealed record Decision(string Kind,string Reviewer,string DecisionValue,string Source,string Snapshot,string Sha256,
        string PackageSha256,string ViewsSha256,string? IndependentReviewSha256);
    sealed record State
    {
        public string SchemaVersion { get; init; } = "bubo-schematic-export-state/v1";
        public required string Task { get; init; }
        public required string Asset { get; init; }
        public required string Version { get; init; }
        public required string Manifest { get; init; }
        public required string RuntimeSha256 { get; init; }
        public List<Attempt> Attempts { get; init; } = [];
        public List<Decision> Decisions { get; init; } = [];
    }
    sealed record SourceSnapshot(AssemblySession.VerifiedAssemblyInput Source, AssemblyExportInputs Inputs,
        SortedDictionary<string,string> StateHashes);
    sealed record Payload(AssemblyExportPackage Package,string Directory,string Manifest,string Sha256);
    static string Digest<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value,Compact)));
    static string Person(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Length > 128 || value.Any(char.IsControl))
            throw new InvalidOperationException("Explicit bounded canonical model author/reviewer identity required");
        return value;
    }
    string Parent(string manifest) => workspace.Relative(manifest)[..^"manifest.json".Length];
    string StatePath(string manifest) => Parent(manifest)+"exports.json";
    string LockPath(string manifest) => workspace.Relative(Path.Combine(Directory.GetParent(Path.GetDirectoryName(workspace.Resolve(manifest))!)!.FullName,"asset.lock"));
    State Load(string manifest, DesignSession.PartInput input)
    {
        if (!File.Exists(workspace.Resolve(StatePath(manifest)))) return new() { Task=input.Manifest.Task,Asset=input.Manifest.Asset,Version=input.Manifest.Version,
            Manifest=workspace.Relative(manifest),RuntimeSha256=input.Manifest.RuntimeBuildSha256 };
        var state=StrictDesignJson.Read<State>(workspace.Resolve(StatePath(manifest)));
        if(state.SchemaVersion!="bubo-schematic-export-state/v1"||state.Task!=input.Manifest.Task||state.Asset!=input.Manifest.Asset||state.Version!=input.Manifest.Version
            ||state.Manifest!=workspace.Relative(manifest)||state.RuntimeSha256!=input.Manifest.RuntimeBuildSha256||state.Attempts.Count>256||state.Decisions.Count>1024
            ||state.Attempts.Select(a=>a.Directory).Distinct(StringComparer.Ordinal).Count()!=state.Attempts.Count)
            throw new InvalidOperationException("Invalid bounded schematic export state identity/history");
        foreach(var a in state.Attempts)
        {
            var prefix=input.Manifest.Staging+"/attempts/"+(a.Operation=="export"?"export-":a.Operation=="review"?"export-review-":throw new InvalidOperationException("Invalid export operation"));
            if(!a.Directory.StartsWith(prefix,StringComparison.Ordinal)||!Regex.IsMatch(a.Directory[prefix.Length..],"^[a-f0-9]{32}$")
                ||a.Status is not ("running" or "complete" or "failed")||a.Bundle.Count!=Scripts.Length||!a.Bundle.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(Scripts)
                ||a.Bundle.Values.Any(h=>!ExportPackageValidator.IsHash(h))||a.Runs.Count>3
                ||a.Runs.Any(p=>p.Key is not ("produce" or "verify-rest" or "verify-diagnostic" or "review")||!ExportPackageValidator.IsHash(p.Value.JobSha256)
                    ||!ExportPackageValidator.IsHash(p.Value.ReportSha256)||!ExportPackageValidator.IsHash(p.Value.NativeSha256)))
                throw new InvalidOperationException("Invalid retained export attempt");
            _=Person(a.Author);ExportPackageValidator.RequireIdentity(a.Identity);workspace.Resolve(a.Directory);
            if(a.Status=="complete"&&(a.CompletedAt is null||!ExportPackageValidator.IsHash(a.PackageSha256)
                ||a.Operation=="export"&&!a.Runs.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(["produce","verify-rest","verify-diagnostic"])
                ||a.Operation=="review"&&(a.ExportDirectory is null||!a.Runs.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(["review"]))))
                throw new InvalidOperationException("Completed export/review lacks complete native evidence");
        }
        foreach(var d in state.Decisions)
            if(d.Kind is not ("independent-model" or "human-model")||d.DecisionValue is not ("approved" or "rejected")||!ExportPackageValidator.IsHash(d.Sha256)
                ||!ExportPackageValidator.IsHash(d.PackageSha256)||!ExportPackageValidator.IsHash(d.ViewsSha256)
                ||d.Snapshot!=Parent(manifest)+"model-review-evidence/"+d.Sha256+".json")throw new InvalidOperationException("Invalid model decision history");
        return state;
    }
    void Save(string manifest,State state)
    {
        RequireStateSize(state);
        workspace.Save(StatePath(manifest),state);
        AuditJournal.Append(workspace,Parent(manifest)+"export-audit.jsonl",new {schemaVersion="bubo-schematic-export-event/v1",state.Asset,state.Version,
            attempts=state.Attempts.Count,decisions=state.Decisions.Count,status=state.Attempts.LastOrDefault()?.Status,at=DateTimeOffset.UtcNow});
    }
    internal static void RequireStateSize<T>(T state)
    {
        // Workspace.Save uses its indented serializer. Bound those exact bytes,
        // including reserve for the current operation's terminal state.
        if(JsonSerializer.SerializeToUtf8Bytes(state,Workspace.Json).Length>JsonLimit-65_536)
            throw new InvalidOperationException("Export history capacity exhausted; preserve prior records");
    }
    static AssemblyExportIdentity Identity(AssemblySession.VerifiedAssemblyInput source)=>new(source.Assembly.Job.Identity,source.Assembly.ApprovalSha256,
        source.Assembly.JobSha256,source.Assembly.ReportSha256,source.Assembly.NativeVerificationSha256,source.Assembly.SceneSha256,
        source.Motion.JobSha256,source.Motion.ReportSha256,source.Motion.NativeVerificationSha256,source.Motion.SceneSha256);
    AssemblyExportFile Ref(string path,string expected,long maximum,CancellationToken ct)
    {
        var actual=Describe(path,maximum,ct);if(actual.Sha256!=expected)throw new InvalidOperationException("Export input differs; expected identities are never rebound");return actual;
    }
    AssemblyExportFile Describe(string path,long maximum,CancellationToken ct)
    {
        var file=workspace.Resolve(path);var info=new FileInfo(file);var hash=PartOutputVerifier.FileHash(workspace,path,ct,maximum);
        if(!info.Exists||info.Length<=0||new FileInfo(file).Length!=info.Length)throw new InvalidOperationException("Export input changed during capture");
        return new(file,hash,info.Length);
    }
    SourceSnapshot Snapshot(string manifest,CancellationToken ct)
    {
        var source=assembly.InspectOwned(manifest,ct);var max=source.Parts.Input.Design.Budgets.MaxExportBytes;
        var inputs=new AssemblyExportInputs(Ref(source.Assembly.Directory+"/job.json",source.Assembly.JobSha256,JsonLimit,ct),
            Ref(source.Assembly.Directory+"/output/assembly-report.json",source.Assembly.ReportSha256,JsonLimit,ct),
            Ref(source.Assembly.Directory+"/native-verification.json",source.Assembly.NativeVerificationSha256,JsonLimit,ct),
            Ref(source.Motion.Directory+"/job.json",source.Motion.JobSha256,JsonLimit,ct),
            Ref(source.Motion.Directory+"/output/motion-report.json",source.Motion.ReportSha256,JsonLimit,ct),
            Ref(source.Motion.Directory+"/native-verification.json",source.Motion.NativeVerificationSha256,JsonLimit,ct),
            Ref(source.Assembly.Scene,source.Assembly.SceneSha256,max,ct),Ref(source.Motion.Scene,source.Motion.SceneSha256,max,ct),null,null,null,null,null);
        var hashes=new SortedDictionary<string,string>(StringComparer.Ordinal);
        foreach(var file in new[]{workspace.Relative(manifest),Parent(manifest)+"parts.json",Parent(manifest)+"assembly.json"})
            hashes.Add(file,PartOutputVerifier.FileHash(workspace,file,ct,JsonLimit));
        return new(source,inputs,hashes);
    }
    void Fresh(SourceSnapshot source,SortedDictionary<string,string> bundle,AssemblyExportToolchain tool,CancellationToken ct)
    {
        workspace.HostContext?.ValidateFresh();runner.FreshResources(bundle);AssemblyGltfToolchain.RequireCurrent(tool,ct);
        foreach(var (path,hash) in source.StateHashes)if(PartOutputVerifier.FileHash(workspace,path,ct,JsonLimit)!=hash)throw new InvalidOperationException("Latest design, parts, assembly or readiness state changed during export");
        foreach(var file in InputFiles(source.Inputs))_ = Ref(file.Path,file.Sha256,file.Bytes,ct);
        ct.ThrowIfCancellationRequested();
    }
    static IEnumerable<AssemblyExportFile> InputFiles(AssemblyExportInputs i)=>new[]{i.AssemblyJob,i.AssemblyReport,i.AssemblyVerification,i.MotionJob,i.MotionReport,
        i.MotionVerification,i.SourceAssembly,i.SourceMotion,i.Glb,i.ProducerReport,i.Package,i.RestVerification,i.DiagnosticVerification}.OfType<AssemblyExportFile>();
    AssemblyExportToolchain Tool(SourceSnapshot source,string blender,CancellationToken ct)
    {
        workspace.HostContext?.EnsureToolAllowed(blender);
        if(!Path.IsPathFullyQualified(blender)||!Path.GetFullPath(blender).Equals(Path.GetFullPath(source.Source.Assembly.ToolPath),OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))
            throw new InvalidOperationException("Export/review must use the exact selected construction/readiness Blender executable");
        return AssemblyGltfToolchain.Capture(blender,source.Source.Assembly.ToolSha256,ct);
    }
    AssemblyExportLimits Limits(SourceSnapshot source)
    {
        var spec=source.Source.Parts.Input.Design;
        return new(Math.Min(ExportPackageValidator.MaximumBytes,spec.Budgets.MaxExportBytes),JsonLimit,Math.Min(ExportPackageValidator.MaximumBytes,spec.Budgets.MaxExportBytes),
            checked(3*Math.Min(250_000,spec.Budgets.MaxGeneratedTriangles)),Math.Min(250_000,spec.Budgets.MaxGeneratedTriangles),65_536,65_536,16_384,
            spec.Budgets.MaxMotionSamples,spec.AssemblyProfile!.MaxGeometryChecks,Math.Min(4096,checked(10+Math.Max(1,spec.Sections.Count)+source.Source.Motion.Job.Plan.Sampling.TotalSamples)),512,8);
    }
    AssemblyExportJob Job(SourceSnapshot source,Attempt attempt,string operation,string? representation,string child,AssemblyExportInputs inputs)
        =>new("bubo-schematic-export-job/v1",operation,representation,workspace.Root,workspace.ResolveWrite(child+"/output"),attempt.Bundle,attempt.Toolchain,
            attempt.Identity,Limits(source),inputs);
    async Task<AssemblyExportNativeVerification> Run(string manifest,State state,Attempt attempt,SourceSnapshot source,string key,AssemblyExportJob job,
        SchematicNativeRunner.Budget budget,CancellationToken ct)
    {
        var child=attempt.Directory+"/"+key;Fresh(source,attempt.Bundle,attempt.Toolchain,ct);
        var bundleBytes=attempt.Bundle.Keys.Sum(name=>new FileInfo(Path.Combine(bundleDirectory,name)).Length);
        var reserve=checked(2L*JsonLimit+budget.LogBytes()); // complete job/native certificate and every still-permitted log byte
        if(RemainingBytes(source,attempt,ct)<=bundleBytes+reserve+1024)throw new InvalidOperationException("Cumulative export allowance cannot admit another complete native operation");
        runner.CopyBundle(child,attempt.Bundle);
        var inventory=Inventory(attempt.Directory,Limits(source).MaxOutputBytes,ct);
        var outputBytes=RemainingBytes(source,attempt,ct)-reserve;
        var outputFiles=16_384-inventory.Count-4; // job, native certificate, log and final technical summary remain outside output
        if(outputBytes<1024||outputFiles<2)throw new InvalidOperationException("Cumulative export file/byte allowance exhausted before native launch");
        job=job with {Limits=job.Limits with {MaxOutputBytes=outputBytes,MaxFiles=outputFiles}};
        var jobFile=child+"/job.json";WriteJson(jobFile,job,ct);
        var hash=PartOutputVerifier.FileHash(workspace,jobFile,ct,JsonLimit);
        var script=job.Operation=="produce"?"export_author.py":job.Operation=="verify"?"export_verify.py":"export_review.py";
        Directory.CreateDirectory(workspace.ResolveWrite(child+"/temp"));
        Directory.CreateDirectory(workspace.ResolveWrite(child+"/output"));
        await runner.Run(attempt.Toolchain.BlenderPath,child,script,hash,attempt.Bundle,"native.log",budget,ct);
        Fresh(source,attempt.Bundle,attempt.Toolchain,ct);
        var native=AssemblyExportOutputVerifier.Verify(workspace,child+"/output",job,hash,ct);
        WriteJson(child+"/native-verification.json",native,ct,Math.Min(JsonLimit,RemainingBytes(source,attempt,ct)));
        attempt.Runs.Add(key,new(hash,PartOutputVerifier.FileHash(workspace,child+"/output/report.json",ct,JsonLimit),
            PartOutputVerifier.FileHash(workspace,child+"/native-verification.json",ct,JsonLimit)));
        Save(manifest,state);return native;
    }
    public async Task<object> Export(string manifest,string blender,string author,CancellationToken cancellation=default)
    {
        author=Person(author);using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));var ct=deadline.Token;
        _=design.InspectParts(manifest,ct);using var ownership=workspace.Lock(LockPath(manifest));
        runner.RequireBudget();var source=Snapshot(manifest,ct);var state=Load(manifest,source.Source.Parts.Input);var bundle=runner.Resources(Scripts);var tool=Tool(source,blender,ct);
        if(state.Attempts.Count>=256)throw new InvalidOperationException("Export/review history is full");
        var attempt=new Attempt{Operation="export",Directory=source.Source.Parts.Input.Manifest.Staging+"/attempts/export-"+Guid.NewGuid().ToString("N"),
            Author=author,Identity=Identity(source.Source),Bundle=bundle,Toolchain=tool,StartedAt=DateTimeOffset.UtcNow};
        state.Attempts.Add(attempt);Save(manifest,state);
        try
        {
            var budget=new SchematicNativeRunner.Budget(timeoutSeconds,maxLogBytes);
            if(RemainingBytes(source,attempt,ct)<Math.Min(16_384,budget.LogBytes()))throw new InvalidOperationException("Cumulative export allowance cannot admit the bounded tool probe");
            _=await runner.Version(blender,attempt.Directory,budget,ct);
            var produced=await Run(manifest,state,attempt,source,"produce",Job(source,attempt,"produce",null,attempt.Directory+"/produce",source.Inputs),budget,ct);
            var payload=Seal(source,attempt,produced.Report,ct);attempt.PackageSha256=payload.Sha256;Save(manifest,state);
            foreach(var representation in new[]{"rest","diagnostic"})
            {
                var path=representation=="rest"?"assembly.glb":"diagnostic-motion/diagnostic.glb";
                var entry=payload.Package.Files.Single(f=>f.Path==path);
                var inputs=source.Inputs with {Glb=Ref(payload.Directory+"/"+path,entry.Sha256,entry.Bytes,ct),
                    ProducerReport=Ref(attempt.Directory+"/produce/output/report.json",attempt.Runs["produce"].ReportSha256,JsonLimit,ct),
                    Package=Ref(payload.Manifest,payload.Sha256,JsonLimit,ct)};
                var key="verify-"+representation;
                _=await Run(manifest,state,attempt,source,key,Job(source,attempt,"verify",representation,attempt.Directory+"/"+key,inputs),budget,ct);
                _=InspectPayload(source,attempt,ct);Inventory(attempt.Directory,Limits(source).MaxOutputBytes,ct);
            }
            WriteTechnicalReview(payload,attempt,ct,RemainingBytes(source,attempt,ct));
            Fresh(source,bundle,tool,ct);var final=assembly.InspectOwned(manifest,ct);
            if(Identity(final)!=attempt.Identity)throw new InvalidOperationException("Assembly/readiness changed before export completion");
            _=InspectPayload(source,attempt,ct);Inventory(attempt.Directory,Limits(source).MaxOutputBytes,ct);_ = budget.Seconds();ct.ThrowIfCancellationRequested();
            attempt.Status="complete";attempt.CompletedAt=DateTimeOffset.UtcNow;Save(manifest,state);
            return Describe(state,source.Source.Parts.Input,true,false,false,false,null,null);
        }
        catch(Exception error){attempt.Status="failed";attempt.Error=error.Message[..Math.Min(4096,error.Message.Length)];attempt.CompletedAt=DateTimeOffset.UtcNow;Save(manifest,state);throw;}
    }
}
