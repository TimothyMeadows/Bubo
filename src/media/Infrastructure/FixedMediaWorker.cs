using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bubo.Media.Application;
using Bubo.Media.Domain;
using OpenCaw.AssetProduction;

namespace Bubo.Media.Infrastructure;

public sealed class FixedMediaWorker(IWorkspace workspace,INativeProcess process):IMediaWorker
{
    public Task<WorkerBatchResult> Run(MediaRequest request,PreparedInstallation installation,string attemptDirectory,CancellationToken cancellation,
        Func<CancellationToken,Task>? beforeStart=null)=>Execute(request,installation,attemptDirectory,false,cancellation,beforeStart);

    public Task<WorkerBatchResult> Probe(MediaRequest request,PreparedInstallation installation,string attemptDirectory,CancellationToken cancellation,
        Func<CancellationToken,Task>? beforeStart=null)=>Execute(request,installation,attemptDirectory,true,cancellation,beforeStart);

    async Task<WorkerBatchResult> Execute(MediaRequest request,PreparedInstallation installation,string attempt,bool probe,CancellationToken cancellation,Func<CancellationToken,Task>? beforeStart)
    {
        var host=workspace.HostContext??throw new InvalidOperationException("Host context required");
        host.ValidateFresh();
        var output=workspace.ResolveWrite(request.OutputDirectory.TrimEnd('/','\\')+"/"+request.RequestId);
        var native=Path.Combine(output,"native");var cache=workspace.ResolveWrite(attempt+"/cache");
        new MediaAdmission(workspace).RequireInstallationSeparation(installation,output,attempt);
        var job=workspace.ResolveWrite(attempt+"/worker-job."+(installation.Backend=="flux-schnell"?"bin":"json"));
        var result=workspace.ResolveWrite(attempt+"/worker-result."+(installation.Backend=="flux-schnell"?"bin":"json"));
        Directory.CreateDirectory(Path.GetDirectoryName(job)!);
        string executable;string[] args;
        if(installation.Backend=="flux-schnell")
        {
            using var stream=new FileStream(job,FileMode.CreateNew,FileAccess.Write,FileShare.Read);using var writer=new BinaryWriter(stream,Encoding.UTF8,true);
            writer.Write(Encoding.ASCII.GetBytes("BFLX0001"));
            foreach(var name in new[]{"flux1-schnell-q8_0.gguf","ae.safetensors","clip_l.safetensors","t5xxl_fp16.safetensors"})Text(writer,installation.Components[name]);
            Text(writer,native);writer.Write((uint)(probe?0:request.Items.Length));
            if(!probe)foreach(var item in request.Items){Text(writer,item.Id);Text(writer,item.Prompt);writer.Write((uint)item.Width!.Value);writer.Write((uint)item.Height!.Value);writer.Write(item.Seed);writer.Write((uint)item.Steps);}
            executable=installation.Worker;args=[job,result];
        }
        else
        {
            var payload=new {schemaVersion="bubo-sfx-worker/v1",operation=probe?"probe":"generate",modelDirectory=installation.ModelDirectory,outputDirectory=native,cacheDirectory=cache,settings=request.Settings,
                items=probe?[]:request.Items.Select(i=>new{i.Id,i.Prompt,i.NegativePrompt,i.Seed,i.Steps,i.Guidance,seconds=i.Seconds!.Value,i.LoopCandidate}).ToArray()};
            using var stream=new FileStream(job,FileMode.CreateNew,FileAccess.Write,FileShare.Read);JsonSerializer.Serialize(stream,payload,MediaJson.Options);
            executable=installation.Python!;args=["-I","-B","-X","utf8",installation.Worker,job,result];
        }
        var inputs=installation.Files.Select(x=>new GpuInputIdentity(x.Path,x.Sha256)).Append(new(workspace.Relative(job),workspace.HashFile(job)))
            .Append(new(installation.ReceiptPath,installation.ReceiptSha256)).ToList();
        if(installation.Backend=="stable-audio-open")inputs.Add(new(installation.Worker,workspace.HashFile(installation.Worker)));
        inputs.AddRange(host.Tools.Where(x=>x.Kind=="platform-module").Select(x=>new GpuInputIdentity(x.Path,x.Sha256)));
        var environment=new Dictionary<string,string> { ["CUDA_VISIBLE_DEVICES"]="0",["CUDA_CACHE_PATH"]=Path.Combine(cache,"cuda"),["TEMP"]=Path.Combine(cache,"tmp"),["TMP"]=Path.Combine(cache,"tmp"),["TORCHINDUCTOR_CACHE_DIR"]=Path.Combine(cache,"inductor"),["TRITON_CACHE_DIR"]=Path.Combine(cache,"triton") };
        var spec=new GpuProcessSpec(executable,args,host.Root,workspace.ResolveWrite(attempt+"/worker.log"),host.TimeoutSeconds,host.MaxLogBytes,host.MaxPrivateBytes,environment,inputs,MaximumWaitSeconds:Math.Min(300,host.TimeoutSeconds));
        await process.RunGpu(spec,cancellation,async token=>
        {
            token.ThrowIfCancellationRequested();host.ValidateFresh();
            if(Directory.Exists(output) || File.Exists(output))throw new InvalidOperationException("Staged output identity already exists");
            if(beforeStart is not null)await beforeStart(token);
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(host.ResolveWritePath(output));Directory.CreateDirectory(host.ResolveWritePath(native));
            foreach(var path in new[]{cache,Path.Combine(cache,"tmp"),Path.Combine(cache,"cuda"),Path.Combine(cache,"inductor"),Path.Combine(cache,"triton")})Directory.CreateDirectory(host.ResolveWritePath(path));
        });
        host.ValidateFresh();
        return installation.Backend=="flux-schnell"?ReadImage(request,installation,result,native,output,probe):ReadSfx(request,installation,result,native,probe);
    }

