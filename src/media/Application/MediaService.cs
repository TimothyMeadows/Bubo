using System.Text.Json;
using System.Text.Json.Nodes;
using Bubo.Media.Domain;
using OpenCaw.AssetProduction;

namespace Bubo.Media.Application;

public sealed class MediaService(IWorkspace workspace,IInstallationVerifier installations,IMediaWorker worker,JsonObject? runtimeBuild=null)
{
    readonly JsonObject? runtimeIdentity=runtimeBuild?.DeepClone().AsObject();
    public object Inspect(string requestPath)
    {
        var admitted=new MediaAdmission(workspace).Read(requestPath);
        var installation=installations.Verify(admitted.Request.Backend,admitted.Request.Installation);
        return new {schemaVersion="bubo-media-status/v1",backend=admitted.Request.Backend,requestSha256=admitted.RequestSha256,
            installationReceiptSha256=installation.ReceiptSha256,catalogSha256=installation.CatalogSha256,environmentSha256=installation.EnvironmentSha256,
            installation.WorkerSha256,installation.WorkerSourceSha256,installation.ResourceCatalogSha256,runtimeBuild=runtimeIdentity,
            verifiedLocalFiles=installation.Files.Count,modelsLoaded=false,generation=false,humanReview="pending",promotion=false};
    }

    public async Task<object> Generate(string requestPath,CancellationToken cancellation)
    {
        var admission=new MediaAdmission(workspace);var admitted=admission.Read(requestPath);var host=admission.Context;
        var installation=installations.Verify(admitted.Request.Backend,admitted.Request.Installation);
        admission.RequireInstallationSeparation(installation,admitted.StateDirectory,admitted.OutputDirectory);
        if(admitted.Request.Acceptance is not null)admission.RequireInstallationSeparation(installation,host.MediaStateRoot);
        var state=admitted.StateDirectory;host.EnsureWriteAllowed(state);Directory.CreateDirectory(workspace.ResolveWrite(state));
        var requestCopy=state+"/request.json";
        using(var output=new FileStream(workspace.ResolveWrite(requestCopy),FileMode.CreateNew,FileAccess.Write,FileShare.Read))
            JsonSerializer.Serialize(output,admitted.Request,MediaJson.Options);
        string? claim=null;
        workspace.Save(state+"/prepared.json",new {schemaVersion="bubo-media-execution/v1",phase="prepared",requestPath=admitted.RequestPath,admitted.RequestSha256,
            hostContextSha256=host.ContextSha256,installation.CatalogSha256,installation.EnvironmentSha256,outputDirectory=admitted.OutputDirectory,generationStarted=false});
        try
        {
            var result=await worker.Run(admitted.Request,installation,state,cancellation,token=>
            {
                token.ThrowIfCancellationRequested();host.ValidateFresh();
                if(workspace.HashProjectFile(admitted.RequestPath)!=admitted.RequestSha256)throw new InvalidOperationException("Media request changed while waiting");
                token.ThrowIfCancellationRequested();
                claim=new AcceptanceLedger(workspace).Claim(admitted);
                token.ThrowIfCancellationRequested();
                workspace.Save(state+"/started.json",new {schemaVersion="bubo-media-execution/v1",phase="started",acceptanceClaim=claim,hostContextSha256=host.ContextSha256,startedAt=DateTimeOffset.UtcNow});
                return Task.CompletedTask;
            });
            host.ValidateFresh();
            Validate(admitted.Request,result);
            // Re-admit inputs after execution; a provider must not mutate its model/environment.
            _=installations.Verify(admitted.Request.Backend,admitted.Request.Installation);
            var outputs=new List<object>();long total=0;
            for(var index=0;index<result.Outputs.Length;index++)
            {
                var item=result.Outputs[index];var full=host.ResolveReadPath(item.Path);
                if(!Within(full,workspace.Resolve(admitted.OutputDirectory)))throw new InvalidOperationException("Worker output escapes its staged request directory");
                if(item.NativeMetadata is {} metadata)
                {
                    metadata.Validate();var path=host.ResolveReadPath(metadata.Path);
                    if(!Within(path,workspace.Resolve(admitted.OutputDirectory)) || new FileInfo(path).Length>524288 || workspace.HashProjectFile(metadata.Path)!=metadata.Sha256)
                        throw new InvalidOperationException("Native metadata escaped staging or changed");
                }
                var bytes=new FileInfo(full).Length;total=checked(total+bytes);
                if(bytes<1 || bytes>admitted.Request.Budget.MaximumBytesPerOutput || total>admitted.Request.Budget.MaximumTotalBytes)
                    throw new InvalidOperationException("Actual outputs exceed their explicit byte budget");
                outputs.Add(new {item.Id,path=workspace.Relative(full),sha256=workspace.HashFile(full),bytes,item.InferenceSeconds,item.Width,item.Height,item.SampleRate,item.Channels,item.Frames,
                    item.Peak,item.Rms,item.ClippedFraction,item.SilentFraction,requested=admitted.Request.Items[index],actual=new{item.Seed,item.Steps,item.Guidance,item.Scheduler,item.NativeMetadata},humanReview="pending",loopReview=admitted.Request.Items[index].LoopCandidate?"required":"not-requested",promotion=false});
            }
            var manifest=new {schemaVersion="bubo-media-manifest/v1",status="generated-staged",requestPath=admitted.RequestPath,admitted.RequestSha256,hostContextSha256=host.ContextSha256,
                mediaPolicySha256=host.MediaSha256,imageStyleSha256=admitted.Capability.Modality=="image"?host.StyleSha256:null,
                hostPlatformModules=host.Tools.Where(x=>x.Kind=="platform-module").Select(x=>new{x.Path,x.Sha256}).ToArray(),
                rights=admitted.Request.Rights,installation=admitted.Request.Installation,installation.CatalogSha256,installation.EnvironmentSha256,
                installation.WorkerSha256,installation.WorkerSourceSha256,installation.ResourceCatalogSha256,runtimeBuild=runtimeIdentity,
                backend=admitted.Request.Backend,requestedSettings=admitted.Request.Settings,actualSettings=result.Execution,
                timing=new{result.LoadingSeconds,result.UnloadingSeconds,perOutputInferenceSeconds=result.Outputs.Select(x=>x.InferenceSeconds).ToArray()},result.ModelLoads,result.Unloaded,
                acceptanceClaim=claim,outputs,humanReview="pending",promotion=false,completedAt=DateTimeOffset.UtcNow};
            workspace.Save(state+"/manifest.json",manifest);return manifest;
        }
        catch(Exception error)
        {
            workspace.Save(state+"/failure.json",new {schemaVersion="bubo-media-execution/v1",status="failed",error=error.Message,requestSha256=admitted.RequestSha256,
                acceptanceClaim=claim,claimRefunded=false,stagedOutputsPreserved=true,humanReview="not-granted",promotion=false,failedAt=DateTimeOffset.UtcNow});
            throw;
        }
    }

