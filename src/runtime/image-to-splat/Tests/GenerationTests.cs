using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bubo.Host;
using Bubo.Splats.Domain;
using Bubo.Splats.Infrastructure;
using Hephaera.AssetProduction;

namespace Bubo.Splats.Tests;

internal static class GenerationTests
{
    static readonly string Catalog=new('d',64),Build=new('e',64),Resource=new('f',64);
    internal static void Run()
    {
        var valid=Sample();SplatGenerationRules.Validate(valid);Check.That(true,"explicit bounded generation controls accepted");
        foreach(var invalid in new[]{valid with{SchemaVersion="unknown"},valid with{Task="../task"},valid with{RunId="../run"},valid with{AttemptKind="retry"},
            valid with{Settings=valid.Settings with{Steps=9}},valid with{Settings=valid.Settings with{Steps=21}},valid with{Settings=valid.Settings with{Guidance=double.NaN}},
            valid with{Settings=valid.Settings with{Shift=double.PositiveInfinity}},valid with{Settings=valid.Settings with{ErodeRadius=9}},
            valid with{Limits=valid.Limits with{MaxInputPixels=16777217}},valid with{Limits=valid.Limits with{MaxAspectRatio=4.1}},
            valid with{Limits=valid.Limits with{MaxInputEdge=8193}},valid with{Limits=valid.Limits with{MaxOutputBytes=1024}},
            valid with{Limits=valid.Limits with{MinGpuFreeBytes=0}},valid with{Limits=valid.Limits with{MaxGpuAllocatedBytes=0}},
            valid with{Request=valid.Request with{Budgets=valid.Request.Budgets with{MaxProcessMemoryBytes=68719476737}}}})
            Check.Throws<InvalidOperationException>(()=>SplatGenerationRules.Validate(invalid),"invalid generation controls rejected");
        var approval=Approval(valid,"state/image-to-splat/ledger");
        foreach(var invalid in new[]{approval with{GenerationApproved=false},approval with{GpuAvailabilityConfirmed=false},approval with{MaximumCandidates=2},
            approval with{MaximumRevisions=2},approval with{SourceSha256=new('0',64)},approval with{StyleSha256=new('0',64)},approval with{CatalogSha256=new('0',64)},
            approval with{LedgerRoot="another-ledger"},approval with{ApprovedBy=""}})
            Check.Throws<InvalidOperationException>(()=>SplatGenerationRules.ValidateApproval(invalid,valid,Catalog,"state/image-to-splat/ledger"),"missing exact approval/GPU/budget gate rejected");
        Check.Throws<JsonException>(()=>SplatJson.Parse<SplatGenerationRequest>(Encoding.UTF8.GetBytes("{}")),"missing required generation fields rejected");
        Check.Throws<InvalidOperationException>(()=>SplatJson.Parse<JsonObject>(Encoding.UTF8.GetBytes("{\"x\":{\"y\":1,\"y\":2}}")),"nested duplicate descriptors rejected");
        var top=Environment.GetEnvironmentVariable("TMPDIR")??Environment.GetEnvironmentVariable("TEMP")??".bubo/test-temp";
        Directory.CreateDirectory(top);var root=Path.Combine(Path.GetFullPath(top),"splat-generation-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        // Fixture state is retained under the declared temporary test root; never delete an unchecked computed tree.
        var project=new Project(root);
        var request=project.Request("candidate1","candidate");
        var output=project.Generate(request);
        Check.That(output.ToString()!.Contains("generated-staged"),"synthetic owned-process port exercises generation success");
        Check.That(project.Process.Starts==1,"one admitted native execution per candidate; fixture starts no process/GPU");
        var package="staging/image-to-splat/test/candidate1/package.json";
        project.Reference(package,"input");
        var result=project.Service().VerifyPackage(package,default);
        Check.That(result.ToString()!.Contains("True"),"fresh independent reload verifies actual synthetic PLY package");
        var payload=Path.Combine(root,"staging/image-to-splat/test/candidate1/gaussian-32768.ply");
        var bytes=File.ReadAllBytes(payload);bytes[^1]^=1;File.WriteAllBytes(payload,bytes);
        Check.Throws<InvalidOperationException>(()=>project.Service().VerifyPackage(package,default),"post-export Gaussian tampering rejected");
        bytes[^1]^=1;File.WriteAllBytes(payload,bytes);
        var candidate=Directory.GetFiles(Path.Combine(root,"shared/image-to-splat/ledger"),"candidate.json",SearchOption.AllDirectories).Single();
        var candidateBytes=File.ReadAllBytes(candidate);
        var duplicate=project.Request("candidate2","candidate");
        Check.Throws<InvalidOperationException>(()=>project.Generate(duplicate),"same-source candidate cannot reset with new run ID");
        Check.That(candidateBytes.SequenceEqual(File.ReadAllBytes(candidate)),"original candidate claim remains byte-identical");
        project.State="different-task-state";
        Check.Throws<InvalidOperationException>(()=>project.Generate(project.Request("state-changed","candidate")),"changing per-task state cannot reset shared source budget");
        Check.That(candidateBytes.SequenceEqual(File.ReadAllBytes(candidate)),"state migration preserves original claim bytes");
        project.State="state";
        var revision=project.Request("revision1","revision");project.Process.FailAfterClaim=true;
        Check.Throws<InvalidOperationException>(()=>project.Generate(revision),"synthetic failed launch retains claimed revision");
        Check.That(Directory.GetFiles(Path.Combine(root,"shared/image-to-splat/ledger"),"revision.json",SearchOption.AllDirectories).Length==1,"interrupted/failed revision remains spent");
        project.Process.FailAfterClaim=false;
        Check.Throws<InvalidOperationException>(()=>project.Generate(project.Request("revision2","revision")),"failed revision cannot automatically retry or reset");
        Check.Throws<InvalidOperationException>(()=>project.Generate(project.Request("candidate1","candidate")),"existing run state cannot be overwritten");
        var other=new Project(root+"-second");var firstRevision=other.Request("revision-first","revision");
        Check.Throws<FileNotFoundException>(()=>other.Generate(firstRevision),"revision cannot precede its original candidate");
        Check.That(!Directory.GetFiles(Path.Combine(other.Root,"state"),"candidate.json",SearchOption.AllDirectories).Any(),"failed preclaim validation creates no stochastic attempt");
        var third=new Project(root+"-queued");var queued=third.Request("queued","candidate");third.Process.BeforeClaim=()=>File.AppendAllText(Path.Combine(third.Root,"inputs/request.json")," ");
        Check.Throws<InvalidOperationException>(()=>third.Generate(queued),"queued descriptor mutation rejected before claim");
        Check.That(!Directory.GetFiles(Path.Combine(third.Root,"state"),"candidate.json",SearchOption.AllDirectories).Any(),"stale queued request consumes no attempt");
        var fourth=new Project(root+"-revision-ok");fourth.Generate(fourth.Request("c","candidate"));fourth.Generate(fourth.Request("r","revision"));
        var revisionPackage="staging/image-to-splat/test/r/package.json";fourth.Reference(revisionPackage,"input");
        _=fourth.Service().VerifyPackage(revisionPackage,default);Check.That(true,"successful revision verifies real original candidate chain");
        var originalClaim=Directory.GetFiles(Path.Combine(fourth.Root,"shared"),"candidate.json",SearchOption.AllDirectories).Single();
        File.AppendAllText(originalClaim," ");
        Check.Throws<InvalidOperationException>(()=>fourth.Service().VerifyPackage(revisionPackage,default),"replaced/restored candidate record must match exact revision parent hash");
        var fifth=new Project(root+"-record-tamper");fifth.Generate(fifth.Request("c","candidate"));
        var artifact="staging/image-to-splat/test/c/artifact-32768.json";var objectFile=SplatJson.ReadFile<SplatArtifact>(Path.Combine(fifth.Root,artifact));
        objectFile=objectFile with{Ancestry=objectFile.Ancestry with{SourceRevision=new string('0',40)}};
        File.WriteAllBytes(Path.Combine(fifth.Root,artifact),JsonSerializer.SerializeToUtf8Bytes(objectFile,SplatJson.Options));
        var packagePath="staging/image-to-splat/test/c/package.json";var objectPackage=SplatJson.ReadFile<SplatGeneratedPackage>(Path.Combine(fifth.Root,packagePath));
        objectPackage=objectPackage with{Variants=[objectPackage.Variants[0] with{Artifact=new(){Path=artifact,Sha256=Workspace.Hash(Path.Combine(fifth.Root,artifact))}}]};
        File.WriteAllBytes(Path.Combine(fifth.Root,packagePath),JsonSerializer.SerializeToUtf8Bytes(objectPackage,SplatJson.Options));fifth.Reference(packagePath,"input");
        Check.Throws<InvalidOperationException>(()=>fifth.Service().VerifyPackage(packagePath,default),"rehashing an artifact cannot replace verified source-model ancestry");
        foreach(var variant in new[]{"over-budget","wrong-scope","missing-memory"})
        {
            var invalidMemory=new Project(root+"-memory-"+variant);
            invalidMemory.Process.ReportMutation=report=>
            {
                if(variant=="over-budget")report["memoryObservations"]!["peakCommittedBytes"]=268435457L;
                else if(variant=="wrong-scope")report["memoryObservations"]!["scope"]="aggregate-job";
                else report.Remove("memoryObservations");
            };
            Check.Throws<InvalidOperationException>(()=>invalidMemory.Generate(invalidMemory.Request("c","candidate")),"native memory receipt cannot omit, exceed or misstate its measured scope: "+variant);
        }
        Console.WriteLine("Generation lifecycle fixtures use synthetic approvals, installation ports and numeric PLYs. No installation, human acceptance, source decode, native model or GPU ran.");
    }
    static SplatGenerationRequest Sample()=>new(){SchemaVersion="bubo-splat-generation/v1",Task="test",RunId="run",AttemptKind="candidate",
        Request=Fixtures.Request() with {GaussianCounts=[32768],Budgets=Fixtures.Budgets() with{MaxDecodedBytes=16777216}},
        Installation=new(){Path="tools/receipt.json",Sha256=new('c',64)},Authorization=new(){Path="inputs/approval.json",Sha256=new('b',64)},
        Settings=new(){Steps=20,Guidance=3,Shift=3,ErodeRadius=1},Limits=new(){MaxInputPixels=16777216,MaxInputEdge=8192,MaxAspectRatio=4,
            MaxOutputBytes=16777216,MaxGpuAllocatedBytes=2147483648,MinGpuFreeBytes=268435456}};
    static SplatGenerationApproval Approval(SplatGenerationRequest request,string ledger)=>new(){SchemaVersion="bubo-splat-generation-approval/v1",Task=request.Task,
        LedgerRoot=ledger,SourceSha256=request.Request.Input.Sha256,StyleSha256=request.Request.StyleSha256,CatalogSha256=Catalog,MaximumCandidates=1,MaximumRevisions=1,
        GenerationApproved=true,GpuAvailabilityConfirmed=true,ApprovedBy="SYNTHETIC CPU TEST; no real approval",SourceRights=new(){Path="inputs/source-rights.json",Sha256=new('a',64)}};
    sealed class Project
    {
        public string Root {get;}
        public string State {get;set;}="state";
        readonly Dictionary<string,string> references=new(StringComparer.Ordinal);
        public FakeProcess Process {get;}=new();
        public Project(string root)
        {
            Root=root;Directory.CreateDirectory(root);
            foreach(var dir in new[]{"inputs","tools","bundle/resources/image-to-splat"})Directory.CreateDirectory(Path.Combine(root,dir));
            File.WriteAllText(Path.Combine(root,"inputs/source.png"),"SYNTHETIC; never decoded");
            File.WriteAllText(Path.Combine(root,"inputs/style.json"),"{}\n");File.WriteAllText(Path.Combine(root,"tools/receipt.json"),"{}\n");
            File.WriteAllText(Path.Combine(root,"tools/python.exe"),"SYNTHETIC; never executed");
            File.WriteAllText(Path.Combine(root,"bundle/resources/image-to-splat/worker.py"),"# SYNTHETIC fixture; never executed\n");
            var artifacts=new JsonArray();foreach(var pair in new Dictionary<string,string>{{"torch","2.7.1+cu128"},{"torchvision","0.22.1+cu128"},{"numpy","2.5.4"},{"safetensors","0.8.0"},{"pillow","13.0.0"},{"tqdm","4.70.1"}})
                artifacts.Add(new JsonObject{["name"]=pair.Key,["version"]=pair.Value});
            File.WriteAllText(Path.Combine(root,"bundle/resources/image-to-splat/catalog.json"),new JsonObject{["environmentCandidates"]=new JsonObject{["win-amd64"]=new JsonObject{["artifacts"]=artifacts}}}.ToJsonString());
            var inventory=new JsonObject{["schemaVersion"]=1,["files"]=new JsonObject{["resources/image-to-splat/catalog.json"]=Workspace.Hash(Path.Combine(root,"bundle/resources/image-to-splat/catalog.json")),["resources/image-to-splat/worker.py"]=Workspace.Hash(Path.Combine(root,"bundle/resources/image-to-splat/worker.py"))}};
            File.WriteAllText(Path.Combine(root,"bundle/resources/catalog.json"),inventory.ToJsonString());
            Directory.CreateDirectory(Path.Combine(root,"tools/models"));
            for(var index=0;index<5;index++)File.WriteAllText(Path.Combine(root,"tools/models/weight"+index+".safetensors"),"SYNTHETIC WEIGHT "+index);
            Reference("inputs/style.json","policy");Reference("tools/receipt.json","input");
        }
        SplatSource Identity(string path)=>new(){Path=path,Sha256=Workspace.Hash(Path.Combine(Root,path))};
        public void Reference(string path,string kind){references[path]=kind;SaveContext();}
        public string Request(string run,string kind)
        {
            var sample=Sample();var original=Identity("inputs/source.png");
            sample=sample with{RunId=run,AttemptKind=kind,Request=sample.Request with{Input=original,StyleSha256=Identity("inputs/style.json").Sha256},Installation=Identity("tools/receipt.json")};
            if(!File.Exists(Path.Combine(Root,"inputs/source-rights.json")))File.WriteAllBytes(Path.Combine(Root,"inputs/source-rights.json"),JsonSerializer.SerializeToUtf8Bytes(
                new SplatSourceRights {SchemaVersion="bubo-splat-source-rights/v1",SourceSha256=original.Sha256,Scope="local-generation",RightsBasis="owned",ApprovedBy="SYNTHETIC CPU TEST"},SplatJson.Options));
            Reference("inputs/source-rights.json","rights");
            if(!File.Exists(Path.Combine(Root,"inputs/approval.json")))File.WriteAllBytes(Path.Combine(Root,"inputs/approval.json"),JsonSerializer.SerializeToUtf8Bytes(Approval(sample,"shared/image-to-splat/ledger") with {SourceRights=Identity("inputs/source-rights.json")},SplatJson.Options));
            sample=sample with{Authorization=Identity("inputs/approval.json")};
            File.WriteAllBytes(Path.Combine(Root,"inputs/request.json"),JsonSerializer.SerializeToUtf8Bytes(sample,SplatJson.Options));
            Reference("inputs/approval.json","rights");Reference("inputs/request.json","input");return "inputs/request.json";
        }
        void SaveContext()
        {
            var context=new{schemaVersion="bubo-host-context/v1",projectRoot=Root,task="test",
                roots=new{state=State,staging="staging",mediaState="shared",tools=new[]{"tools"},inputs=new[]{"inputs"},outputs=Array.Empty<string>()},
                policy=new{pipeline="LOCAL",styleSha256=Workspace.Hash(Path.Combine(Root,"inputs/style.json")),media=new{path="inputs/style.json",sha256=Workspace.Hash(Path.Combine(Root,"inputs/style.json"))},engine=new{effectiveEngine="THREEJS"},libraries=Array.Empty<object>()},
                references=references.Select(x=>new{path=x.Key,sha256=Workspace.Hash(Path.Combine(Root,x.Key)),kind=x.Value}).ToArray(),tools=Array.Empty<object>(),
                budgets=new{timeoutSeconds=30,maxLogBytes=1048576,maxPrivateBytes=268435456},permissions=new{provisioning=false,promotion=false}};
            File.WriteAllBytes(Path.Combine(Root,"context.json"),JsonSerializer.SerializeToUtf8Bytes(context,SplatJson.Options));
        }
        public object Generate(string path)=>Service().Generate(path,default).GetAwaiter().GetResult();
        public SplatGeneration Service()
        {
            var host=Bubo.Host.HostExecutionContext.Load(Root,"context.json");var workspace=new Workspace(Root,host);
            return new(workspace,new AuditedResources(Path.Combine(Root,"bundle"),Workspace.Hash(Path.Combine(Root,"bundle/resources/catalog.json"))),Process,Build,Resource,new Installation(this));
        }
        sealed class Installation(Project project):ISplatInstallationVerifier
        {
            public PreparedSplatInstallation Verify(SplatSource receipt)=>new(Path.Combine(project.Root,"tools/python.exe"),Path.Combine(project.Root,"tools/models"),Path.Combine(project.Root,"tools/source"),
                new('a',64),Catalog,receipt.Path,receipt.Sha256,
                Enumerable.Range(0,5).Select(x=>new GpuInputIdentity("tools/models/weight"+x+".safetensors",Workspace.Hash(Path.Combine(project.Root,"tools/models/weight"+x+".safetensors")))).ToArray(),
                [Path.Combine(project.Root,"tools")]);
        }
    }
    sealed class FakeProcess:INativeProcess
    {
        public int Starts {get;private set;}
        public bool FailAfterClaim {get;set;}
        public Action? BeforeClaim {get;set;}
        public Action<JsonObject>? ReportMutation {get;set;}
        public bool SupportsPrivateMemoryLimit=>true;
        public Task Run(string executable,IEnumerable<string> arguments,string cwd,string log,int seconds,CancellationToken cancellation=default,long maximumLogBytes=16777216,IReadOnlyDictionary<string,string>? environment=null)
            =>throw new InvalidOperationException("Unapproved non-GPU process route");
        public async Task RunGpu(GpuProcessSpec spec,CancellationToken cancellation=default,Func<CancellationToken,Task>? beforeStart=null)
        {
            BeforeClaim?.Invoke();await beforeStart!(cancellation);Starts++;
            if(FailAfterClaim)throw new InvalidOperationException("SYNTHETIC launch interruption");
            var job=SplatJson.ReadFile<JsonObject>(spec.Arguments[5]);
            var output=job["outputDirectory"]!.GetValue<string>();Directory.CreateDirectory(output);
            var outputs=new JsonArray();
            foreach(var node in job["settings"]!["gaussianCounts"]!.AsArray())
            {
                var count=node!.GetValue<int>();var bytes=Fixtures.Ply(rows:Enumerable.Repeat(Fixtures.Row(),count).ToArray());
                var name="gaussian-"+count+".ply";File.WriteAllBytes(Path.Combine(output,name),bytes);
                outputs.Add(new JsonObject{["path"]=name,["sha256"]=Fixtures.Hash(bytes),["bytes"]=bytes.LongLength,["gaussianCount"]=count});
            }
            var report=new JsonObject{["schemaVersion"]="bubo-splat-worker-result/v1",["status"]="succeeded",["operation"]="generate",["backend"]="triposplat",
                ["sourceRevision"]="d8db9e018b413dd9c4a9fe22463781bf98e8e68d",["modelRevision"]="56a96e603204ec410c4da60c13ea4fa09a2169a9",
                ["inputSha256"]=job["input"]!["sha256"]!.DeepClone(),["settings"]=job["settings"]!.DeepClone(),
                ["python"]=new JsonObject{["version"]="3.12.16",["executable"]=spec.Executable},["versions"]=new JsonObject{["torch"]="2.7.1+cu128",["torchvision"]="0.22.1+cu128",["numpy"]="2.5.4",["safetensors"]="0.8.0",["pillow"]="13.0.0",["tqdm"]="4.70.1"},
                ["device"]=new JsonObject{["type"]="cuda",["index"]=0,["name"]="SYNTHETIC; no GPU",["totalBytes"]=2147483648L,["initialFreeBytes"]=1073741824L},
                ["preprocessing"]=new JsonObject{["inputWidth"]=1024,["inputHeight"]=1024,["mode"]="RGBA",["preparedWidth"]=1024,["preparedHeight"]=1024,
                    ["preparedPixelSha256"]=new string('a',64),["boundingBox"]=new JsonArray(1,1,1022,1022),["crop"]=new JsonArray(-100,-100,1100,1100),
                    ["maskSource"]="input-alpha",["maskValidationStage"]="cpu-before-model-load"},["loadingSeconds"]=.1,["preprocessingSeconds"]=.1,["encodingSeconds"]=.1,["inferenceSeconds"]=.1,["unloadingSeconds"]=.1,
                ["densityDecoding"]=new JsonArray(new JsonObject{["gaussianCount"]=32768,["seconds"]=.1}),["conditioningSha256"]=new string('a',64),["latentSha256"]=new string('b',64),["execution"]=new JsonObject{["device"]="cuda:0",["offload"]="none",["torchCuda"]="12.8",["decoderSampling"]="systematic; global seeded RNG",["globalSeed"]=42,
                    ["componentPrecisions"]=new JsonObject{["dinov3"]="bfloat16",["vae_encoder"]="bfloat16",["rmbg"]="float16",["flow_model"]="float16",["decoder"]="float16"}},["outputs"]=outputs,
                ["modelLoads"]=1,["stochasticInferences"]=1,["peakTorchAllocatedBytes"]=536870912L,["peakTorchReservedBytes"]=805306368L,
                ["cleanup"]=new JsonObject{["allocatedBeforeWorkspaceClearBytes"]=0,["reservedBeforeWorkspaceClearBytes"]=0,["endingAllocatedBytes"]=0,["endingReservedBytes"]=0,["mechanism"]="torch._C._cuda_clearCublasWorkspaces"},
                ["memoryObservations"]=new JsonObject{["scope"]="worker-process; not aggregate job",["peakCommittedBytes"]=134217728L,["currentPrivateBytes"]=33554432L,
                    ["peakWorkingSetBytes"]=167772160L,["currentWorkingSetBytes"]=50331648L,["measurement"]="Windows process memory counters"},
                ["cacheObservations"]=new JsonObject{["maximumObservedBytes"]=0,["byteLimit"]=67108864,["fileLimit"]=4096,["measurement"]="stage-sampled; not OS quota"},
                ["loadedModules"]=new JsonArray(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"kernel32.dll")),["aggregateDriverMemoryMeasured"]=false,["networkAttempts"]=0,["failureStage"]=null,["failure"]=null,["humanReview"]="pending",["promotion"]=false};
            ReportMutation?.Invoke(report);File.WriteAllText(spec.Arguments[6],report.ToJsonString());
        }
    }
}