    internal WorkerBatchResult ReadImage(MediaRequest request,PreparedInstallation installation,string file,string native,string output,bool probe)
    {
        using var stream=File.OpenRead(file);if(stream.Length>1048576)throw new InvalidOperationException("Native result exceeds1MiB");using var reader=new BinaryReader(stream,Encoding.UTF8);
        if(!reader.ReadBytes(8).SequenceEqual(Encoding.ASCII.GetBytes("BFLXR001")))throw new InvalidOperationException("Unsupported native result protocol");
        var count=reader.ReadUInt32();if(count!=(probe?0:request.Items.Length))throw new InvalidOperationException("Native result count differs");
        var loading=reader.ReadDouble();var unloading=reader.ReadDouble();var device=Text(reader);var uuid=Text(reader);var version=Text(reader);var major=reader.ReadUInt32();var minor=reader.ReadUInt32();
        var commit=Text(reader);if(commit.Length<7 || !"77b72a2efed78f55de26892c7e909cee001e8b83".StartsWith(commit,StringComparison.Ordinal))throw new InvalidOperationException("Actual linked native library revision differs");
        var moduleCount=reader.ReadUInt32();if(moduleCount>2048)throw new InvalidOperationException("Unbounded runtime module list");
        var modules=Enumerable.Range(0,(int)moduleCount).Select(_=>Text(reader)).ToArray();
        ValidateModules(installation,modules);
        if(major==0 || minor>99 || device.Length==0 || uuid.Length!=32)throw new InvalidOperationException("Missing actual CUDA identity");
        var outputs=new List<WorkerOutput>();
        for(var i=0;i<count;i++)
        {
            var item=request.Items[i];var id=Text(reader);var pixels=Text(reader);var info=Text(reader);var scheduler=Text(reader);var inference=reader.ReadDouble();
            var width=checked((int)reader.ReadUInt32());var height=checked((int)reader.ReadUInt32());var channels=checked((int)reader.ReadUInt32());var seed=reader.ReadInt64();var steps=reader.ReadUInt32();
            if(id!=item.Id || pixels!=$"{i:000}.rgb" || info!=$"{i:000}.info.json" || width!=item.Width || height!=item.Height || seed!=item.Seed || steps!=item.Steps)throw new InvalidOperationException("Native actual/request identity differs");
            var path=workspace.ResolveWrite(Path.Combine(output,$"{i:000}.png"));
            ImagePng.Write(workspace.Resolve(Path.Combine(native,pixels)),path,width,height,channels);
            var dimensions=ImagePng.Inspect(path);if(dimensions.Width!=width || dimensions.Height!=height)throw new InvalidOperationException("Encoded image dimensions differ");
            var metadata=workspace.Resolve(Path.Combine(native,info));if(new FileInfo(metadata).Length>524288)throw new InvalidOperationException("Unbounded native metadata");
            var actual=JsonNode.Parse(File.ReadAllBytes(metadata))!.AsObject();
            if(actual["schema"]?.GetValue<string>()!="sdcpp.generation/v1" || actual["kind"]?.GetValue<string>()!="image"
                || actual["prompt"]?.GetValue<string>()!=item.Prompt || actual["negative_prompt"]?.GetValue<string>()!=""
                || actual["width"]?.GetValue<int>()!=width || actual["height"]?.GetValue<int>()!=height
                || actual["seed"]?.GetValue<string>()!=seed.ToString(System.Globalization.CultureInfo.InvariantCulture)
                || actual["sample_params"]?["sample_steps"]?.GetValue<int>()!=item.Steps
                || actual["sample_params"]?["guidance"]?["txt_cfg"]?.GetValue<double>()!=item.Guidance
                || actual["sample_params"]?["sample_method"]?.GetValue<string>()!="euler"
                || actual["sample_params"]?["scheduler"]?.GetValue<string>()!=scheduler)
                throw new InvalidOperationException("Actual native generation metadata differs from the request");
            outputs.Add(new(id,workspace.Relative(path),inference,width,height,null,null,null,null,null,null,null,seed,(int)steps,item.Guidance,scheduler,new(workspace.Relative(metadata),workspace.HashFile(metadata))));
        }
        if(stream.Position!=stream.Length)throw new InvalidOperationException("Trailing native result fields");
        return new("bubo-flux-worker/v1",new("cuda:0",device,"q8_0","text-encoder-cpu",["diffusion-weight-storage:q8_0","other-component-storage:catalogued; compute precision not individually exposed"],uuid,version,commit,modules),loading,unloading,outputs.ToArray(),1,true);
    }