    public async Task<object> Probe(string requestPath,Func<MediaRequest,PreparedInstallation,string,CancellationToken,Task<WorkerBatchResult>> probe,CancellationToken cancellation)
    {
        var admitted=new MediaAdmission(workspace).Read(requestPath);var installation=installations.Verify(admitted.Request.Backend,admitted.Request.Installation);
        var id="probe-"+Guid.NewGuid().ToString("N");var request=admitted.Request with {RequestId=id,Acceptance=null};
        var state=workspace.HostContext!.StateRoot+"/media-probes/"+id;
        new MediaAdmission(workspace).RequireInstallationSeparation(installation,state,request.OutputDirectory.TrimEnd('/','\\')+"/"+id);
        Directory.CreateDirectory(workspace.ResolveWrite(state));
        var result=await probe(request,installation,state,cancellation);
        if(result.Outputs.Length!=0 || result.ModelLoads!=1 || !result.Unloaded || !Finite(result.LoadingSeconds) || !Finite(result.UnloadingSeconds))
            throw new InvalidOperationException("Load/unload probe sampled or failed to unload");
        workspace.HostContext.ValidateFresh();_ = installations.Verify(request.Backend,request.Installation);
        var receipt=new {schemaVersion="bubo-media-probe/v1",backend=request.Backend,result.Execution,result.LoadingSeconds,result.UnloadingSeconds,result.ModelLoads,result.Unloaded,
            installation.CatalogSha256,installation.EnvironmentSha256,installation.WorkerSha256,installation.WorkerSourceSha256,installation.ResourceCatalogSha256,runtimeBuild=runtimeIdentity,
            sourceRequestSha256=admitted.RequestSha256,samplingPerformed=false,acceptanceBudgetConsumed=false,promotion=false};
        workspace.Save(state+"/result.json",receipt);return receipt;
    }

    static void Validate(MediaRequest request,WorkerBatchResult result)
    {
        if(result.Protocol!=MediaCapabilities.Require(request.Backend).WorkerProtocol || result.ModelLoads!=1 || !result.Unloaded || result.Outputs.Length!=request.Items.Length
            || !Finite(result.LoadingSeconds) || !Finite(result.UnloadingSeconds) || result.Execution.Device!=request.Settings.Device
            || result.Execution.Precision!=request.Settings.Precision || result.Execution.Offload!=request.Settings.Offload || string.IsNullOrWhiteSpace(result.Execution.DeviceName))
            throw new InvalidOperationException("Worker result/lifecycle/settings differ from the finite request");
        for(var i=0;i<result.Outputs.Length;i++)
        {
            var actual=result.Outputs[i];var expected=request.Items[i];
            if(actual.Id!=expected.Id || !Finite(actual.InferenceSeconds) || actual.Seed!=expected.Seed || actual.Steps!=expected.Steps || actual.Guidance!=expected.Guidance || string.IsNullOrWhiteSpace(actual.Scheduler))throw new InvalidOperationException("Worker output identity/timing/actual sampling settings differ");
            if(request.Backend=="flux-schnell" && (actual.Width!=expected.Width || actual.Height!=expected.Height))throw new InvalidOperationException("Worker image dimensions differ");
            if(request.Backend is "stable-audio-open" or "ace-step-turbo" && (actual.SampleRate!=(request.Backend=="ace-step-turbo"?48000:44100) || actual.Channels!=2 || actual.Frames is not >0 || actual.Peak is not >=0
                || actual.Rms is not >=0 || actual.ClippedFraction is not >=0 or >1 || actual.SilentFraction is not >=0 or >1
                || !Finite(actual.Peak.Value) || !Finite(actual.Rms.Value)))throw new InvalidOperationException("Invalid independently measured audio");
        }
    }
    static bool Finite(double value)=>double.IsFinite(value) && value>=0;
    static bool Within(string path,string parent)
    {
        var comparison=OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal;
        return Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent))+Path.DirectorySeparatorChar,comparison);
    }
}
