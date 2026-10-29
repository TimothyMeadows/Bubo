using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bubo.Media.Application;
using Bubo.Media.Domain;
using Bubo.Media.Infrastructure;
using OpenCaw.AssetProduction;
using HostExecutionContext = Bubo.Host.HostExecutionContext;

static class ApplicationFixtures
{
    public static void Run(Action<bool,string> check)
    {
        void Denied(Action action,string name)
        {
            try{action();}catch(Exception e) when(e is InvalidOperationException or IOException or JsonException){check(true,name);return;}
            throw new InvalidOperationException("Accepted "+name);
        }
        var root=Path.Combine(AppContext.BaseDirectory,"test-fixtures","admission-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(root,"inputs"));
        void Save(string path,object value)=>File.WriteAllText(Path.Combine(root,path),JsonSerializer.Serialize(value,MediaJson.Options));
        string Hash(string path)=>Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(root,path))));
        Save("inputs/media.json",new{purpose="OWNED FIXTURE POLICY; no generation or human acceptance"});
        Save("inputs/style.json",new{purpose="OWNED FIXTURE STYLE"});
        Save("inputs/install.json",new{purpose="OWNED RECEIPT; installation verifier not exercised"});
        Save("inputs/approval.json",new AcceptanceBudgetApproval("bubo-media-acceptance-budget/v1","shared-media",AcceptanceAttempt.Cases,1,1));
        (MediaRequest Request,MediaAdmission Admission,Workspace Workspace) Make(string task,string id,string kind="candidate",bool acceptance=true,bool style=true,bool shared=true,string output="staging",JsonArray? extraTools=null)
        {
            Save("inputs/rights.json",new MediaRights("bubo-media-rights/v1",task,"owned-test-fixture",true,true));
            var request=new MediaRequest {SchemaVersion="bubo-media-request/v1",RequestId=id,Backend="flux-schnell",Task=task,OutputDirectory=output,Rights=new("inputs/rights.json",Hash("inputs/rights.json")),Installation=new("inputs/install.json",Hash("inputs/install.json")),Settings=new("cuda:0","q8_0","text-encoder-cpu"),Budget=new(1,1048576,1048576,1048576,47),Items=[new(){Id="one",Prompt="OWNED FIXTURE ONLY",Seed=1,Steps=4,Guidance=1,Width=512,Height=512}],Acceptance=acceptance?new("image",kind,new("inputs/approval.json",Hash("inputs/approval.json"))):null};
            Save("inputs/request.json",request);
            var roots=new JsonObject{["state"]="state/"+task,["staging"]="staging",["tools"]=new JsonArray("tools"),["inputs"]=new JsonArray("inputs"),["outputs"]=new JsonArray("runtime")};
            if(shared)roots["mediaState"]="shared-media";
            var policy=new JsonObject{["purpose"]="local-media",["pipeline"]="LOCAL",["media"]=new JsonObject{["path"]="inputs/media.json",["sha256"]=Hash("inputs/media.json")}};
            if(style)policy["imageStyle"]=new JsonObject{["path"]="inputs/style.json",["sha256"]=Hash("inputs/style.json")};
            var references=new JsonArray();
            foreach(var (path,kindOf) in new[]{("inputs/media.json","policy"),("inputs/style.json","policy"),("inputs/install.json","input"),("inputs/rights.json","rights"),("inputs/approval.json","policy"),("inputs/request.json","input")})
                references.Add(new JsonObject{["path"]=path,["sha256"]=Hash(path),["kind"]=kindOf});
            var context=new JsonObject{["schemaVersion"]="bubo-host-context/v1",["projectRoot"]=root,["task"]=task,["roots"]=roots,["policy"]=policy,["references"]=references,["tools"]=extraTools??new JsonArray(),["budgets"]=new JsonObject{["timeoutSeconds"]=30,["maxLogBytes"]=4096}};
            File.WriteAllText(Path.Combine(root,"context.json"),context.ToJsonString());
            var workspace=new Workspace(root,HostExecutionContext.Load(root,"context.json"));return(request,new MediaAdmission(workspace),workspace);
        }
        var first=Make("first-task","first");var admitted=first.Admission.Read("inputs/request.json");
        check(admitted.Request.Task=="first-task" && admitted.OutputDirectory=="staging/first","actual admission binds scoped rights and fresh non-runtime output");
        check(!Directory.Exists(Path.Combine(root,"state")) && !Directory.Exists(Path.Combine(root,"staging")),"admission performs no state/output writes");
        var bad=Make("bad-runtime","bad-runtime",output:"runtime");Denied(()=>bad.Admission.Read("inputs/request.json"),"runtime output rejected despite broader host write permission");
        bad=Make("bad-style","bad-style",style:false);Denied(()=>bad.Admission.Read("inputs/request.json"),"image cannot omit actual style policy");
        bad=Make("bad-shared","bad-shared",shared:false);Denied(()=>bad.Admission.Read("inputs/request.json"),"acceptance cannot reset into a per-task default ledger");
        bad=Make("stale-rights","stale-rights");File.AppendAllText(Path.Combine(root,"inputs/rights.json"),"changed");Denied(()=>bad.Admission.Read("inputs/request.json"),"mutated rights rejected before output writes");
        first=Make("first-task","first");admitted=first.Admission.Read("inputs/request.json");
        var ledger=new AcceptanceLedger(first.Workspace);var candidate=ledger.Claim(admitted)!;var original=File.ReadAllBytes(Path.Combine(root,candidate));
        check(File.Exists(Path.Combine(root,candidate)),"candidate reservation persists an actual immutable claim");
        Denied(()=>ledger.Claim(admitted),"failed/unknown candidate is not automatically refunded");
        var second=Make("second-task","second");var secondAdmitted=second.Admission.Read("inputs/request.json");
        Denied(()=>new AcceptanceLedger(second.Workspace).Claim(secondAdmitted),"a later task shares and cannot reset the original candidate budget");
        var revision=Make("second-task","revision","revision");var revisionAdmitted=revision.Admission.Read("inputs/request.json");
        var revisionPath=new AcceptanceLedger(revision.Workspace).Claim(revisionAdmitted)!;
        var claim=MediaJson.Read<AcceptanceClaim>(File.ReadAllBytes(Path.Combine(root,revisionPath)));
        check(claim.CandidateClaimSha256==Convert.ToHexStringLower(SHA256.HashData(original)),"revision binds exact original claim ancestry");
        var third=Make("third-task","third","revision");
        Denied(()=>new AcceptanceLedger(third.Workspace).Claim(third.Admission.Read("inputs/request.json")),"third acceptance attempt rejected across task boundaries");
        check(File.ReadAllBytes(Path.Combine(root,candidate)).SequenceEqual(original),"later claims preserve the original candidate bytes");
        var wrong=Make("service-wrong-seed","service-wrong-seed",acceptance:false);
        Denied(()=>new MediaService(wrong.Workspace,new ReceiptFixture(),new OutputFixture(wrong.Workspace,true)).Generate("inputs/request.json",default).GetAwaiter().GetResult(),"SIMULATED full service rejects wrong actual seed before manifest publication");
        check(File.Exists(Path.Combine(root,"state/service-wrong-seed/media/service-wrong-seed/failure.json"))&&!File.Exists(Path.Combine(root,"state/service-wrong-seed/media/service-wrong-seed/manifest.json")),"SIMULATED rejected output retains failure evidence without publishing a successful manifest");
        var valid=Make("service-valid","service-valid",acceptance:false);
        var generated=new MediaService(valid.Workspace,new ReceiptFixture(),new OutputFixture(valid.Workspace,false),new JsonObject{["fixture"]="SIMULATED caller"}).Generate("inputs/request.json",default).GetAwaiter().GetResult();
        var manifest=JsonSerializer.SerializeToNode(generated,MediaJson.Options)!.AsObject();
        check(manifest["outputs"]![0]!["actual"]!["seed"]!.GetValue<long>()==1 && manifest["workerSha256"]!.GetValue<string>()==new string('c',64)
            && manifest["runtimeBuild"]!["fixture"]!.GetValue<string>()=="SIMULATED caller","SIMULATED staged manifest retains actual sampling and worker/caller identities");
        check(manifest["humanReview"]!.GetValue<string>()=="pending" && manifest["promotion"]!.GetValue<bool>()==false,"SIMULATED successful service grants no human acceptance or promotion");
        if(OperatingSystem.IsWindows())
        {
            var module=Path.Combine(root,"owned-platform-fixture.dll");File.WriteAllText(module,"SIMULATED module identity; never loaded");var hash=Workspace.Hash(module);
            JsonArray Tools(string kind)=>new(new JsonObject{["path"]=module,["sha256"]=hash,["kind"]=kind});
            var ordinary=Make("module-ordinary","module-ordinary",acceptance:false,extraTools:Tools("tool"));var prepared=new ReceiptFixture().Verify("flux-schnell",ordinary.Request.Installation);
            Denied(()=>new FixedMediaWorker(ordinary.Workspace,new NoNative()).ValidateModules(prepared,[module]),"SIMULATED ordinary executable registration cannot authorize a platform DLL");
            var platform=Make("module-platform","module-platform",acceptance:false,extraTools:Tools("platform-module"));var auditor=new FixedMediaWorker(platform.Workspace,new NoNative());
            auditor.ValidateModules(prepared,[module]);check(true,"SIMULATED exact host-platform module hash is admitted without broad directory trust");
            var shadow=prepared with {Files=[new("tools/owned-platform-fixture.dll",hash,new FileInfo(module).Length)]};
            Denied(()=>auditor.ValidateModules(shadow,[module]),"SIMULATED platform permission cannot shadow a pinned dependency basename");
            File.AppendAllText(module,"modified");Denied(()=>auditor.ValidateModules(prepared,[module]),"SIMULATED host-platform module mutation is rejected");
        }
        else Console.WriteLine("Windows platform-module audit fixtures not run on this platform");
        var adapter=Make("adapter-numbered","adapter-numbered",acceptance:false);
        AdapterFixtures.Run(adapter.Workspace,adapter.Request,new ReceiptFixture().Verify("flux-schnell",adapter.Request.Installation),check);
        foreach(var (label,protectedPath) in new[]{("output","staging"),("state","state/overlap-state"),("ledger","shared-media")})
        {
            var overlapping=Make("overlap-"+label,"overlap-"+label);
            var before=Directory.GetFiles(root,"*",SearchOption.AllDirectories).ToDictionary(path=>path,File.ReadAllBytes);
            var never=new NeverWorker();
            var service=new MediaService(overlapping.Workspace,new ReceiptFixture([Path.Combine(root,protectedPath)]),never);
            Denied(()=>service.Generate("inputs/request.json",default).GetAwaiter().GetResult(),"SIMULATED "+label+" installation overlap rejected before generation");
            check(!never.Called && before.Count==Directory.GetFiles(root,"*",SearchOption.AllDirectories).Length && before.All(pair=>pair.Value.SequenceEqual(File.ReadAllBytes(pair.Key))),
                "SIMULATED "+label+" overlap creates no state/output or acceptance claim and preserves dependencies");
        }
        var probeOverlap=Make("probe-overlap","probe-overlap",acceptance:false);
        var probeService=new MediaService(probeOverlap.Workspace,new ReceiptFixture([Path.Combine(root,"state/probe-overlap")]),new NeverWorker());
        Denied(()=>probeService.Probe("inputs/request.json",(_,_,_,_)=>throw new InvalidOperationException("Unexpected probe launch"),default).GetAwaiter().GetResult(),"SIMULATED probe installation overlap rejected");
        check(!Directory.Exists(Path.Combine(root,"state/probe-overlap")),"SIMULATED rejected probe creates no installation cache/state");
    }
    sealed class ReceiptFixture(IReadOnlyList<string>? immutable=null):IInstallationVerifier
    {
        public PreparedInstallation Verify(string backend,FileIdentity receipt)=>new(backend,receipt.Path,receipt.Sha256,"SIMULATED worker","SIMULATED model",null,new Dictionary<string,string>(),new string('a',64),new string('b',64),[],new string('c',64),new string('d',64),new string('e',64),immutable);
    }
    sealed class NeverWorker:IMediaWorker
    {
        public bool Called;
        public Task<WorkerBatchResult> Run(MediaRequest request,PreparedInstallation installation,string attemptDirectory,CancellationToken cancellation,Func<CancellationToken,Task>? beforeStart=null)
        {Called=true;throw new InvalidOperationException("Unexpected worker launch in overlap fixture");}
    }
    sealed class NoNative:INativeProcess
    {
        public Task Run(string executable,IEnumerable<string> arguments,string cwd,string log,int seconds,CancellationToken cancellation=default,long maximumLogBytes=16777216,IReadOnlyDictionary<string,string>? environment=null)=>throw new InvalidOperationException("Fixture forbids native launches");
    }
    sealed class OutputFixture(Workspace workspace,bool wrongSeed):IMediaWorker
    {
        public async Task<WorkerBatchResult> Run(MediaRequest request,PreparedInstallation installation,string attemptDirectory,CancellationToken cancellation,Func<CancellationToken,Task>? beforeStart=null)
        {
            if(beforeStart is not null)await beforeStart(cancellation);
            var directory=workspace.ResolveWrite(request.OutputDirectory+"/"+request.RequestId);Directory.CreateDirectory(directory);
            var item=request.Items[0];var raw=Path.Combine(directory,"fixture.rgb");File.WriteAllBytes(raw,new byte[item.Width!.Value*item.Height!.Value*3]);
            var image=Path.Combine(directory,"fixture.png");ImagePng.Write(raw,image,item.Width.Value,item.Height.Value,3);
            var metadata=Path.Combine(directory,"fixture.json");File.WriteAllText(metadata,"{\"fixture\":\"SIMULATED pixels; no model or GPU\"}");
            return new("bubo-flux-worker/v1",new("cuda:0","SIMULATED fixture; no GPU","q8_0","text-encoder-cpu",[]),.1,.1,
                [new(item.Id,workspace.Relative(image),.1,item.Width,item.Height,null,null,null,null,null,null,null,wrongSeed?item.Seed+1:item.Seed,item.Steps,item.Guidance,"SIMULATED scheduler",new(workspace.Relative(metadata),workspace.HashFile(metadata)))],1,true);
        }
    }
}
