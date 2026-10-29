using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HostExecutionContext = Bubo.Host.HostExecutionContext;
using Bubo.Media.Domain;
using OpenCaw.AssetProduction;

if(args.Length>=2 && args[0]=="gpu-guardian" && args[1]=="run")return await GpuGuardian.Serve(args[2..]);
if(args.Length>0 && args[0].StartsWith("--owned-",StringComparison.Ordinal))return await GpuGuardianFixtures.Child(args);
if(args is ["--verify-installed-python",var project,var contextFile,var environment,var pythonBase,var wheelhouse,var archive,var catalogFile,var specFile])
{
    var diagnosticHost=HostExecutionContext.Load(project,contextFile);
    var catalog=JsonNode.Parse(File.ReadAllBytes(catalogFile))!.AsObject();
    var spec=JsonNode.Parse(File.ReadAllBytes(specFile))!.AsObject();
    Bubo.Media.Infrastructure.PythonIntegrity.Verify(diagnosticHost,diagnosticHost.ResolveReadPath(environment),diagnosticHost.ResolveReadPath(pythonBase),diagnosticHost.ResolveReadPath(wheelhouse),diagnosticHost.ResolveReadPath(archive),spec,
        catalog["pythonRuntimes"]![OperatingSystem.IsWindows()?"x86_64-pc-windows-msvc":"x86_64-unknown-linux-gnu"]!.AsObject(),catalog);
    Console.WriteLine("PASS: installed Python content matches pinned base/wheel archives; no model loading or sampling");return 0;
}
if(args is ["--audit-module-result",var auditRoot,var auditContext,var receiptPath,var resultPath,var expectedResultHash])
{
    var auditHost=HostExecutionContext.Load(auditRoot,auditContext);auditHost.RequireReference(receiptPath,"input");
    var receiptFile=auditHost.ResolveReadPath(receiptPath);var resultFile=auditHost.ResolveReadPath(resultPath);
    if(new FileInfo(receiptFile).Length>32*1024*1024 || new FileInfo(resultFile).Length>1048576 || Workspace.Hash(resultFile)!=expectedResultHash)throw new InvalidOperationException("Bound diagnostic evidence changed");
    var document=JsonNode.Parse(File.ReadAllBytes(receiptFile))!.AsObject();var observed=JsonNode.Parse(File.ReadAllBytes(resultFile))!.AsObject();
    var files=JsonSerializer.Deserialize<Bubo.Media.Application.InstalledFile[]>(document["files"]!.ToJsonString(),MediaJson.Options)!;
    var prepared=new Bubo.Media.Application.PreparedInstallation(document["backend"]!.GetValue<string>(),receiptPath,Workspace.Hash(receiptFile),"diagnostic-only", "diagnostic-only",null,new Dictionary<string,string>(),document["catalogSha256"]!.GetValue<string>(),document["environmentSha256"]!.GetValue<string>(),files);
    new Bubo.Media.Infrastructure.FixedMediaWorker(new Workspace(auditHost.Root,auditHost),new NativeProcess()).ValidateModules(prepared,observed["loadedModules"]!.AsArray().Select(x=>x!.GetValue<string>()).ToArray());
    Console.WriteLine(JsonSerializer.Serialize(new {status="pass",scope="observed loaded-module policy only; prior installation and worker lifecycle evidence remain separate",sourceResultSha256=expectedResultHash,hostContextSha256=auditHost.ContextSha256,installationReceiptSha256=prepared.ReceiptSha256,newModelExecution=false,modelSampling=false},MediaJson.Options));return 0;
}