    WorkerBatchResult ReadSfx(MediaRequest request,PreparedInstallation installation,string file,string native,bool probe)
    {
        var value=JsonNode.Parse(MediaAdmission.ReadBounded(file))!.AsObject();
        if(value["protocol"]?.GetValue<string>()!="bubo-sfx-worker/v1" || value["modelLoads"]?.GetValue<int>()!=1 || value["unloaded"]?.GetValue<bool>()!=true
            || value["networkAttempts"]?.GetValue<int>()!=0 || value["endingTorchAllocatedBytes"]?.GetValue<long>()!=0)throw new InvalidOperationException("SFX lifecycle/offline evidence differs");
        var actual=value["execution"]!.AsObject();
        var python=value["python"]!.AsObject();
        if(python["version"]?.GetValue<string>()!="3.12.16" || !Path.GetFullPath(python["executable"]!.GetValue<string>()).Equals(installation.Python,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal)
            || !Path.GetFullPath(python["prefix"]!.GetValue<string>()).Equals(Path.GetDirectoryName(Path.GetDirectoryName(installation.Python))!,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))
            throw new InvalidOperationException("Actual Python executable/environment differs");
        var modules=value["loadedModules"]!.AsArray().Select(x=>x!.GetValue<string>()).ToArray();ValidateModules(installation,modules);
        if(actual["device"]?.GetValue<string>()!="cuda:0" || actual["precision"]?.GetValue<string>()!=request.Settings.Precision || actual["offload"]?.GetValue<string>()!="none")throw new InvalidOperationException("Actual SFX settings differ");
        var data=value["outputs"]!.AsArray();if(data.Count!=(probe?0:request.Items.Length))throw new InvalidOperationException("SFX output count differs");
        var outputs=new List<WorkerOutput>();
        for(var i=0;i<data.Count;i++)
        {
            var item=request.Items[i];var row=data[i]!.AsObject();
            if(row["id"]?.GetValue<string>()!=item.Id || row["path"]?.GetValue<string>()!=$"{i:000}.wav" || row["seed"]?.GetValue<long>()!=item.Seed
                || row["steps"]?.GetValue<int>()!=item.Steps || row["guidance"]?.GetValue<double>()!=item.Guidance || row["loopReviewed"]?.GetValue<bool>()!=false)throw new InvalidOperationException("Actual SFX/request identity differs");
            var path=workspace.Resolve(Path.Combine(native,$"{i:000}.wav"));var measurements=WaveInspection.Read(path,request.Budget.MaximumBytesPerOutput);
            if(Math.Abs(measurements.Frames-checked((long)Math.Round(item.Seconds!.Value*measurements.SampleRate,MidpointRounding.ToEven)))>1)throw new InvalidOperationException("Actual SFX duration differs");
            outputs.Add(new(item.Id,workspace.Relative(path),row["inferenceSeconds"]!.GetValue<double>(),null,null,measurements.SampleRate,measurements.Channels,measurements.Frames,measurements.Peak,measurements.Rms,measurements.ClippedFraction,measurements.SilentFraction,
                row["seed"]!.GetValue<long>(),row["steps"]!.GetValue<int>(),row["guidance"]!.GetValue<double>(),row["scheduler"]!.GetValue<string>()));
        }
        return new("bubo-sfx-worker/v1",new("cuda:0",actual["deviceName"]!.GetValue<string>(),request.Settings.Precision,"none",actual["componentPrecisions"]!.AsArray().Select(x=>x!.GetValue<string>()).ToArray(),actual["deviceUuid"]!.GetValue<string>(),LoadedModules:modules),
            value["loadingSeconds"]!.GetValue<double>(),value["unloadingSeconds"]!.GetValue<double>(),outputs.ToArray(),1,true);
    }
    internal void ValidateModules(PreparedInstallation installation,string[] modules)
    {
        if(!OperatingSystem.IsWindows())return; // Linux native loader evidence remains explicitly unverified.
        if(modules.Length is <1 or >2048)throw new InvalidOperationException("Missing/unbounded actual runtime module evidence");
        var host=workspace.HostContext!;
        var installed=installation.Files.Select(x=>Path.GetFullPath(x.Path,host.Root)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var controlledNames=installed.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var windows=Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var system=Environment.GetFolderPath(Environment.SpecialFolder.System);
        foreach(var module in modules)
        {
            var path=Path.GetFullPath(module);
            if(installed.Contains(path))continue;
            if(controlledNames.Contains(Path.GetFileName(path)))throw new InvalidOperationException("Loaded dependency shadows its pinned installation: "+path);
            if(MediaAdmission.Within(path,system)||MediaAdmission.Within(path,Path.Combine(windows,"WinSxS")))continue;
            var platform=host.Tools.SingleOrDefault(x=>x.Kind=="platform-module" && x.Path.Equals(path,StringComparison.OrdinalIgnoreCase));
            if(platform is not null && host.HashToolFile(path)==platform.Sha256)continue;
            throw new InvalidOperationException("Loaded dependency is outside the pinned installation/system-driver boundary: "+path);
        }
    }
    static void Text(BinaryWriter writer,string value){var bytes=Encoding.UTF8.GetBytes(value);writer.Write((uint)bytes.Length);writer.Write(bytes);}
    static string Text(BinaryReader reader){var length=reader.ReadUInt32();if(length>32768)throw new InvalidOperationException("Unbounded native string");var bytes=reader.ReadBytes((int)length);if(bytes.Length!=length)throw new EndOfStreamException();return new UTF8Encoding(false,true).GetString(bytes);}
}
