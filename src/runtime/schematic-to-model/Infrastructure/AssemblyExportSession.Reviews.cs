using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bubo.Schematic.Domain;

namespace Bubo.Schematic.Infrastructure;

public sealed record SchematicModelReview(string SchemaVersion,string Kind,string Decision,string Reviewer,string Role,bool Independent,bool Human,string Reason,
    string ModelSha256,string PackageSha256,string RestVerificationSha256,string DiagnosticVerificationSha256,string ViewsSha256,
    string? IndependentReviewSha256,IReadOnlyDictionary<string,string> ReviewedArtifacts);

public sealed partial class AssemblyExportSession
{
    static Attempt? Latest(State state,string operation)=>state.Attempts.LastOrDefault(a=>a.Operation==operation);
    AssemblyExportNativeVerification InspectRun(SourceSnapshot source,Attempt attempt,string key,CancellationToken ct)
    {
        var record=attempt.Runs.GetValueOrDefault(key)??throw new InvalidOperationException("Missing completed export operation");var directory=attempt.Directory+"/"+key;
        runner.FreshCopy(directory,attempt.Bundle);
        _=Ref(directory+"/job.json",record.JobSha256,JsonLimit,ct);_=Ref(directory+"/output/report.json",record.ReportSha256,JsonLimit,ct);
        _=Ref(directory+"/native-verification.json",record.NativeSha256,JsonLimit,ct);
        var job=StrictDesignJson.Read<AssemblyExportJob>(workspace.Resolve(directory+"/job.json"));
        if(job.Identity!=attempt.Identity||job.ProjectRoot!=workspace.Root||job.Output!=workspace.Resolve(directory+"/output")
            ||Digest(job.Bundle)!=Digest(attempt.Bundle)||Digest(job.Toolchain)!=Digest(attempt.Toolchain)
            ||(job.Limits with {MaxOutputBytes=Limits(source).MaxOutputBytes,MaxFiles=Limits(source).MaxFiles})!=Limits(source)
            ||job.Limits.MaxOutputBytes<=0||job.Limits.MaxOutputBytes>Limits(source).MaxOutputBytes||job.Limits.MaxFiles<=0||job.Limits.MaxFiles>Limits(source).MaxFiles
            ||job.Operation!=(key=="produce"?"produce":key=="review"?"review":"verify")
            ||job.Representation!=(key=="verify-rest"?"rest":key=="verify-diagnostic"?"diagnostic":null))
            throw new InvalidOperationException("Retained export job differs from its current closed operation");
        foreach(var reference in InputFiles(job.Inputs))_=Ref(reference.Path,reference.Sha256,reference.Bytes,ct);
        var baseRefs=InputFiles(source.Inputs).ToArray();var jobRefs=InputFiles(job.Inputs).Take(8).ToArray();
        if(!baseRefs.SequenceEqual(jobRefs))throw new InvalidOperationException("Retained export job has substituted source ancestry");
        var native=AssemblyExportOutputVerifier.Verify(workspace,directory+"/output",job,record.JobSha256,ct);
        var captured=StrictDesignJson.Read<AssemblyExportNativeVerification>(workspace.Resolve(directory+"/native-verification.json"));
        if(Digest(native)!=Digest(captured))throw new InvalidOperationException("Retained native export certificate differs from current actual measurements");
        return native;
    }
    Payload InspectExport(SourceSnapshot source,Attempt attempt,CancellationToken ct)
    {
        if(attempt.Operation!="export"||attempt.Status!="complete"||attempt.Identity!=Identity(source.Source))throw new InvalidOperationException("Latest complete current export is required");
        Fresh(source,attempt.Bundle,attempt.Toolchain,ct);var package=InspectPayload(source,attempt,ct);
        foreach(var key in new[]{"produce","verify-rest","verify-diagnostic"})
        {
            var native=InspectRun(source,attempt,key,ct);
            var expected=source.Inputs;
            if(key!="produce")
            {
                var name=key=="verify-rest"?"assembly.glb":"diagnostic-motion/diagnostic.glb";var file=package.Package.Files.Single(f=>f.Path==name);
                expected=expected with {Glb=Ref(package.Directory+"/"+name,file.Sha256,file.Bytes,ct),Package=Ref(package.Manifest,package.Sha256,JsonLimit,ct),
                    ProducerReport=Ref(attempt.Directory+"/produce/output/report.json",attempt.Runs["produce"].ReportSha256,JsonLimit,ct)};
            }
            if(Digest(native.Report.Inputs)!=Digest(expected))throw new InvalidOperationException("Portable verification substituted another package or producer receipt");
        }
        _=Ref(attempt.Directory+"/technical-review.json",PartOutputVerifier.FileHash(workspace,attempt.Directory+"/technical-review.json",ct,JsonLimit),JsonLimit,ct);
        RequireTechnicalReview(package,attempt,ct);return package;
    }
    void WriteTechnicalReview(Payload package,Attempt attempt,CancellationToken ct,long maximum)
    {
        WriteJson(attempt.Directory+"/technical-review.json",new{schemaVersion="bubo-schematic-technical-review/v1",decision="approved",reviewer="Bubo native portable verifier",
            modelSha256=package.Package.Files.Single(f=>f.Path=="assembly.glb").Sha256,packageSha256=package.Sha256,
            restVerificationSha256=attempt.Runs["verify-rest"].NativeSha256,diagnosticVerificationSha256=attempt.Runs["verify-diagnostic"].NativeSha256,
            independent=false,human=false,humanModelAccepted=false,runtimeAccepted=false},ct,Math.Min(JsonLimit,maximum));
    }
    void RequireTechnicalReview(Payload package,Attempt attempt,CancellationToken ct)
    {
        var obj=StrictDesignJson.Parse<JsonObject>(StrictDesignJson.ReadBytes(workspace.Resolve(attempt.Directory+"/technical-review.json")));
        var expected=JsonSerializer.SerializeToNode(new{schemaVersion="bubo-schematic-technical-review/v1",decision="approved",reviewer="Bubo native portable verifier",
            modelSha256=package.Package.Files.Single(f=>f.Path=="assembly.glb").Sha256,packageSha256=package.Sha256,
            restVerificationSha256=attempt.Runs["verify-rest"].NativeSha256,diagnosticVerificationSha256=attempt.Runs["verify-diagnostic"].NativeSha256,
            independent=false,human=false,humanModelAccepted=false,runtimeAccepted=false},Compact);
        if(!JsonNode.DeepEquals(obj,expected))throw new InvalidOperationException("Technical review binding differs from current native evidence");ct.ThrowIfCancellationRequested();
    }
    AssemblyExportNativeVerification InspectViews(SourceSnapshot source,Attempt export,Attempt views,CancellationToken ct)
    {
        if(views.Operation!="review"||views.Status!="complete"||views.ExportDirectory!=export.Directory||views.PackageSha256!=export.PackageSha256||views.Identity!=export.Identity
            ||views.Toolchain.Fingerprint!=export.Toolchain.Fingerprint||Digest(views.Bundle)!=Digest(export.Bundle))throw new InvalidOperationException("Complete current model review views required");
        var result=InspectRun(source,views,"review",ct);var package=InspectPayload(source,export,ct);
        var expected=source.Inputs with {Package=Ref(package.Manifest,package.Sha256,JsonLimit,ct),
            ProducerReport=Ref(export.Directory+"/produce/output/report.json",export.Runs["produce"].ReportSha256,JsonLimit,ct),
            RestVerification=Ref(export.Directory+"/verify-rest/native-verification.json",export.Runs["verify-rest"].NativeSha256,JsonLimit,ct),
            DiagnosticVerification=Ref(export.Directory+"/verify-diagnostic/native-verification.json",export.Runs["verify-diagnostic"].NativeSha256,JsonLimit,ct)};
        if(Digest(result.Report.Inputs)!=Digest(expected))throw new InvalidOperationException("Model view verification substituted prior technical evidence");
        return result;
    }
    public async Task<object> Review(string manifest,string blender,CancellationToken cancellation=default)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));var ct=deadline.Token;
        _=design.InspectParts(manifest,ct);using var ownership=workspace.Lock(LockPath(manifest));runner.RequireBudget();var source=Snapshot(manifest,ct);
        var state=Load(manifest,source.Source.Parts.Input);var exported=Latest(state,"export")??throw new InvalidOperationException("Export before model review");
        var package=InspectExport(source,exported,ct);var tool=Tool(source,blender,ct);var bundle=runner.Resources(Scripts);
        if(tool.Fingerprint!=exported.Toolchain.Fingerprint||Digest(bundle)!=Digest(exported.Bundle))throw new InvalidOperationException("New review tool/bundle requires a new export");
        if(state.Attempts.Count>=256)throw new InvalidOperationException("Export/review history is full");
        var views=new Attempt{Operation="review",Directory=source.Source.Parts.Input.Manifest.Staging+"/attempts/export-review-"+Guid.NewGuid().ToString("N"),
            Author=exported.Author,Identity=exported.Identity,Bundle=bundle,Toolchain=tool,StartedAt=DateTimeOffset.UtcNow,ExportDirectory=exported.Directory,PackageSha256=package.Sha256};
        state.Attempts.Add(views);Save(manifest,state);
        try
        {
            var budget=new SchematicNativeRunner.Budget(timeoutSeconds,maxLogBytes);
            var inputs=source.Inputs with {Package=Ref(package.Manifest,package.Sha256,JsonLimit,ct),
                ProducerReport=Ref(exported.Directory+"/produce/output/report.json",exported.Runs["produce"].ReportSha256,JsonLimit,ct),
                RestVerification=Ref(exported.Directory+"/verify-rest/native-verification.json",exported.Runs["verify-rest"].NativeSha256,JsonLimit,ct),
                DiagnosticVerification=Ref(exported.Directory+"/verify-diagnostic/native-verification.json",exported.Runs["verify-diagnostic"].NativeSha256,JsonLimit,ct)};
            _=await Run(manifest,state,views,source,"review",Job(source,views,"review",null,views.Directory+"/review",inputs),budget,ct);
            _=InspectExport(source,exported,ct);Fresh(source,bundle,tool,ct);Inventory(views.Directory,Limits(source).MaxOutputBytes,ct);_ = budget.Seconds();ct.ThrowIfCancellationRequested();
            views.Status="complete";views.CompletedAt=DateTimeOffset.UtcNow;Save(manifest,state);
            _=InspectViews(source,exported,views,ct);return Describe(state,source.Source.Parts.Input,true,true,false,false,null,null);
        }
        catch(Exception error){views.Status="failed";views.Error=error.Message[..Math.Min(4096,error.Message.Length)];views.CompletedAt=DateTimeOffset.UtcNow;Save(manifest,state);throw;}
    }
    SortedDictionary<string,string> Artifacts(Payload package,Attempt export,Attempt views,AssemblyExportReport report,CancellationToken ct)
    {
        var artifacts=new SortedDictionary<string,string>(StringComparer.Ordinal);
        void Add(string path,string hash){_=Ref(path,hash,LimitsMaximum(package),ct);artifacts.Add(workspace.Relative(path),hash);}
        foreach(var f in package.Package.Files)Add(package.Directory+"/"+f.Path,f.Sha256);
        Add(package.Manifest,package.Sha256);
        foreach(var key in new[]{"verify-rest","verify-diagnostic"})Add(export.Directory+"/"+key+"/native-verification.json",export.Runs[key].NativeSha256);
        Add(export.Directory+"/technical-review.json",PartOutputVerifier.FileHash(workspace,export.Directory+"/technical-review.json",ct,JsonLimit));
        Add(views.Directory+"/review/output/report.json",views.Runs["review"].ReportSha256);
        foreach(var file in report.Outputs)Add(views.Directory+"/review/output/"+file.Path,file.Sha256);
        return artifacts;
    }
    static long LimitsMaximum(Payload package)=>Math.Min(ExportPackageValidator.MaximumBytes,package.Package.Files.Sum(f=>f.Bytes)+JsonLimit);
    SchematicModelReview ReadDecision(Decision decision,Payload package,Attempt export,Attempt views,SortedDictionary<string,string> artifacts,string? independent,CancellationToken ct)
    {
        _=Ref(decision.Source,decision.Sha256,JsonLimit,ct);_=Ref(decision.Snapshot,decision.Sha256,JsonLimit,ct);
        var review=StrictDesignJson.Read<SchematicModelReview>(workspace.Resolve(decision.Snapshot));RequireDecision(review,package,export,views,artifacts,independent);
        if(review.Kind!=decision.Kind||review.Reviewer!=decision.Reviewer||review.Decision!=decision.DecisionValue||review.IndependentReviewSha256!=decision.IndependentReviewSha256)
            throw new InvalidOperationException("Saved model decision differs from submitted evidence");return review;
    }
    static void RequireDecision(SchematicModelReview r,Payload package,Attempt export,Attempt views,SortedDictionary<string,string> artifacts,string? independent)
    {
        _=Person(r.Reviewer);
        if(r.SchemaVersion!="bubo-schematic-model-review/v1"||r.Kind is not ("independent-model" or "human-model")||r.Decision is not ("approved" or "rejected")
            ||string.IsNullOrWhiteSpace(r.Reason)||r.Reason.Length>8192||string.IsNullOrWhiteSpace(r.Role)||r.Role.Length>128
            ||r.ModelSha256!=package.Package.Files.Single(f=>f.Path=="assembly.glb").Sha256||r.PackageSha256!=package.Sha256
            ||r.RestVerificationSha256!=export.Runs["verify-rest"].NativeSha256||r.DiagnosticVerificationSha256!=export.Runs["verify-diagnostic"].NativeSha256
            ||r.ViewsSha256!=views.Runs["review"].ReportSha256||r.ReviewedArtifacts.Count!=artifacts.Count
            ||artifacts.Any(pair=>r.ReviewedArtifacts.GetValueOrDefault(pair.Key)!=pair.Value))throw new InvalidOperationException("Model decision must cover the exact complete delivered package, technical certificates and review artifacts");
        if(r.Kind=="independent-model"&&(!r.Independent||string.Equals(r.Reviewer,export.Author,StringComparison.OrdinalIgnoreCase)||r.IndependentReviewSha256 is not null)
            ||r.Kind=="human-model"&&(!r.Human||independent is null||r.IndependentReviewSha256!=independent))throw new InvalidOperationException("Independent and human model acceptance remain separate current decisions");
    }
    (bool Independent,bool Human,Decision? Review) Decisions(State state,Payload package,Attempt export,Attempt views,SortedDictionary<string,string> artifacts,CancellationToken ct)
    {
        var independent=state.Decisions.LastOrDefault(d=>d.Kind=="independent-model"&&d.PackageSha256==package.Sha256&&d.ViewsSha256==views.Runs["review"].ReportSha256);
        var accepted=independent is not null&&ReadDecision(independent,package,export,views,artifacts,null,ct).Decision=="approved";
        var human=state.Decisions.LastOrDefault(d=>d.Kind=="human-model"&&d.PackageSha256==package.Sha256&&d.ViewsSha256==views.Runs["review"].ReportSha256);
        var humanAccepted=accepted&&human is not null&&human.IndependentReviewSha256==independent!.Sha256
            &&ReadDecision(human,package,export,views,artifacts,independent.Sha256,ct).Decision=="approved";
        return(accepted,humanAccepted,accepted?independent:null);
    }
    public object Record(string manifest,string kind,string reviewer,string evidence,CancellationToken ct=default)
    {
        if(kind is not ("independent-model" or "human-model"))throw new InvalidOperationException("Unknown schematic model review kind");
        _=design.InspectParts(manifest,ct);using var ownership=workspace.Lock(LockPath(manifest));var source=Snapshot(manifest,ct);var state=Load(manifest,source.Source.Parts.Input);
        var export=Latest(state,"export")??throw new InvalidOperationException("Current export required");var package=InspectExport(source,export,ct);
        var views=Latest(state,"review")??throw new InvalidOperationException("Complete model review views required");var report=InspectViews(source,export,views,ct).Report;
        var artifacts=Artifacts(package,export,views,report,ct);var decisions=Decisions(state,package,export,views,artifacts,ct);
        evidence=workspace.Relative(evidence);var suffix="/schematic-to-model/"+state.Asset+"/"+state.Version+"/manifest.json";var relative=workspace.Relative(manifest);
        if(!relative.EndsWith(suffix,StringComparison.Ordinal)||!evidence.StartsWith(relative[..^suffix.Length]+"/",StringComparison.Ordinal))throw new InvalidOperationException("Model review evidence must be under the active task state root");
        workspace.HostContext?.RequireReference(evidence);var bytes=StrictDesignJson.ReadBytes(workspace.Resolve(evidence));var review=StrictDesignJson.Parse<SchematicModelReview>(bytes);
        RequireDecision(review,package,export,views,artifacts,kind=="human-model"?decisions.Review?.Sha256:null);
        if(review.Kind!=kind||review.Reviewer!=reviewer)throw new InvalidOperationException("Submitted review kind/reviewer differs");
        if(state.Decisions.Count>=1024)throw new InvalidOperationException("Model decision history is full");
        var hash=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));var snapshot=Parent(manifest)+"model-review-evidence/"+hash+".json";
        if(!File.Exists(workspace.Resolve(snapshot)))runner.WriteNew(snapshot,bytes);else _=Ref(snapshot,hash,JsonLimit,ct);
        _=Ref(evidence,hash,JsonLimit,ct);Fresh(source,export.Bundle,export.Toolchain,ct);_=InspectPayload(source,export,ct);
        state.Decisions.Add(new(kind,reviewer,review.Decision,evidence,snapshot,hash,package.Sha256,views.Runs["review"].ReportSha256,review.IndependentReviewSha256));Save(manifest,state);
        decisions=Decisions(state,package,export,views,artifacts,ct);var handoff=decisions.Review is null?null:WriteHandoff(manifest,package,export,decisions.Review,ct);
        return Describe(state,source.Source.Parts.Input,true,true,decisions.Independent,decisions.Human,artifacts,handoff);
    }
    JsonObject GltfJson(string file)
    {
        using var stream=new FileStream(workspace.Resolve(file),FileMode.Open,FileAccess.Read,FileShare.Read);Span<byte> header=stackalloc byte[20];stream.ReadExactly(header);
        var count=BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        if(BinaryPrimitives.ReadUInt32LittleEndian(header)!=0x46546c67||BinaryPrimitives.ReadUInt32LittleEndian(header[4..])!=2
            ||BinaryPrimitives.ReadUInt32LittleEndian(header[8..])!=stream.Length||BinaryPrimitives.ReadUInt32LittleEndian(header[16..])!=0x4e4f534a||count>JsonLimit||count>stream.Length-20)
            throw new InvalidOperationException("Invalid bounded portable GLB JSON");
        var bytes=new byte[checked((int)count)];stream.ReadExactly(bytes);return StrictDesignJson.Parse<JsonObject>(bytes);
    }
    JsonObject Handoff(string manifest,Payload package,Attempt export,Decision independent,CancellationToken ct)
    {
        JsonObject Reference(string path,string hash)=>new(){["path"]=workspace.Relative(path),["sha256"]=hash};
        var model=package.Package.Files.Single(f=>f.Path=="assembly.glb");var gltf=GltfJson(package.Directory+"/assembly.glb");
        var sourceState=new JsonObject();
        foreach(var (name,path) in new[]{("design",workspace.Relative(manifest)),("parts",Parent(manifest)+"parts.json"),("assembly",Parent(manifest)+"assembly.json"),("export",StatePath(manifest))})
            sourceState[name]=Reference(path,PartOutputVerifier.FileHash(workspace,path,ct,JsonLimit));
        return new JsonObject{["schemaVersion"]="bubo-mechanical-assembly-handoff/v1",["package"]=Reference(package.Manifest,package.Sha256),
            ["model"]=Reference(package.Directory+"/assembly.glb",model.Sha256),
            ["design"]=Reference(package.Directory+"/assembly-design.json",package.Package.Files.Single(f=>f.Path=="assembly-design.json").Sha256),
            ["metadata"]=Reference(package.Directory+"/assembly-metadata.json",package.Package.Files.Single(f=>f.Path=="assembly-metadata.json").Sha256),
            ["provenance"]=Reference(package.Directory+"/provenance/assembly-report.json",package.Package.Files.Single(f=>f.Path=="provenance/assembly-report.json").Sha256),
            ["technicalReview"]=Reference(export.Directory+"/technical-review.json",PartOutputVerifier.FileHash(workspace,export.Directory+"/technical-review.json",ct,JsonLimit)),
            ["independentReview"]=Reference(independent.Snapshot,independent.Sha256),["sourceState"]=sourceState,["components"]=JsonSerializer.SerializeToNode(package.Package.Components,Compact),
            ["bindings"]=new JsonObject{["nodes"]=gltf["nodes"]!.DeepClone(),["skins"]=(gltf["skins"]??new JsonArray()).DeepClone()}};
    }
    string WriteHandoff(string manifest,Payload package,Attempt export,Decision independent,CancellationToken ct)
    {
        var handoff=Handoff(manifest,package,export,independent,ct);
        var path=Parent(manifest)+"mechanical-handoffs/"+Digest(handoff)+".json";
        if(!File.Exists(workspace.Resolve(path)))WriteJson(path,handoff,ct);else if(!JsonNode.DeepEquals(StrictDesignJson.Read<JsonObject>(workspace.Resolve(path)),handoff))throw new InvalidOperationException("Immutable mechanical handoff changed");
        ct.ThrowIfCancellationRequested();return path;
    }
    public object Status(string manifest,CancellationToken ct=default)
    {
        _=design.InspectParts(manifest,ct);using var ownership=workspace.Lock(LockPath(manifest));var input=design.InspectParts(manifest,ct);var state=Load(manifest,input);
        var export=Latest(state,"export");var views=Latest(state,"review");var valid=false;var viewed=false;var independent=false;var human=false;string? handoff=null;string? unavailable=null;
        SortedDictionary<string,string>? artifacts=null;
        if(export is {Status:"complete"}&&input.Approved)
        {
            SourceSnapshot? source=null;
            try{source=Snapshot(manifest,ct);}catch(InvalidOperationException error){unavailable=error.Message;}
            if(source is not null&&export.Identity==Identity(source.Source))
            {
                var package=InspectExport(source,export,ct);valid=true;
                if(views is {Status:"complete"}&&views.ExportDirectory==export.Directory&&views.PackageSha256==package.Sha256)
                {
                    var report=InspectViews(source,export,views,ct).Report;viewed=true;artifacts=Artifacts(package,export,views,report,ct);
                    var decisions=Decisions(state,package,export,views,artifacts,ct);independent=decisions.Independent;human=decisions.Human;
                    if(decisions.Review is not null)
                    {
                        // Read-only status checks the exact current descriptor and never manufactures one.
                        var expected=Handoff(manifest,package,export,decisions.Review,ct);var candidate=Parent(manifest)+"mechanical-handoffs/"+Digest(expected)+".json";
                        if(File.Exists(workspace.Resolve(candidate)))
                        {
                            if(!JsonNode.DeepEquals(StrictDesignJson.Read<JsonObject>(workspace.Resolve(candidate)),expected))throw new InvalidOperationException("Current mechanical handoff changed");
                            handoff=candidate;
                        }
                    }
                }
                Fresh(source,export.Bundle,export.Toolchain,ct);
            }
            else unavailable??="Current approval, parts, assembly or diagnostic evidence invalidated prior export";
        }
        var result=JsonSerializer.SerializeToNode(Describe(state,input,valid,viewed,independent,human,artifacts,handoff),Compact)!.AsObject();result["unavailable"]=unavailable;return result;
    }
    public object Doctor(string manifest,string? blender,CancellationToken ct=default)
    {
        var input=design.InspectParts(manifest,ct);var unavailable=new List<string>();AssemblyExportToolchain? tool=null;
        if(!input.Approved||input.Design.SchemaVersion!="bubo-assembly-design/v2")unavailable.Add("Current complete approved v2 assembly design required");
        try{runner.RequireBudget();_=runner.Resources(Scripts);}catch(Exception e)when(e is InvalidOperationException or IOException or PlatformNotSupportedException){unavailable.Add(e.Message);}
        if(blender is null)unavailable.Add("Explicit existing registered Blender executable required");else try{workspace.HostContext?.EnsureToolAllowed(blender);tool=AssemblyGltfToolchain.Capture(blender,runner.ToolHash(blender),ct);}
        catch(Exception e)when(e is InvalidOperationException or IOException){unavailable.Add(e.Message);}
        var currentSource=false;
        if(input.Approved&&input.Design.SchemaVersion=="bubo-assembly-design/v2")
        {
            using var ownership=workspace.Lock(LockPath(manifest));
            try{var source=Snapshot(manifest,ct);if(blender is not null)_=Tool(source,blender,ct);currentSource=true;}
            catch(Exception e)when(e is InvalidOperationException or IOException){unavailable.Add("Current independently verified parts, assembly and complete motion proof required: "+e.Message);}
        }
        workspace.HostContext?.ValidateFresh();return new{schemaVersion="bubo-schematic-export-doctor/v1",declared=true,nativeReadiness="unverified",toolchainFingerprint=tool?.Fingerprint,unavailable,
            currentSourceVerified=currentSource,designEligible=input.Approved&&input.Design.SchemaVersion=="bubo-assembly-design/v2",
            provisioningRequired=false,toolProbePerformed=false,modelExportAvailable=currentSource&&unavailable.Count==0,humanModelAccepted=false,runtimeAcceptance=false};
    }
    static object Describe(State state,DesignSession.PartInput input,bool exported,bool viewed,bool independent,bool human,SortedDictionary<string,string>? artifacts,string? handoff)
    {
        var package=Latest(state,"export");var views=Latest(state,"review");
        return new{schemaVersion="bubo-schematic-export-status/v1",state.Asset,state.Version,phase="portable-assembly-export",wholeAssemblyApproved=input.Approved,
            currentExportValid=exported,portableVerified=exported,packageManifest=exported?package!.Directory+"/package/package.json":null,packageSha256=exported?package!.PackageSha256:null,
            restVerification=exported?package!.Directory+"/verify-rest/native-verification.json":null,restVerificationSha256=exported?package!.Runs["verify-rest"].NativeSha256:null,
            diagnosticVerification=exported?package!.Directory+"/verify-diagnostic/native-verification.json":null,diagnosticVerificationSha256=exported?package!.Runs["verify-diagnostic"].NativeSha256:null,
            currentViewsValid=viewed,views=viewed?views!.Directory+"/review/output/report.json":null,viewsSha256=viewed?views!.Runs["review"].ReportSha256:null,
            independentModelReviewed=independent,humanModelAccepted=human,runtimeAcceptance=false,mechanicalHandoff=handoff,reviewArtifacts=artifacts,
            exports=state.Attempts.Count(a=>a.Operation=="export"),viewAttempts=state.Attempts.Count(a=>a.Operation=="review"),lastExportStatus=package?.Status,lastExportError=package?.Error,
            lastViewStatus=views?.Status,lastViewError=views?.Error,continuousCollisionCertified=false};
    }
}