var count = 0;
void Check(bool value, string name)
{
    if (!value) throw new InvalidOperationException("FAIL: " + name);
    count++; Console.WriteLine("PASS: " + name);
}
void Denied(Action run, string name)
{
    try { run(); } catch (Exception e) when (e is InvalidOperationException or JsonException or ArgumentException)
    { Check(true, name); return; }
    throw new InvalidOperationException("FAIL: accepted " + name);
}
var identity = new FileIdentity("inputs/receipt.json", new string('a',64));
var item = new GenerationItem { Id="one",Prompt="An owned engineering illustration",Seed=1,Steps=4,Guidance=1,Width=512,Height=512 };
var request = new MediaRequest
{
    SchemaVersion="bubo-media-request/v1", RequestId="owned-candidate", Backend="flux-schnell", Task="media-contract-tests",
    OutputDirectory="staging/owned", Rights=identity,Installation=identity,Settings=new("cuda:0","q8_0","text-encoder-cpu"),
    Budget=new(4,1048576,4194304,1048576,47),Items=[item]
};
var image = MediaCapabilities.Require("flux-schnell");
var sfx = MediaCapabilities.Require("stable-audio-open");
request.Validate(image);Check(true,"explicit bounded FLUX request admitted");
var bytes=JsonSerializer.SerializeToUtf8Bytes(request,MediaJson.Options);
MediaJson.Read<MediaRequest>(bytes).Validate(image);Check(true,"strict v1 contract round trip");
Denied(()=>(request with { SchemaVersion="v2" }).Validate(image),"unsupported API schema");
Denied(()=>MediaCapabilities.Require("cloud-fallback"),"unselected backend");
Denied(()=>(request with { Settings=new("cpu","q8_0","text-encoder-cpu") }).Validate(image),"CPU device fallback");
Denied(()=>(request with { Settings=new("cuda:0","q4_0","text-encoder-cpu") }).Validate(image),"unselected image quantization");
Denied(()=>(request with { Settings=new("cuda:0","q8_0","auto") }).Validate(image),"implicit offload choice");
Denied(()=>(request with { RequestId="../escape" }).Validate(image),"traversing request identity");
Denied(()=>(request with { Rights=new("inputs/a",new string('a',40)) }).Validate(image),"Git SHA1 cannot masquerade as SHA256");
Denied(()=>(request with { Items=[] }).Validate(image),"empty batch");
Denied(()=>(request with { Items=[item,item] }).Validate(image),"duplicate batch output identities");
Denied(()=>(request with { Items=Enumerable.Range(0,5).Select(x=>item with { Id="item"+x }).ToArray() }).Validate(image),"unbounded batch");
Denied(()=>(request with { Items=[item with { Seed=-1 }] }).Validate(image),"implicit random seed");
Denied(()=>(request with { Items=[item with { Width=513 }] }).Validate(image),"unsupported image dimensions");
Denied(()=>(request with { Items=[item with { Steps=5 }] }).Validate(image),"non-schnell step budget");
Denied(()=>(request with { Items=[item with { Guidance=7 }] }).Validate(image),"unsupported schnell guidance");
Denied(()=>(request with { Items=[item with { NegativePrompt="unsupported at guidance1" }] }).Validate(image),"ineffective image negative prompt");
Denied(()=>(request with { Items=[item with { Prompt=new string('\u754c',1400) }] }).Validate(image),"image UTF8 byte budget is checked before native reservation");
(request with { Items=[item with { Prompt=new string('\u754c',1000) }] }).Validate(image);Check(true,"bounded multibyte image prompt remains supported");
Denied(()=>(request with { Items=[item with { Seconds=5 }] }).Validate(image),"audio controls on image");
Denied(()=>(request with { Budget=request.Budget with { MaximumPixels=4096 } }).Validate(image),"pixel budget overflow");
Denied(()=>(request with { Budget=request.Budget with { MaximumTotalBytes=1 } }).Validate(image),"incoherent byte budgets");
Denied(()=>MediaJson.Read<MediaRequest>(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("\"schemaVersion\":", "\"callback\":\"arbitrary.py\",\"schemaVersion\":"))),"unknown callback contract field");
Denied(()=>MediaJson.Read<MediaRequest>(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("\"schemaVersion\":", "\"schemaVersion\":\"duplicate\",\"schemaVersion\":"))),"duplicate JSON property");
Denied(()=>MediaJson.Read<MediaRequest>(Encoding.UTF8.GetBytes("{}")),"missing required request fields");
var audio=request with { Backend="stable-audio-open",Settings=new("cuda:0","float16","none"),Items=[item with { Width=null,Height=null,Seconds=5,Steps=30,Guidance=7,LoopCandidate=true }] };
audio.Validate(sfx);Check(true,"bounded SFX loop candidate carries intent without loop acceptance");
Denied(()=>(audio with { Items=[audio.Items[0] with { Guidance=1,NegativePrompt="would be ignored" }] }).Validate(sfx),"ineffective SFX negative prompt rejected before reservation");
Denied(()=>(audio with { Items=[audio.Items[0] with { Seconds=48 }] }).Validate(sfx),"unsupported SFX duration");
Denied(()=>(audio with { Items=[audio.Items[0] with { Seconds=double.NaN }] }).Validate(sfx),"nonfinite SFX duration");
Denied(()=>(audio with { Items=[audio.Items[0] with { Width=512 }] }).Validate(sfx),"image controls on SFX");
Denied(()=>(audio with { Acceptance=new("image","candidate",identity) }).Validate(sfx),"cross-modality acceptance claim");
Denied(()=>(request with { Acceptance=new("image","third-attempt",identity) }).Validate(image),"unapproved acceptance attempt kind");
Denied(()=>(request with { Acceptance=new("image","candidate",identity),Items=[item,item with { Id="two" }] }).Validate(image),"acceptance batch cannot consume multiple candidates");
(request with { Acceptance=new("image","candidate",identity) }).Validate(image);Check(true,"one matching acceptance attempt admitted for later ledger enforcement");
var musicCapability=MediaCapabilities.Require("ace-step-turbo");
var musicRequest=request with
{
    Backend="ace-step-turbo",Settings=new("cuda:0","bfloat16","none"),Budget=request.Budget with{MaximumAudioSeconds=600},
    Items=[item with{Prompt="Original piano and percussion",Width=null,Height=null,Seconds=12,Steps=8,Music=new("instrumental","","unknown",120,"C major","4",3)}],
    Acceptance=new("instrumental","candidate",identity)
};
musicRequest.Validate(musicCapability);Check(true,"music instrumental binds the shared instrumental case without lyrics or planning controls");
var song=musicRequest with{Items=[musicRequest.Items[0] with{Music=musicRequest.Items[0].Music! with{Mode="supplied-lyrics",Lyrics="Bright bells ring\nWhile we sing",VocalLanguage="en"}}],Acceptance=new("supplied-lyrics","candidate",identity)};
song.Validate(musicCapability);Check(true,"supplied song retains explicit caller lyrics and its distinct shared case");
Denied(()=>(musicRequest with{Acceptance=new("supplied-lyrics","candidate",identity)}).Validate(musicCapability),"instrumental cannot spend the supplied-lyrics allowance");
Denied(()=>(song with{Acceptance=new("instrumental","candidate",identity)}).Validate(musicCapability),"supplied song cannot spend the instrumental allowance");
Denied(()=>(musicRequest with{Items=[musicRequest.Items[0] with{Music=null}]}).Validate(musicCapability),"music mode cannot be inferred from absent options");
Denied(()=>(song with{Items=[song.Items[0] with{Music=song.Items[0].Music! with{Lyrics=" "}}]}).Validate(musicCapability),"singing cannot invent missing caller lyrics");
Denied(()=>(musicRequest with{Items=[musicRequest.Items[0] with{Music=musicRequest.Items[0].Music! with{Lyrics="unexpected words"}}]}).Validate(musicCapability),"instrumental cannot silently ignore supplied lyrics");
Denied(()=>(request with{Items=[item with{Music=musicRequest.Items[0].Music}]}).Validate(image),"image cannot silently ignore music options");
Denied(()=>(audio with{Items=[audio.Items[0] with{Music=musicRequest.Items[0].Music}]}).Validate(sfx),"SFX cannot silently ignore music options");
Denied(()=>(musicRequest with{Items=[musicRequest.Items[0] with{Guidance=7}]}).Validate(musicCapability),"Turbo cannot accept ineffective CFG guidance");
Denied(()=>(musicRequest with{Items=[musicRequest.Items[0] with{NegativePrompt="unused"}]}).Validate(musicCapability),"Turbo cannot accept an ineffective negative prompt");
Denied(()=>(musicRequest with{Items=[musicRequest.Items[0] with{Seconds=9}]}).Validate(musicCapability),"music duration below the selected model minimum rejected");
Denied(()=>(musicRequest with{Items=[musicRequest.Items[0] with{Seconds=601}]}).Validate(musicCapability),"music duration above bounded maximum rejected");
Denied(()=>(musicRequest with{Items=[musicRequest.Items[0] with{Seconds=12.1}]}).Validate(musicCapability),"music cannot silently round unsupported fractional duration");
Denied(()=>(song with{Items=[song.Items[0] with{Music=song.Items[0].Music! with{VocalLanguage="zz"}}]}).Validate(musicCapability),"unsupported music language cannot silently fall back");
Denied(()=>(musicRequest with{Items=[musicRequest.Items[0] with{Seed=long.MaxValue}]}).Validate(musicCapability),"music seed outside the supported exact integer range rejected");
var musicJson=JsonSerializer.Serialize(musicRequest,MediaJson.Options);
Denied(()=>MediaJson.Read<MediaRequest>(Encoding.UTF8.GetBytes(musicJson.Replace("\"mode\":", "\"planningLm\":true,\"mode\":"))),"caller cannot enable an unregistered planning-LM setting");

