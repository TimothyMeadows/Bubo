using System.Text;
using System.Text.Json.Nodes;
using Bubo.Media.Application;
using Bubo.Media.Domain;
using OpenCaw.AssetProduction;

namespace Bubo.Media.Infrastructure;

public sealed partial class FixedMediaWorker
{
    const string VoiceSourceRevision="022e286b98fbec7e1e916cb940cdf532cd9f488e";
    internal WorkerBatchResult ReadVoice(MediaRequest request,PreparedInstallation installation,string file,string native,string cache,bool probe)
    {
        var value=JsonNode.Parse(MediaAdmission.ReadBounded(file))!.AsObject();
        if(value["protocol"]?.GetValue<string>()!="bubo-voice-worker/v1"||value["backend"]?.GetValue<string>()!=request.Backend
            ||value["sourceRevision"]?.GetValue<string>()!=VoiceSourceRevision||value["modelLoads"]?.GetValue<int>()!=1||value["unloaded"]?.GetValue<bool>()!=true
            ||value["networkAttempts"]?.GetValue<int>()!=0||value["endingTorchAllocatedBytes"]?.GetValue<long>()!=0
            ||value["operation"]?.GetValue<string>()!=(probe?"probe":"generate"))throw new InvalidOperationException("Voice worker identity/offline/lifecycle differs");
        var cleanup=MediaJson.Read<WorkerCleanup>(Encoding.UTF8.GetBytes(value["cleanup"]!.ToJsonString()));
        if(cleanup.Mechanism!="torch._C._cuda_clearCublasWorkspaces"||cleanup.AllocatedBeforeWorkspaceClearBytes<0||cleanup.ReservedBeforeWorkspaceClearBytes<0
            ||cleanup.EndingAllocatedBytes!=0||cleanup.EndingReservedBytes!=0)throw new InvalidOperationException("Voice lacks measured zero-allocation cleanup");
        var python=value["python"]!.AsObject();
        if(python["version"]?.GetValue<string>()!="3.12.16"||!SamePath(python["executable"]!.GetValue<string>(),installation.Python!)
            ||!SamePath(python["prefix"]!.GetValue<string>(),Path.GetDirectoryName(Path.GetDirectoryName(installation.Python))!))
            throw new InvalidOperationException("Actual voice Python executable/environment differs");
        var execution=value["execution"]!.AsObject();
        if(execution["device"]?.GetValue<string>()!="cuda:0"||execution["precision"]?.GetValue<string>()!="bfloat16"||execution["offload"]?.GetValue<string>()!="none"
            ||execution["attention"]?.GetValue<string>()!="sdpa"||value["offlineProfile"]?["ipv6CapabilityProbeDisabled"]?.GetValue<bool>()!=true)
            throw new InvalidOperationException("Actual voice placement/attention/offline profile differs");
        var expectedComponents=request.Backend=="qwen3-tts-base"?new[]{"talker:bfloat16","speech-tokenizer:bfloat16","speaker-encoder:bfloat16"}:new[]{"talker:bfloat16","speech-tokenizer:bfloat16"};
        var components=execution["componentPrecisions"]!.AsArray().Select(node=>node!.GetValue<string>()).ToArray();
        if(!components.SequenceEqual(expectedComponents))throw new InvalidOperationException("Actual voice component precision differs");
        var modules=value["loadedModules"]!.AsArray().Select(node=>node!.GetValue<string>()).ToArray();ValidateModules(installation,modules);
        var imports=value["sourceModules"]!.AsObject();
        ValidateVoiceSourceModules(installation,imports,workspace.Root);
        var data=value["outputs"]!.AsArray();if(data.Count!=(probe?0:request.Items.Length))throw new InvalidOperationException("Voice output count differs");
        var outputs=new List<WorkerOutput>();
        for(var index=0;index<data.Count;index++)
        {
            var item=request.Items[index];var row=data[index]!.AsObject();
            if(row["id"]?.GetValue<string>()!=item.Id||row["path"]?.GetValue<string>()!=$"{index:000}.wav"||row["metadata"]?.GetValue<string>()!=$"{index:000}.settings.json"
                ||row["seed"]?.GetValue<long>()!=item.Seed||row["scheduler"]?.GetValue<string>()!="autoregressive-sampling"
                ||MediaJson.Read<VoiceSampling>(Encoding.UTF8.GetBytes(row["sampling"]!.ToJsonString()))!=item.Voice!.Sampling)
                throw new InvalidOperationException("Actual voice output/sampling identity differs");
            var path=workspace.Resolve(Path.Combine(native,$"{index:000}.wav"));
            var measured=WaveInspection.Read(path,request.Budget.MaximumBytesPerOutput,24000,1);
            if(measured.DurationSeconds>request.Budget.MaximumAudioSeconds)throw new InvalidOperationException("Voice output exceeds duration budget");
            var metadataPath=workspace.Resolve(Path.Combine(native,$"{index:000}.settings.json"));
            var metadata=JsonNode.Parse(MediaAdmission.ReadBounded(metadataPath))!.AsObject();
            ValidateVoiceMetadata(item,request.Backend,metadata,measured,cache,index);
            outputs.Add(new(item.Id,workspace.Relative(path),row["inferenceSeconds"]!.GetValue<double>(),null,null,measured.SampleRate,measured.Channels,measured.Frames,
                measured.Peak,measured.Rms,measured.ClippedFraction,measured.SilentFraction,item.Seed,null,null,"autoregressive-sampling",new(workspace.Relative(metadataPath),workspace.HashFile(metadataPath))));
        }
        var revision=request.Backend=="qwen3-tts-base"?"fd4b254389122332181a7c3db7f27e918eec64e3":"0c0e3051f131929182e2c023b9537f8b1c68adfe";
        return new("bubo-voice-worker/v1",new("cuda:0",execution["deviceName"]!.GetValue<string>(),"bfloat16","none",components,execution["deviceUuid"]!.GetValue<string>(),
            ModelVersion:revision,LinkedLibraryCommit:VoiceSourceRevision,LoadedModules:modules),value["loadingSeconds"]!.GetValue<double>(),value["unloadingSeconds"]!.GetValue<double>(),outputs.ToArray(),1,true,cleanup);
    }
    internal static void ValidateVoiceSourceModules(PreparedInstallation installation,JsonObject imports,string projectRoot)
    {
        var expected=installation.RuntimeSourceFiles??throw new InvalidOperationException("Missing pinned voice runtime source closure");
        if(imports.Count!=10||expected.Count!=10||installation.SourceDirectory is null)throw new InvalidOperationException("Unexpected voice source module closure");
        var seen=new HashSet<string>(StringComparer.Ordinal);
        foreach(var module in imports)
        {
            var row=module.Value!.AsObject();var path=row["path"]!.GetValue<string>();
            var relative=Path.GetRelativePath(installation.SourceDirectory!,path).Replace('\\','/');
            var expectedModule=(relative.EndsWith("/__init__.py",StringComparison.Ordinal)?relative[..^12]:relative.EndsWith(".py",StringComparison.Ordinal)?relative[..^3]:"").Replace('/','.');
            if(!MediaAdmission.Within(path,installation.SourceDirectory!)||!expected.TryGetValue(relative,out var pinned)||pinned!=row["sha256"]?.GetValue<string>()||!seen.Add(relative)
                ||!installation.Files.Any(item=>SamePath(Path.GetFullPath(item.Path,projectRoot),path)&&item.Sha256==pinned)
                ||module.Key!=expectedModule||relative.Contains("tokenizer_25hz",StringComparison.Ordinal))throw new InvalidOperationException("Actual imported voice source identity differs");
        }
        if(!seen.SetEquals(expected.Keys))throw new InvalidOperationException("Actual voice runtime source set differs from the pinned closure");
    }
    void ValidateVoiceMetadata(GenerationItem item,string backend,JsonObject value,WaveMeasurements measured,string cache,int index)
    {
        var voice=item.Voice!;var ending=value["termination"]!.AsObject();
        if(value["protocol"]?.GetValue<string>()!="bubo-voice-settings/v1"||value["backend"]?.GetValue<string>()!=backend||value["sourceRevision"]?.GetValue<string>()!=VoiceSourceRevision
            ||value["text"]?.GetValue<string>()!=item.Prompt||value["language"]?.GetValue<string>()!=voice.Language||value["preset"]?.GetValue<string>()!=voice.Preset
            ||value["expression"]?.GetValue<string>()!=voice.Expression||value["nonStreamingMode"]?.GetValue<bool>()!=true||value["normalization"]?.GetValue<bool>()!=false
            ||value["attention"]?.GetValue<string>()!="sdpa"||value["actualOutputCount"]?.GetValue<int>()!=1
            ||MediaJson.Read<VoiceSampling>(Encoding.UTF8.GetBytes(value["sampling"]!.ToJsonString()))!=voice.Sampling
            ||value["rng"]?["seed"]?.GetValue<long>()!=item.Seed||value["rng"]?["torchInitialSeed"]?.GetValue<long>()!=item.Seed
            ||ending["reason"]?.GetValue<string>()!="eos"||ending["eosObserved"]?.GetValue<bool>()!=true||ending["truncated"]?.GetValue<bool>()!=false
            ||ending["eosTokenId"]?.GetValue<int>()!=2150||ending["maximumNewTokens"]?.GetValue<int>()!=voice.Sampling.MaximumNewTokens
            ||ending["generatedTokens"]?.GetValue<int>() is not {} tokens||tokens<3||tokens>voice.Sampling.MaximumNewTokens||ending["audioCodeFrames"]?.GetValue<int>()!=tokens-1
            ||value["intrinsicDecoderClamp"]?["preserved"]?.GetValue<bool>()!=true||value["intrinsicDecoderClamp"]?["minimum"]?.GetValue<int>()!=-1||value["intrinsicDecoderClamp"]?["maximum"]?.GetValue<int>()!=1)
            throw new InvalidOperationException("Voice metadata differs from actual explicit request/termination/decoder profile");
        var metrics=value["measurements"]!.AsObject();
        if(Math.Abs(measured.Frames-(ending["audioCodeFrames"]!.GetValue<long>()*1920))>(backend=="qwen3-tts-base"?1:0))
            throw new InvalidOperationException("Voice waveform duration differs from actual generated code frames");
        if(metrics["sampleRate"]?.GetValue<int>()!=24000||metrics["channels"]?.GetValue<int>()!=1||metrics["frames"]?.GetValue<long>()!=measured.Frames
            ||!Near(metrics,"peak",measured.Peak)||!Near(metrics,"rms",measured.Rms)||!Near(metrics,"clippedFraction",measured.ClippedFraction)||!Near(metrics,"silentFraction",measured.SilentFraction))
            throw new InvalidOperationException("Voice measurements differ from independently inspected waveform");
        var weights=backend=="qwen3-tts-base"?"38fc7fc51c5e776e840414b6fd443962e9411b9654888fd7913e4da643cb857c":"38b1d5971bdbd982b561cccec982669a53b0537c3cf5e9bd4778ed07bb2f5137";
        if(value["identities"]?["weightsSha256"]?.GetValue<string>()!=weights||value["identities"]?["speechTokenizerWeightsSha256"]?.GetValue<string>()!="836b7b357f5ea43e889936a3709af68dfe3751881acefe4ecf0dbd30ba571258")
            throw new InvalidOperationException("Voice model/codec identities differ");
        if(voice.Reference is not {} reference)
        {
            if(value["reference"] is not null||value["referencePrefixRemoval"] is not null)throw new InvalidOperationException("Preset voice cannot inherit cloning evidence");
            return;
        }
        var ancestry=value["reference"]!.AsObject();
        if(ancestry["originalSha256"]?.GetValue<string>()!=reference.Audio.Sha256||ancestry["transcriptSha256"]?.GetValue<string>()!=reference.Transcript.Sha256
            ||ancestry["rightsSha256"]?.GetValue<string>()!=reference.Rights.Sha256||ancestry["consentSha256"]?.GetValue<string>()!=reference.Consent.Sha256
            ||ancestry["decoder"]?.GetValue<string>()!="PyAV"||ancestry["decoderVersion"]?.GetValue<string>()!="19.0.2"||ancestry["externalDataReferences"]?.GetValue<bool>()!=false
            ||ancestry["denoise"]?.GetValue<bool>()!=false||ancestry["trim"]?.GetValue<bool>()!=false||ancestry["normalization"]?.GetValue<bool>()!=false||ancestry["xVectorOnlyMode"]?.GetValue<bool>()!=false
            ||ancestry["sampleRate"]?.GetValue<int>()!=24000||ancestry["channels"]?.GetValue<int>()!=1||ancestry["sampleFormat"]?.GetValue<string>()!="float32"
            ||ancestry["decodedFile"]?.GetValue<string>()!=$"reference-{index:000}.wav")throw new InvalidOperationException("Voice reference/consent/conversion ancestry differs");
        var decoded=workspace.Resolve(Path.Combine(cache,$"reference-{index:000}.wav"));
        if(workspace.HashFile(decoded)!=ancestry["decodedSha256"]?.GetValue<string>())throw new InvalidOperationException("Decoded private voice reference changed");
        var decodedMeasures=WaveInspection.Read(decoded,20971520,24000,1);
        if(decodedMeasures.DurationSeconds is <1 or >30||decodedMeasures.Frames!=ancestry["frames"]?.GetValue<long>())throw new InvalidOperationException("Decoded reference budget/identity differs");
    }
    static bool SamePath(string a,string b)=>Path.GetFullPath(a).Equals(Path.GetFullPath(b),OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal);
    static bool Near(JsonObject value,string name,double expected)=>value[name]?.GetValue<double>() is {} actual&&double.IsFinite(actual)&&Math.Abs(actual-expected)<=1e-9;
}
