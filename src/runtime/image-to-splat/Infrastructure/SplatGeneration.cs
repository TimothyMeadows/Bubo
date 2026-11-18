using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Bubo.Splats.Application;
using Bubo.Splats.Domain;
using Hephaera.AssetProduction;

namespace Bubo.Splats.Infrastructure;

public sealed record SplatPackageVariant(SplatSource Artifact,SplatSource Payload,GaussianInspectionResult Inspection);
public sealed record SplatGeneratedPackage(string SchemaVersion,string Task,string RunId,string CatalogSha256,string RuntimeBuildSha256,
    string ResourceCatalogSha256,SplatSource Installation,string EnvironmentSha256,SplatSource Request,SplatSource Source,
    SplatSource WorkerResult,SplatSource Claim,string? CandidateClaimSha256,SplatPackageVariant[] Variants,string HumanReview,bool Promotion);

/// <summary>Fixed native orchestration. No provider selection, licence acceptance, retry or promotion.</summary>
public sealed class SplatGeneration(Workspace workspace,AuditedResources resources,INativeProcess process,string buildSha256,string resourceSha256,ISplatInstallationVerifier? installationVerifier=null)
{
    public async Task<object> Generate(string path,CancellationToken cancellation)
    {
        var host=workspace.HostContext??throw new InvalidOperationException("Host context required");
        host.ValidateFresh();
        if(host.InspectionOnly || host.MediaOnly || host.Pipeline!="LOCAL")throw new InvalidOperationException("Explicit full-art LOCAL context required");
        var request=SplatJson.Read<SplatGenerationRequest>(workspace,path);SplatGenerationRules.Validate(request);
        var requestHash=workspace.HashProjectFile(path);
        if(request.Task!=host.TaskId || request.Request.StyleSha256!=host.StyleSha256)throw new InvalidOperationException("Generation task/STYLE differs from host");
        var budget=request.Request.Budgets;
        if(budget.TimeoutSeconds>host.TimeoutSeconds || budget.MaxLogBytes>host.MaxLogBytes
            || host.MaxPrivateBytes is null || budget.MaxProcessMemoryBytes>host.MaxPrivateBytes || !process.SupportsPrivateMemoryLimit)
            throw new InvalidOperationException("Explicit supported host-owned committed-memory/time/log ceilings required before launch");
        var installed=(installationVerifier??new SplatInstallation(workspace,resources)).Verify(request.Installation);
        var ledger=new SplatLedger(workspace);_=ledger.Admit(request,installed.CatalogSha256);
        var state=host.StateRoot+"/image-to-splat/runs/"+request.Task+"/"+request.RunId;
        var output=host.StagingRoot+"/image-to-splat/"+request.Task+"/"+request.RunId;
        var cache=state+"/cache";
        var source=workspace.Resolve(request.Request.Input.Path);
        using var weightLease=new ReadOnlyWeights(workspace,installed,cancellation);
        var maximumPayload=request.Request.GaussianCounts.Sum(x=>(long)x*68+65536);
        if(new FileInfo(source).Length>request.Limits.MaxOutputBytes-maximumPayload-2097152)
            throw new InvalidOperationException("Original source plus payloads/metadata exceed total package byte budget");
        foreach(var root in installed.Roots)
            if(Overlap(workspace.Resolve(state),root) || Overlap(workspace.Resolve(output),root)
                || Overlap(workspace.Resolve(host.MediaStateRoot),root) || Within(source,root))
                throw new InvalidOperationException("Generation source/state/staging must be separate from immutable installation roots");
        if(Overlap(workspace.Resolve(state),workspace.Resolve(output)) || Within(source,workspace.Resolve(state)) || Within(source,workspace.Resolve(output)))
            throw new InvalidOperationException("Generation input/state/staging overlap");
        if(Directory.Exists(workspace.Resolve(state)) || File.Exists(workspace.Resolve(state))
            || Directory.Exists(workspace.Resolve(output)) || File.Exists(workspace.Resolve(output)))
            throw new InvalidOperationException("Run identity already exists; preserve interrupted state and select a fresh run ID");
        // The source snapshot and prepared state do not consume an attempt. The immutable claim
        // is created under the GPU lease immediately before native start; uncertainty stays spent.
        host.EnsureWriteAllowed(state);host.EnsureWriteAllowed(output);
        Directory.CreateDirectory(workspace.ResolveWrite(Path.GetDirectoryName(output)!));
        using var sourceInput=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(sourceInput.Length<1 || sourceInput.Length>budget.MaxInputBytes || BoundHash(source,budget.MaxInputBytes,cancellation)!=request.Request.Input.Sha256)
            throw new InvalidOperationException("Source is missing, oversized or differs from approval");
        var snapshot=state+"/source.img";
        Directory.CreateDirectory(workspace.ResolveWrite(state));
        using(var target=new FileStream(workspace.ResolveWrite(snapshot),FileMode.CreateNew,FileAccess.Write,FileShare.Read))
        { CopyBounded(sourceInput,target,budget.MaxInputBytes,cancellation);target.Flush(true); }
        if(workspace.HashProjectFile(snapshot)!=request.Request.Input.Sha256)throw new InvalidOperationException("Source snapshot identity differs");
        Copy(path,state+"/request.json",65536,cancellation);
        if(workspace.HashProjectFile(state+"/request.json")!=requestHash)throw new InvalidOperationException("Request snapshot differs from admitted bytes");
        var worker=resources.Resolve("resources/image-to-splat/worker.py");
        var job=state+"/job.json";var result=state+"/worker-result.json";
        SplatJson.WriteNew(workspace,job,new {schemaVersion="bubo-splat-worker/v1",operation="generate",projectRoot=host.Root,
            input=new {path=workspace.Resolve(snapshot),sha256=request.Request.Input.Sha256},sourceDirectory=installed.SourceDirectory,
            modelDirectory=installed.ModelDirectory,outputDirectory=workspace.ResolveWrite(output),cacheDirectory=workspace.ResolveWrite(cache),
            settings=new {request.Request.Seed,request.Settings.Steps,request.Settings.Guidance,request.Settings.Shift,request.Settings.ErodeRadius,request.Request.GaussianCounts},
            limits=new {budget.MaxInputBytes,request.Limits.MaxInputPixels,request.Limits.MaxInputEdge,request.Limits.MaxAspectRatio,
                request.Limits.MaxOutputBytes,request.Limits.MaxGpuAllocatedBytes,request.Limits.MinGpuFreeBytes}});
        var jobHash=workspace.HashProjectFile(job);
        var catalogFile=resources.Resolve("resources/image-to-splat/catalog.json");
        var resourceCatalogFile=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(catalogFile)!,"..","catalog.json"));
        var inputs=installed.Inputs.Concat(new[]{new GpuInputIdentity(worker,resources.Hash("resources/image-to-splat/worker.py")),
            new GpuInputIdentity(catalogFile,resources.Hash("resources/image-to-splat/catalog.json")),
            new GpuInputIdentity(resourceCatalogFile,resources.CatalogSha256),
            new GpuInputIdentity(job,jobHash),new GpuInputIdentity(snapshot,request.Request.Input.Sha256),
            new GpuInputIdentity(request.Request.Input.Path,request.Request.Input.Sha256),new GpuInputIdentity(path,requestHash),
            new GpuInputIdentity(request.Authorization.Path,request.Authorization.Sha256)}).ToArray();
        var environment=new Dictionary<string,string> { ["CUDA_VISIBLE_DEVICES"]="0",["CUDA_CACHE_PATH"]=workspace.ResolveWrite(cache+"/cuda"),
            ["TEMP"]=workspace.ResolveWrite(cache+"/tmp"),["TMP"]=workspace.ResolveWrite(cache+"/tmp"),["PYTHONDONTWRITEBYTECODE"]="1",
            ["TORCH_HOME"]=workspace.ResolveWrite(cache+"/torch"),["HF_HOME"]=workspace.ResolveWrite(cache+"/hf"),
            ["HF_HUB_OFFLINE"]="1",["TRANSFORMERS_OFFLINE"]="1" };
        var spec=new GpuProcessSpec(installed.Python,["-I","-B","-X","utf8",worker,workspace.Resolve(job),workspace.ResolveWrite(result),jobHash],
            host.Root,workspace.ResolveWrite(state+"/worker.log"),budget.TimeoutSeconds,budget.MaxLogBytes,budget.MaxProcessMemoryBytes,
            environment,inputs,MaximumWaitSeconds:Math.Min(300,budget.TimeoutSeconds));
        string? claim=null;
        try
        {
            await process.RunGpu(spec,cancellation,token=>
            {
                token.ThrowIfCancellationRequested();host.ValidateFresh();
                // Guardian already reverified all installed inputs under ownership. Verify the
                // small policy/descriptor again before committing the irrevocable attempt.
                if(workspace.HashProjectFile(path)!=requestHash)throw new InvalidOperationException("Generation request changed in queue");
                if(Directory.Exists(workspace.Resolve(output)) || File.Exists(workspace.Resolve(output)))throw new InvalidOperationException("Output appeared while queued");
                claim=ledger.Claim(request,requestHash,installed.CatalogSha256);
                SplatJson.WriteNew(workspace,state+"/started.json",new {schemaVersion="bubo-splat-started/v1",claim,hostContextSha256=host.ContextSha256,startedAt=DateTimeOffset.UtcNow});
                return Task.CompletedTask;
            });
            cancellation.ThrowIfCancellationRequested();host.ValidateFresh();
            if(claim is null)throw new InvalidOperationException("Native execution returned without its source-bound claim");
            _=(installationVerifier??new SplatInstallation(workspace,resources)).Verify(request.Installation);
            var report=SplatJson.ReadFile<JsonObject>(workspace.Resolve(result),1048576);
            ValidateReport(report,request,installed,output);
            if(workspace.HashProjectFile(request.Request.Input.Path)!=request.Request.Input.Sha256
                || workspace.HashProjectFile(path)!=requestHash)throw new InvalidOperationException("Source/request changed during generation");
            Copy(snapshot,output+"/source.img",budget.MaxInputBytes,cancellation);Copy(state+"/request.json",output+"/request.json",65536,cancellation);Copy(result,output+"/worker-result.json",1048576,cancellation);
            var packageSource=Identity(output+"/source.img",budget.MaxInputBytes,cancellation);
            var variants=new List<SplatPackageVariant>();
            var plyBudget=budget with {MaxInputBytes=Math.Max(budget.MaxInputBytes,request.Limits.MaxOutputBytes)};
            foreach(var count in request.Request.GaussianCounts)
            {
                cancellation.ThrowIfCancellationRequested();var payload=Identity(output+"/gaussian-"+count+".ply",checked((long)count*68+budget.MaxHeaderBytes),cancellation);
                var observed=report["outputs"]!.AsArray().Single(x=>x!["gaussianCount"]!.GetValue<int>()==count)!;
                if(payload.Sha256!=observed["sha256"]!.GetValue<string>() || new FileInfo(workspace.Resolve(payload.Path)).Length!=observed["bytes"]!.GetValue<long>())
                    throw new InvalidOperationException("Native payload bytes differ from worker observations");
                using var stream=File.OpenRead(workspace.Resolve(payload.Path));
                var inspected=GaussianInspection.Inspect(stream,plyBudget,payload.Sha256,cancellation);
                if(inspected.GaussianCount!=count)throw new InvalidOperationException("Decoded density differs from requested profile");
                var artifact=new SplatArtifact {SchemaVersion="bubo-splat-artifact/v1",Backend="triposplat",StyleSha256=request.Request.StyleSha256,
                    Input=packageSource,Payload=payload,Profile=SplatRules.GaussianProfile,GaussianCount=count,Coordinates="source-local",Units="uncalibrated",ScaleConfidence="unverified",
                    Ancestry=new SplatAncestry {RequestSha256=requestHash,SourceRevision=Text(report,"sourceRevision"),ModelRevision=Text(report,"modelRevision"),
                        EnvironmentSha256=installed.EnvironmentSha256,RuntimeBuildSha256=buildSha256,ResourceCatalogSha256=resourceSha256,ParentArtifactSha256=null},Budgets=plyBudget};
                SplatRules.ValidateArtifact(artifact);
                var artifactPath=output+"/artifact-"+count+".json";SplatJson.WriteNew(workspace,artifactPath,artifact);
                variants.Add(new(Identity(artifactPath),payload,inspected));
            }
            var attempt=SplatJson.ReadFile<SplatAttemptClaim>(workspace.Resolve(claim),65536);
            var package=new SplatGeneratedPackage("bubo-splat-package/v1",request.Task,request.RunId,installed.CatalogSha256,buildSha256,resourceSha256,
                request.Installation,installed.EnvironmentSha256,Identity(output+"/request.json"),packageSource,Identity(output+"/worker-result.json",1048576,cancellation),Identity(claim),
                attempt.CandidateClaimSha256,variants.ToArray(),"pending",false);
            RequireInventory(output,package,request.Limits.MaxOutputBytes,includeManifest:false);
            SplatJson.WriteNew(workspace,output+"/package.json",package);
            RequireInventory(output,package,request.Limits.MaxOutputBytes,includeManifest:true);
            var manifest=Identity(output+"/package.json");
            SplatJson.WriteNew(workspace,state+"/completed.json",new {schemaVersion="bubo-splat-completed/v1",manifest,claim,technicalOnly=true,humanReview="pending",promotion=false});
            return new {schemaVersion="bubo-splat-generation-result/v1",status="generated-staged",manifest,requestSha256=requestHash,
                outputs=variants.Count,technicalOnly=true,humanReview="pending",runtimeAcceptance=false,promotion=false};
        }
        catch(Exception error)
        {
            SplatJson.WriteNew(workspace,state+"/failed.json",new {schemaVersion="bubo-splat-failed/v1",claim,
                phase=claim is null?"before-attempt-claim":"claimed-launch-or-verification-failed",errorType=error.GetType().Name,
                attemptRetained=claim is not null,automaticRetry=false,failedAt=DateTimeOffset.UtcNow});
            throw;
        }
    }
    public object VerifyPackage(string path,CancellationToken cancellation)
    {
        var host=workspace.HostContext??throw new InvalidOperationException("Host context required");host.ValidateFresh();
        if(host.InspectionOnly || host.MediaOnly || host.Pipeline!="LOCAL")throw new InvalidOperationException("Explicit full-art LOCAL context required");
        var package=SplatJson.Read<SplatGeneratedPackage>(workspace,path);
        if(package.SchemaVersion!="bubo-splat-package/v1" || package.Task!=host.TaskId || package.RuntimeBuildSha256!=buildSha256
            || package.ResourceCatalogSha256!=resourceSha256 || package.HumanReview!="pending" || package.Promotion)
            throw new InvalidOperationException("Package execution identity or technical-only boundary differs");
        var installation=(installationVerifier??new SplatInstallation(workspace,resources)).Verify(package.Installation);
        if(package.CatalogSha256!=installation.CatalogSha256 || package.EnvironmentSha256!=installation.EnvironmentSha256)
            throw new InvalidOperationException("Package dependency identity differs");
        var request=SplatJson.ReadIdentity<SplatGenerationRequest>(workspace.Resolve(package.Request.Path),package.Request.Sha256);
        SplatGenerationRules.Validate(request);
        if(request.Task!=package.Task || request.RunId!=package.RunId || request.Request.StyleSha256!=host.StyleSha256
            || request.Request.Input.Sha256!=package.Source.Sha256 || request.Installation!=package.Installation)
            throw new InvalidOperationException("Package request/source/installation differs");
        var output=host.StagingRoot+"/image-to-splat/"+package.Task+"/"+package.RunId;
        if(workspace.Relative(path)!=output+"/package.json")throw new InvalidOperationException("Package is outside its canonical immutable staging directory");
        RequireInventory(output,package,request.Limits.MaxOutputBytes,includeManifest:true);
        foreach(var identity in new[]{package.Source,package.Request,package.WorkerResult,package.Claim}.Concat(package.Variants.SelectMany(x=>new[]{x.Artifact,x.Payload})))
        {
            cancellation.ThrowIfCancellationRequested();SplatRules.ValidateSource(identity);
            var maximum=identity==package.Source?request.Request.Budgets.MaxInputBytes:identity==package.WorkerResult?1048576L:
                package.Variants.Any(x=>x.Payload==identity)?checked((long)request.Request.Budgets.MaxGaussians*68+request.Request.Budgets.MaxHeaderBytes):65536L;
            if(BoundHash(workspace.Resolve(identity.Path),maximum,cancellation)!=identity.Sha256)throw new InvalidOperationException("Package file changed");
        }
        if(package.Source.Path!=output+"/source.img" || package.Request.Path!=output+"/request.json" || package.WorkerResult.Path!=output+"/worker-result.json")
            throw new InvalidOperationException("Canonical package files differ");
        var ledger=new SplatLedger(workspace);_=ledger.Admit(request,installation.CatalogSha256);
        var claimPath=ledger.Root+"/"+package.Source.Sha256+"/"+request.AttemptKind+".json";
        if(package.Claim.Path!=claimPath)throw new InvalidOperationException("Claim path differs from shared source ledger");
        var claim=SplatJson.ReadFile<SplatAttemptClaim>(workspace.Resolve(claimPath),65536);
        if(request.AttemptKind=="revision")
        {
            var candidatePath=ledger.Root+"/"+package.Source.Sha256+"/candidate.json";
            var candidate=SplatJson.ReadFile<SplatAttemptClaim>(workspace.Resolve(candidatePath),65536);
            if(candidate.SchemaVersion!="bubo-splat-attempt-claim/v1" || candidate.SourceSha256!=package.Source.Sha256 || candidate.Kind!="candidate"
                || candidate.ApprovalSha256!=request.Authorization.Sha256 || workspace.HashProjectFile(candidatePath)!=package.CandidateClaimSha256)
                throw new InvalidOperationException("Original candidate is missing, changed or unrelated to revision");
        }
        if(claim.SchemaVersion!="bubo-splat-attempt-claim/v1" || claim.SourceSha256!=package.Source.Sha256 || claim.Kind!=request.AttemptKind
            || claim.Task!=package.Task || claim.RunId!=package.RunId || claim.RequestSha256!=package.Request.Sha256
            || claim.ApprovalSha256!=request.Authorization.Sha256 || claim.CandidateClaimSha256!=package.CandidateClaimSha256)
            throw new InvalidOperationException("Package has broken candidate/revision ancestry");
        var report=SplatJson.ReadFile<JsonObject>(workspace.Resolve(package.WorkerResult.Path),1048576);
        ValidateReport(report,request,installation,output);
        if(package.Variants is null || package.Variants.Length!=request.Request.GaussianCounts.Length)
            throw new InvalidOperationException("Package variants differ");
        for(var index=0;index<package.Variants.Length;index++)
        {
            var count=request.Request.GaussianCounts[index];var variant=package.Variants[index];
            if(variant.Payload.Path!=output+"/gaussian-"+count+".ply" || variant.Artifact.Path!=output+"/artifact-"+count+".json")
                throw new InvalidOperationException("Package variant identity differs");
            var artifact=SplatJson.ReadIdentity<SplatArtifact>(workspace.Resolve(variant.Artifact.Path),variant.Artifact.Sha256);
            SplatRules.ValidateArtifact(artifact);
            if(artifact.Payload!=variant.Payload || artifact.Input!=package.Source || artifact.GaussianCount!=count
                || artifact.Ancestry.RequestSha256!=package.Request.Sha256 || artifact.Ancestry.EnvironmentSha256!=package.EnvironmentSha256
                || artifact.Ancestry.RuntimeBuildSha256!=buildSha256 || artifact.Ancestry.ResourceCatalogSha256!=resourceSha256
                || artifact.Ancestry.SourceRevision!=Text(report,"sourceRevision") || artifact.Ancestry.ModelRevision!=Text(report,"modelRevision")
                || artifact.Ancestry.ParentArtifactSha256 is not null || artifact.StyleSha256!=host.StyleSha256)
                throw new InvalidOperationException("Artifact differs from verified package ancestry");
            using var input=File.OpenRead(workspace.Resolve(variant.Payload.Path));
            var actual=GaussianInspection.Inspect(input,artifact.Budgets,variant.Payload.Sha256,cancellation);
            if(SplatJson.Hash(actual)!=SplatJson.Hash(variant.Inspection))throw new InvalidOperationException("Independent payload inspection differs");
            var native=report["outputs"]![index]!;
            if(native["sha256"]!.GetValue<string>()!=actual.Sha256 || native["bytes"]!.GetValue<long>()!=actual.FileBytes)
                throw new InvalidOperationException("Worker receipt differs from independently reloaded Gaussian data");
        }
        host.ValidateFresh();cancellation.ThrowIfCancellationRequested();
        return new {schemaVersion="bubo-splat-package-verification/v1",valid=true,technicalOnly=true,variants=package.Variants.Length,
            constructionRecordsVerified=true,historicalComponentAncestryIndependentlyVerified=false,humanReview="pending",runtimeAcceptance=false,promotion=false,gpuUsed=false};
    }
    SplatSource Identity(string path,long maximum=65536,CancellationToken cancellation=default)=>new() {Path=workspace.Relative(path),Sha256=BoundHash(workspace.Resolve(path),maximum,cancellation)};
    static string BoundHash(string full,long maximum,CancellationToken cancellation)
    {
        if((File.GetAttributes(full)&(FileAttributes.ReparsePoint|FileAttributes.Directory|FileAttributes.Device))!=0)
            throw new InvalidOperationException("Only regular unlinked files may be hashed");
        using var input=new FileStream(full,FileMode.Open,FileAccess.Read,FileShare.Read);
        var initial=input.Length;if(initial<1 || initial>maximum)throw new InvalidOperationException("File exceeds finite identity byte bound before hashing");
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var buffer=new byte[65536];long total=0;
        while(true)
        {
            cancellation.ThrowIfCancellationRequested();var read=input.Read(buffer.AsSpan(0,(int)Math.Min(buffer.Length,initial-total+1)));
            if(read==0)break;total+=read;if(total>initial)throw new InvalidOperationException("File grew while hashing");hash.AppendData(buffer.AsSpan(0,read));
        }
        if(total!=initial || input.Length!=initial)throw new InvalidOperationException("File changed while hashing");
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    static void CopyBounded(Stream source,Stream destination,long maximum,CancellationToken cancellation)
    {
        var initial=source.Length;if(initial<1 || initial>maximum)throw new InvalidOperationException("Copy source exceeds its finite byte bound");
        var buffer=new byte[65536];long total=0;
        while(true)
        {
            cancellation.ThrowIfCancellationRequested();var read=source.Read(buffer.AsSpan(0,(int)Math.Min(buffer.Length,initial-total+1)));
            if(read==0)break;total+=read;if(total>initial)throw new InvalidOperationException("Copy source grew");destination.Write(buffer.AsSpan(0,read));
        }
        if(total!=initial || source.Length!=initial)throw new InvalidOperationException("Copy source changed");
    }
    void Copy(string from,string to,long maximum,CancellationToken cancellation)
    {
        var target=workspace.ResolveWrite(to);
        using var source=File.OpenRead(workspace.Resolve(from));using var destination=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.Read);
        CopyBounded(source,destination,maximum,cancellation);destination.Flush(true);
    }
    void RequireInventory(string root,SplatGeneratedPackage package,long max,bool includeManifest)
    {
        var expected=package.Variants.SelectMany(x=>new[]{x.Artifact.Path,x.Payload.Path}).Concat(new[]{package.Source.Path,package.Request.Path,package.WorkerResult.Path}).ToHashSet(StringComparer.Ordinal);
        if(includeManifest)expected.Add(root+"/package.json");
        long total=0;var actual=new HashSet<string>(StringComparer.Ordinal);
        foreach(var full in Directory.EnumerateFileSystemEntries(workspace.Resolve(root)))
        {
            var path=workspace.Relative(full);
            if((File.GetAttributes(workspace.Resolve(path))&(FileAttributes.ReparsePoint|FileAttributes.Directory|FileAttributes.Device))!=0)
                throw new InvalidOperationException("Only unlinked regular package files permitted");
            if(!expected.Contains(path) || actual.Count>=expected.Count || !actual.Add(path))
                throw new InvalidOperationException("Package contains an unexpected/duplicate file");
            total=checked(total+new FileInfo(full).Length);
            if(total>max)throw new InvalidOperationException("Complete staged package exceeds total byte bound");
        }
        if(!actual.SetEquals(expected))throw new InvalidOperationException("Package has missing/extra files");
    }
    void ValidateReport(JsonObject report,SplatGenerationRequest request,PreparedSplatInstallation installed,string output)
    {
        if(Text(report,"schemaVersion")!="bubo-splat-worker-result/v1" || Text(report,"status")!="succeeded" || Text(report,"operation")!="generate"
            || Text(report,"backend")!="triposplat" || Text(report,"inputSha256")!=request.Request.Input.Sha256
            || Text(report,"sourceRevision")!="d8db9e018b413dd9c4a9fe22463781bf98e8e68d"
            || Text(report,"modelRevision")!="56a96e603204ec410c4da60c13ea4fa09a2169a9"
            || report["modelLoads"]!.GetValue<int>()!=1 || report["stochasticInferences"]!.GetValue<int>()!=1
            || report["networkAttempts"]!.GetValue<int>()!=0 || report["promotion"]!.GetValue<bool>() || Text(report,"humanReview")!="pending")
            throw new InvalidOperationException("Actual worker identity/candidate observations differ");
        var allowed="schemaVersion status operation backend sourceRevision modelRevision inputSha256 settings python versions device preprocessing loadingSeconds preprocessingSeconds encodingSeconds inferenceSeconds unloadingSeconds densityDecoding conditioningSha256 latentSha256 execution outputs modelLoads stochasticInferences peakTorchAllocatedBytes peakTorchReservedBytes cleanup networkAttempts failureStage failure humanReview promotion aggregateDriverMemoryMeasured loadedModules cacheObservations memoryObservations".Split(' ').ToHashSet(StringComparer.Ordinal);
        if(report.Any(x=>!allowed.Contains(x.Key)) || allowed.Where(x=>x!="aggregateDriverMemoryMeasured").Any(x=>!report.ContainsKey(x))
            || report["failureStage"] is not null || report["failure"] is not null)
            throw new InvalidOperationException("Incomplete or unknown native result fields");
        var settings=report["settings"]!.AsObject();
        if(settings["seed"]!.GetValue<long>()!=request.Request.Seed || settings["steps"]!.GetValue<int>()!=request.Settings.Steps
            || settings["guidance"]!.GetValue<double>()!=request.Settings.Guidance || settings["shift"]!.GetValue<double>()!=request.Settings.Shift
            || settings["erodeRadius"]!.GetValue<int>()!=request.Settings.ErodeRadius
            || !settings["gaussianCounts"]!.AsArray().Select(x=>x!.GetValue<int>()).SequenceEqual(request.Request.GaussianCounts))
            throw new InvalidOperationException("Actual sampling settings differ from approved request");
        foreach(var name in new[]{"loadingSeconds","preprocessingSeconds","encodingSeconds","inferenceSeconds","unloadingSeconds"})
        { var value=report[name]!.GetValue<double>();if(!double.IsFinite(value) || value<0 || value>request.Request.Budgets.TimeoutSeconds)throw new InvalidOperationException("Invalid stage timing"); }
        foreach(var name in new[]{"conditioningSha256","latentSha256"})SplatRules.ValidateHash(Text(report,name));
        var cleanup=report["cleanup"]!.AsObject();
        if(cleanup["endingAllocatedBytes"]!.GetValue<long>()!=0 || cleanup["endingReservedBytes"]!.GetValue<long>()!=0)
            throw new InvalidOperationException("Worker did not release its Torch allocations/reservations");
        var allocated=report["peakTorchAllocatedBytes"]!.GetValue<long>();var reserved=report["peakTorchReservedBytes"]!.GetValue<long>();
        if(allocated<1 || allocated>request.Limits.MaxGpuAllocatedBytes || reserved<allocated)
            throw new InvalidOperationException("Measured Torch GPU allocation exceeds explicit profile");
        var device=report["device"]!.AsObject();
        if(Text(device,"type")!="cuda" || device["index"]!.GetValue<int>()!=0 || string.IsNullOrWhiteSpace(Text(device,"name"))
            || device["initialFreeBytes"]!.GetValue<long>()<request.Limits.MinGpuFreeBytes)
            throw new InvalidOperationException("Actual selected GPU/free headroom differs");
        var python=report["python"]!.AsObject();if(Text(python,"version")!="3.12.16" || !string.Equals(Path.GetFullPath(Text(python,"executable")),Path.GetFullPath(installed.Python),OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))
            throw new InvalidOperationException("Actual dedicated Python differs");
        var versions=report["versions"]!.AsObject();
        var versionNames=new[]{"torch","torchvision","numpy","safetensors","pillow","tqdm"};
        Keys(versions,versionNames);
        var catalog=SplatJson.ReadFile<JsonObject>(resources.Resolve("resources/image-to-splat/catalog.json"));
        foreach(var name in versionNames)
        {
            var expected=catalog["environmentCandidates"]!["win-amd64"]!["artifacts"]!.AsArray().Single(x=>x!["name"]!.GetValue<string>().ToLowerInvariant()==name)!;
            if(Text(versions,name)!=expected["version"]!.GetValue<string>())throw new InvalidOperationException("Actual pinned dependency version differs");
        }
        var execution=report["execution"]!.AsObject();Keys(execution,["device","offload","torchCuda","decoderSampling","globalSeed","componentPrecisions"]);
        if(Text(execution,"device")!="cuda:0" || Text(execution,"offload")!="none" || Text(execution,"torchCuda")!="12.8"
            || Text(execution,"decoderSampling")!="systematic; global seeded RNG" || execution["globalSeed"]!.GetValue<long>()!=request.Request.Seed)
            throw new InvalidOperationException("Actual execution/offload/seed profile differs");
        var precisions=execution["componentPrecisions"]!.AsObject();Keys(precisions,["dinov3","vae_encoder","rmbg","flow_model","decoder"]);
        foreach(var field in precisions)
            if(field.Value!.GetValue<string>()!=(field.Key is "dinov3" or "vae_encoder"?"bfloat16":"float16"))throw new InvalidOperationException("Actual mixed-precision placement differs");
        var pre=report["preprocessing"]!.AsObject();Keys(pre,["boundingBox","crop","maskSource","maskValidationStage","inputWidth","inputHeight","mode","preparedWidth","preparedHeight","preparedPixelSha256"]);
        var width=pre["inputWidth"]!.GetValue<int>();var height=pre["inputHeight"]!.GetValue<int>();
        if(width<2 || height<2 || width>request.Limits.MaxInputEdge || height>request.Limits.MaxInputEdge || (long)width*height>request.Limits.MaxInputPixels
            || (double)Math.Max(width,height)/Math.Min(width,height)>request.Limits.MaxAspectRatio || pre["preparedWidth"]!.GetValue<int>()!=1024
            || pre["preparedHeight"]!.GetValue<int>()!=1024 || Text(pre,"mode") is not ("RGB" or "RGBA"))
            throw new InvalidOperationException("Actual image preprocessing dimensions differ");
        SplatRules.ValidateHash(Text(pre,"preparedPixelSha256"));
        var alpha=Text(pre,"maskSource")=="input-alpha";
        if((alpha && Text(pre,"mode")!="RGBA") || Text(pre,"maskSource") is not ("input-alpha" or "birefnet")
            || Text(pre,"maskValidationStage")!=(alpha?"cpu-before-model-load":"after-birefnet-before-encoding"))
            throw new InvalidOperationException("Actual foreground mask provenance differs");
        var box=pre["boundingBox"]!.AsArray().Select(x=>x!.GetValue<int>()).ToArray();var crop=pre["crop"]!.AsArray().Select(x=>x!.GetValue<int>()).ToArray();
        if(box.Length!=4 || crop.Length!=4 || box[0]<0 || box[1]<0 || box[2]<=box[0] || box[3]<=box[1]
            || (long)crop[2]-crop[0]<1 || (long)crop[3]-crop[1]<1 || (long)crop[2]-crop[0]>request.Limits.MaxInputEdge
            || (long)crop[3]-crop[1]>request.Limits.MaxInputEdge || ((long)crop[2]-crop[0])*((long)crop[3]-crop[1])>request.Limits.MaxInputPixels)
            throw new InvalidOperationException("Actual mask/crop geometry is invalid");
        var densities=report["densityDecoding"]!.AsArray();
        if(densities.Count!=request.Request.GaussianCounts.Length)throw new InvalidOperationException("Density timing coverage differs");
        for(var i=0;i<densities.Count;i++)
        {
            var entry=densities[i]!.AsObject();Keys(entry,["gaussianCount","seconds"]);var seconds=entry["seconds"]!.GetValue<double>();
            if(entry["gaussianCount"]!.GetValue<int>()!=request.Request.GaussianCounts[i] || !double.IsFinite(seconds) || seconds<0 || seconds>request.Request.Budgets.TimeoutSeconds)
                throw new InvalidOperationException("Actual density-decoding observations differ");
        }
        if(reserved>device["totalBytes"]!.GetValue<long>() || device["initialFreeBytes"]!.GetValue<long>()>device["totalBytes"]!.GetValue<long>()
            || request.Limits.MaxGpuAllocatedBytes>device["totalBytes"]!.GetValue<long>() || report["aggregateDriverMemoryMeasured"]!.GetValue<bool>())
            throw new InvalidOperationException("Actual memory scope or device observations differ");
        Keys(cleanup,["allocatedBeforeWorkspaceClearBytes","reservedBeforeWorkspaceClearBytes","endingAllocatedBytes","endingReservedBytes","mechanism"]);
        if(Text(cleanup,"mechanism")!="torch._C._cuda_clearCublasWorkspaces" || cleanup["allocatedBeforeWorkspaceClearBytes"]!.GetValue<long>()<0
            || cleanup["allocatedBeforeWorkspaceClearBytes"]!.GetValue<long>()>allocated || cleanup["reservedBeforeWorkspaceClearBytes"]!.GetValue<long>()<0
            || cleanup["reservedBeforeWorkspaceClearBytes"]!.GetValue<long>()>reserved)
            throw new InvalidOperationException("Actual cleanup observations differ");
        var cache=report["cacheObservations"]!.AsObject();Keys(cache,["maximumObservedBytes","byteLimit","fileLimit","measurement"]);
        if(cache["maximumObservedBytes"]!.GetValue<long>() is <0 or >67108864 || cache["byteLimit"]!.GetValue<long>()!=67108864
            || cache["fileLimit"]!.GetValue<int>()!=4096 || Text(cache,"measurement")!="stage-sampled; not OS quota")
            throw new InvalidOperationException("Actual cache/temp observations exceed the fixed bounded profile");
        var memory=report["memoryObservations"]!.AsObject();Keys(memory,["scope","peakCommittedBytes","currentPrivateBytes","peakWorkingSetBytes","currentWorkingSetBytes","measurement"]);
        var peakCommit=memory["peakCommittedBytes"]!.GetValue<long>();var currentPrivate=memory["currentPrivateBytes"]!.GetValue<long>();
        var peakResident=memory["peakWorkingSetBytes"]!.GetValue<long>();var currentResident=memory["currentWorkingSetBytes"]!.GetValue<long>();
        if(Text(memory,"scope")!="worker-process; not aggregate job" || Text(memory,"measurement")!="Windows process memory counters"
            || peakCommit<1 || peakCommit>request.Request.Budgets.MaxProcessMemoryBytes || currentPrivate<1 || currentPrivate>peakCommit
            || peakResident<1 || currentResident<1 || currentResident>peakResident)
            throw new InvalidOperationException("Actual committed/private/resident memory evidence differs from the bounded worker scope");
        var modules=report["loadedModules"]!.AsArray().Select(x=>x!.GetValue<string>()).ToArray();
        Bubo.Media.Infrastructure.StandalonePythonIntegrity.VerifyLoadedModules(workspace.HostContext!,installed.Inputs.Select(x=>Path.GetFullPath(x.Path,workspace.Root)),modules);
        var files=report["outputs"]!.AsArray();
        if(files.Count!=request.Request.GaussianCounts.Length)throw new InvalidOperationException("Native output count differs");
        for(var i=0;i<files.Count;i++)
        {
            var entry=files[i]!.AsObject();var count=request.Request.GaussianCounts[i];
            if(Text(entry,"path")!="gaussian-"+count+".ply" || entry["gaussianCount"]!.GetValue<int>()!=count)
                throw new InvalidOperationException("Native payload name/density differs");
            SplatRules.ValidateHash(Text(entry,"sha256"));
        }
    }
    sealed class ReadOnlyWeights : IDisposable
    {
        readonly List<FileStream> streams=[];
        public ReadOnlyWeights(Workspace workspace,PreparedSplatInstallation installed,CancellationToken cancellation)
        {
            try
            {
                var weights=installed.Inputs.Where(x=>Within(Path.GetFullPath(x.Path,workspace.Root),installed.ModelDirectory)).ToArray();
                if(weights.Length!=5)throw new InvalidOperationException("Exact five-weight closure required");
                foreach(var identity in weights)
                {
                    var full=workspace.Resolve(identity.Path);var stream=new FileStream(full,FileMode.Open,FileAccess.Read,FileShare.Read);streams.Add(stream);
                    if(BoundHash(full,2147483648,cancellation)!=identity.Sha256)throw new InvalidOperationException("Weight changed before read lease");
                }
            }
            catch {Dispose();throw;}
        }
        public void Dispose(){foreach(var stream in streams)stream.Dispose();streams.Clear();}
    }
    static void Keys(JsonObject value,string[] expected)
    { if(value.Count!=expected.Length || expected.Any(x=>!value.ContainsKey(x)))throw new InvalidOperationException("Missing or unknown native observations"); }
    static string Text(JsonObject value,string key)=>value[key]?.GetValue<string>() is {Length:>0} text?text:throw new InvalidOperationException("Missing native result "+key);
    static bool Within(string path,string root)
    {
        path=Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var comparison=OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal;
        return path.Equals(root,comparison) || path.StartsWith(root+Path.DirectorySeparatorChar,comparison);
    }
    static bool Overlap(string a,string b)=>Within(a,b) || Within(b,a);
}