var root=Path.Combine(AppContext.BaseDirectory,"test-fixtures","media-host-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
File.WriteAllText(Path.Combine(root,"media.json"),"owned test media policy; not user acceptance");
File.WriteAllText(Path.Combine(root,"style.json"),"owned test image style; not user acceptance");
string Hash(string file)=>Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(root,file))));
var mediaHash=Hash("media.json");var styleHash=Hash("style.json");
var policy=new JsonObject { ["purpose"]="local-media",["pipeline"]="LOCAL",["media"]=new JsonObject { ["path"]="media.json",["sha256"]=mediaHash } };
var host=new JsonObject
{
    ["schemaVersion"]="bubo-host-context/v1",["projectRoot"]=root,["task"]="media-contract-tests",
    ["roots"]=new JsonObject { ["state"]="state",["staging"]="staging",["mediaState"]="shared-media",["tools"]=new JsonArray("tools"),["inputs"]=new JsonArray("inputs"),["outputs"]=new JsonArray("runtime") },
    ["policy"]=policy,["references"]=new JsonArray(new JsonObject { ["path"]="media.json",["sha256"]=mediaHash,["kind"]="policy" },new JsonObject { ["path"]="style.json",["sha256"]=styleHash,["kind"]="policy" }),
    ["tools"]=new JsonArray(),["budgets"]=new JsonObject { ["timeoutSeconds"]=60,["maxLogBytes"]=4096 }
};
HostExecutionContext Load(JsonObject value)
{
    File.WriteAllText(Path.Combine(root,"context.json"),value.ToJsonString());
    return HostExecutionContext.Load(root,"context.json");
}
var context=Load(host);
Check(context.MediaOnly && !context.InspectionOnly && context.Engine.Count==0 && context.StyleSha256=="","audio policy needs no fabricated engine or style");
Check(context.MediaStateRoot=="shared-media","host declares shared media state independent of task execution state");
context.EnsureWriteAllowed("shared-media/acceptance-ledger.json");Check(true,"shared ledger root admitted");
Denied(()=>context.EnsureWriteAllowed("media.json"),"media policy remains readonly");
var bad=host.DeepClone().AsObject();bad["policy"]!["pipeline"]="CLOUD";Denied(()=>Load(bad),"media purpose cannot silently select CLOUD");
bad=host.DeepClone().AsObject();bad["policy"]!["engine"]=new JsonObject { ["effectiveEngine"]="THREEJS" };Denied(()=>Load(bad),"audio purpose cannot infer an engine");
bad=host.DeepClone().AsObject();bad["policy"]!.AsObject().Remove("media");Denied(()=>Load(bad),"explicit media policy is mandatory");
bad=host.DeepClone().AsObject();bad["roots"]!["mediaState"]="../outside";Denied(()=>Load(bad),"shared-state escape");
bad=host.DeepClone().AsObject();bad["policy"]!["imageStyle"]="unbound";Denied(()=>Load(bad),"unbound image-style value");
var styled=host.DeepClone().AsObject();styled["policy"]!["imageStyle"]=new JsonObject { ["path"]="style.json",["sha256"]=styleHash };
Check(Load(styled).StyleSha256==styleHash,"image style binds its registered policy hash");
bad=styled.DeepClone().AsObject();bad["policy"]!["imageStyle"]!["sha256"]=new string('f',64);Denied(()=>Load(bad),"mismatched image-style receipt");
context=Load(host);File.AppendAllText(Path.Combine(root,"media.json"),"changed");Denied(context.ValidateFresh,"stale media policy rejected");
Console.WriteLine($"PASS: {count} media request/host contract assertions; no model execution, license assertion or human acceptance.");
ApplicationFixtures.Run(Check);
CodecFixtures.Run(Check);
var gpuCount=await GpuGuardianFixtures.Run(Check);
Console.WriteLine($"PASS: {gpuCount} actual guardian lifecycle assertions; total{count} assertions.");
return 0;
