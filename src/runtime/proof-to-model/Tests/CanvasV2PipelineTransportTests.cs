using System.Text;
using System.Text.Json.Nodes;
using Hephaera.ProofToModel.Application;
using Hephaera.ProofToModel.Domain;
using Hephaera.ProofToModel.Infrastructure;
using NativeWorkspace = Hephaera.ProofToModel.Infrastructure.Workspace;

/// <summary>CPU-only synthetic process port. Never launches a DLL/GPU/native engine, and asserts no real approvals.</summary>
static class CanvasV2PipelineTransportTests
{
    const string TaskName = "cv2-fixture";
    const string TaskRoot = ".hephaera/.cache/tasks/" + TaskName;
    const string ModelDirectory = ".local/tools/proof-to-model/models/synthetic-mv";
    static readonly string[] ModelNames = ["dinov3.gguf", "pixal3d_naf.gguf", "pixal3d_ss_flow_mv.gguf", "pixal3d_shape_flow_512_mv.gguf", "pixal3d_shape_flow_1024_mv.gguf", "pixal3d_tex_flow_1024_mv.gguf", "ss_dec.gguf", "shape_dec.gguf", "tex_dec.gguf"];
    static void Demand(bool condition, string message) { if (!condition) throw new InvalidOperationException("SYNTHETIC TRANSPORT: " + message); }
    public static async Task Run(Workspace parent, Action<bool,string> check, Func<Func<Task>,string,Task> reject)
    {
        var good = await Fixture.Create(parent, "good");
        check(good.State.Approvals.Count==0 && good.State.Stages.Count==0, "V2 fresh init transfers no prior stage or approval");
        var middle=good.W.Load<Manifest>(good.MiddleState+"/manifest.json");
        var inherited=CanvasStageLineage.Parse(Encoding.UTF8.GetBytes(good.W.Load<JsonObject>(TaskRoot+"/"+good.Name+"-lineage.json").ToJsonString()));
        check(middle.Stages["preprocess"].Status=="failed" && middle.Approvals.Any(a=>a.Kind=="proof-set") && inherited.InheritedLineage is null && inherited.Limit("preprocess",1)==2, "Actual retained intermediate has synthetic prior proof approval/failure and original v1 parser keeps ceiling2; fresh v2 inherits neither stage nor approval");
        await good.Register();
        var before=await good.Status();
        check(before["stageLineage"]!["consumed"]!["classify"]!.GetValue<int>()==4 && before["stageLineage"]!["consumed"]!["prepare-proofs"]!.GetValue<int>()==3 && before["stageLineage"]!["consumed"]!["preprocess"]!.GetValue<int>()==2, "Actual v2 status sums original2classify including EMPTY plus immediate1 plus new1, proof1+1+1, preprocess1+1");
        await reject(()=>good.Execute("preprocess"), "New v2 subject still requires new proof-set approval");
        check(good.Process.Mattes==0 && good.Process.Models==0, "Missing current proof approval cannot launch simulated child");
        await good.Approve("proof-set"); await good.Execute("preprocess");
        check(good.Process.Mattes==4 && good.Process.Models==0, "Exactly four simulated matte children on v2 source-canvas branch; no model yet");
        var dataset=good.W.Load<JsonObject>(good.State.Stages["preprocess"].Artifacts["dataset"]);
        check(dataset["views"]!.AsArray().Count==4 && dataset["views"]!.AsArray().All(v=>v!["normalization"]!["operation"]!.GetValue<string>()=="birefnet-preserve-canvas-and-common-square-pad"), "V2 actual pipeline produces four nominal camera records with neural branch source-canvas bookkeeping");
        await reject(()=>good.Execute("reconstruct"), "Separate new preprocessing approval remains necessary");
        check(good.Process.Models==0, "Missing preprocessing review cannot launch simulated model");
        await good.Approve("preprocessing"); await good.Execute("reconstruct");
        var after=await good.Status();
        check(after["stageLineage"]!["consumed"]!["preprocess"]!.GetValue<int>()==3 && after["stageLineage"]!["limits"]!["preprocess"]!.GetValue<int>()==3 && after["stageLineage"]!["consumed"]!["reconstruct"]!.GetValue<int>()==1, "V2 cumulative old+immediate+new preprocess3 and model1, no reset");
        await reject(()=>good.Execute("preprocess"), "Fourth preprocessing cannot fit ceiling3");
        await reject(()=>good.Execute("reconstruct"), "Second model cannot fit unchanged model1");
        await reject(()=>good.Execute("classify"), "Fifth classification cannot fit unchanged metadata4");
        check(good.Process.Mattes==4 && good.Process.Models==1, "Exhausted stage/model/metadata checks launch no extra simulated child");
        foreach(var scope in new[]{good.PriorState,good.PriorArt,good.MiddleState,good.MiddleArt,".local/tools/canvas"})
            await reject(()=>Task.Run(()=>good.W.HostContext!.EnsureWriteAllowed(scope)), "Real selected HostContext keeps ancestor/bundle readonly: "+scope);
        foreach(var row in new[]{("missing-grant","matching registered host reference"),("wrong-grant","Exact one-additional-stage"),("stale-prior","Canvas tool/input bytes changed"),("samepath-inherited","Immediate prior immutable stage inputs"),("add-original","Prior attempt directories/history changed"),("remove-empty","Prior attempt directories/history changed"),("add-immediate","Prior attempt directories/history changed")})
        {
            SimulatedProcess? observed=null;var failure="";
            await reject(async()=>{try{await Fixture.Create(parent,row.Item1,process=>observed=process);}catch(InvalidOperationException error){failure=error.Message;throw;}}, "Actual v2 init rejects "+row.Item1+" before simulated child");
            check(observed is not null && observed.Mattes==0 && observed.Models==0 && failure.Contains(row.Item2,StringComparison.Ordinal), "Refusal reaches intended native v2 validation, not unrelated fixture failure: "+row.Item1);
        }

    }
    sealed class Fixture
    {
        public Workspace Data {get;}
        public NativeWorkspace W {get; private set;} = null!;
        public Dictionary<string,string> Options {get;} = new();
        public SimulatedProcess Process {get;}
        public string Name {get;}
        public string Version {get;private set;}="canvas";
        public string ManifestPath => TaskRoot + "/proof-to-model/" + Name + "/"+Version+"/manifest.json";
        public string MiddleState => TaskRoot+"/proof-to-model/"+Name+"/canvas";
        public string MiddleArt => ".art-staging/proof-to-model/"+Name+"/canvas";
        public string PriorState => TaskRoot + "/proof-to-model/" + Name + "/old";
        public string PriorArt => ".art-staging/proof-to-model/" + Name + "/old";
        public string LineagePath {get;private set;}
        public string OriginalEmpty => PriorArt+"/attempts/classify-empty";
        public Manifest State => W.Load<Manifest>(ManifestPath);
        Fixture(string root,string name) { Data = new Workspace(root); Name=name; LineagePath=TaskRoot+"/"+name+"-lineage.json"; Process=new SimulatedProcess(Data); }
        public Task Execute(string command) => new Pipeline(W,Options,Process,new AlphaProcessor()).Execute(command,default);
        public static async Task<Fixture> Create(Workspace parent,string name,Action<SimulatedProcess>? beforeInit=null)
        {
            var root=parent.Resolve("cv2-" + name + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            var f=new Fixture(root,name); var workspace=f.Data;
            File.WriteAllText(workspace.Resolve("STYLE.md"), "Primary Hephaera art pipeline:\n- PROOF_TO_MODEL\n");
            workspace.Save("rights.json",new {authorized=true,source="SYNTHETIC CPU FIXTURE ONLY; no real production rights"});
            workspace.Save("recipe.json",new { targetFaces=100, textureSize=32, cageExtrusion=.1,maxRayDistance=.1,uvMargin=.01,lodRatios=new[]{.5},colliderMaxFaces=100,maxTriangles=1000,maxMaterials=3,maxTextureBytes=100000,parts=Array.Empty<string>(),riggingRequired=false,animationRequired=false,proofPreparation=new{maxSetRevisions=4}});
            var models = new JsonArray();
            Directory.CreateDirectory(workspace.Resolve(ModelDirectory));
            foreach (var model in ModelNames)
            {
                var path = ModelDirectory + "/" + model;
                File.WriteAllText(workspace.Resolve(path), "SIMULATED MODEL IDENTITY ONLY; NOT GGUF WEIGHTS: " + model);
                models.Add(new JsonObject { ["path"] = path, ["sha256"] = workspace.HashFile(workspace.Resolve(path)), ["license"] = "synthetic-test-fixture", ["licenseAccepted"] = true });
            }
            File.WriteAllText(workspace.Resolve("simulated-engine.bin"), "SIMULATED EXECUTABLE IDENTITY; NO NATIVE ENGINE IS EXECUTED");
            var toolchain = new JsonObject
            {
                ["schemaVersion"] = 1, ["modelFamily"] = "mv", ["viewCount"] = 6, ["modelDir"] = ModelDirectory,
                ["engine"] = new JsonObject { ["path"] = "simulated-engine.bin", ["sha256"] = workspace.HashFile(workspace.Resolve("simulated-engine.bin")), ["version"] = "synthetic-process-fixture" },
                ["models"] = models, ["files"] = new JsonArray(),
                ["limits"] = new JsonObject { ["maxAttempts"] = 1, ["timeoutSeconds"] = 10, ["maxLogBytes"] = 100000 },
                ["reconstruction"] = new JsonObject
                {
                    ["arguments"] = new JsonArray("--views", "{input}", "--models", "{modelDir}", "--pixal3d-weights", "mv", "--num-views", "6", "--res", "1024", "--require-gpu", "{output}")
                }
            };
            {
                toolchain["reconstruction"]!["arguments"] = new JsonArray("--views", "{input}", "--models", "{modelDir}", "--pixal3d-weights", "mv", "--num-views", "6", "--res", "1024", "--seed", "42", "--gss", "10", "--require-gpu", "{output}");
                
                
            }
            toolchain["preprocessing"] = new JsonObject { ["arguments"] = new JsonArray("--models", "{modelDir}", "--bg-removal", "birefnet", "--bg-only", "--dump-bg", "{input}", "{output}"), ["outputPattern"] = "{outputStem}_cutout.png" };
            workspace.Save("toolchain.json",toolchain);
            var originals=new JsonArray();
            for(var i=0;i<6;i++)
            {
                var pixels=new byte[8*8*4]; for(var p=0;p<64;p++){pixels[4*p]=160; pixels[4*p+1]=(byte)(30+i);pixels[4*p+2]=70;pixels[4*p+3]=255;}
                var path="source-"+i+".png"; RgbaPng.Write(workspace.Resolve(path),new(8,8,pixels)); originals.Add(new JsonObject{["id"]="s"+i,["path"]=path});
            }
            workspace.Save("sources.json",new JsonObject{["schemaVersion"]=1,["images"]=originals});
            workspace.Save("profile.json",new JsonObject{["schemaVersion"]=1,["id"]="cardinal-four-v1",["mode"]="multi-view-four",["modelFamily"]="mv",["viewCount"]=4,["views"]=new JsonArray("front","right","back","left"),["baseToolchain"]=Ref(workspace,"toolchain.json")});
            foreach(var pair in new Dictionary<string,string>{["task"]=TaskName,["asset"]=name,["version"]="old",["actor"]="synthetic-builder",["sources"]="sources.json",["rights"]="rights.json",["recipe"]="recipe.json",["toolchain"]="toolchain.json",["mode"]="multi-view-four",["conditioning-profile"]="profile.json"})f.Options[pair.Key]=pair.Value;
            await new Pipeline(workspace,f.Options,f.Process,new AlphaProcessor()).Execute("init",default);
            // Original metadata stages run through the existing fixture-only
            // bootstrap adapter, before either immutable ancestor is sealed.
            var originalOptions=new Dictionary<string,string>(f.Options){["manifest"]=f.PriorState+"/manifest.json"};
            var originalState=workspace.Load<Manifest>(originalOptions["manifest"]);
            var originalClassification=TaskRoot+"/"+name+"-old-classification.json";
            workspace.Save(originalClassification,PreparationTests.Classification(workspace,originalState));originalOptions["classification"]=originalClassification;
            await new Pipeline(workspace,originalOptions,f.Process,new AlphaProcessor()).Execute("classify",default);
            originalState=workspace.Load<Manifest>(originalOptions["manifest"]);
            var originalProofs=PreparationTests.ProofSet(workspace,originalState);originalProofs["schemaVersion"]=2;originalProofs["conditioningProfile"]="cardinal-four-v1";originalProofs["viewCount"]=4;originalProofs["views"]!.AsArray().RemoveAt(5);originalProofs["views"]!.AsArray().RemoveAt(4);
            var originalProofPath=TaskRoot+"/"+name+"-old-proofs.json";workspace.Save(originalProofPath,originalProofs);originalOptions["proof-set"]=originalProofPath;
            await new Pipeline(workspace,originalOptions,f.Process,new AlphaProcessor()).Execute("prepare-proofs",default);
            var priorPath=f.PriorState+"/manifest.json"; var prior=workspace.Load<Manifest>(priorPath);
            var failed=f.PriorArt+"/attempts/preprocess-failed";Directory.CreateDirectory(workspace.Resolve(failed));
            prior.History.Add(new JsonObject{["event"]="stage-start",["stage"]="preprocess",["attempt"]=failed});
            prior.Stages["preprocess"]=new Stage{Status="failed",Attempt=failed,Error="SYNTHETIC historical failure, no real native work"};
            Directory.CreateDirectory(workspace.Resolve(f.OriginalEmpty));
            workspace.Save(priorPath,prior);
            Directory.CreateDirectory(workspace.Resolve(".local/tools/canvas")); File.WriteAllText(workspace.Resolve(".local/tools/canvas/matting.bin"),"SYNTHETIC MATTER; never executable");
            var auth=TaskRoot+"/"+name+"-synthetic-authority.json";workspace.Save(auth,new {synthetic=true,decision="approved",scope="CPU fixture only, not a user or production grant"});
            var profilePath=TaskRoot+"/"+name+"-matting.json";
            workspace.Save(profilePath,new JsonObject{["schemaVersion"]=1,["id"]=CanvasMattingProfile.Id,["mode"]="multi-view-four",["baseToolchain"]=Ref(workspace,"toolchain.json"),["executable"]=Ref(workspace,".local/tools/canvas/matting.bin",true),["files"]=new JsonArray(Ref(workspace,".local/tools/canvas/matting.bin",true)),["maxMattingChildren"]=4});
            var attempts=new JsonObject();var dirs=new JsonObject();var counts=new JsonObject();var limits=new JsonObject();
            foreach(var stage in CanvasStageLineage.Stages){attempts[stage]=new JsonArray(CanvasAttemptInventory.Scan(workspace,f.PriorArt,prior.History,stage).Select(p=>(JsonNode?)JsonValue.Create(p)).ToArray());dirs[stage]=new JsonArray(CanvasAttemptInventory.Directories(workspace,f.PriorArt,stage).Select(p=>(JsonNode?)JsonValue.Create(p)).ToArray());counts[stage]=prior.History.Count(item=>item["event"]?.GetValue<string>()=="stage-start"&&item["stage"]?.GetValue<string>()==stage);limits[stage]=stage is "classify" or "prepare-proofs"?4:stage=="preprocess"?2:1;}
            workspace.Save(f.LineagePath,new JsonObject{["schemaVersion"]=1,["kind"]="proof-to-model-stage-lineage",["decision"]="approved",["task"]=TaskName,["asset"]=name,["sourceVersion"]="old",["targetVersion"]="canvas",["authorization"]=Ref(workspace,auth),["inputApproval"]=Ref(workspace,auth),["priorManifest"]=Ref(workspace,priorPath),["priorStateRoot"]=f.PriorState,["priorStaging"]=f.PriorArt,["priorAttempts"]=attempts,["priorDirectories"]=dirs,["priorHistoryCounts"]=counts,["stageLimits"]=limits});
            var refs=new JsonArray(); foreach(var file in new[]{"STYLE.md","rights.json","recipe.json","toolchain.json","profile.json","sources.json",auth,profilePath,f.LineagePath,priorPath}.Concat(Enumerable.Range(0,6).Select(i=>"source-"+i+".png"))) {var r=Ref(workspace,file);r["kind"]=file=="STYLE.md"?"policy":"input";refs.Add(r);}
            var tools=new JsonArray(); foreach(var file in new[]{"simulated-engine.bin",".local/tools/canvas/matting.bin"})tools.Add(new JsonObject{["path"]=workspace.Resolve(file),["sha256"]=workspace.HashProjectFile(file)});
            workspace.Save("strict-context.json",new JsonObject{
                ["schemaVersion"]="bubo-host-context/v1",["projectRoot"]=root,["task"]=TaskName,
                ["roots"]=new JsonObject{["state"]=".hephaera/.cache/tasks",["staging"]=".art-staging",["tools"]=new JsonArray(".local/tools"),["inputs"]=new JsonArray(f.PriorState,f.PriorArt,".local/tools/canvas"),["outputs"]=new JsonArray("assets/models")},
                ["policy"]=new JsonObject{["pipeline"]="PROOF_TO_MODEL",["styleSha256"]=workspace.HashProjectFile("STYLE.md"),["engine"]=new JsonObject{["effectiveEngine"]="THREEJS"},["libraries"]=new JsonArray()},
                ["references"]=refs,["tools"]=tools,["budgets"]=new JsonObject{["timeoutSeconds"]=30,["maxLogBytes"]=100000},["permissions"]=new JsonObject{["provisioning"]=false,["promotion"]=false}});
            f.W=new NativeWorkspace(root,Bubo.Host.HostExecutionContext.Load(root,"strict-context.json"));f.Process.Context=f.W.HostContext;
            f.Options["version"]="canvas";f.Options["preprocessing-profile"]=profilePath;f.Options["stage-lineage"]=f.LineagePath;
            await f.Execute("init");f.Options["manifest"]=f.ManifestPath;
            // These are actual synthetic v1 classify/proof registration/record calls.
            // Both historical failure states are fixture material, not evidence
            // that a real native/matting process ran or a user approved anything.
            await f.Register();await f.Approve("proof-set");
            var middle=f.State;var middleFailed=f.MiddleArt+"/attempts/preprocess-failed";
            Directory.CreateDirectory(workspace.Resolve(middleFailed));
            middle.History.Add(new JsonObject{["event"]="stage-start",["stage"]="preprocess",["attempt"]=middleFailed});
            middle.Stages["preprocess"]=new Stage{Status="failed",Attempt=middleFailed,Error="SYNTHETIC historical admission failure, no process",Inputs=new(){[profilePath]=workspace.HashProjectFile(profilePath),[f.LineagePath]=workspace.HashProjectFile(f.LineagePath)}};
            workspace.Save(f.MiddleState+"/manifest.json",middle);
            var inheritedPath=f.LineagePath;var inherited=Ref(workspace,inheritedPath);
            f.Version="cwd";f.LineagePath=TaskRoot+"/"+name+"-cwd-lineage.json";
            var freshProfile=TaskRoot+"/"+name+"-cwd-matting.json";workspace.Save(freshProfile,workspace.Load<JsonObject>(profilePath));
            var grantPath=TaskRoot+"/"+name+"-SYNTHETIC-extra-stage.json";
            var immediateRef=Ref(workspace,f.MiddleState+"/manifest.json");
            var grant=new JsonObject{["schemaVersion"]=1,["kind"]="proof-to-model-additional-stage",["authorized"]=true,["actor"]="SYNTHETIC CPU FIXTURE ONLY; NOT A REAL HUMAN OR PRODUCTION GRANT",["task"]=TaskName,["asset"]=name,["sourceVersion"]="canvas",["targetVersion"]="cwd",["stage"]="preprocess",["priorManifest"]=immediateRef.DeepClone(),["inheritedLineage"]=inherited.DeepClone(),["consumedAttempts"]=2,["maximumAttempts"]=3,["maximumChildren"]=4,["automaticRetries"]=false};
            var a2=new JsonObject();var d2=new JsonObject();var h2=new JsonObject();var l2=new JsonObject();
            foreach(var stage in CanvasStageLineage.Stages){a2[stage]=new JsonArray(CanvasAttemptInventory.Scan(workspace,f.MiddleArt,middle.History,stage).Select(p=>(JsonNode?)JsonValue.Create(p)).ToArray());d2[stage]=new JsonArray(CanvasAttemptInventory.Directories(workspace,f.MiddleArt,stage).Select(p=>(JsonNode?)JsonValue.Create(p)).ToArray());h2[stage]=middle.History.Count(item=>item["event"]?.GetValue<string>()=="stage-start"&&item["stage"]?.GetValue<string>()==stage);l2[stage]=stage is "classify" or "prepare-proofs"?4:stage=="preprocess"?3:1;}
            var chained=new JsonObject{["schemaVersion"]=2,["kind"]="proof-to-model-stage-lineage",["decision"]="approved",["task"]=TaskName,["asset"]=name,["sourceVersion"]="canvas",["targetVersion"]="cwd",["authorization"]=RefPlaceholder(grantPath),["inputApproval"]=Ref(workspace,auth),["priorManifest"]=immediateRef.DeepClone(),["priorStateRoot"]=f.MiddleState,["priorStaging"]=f.MiddleArt,["priorAttempts"]=a2,["priorDirectories"]=d2,["priorHistoryCounts"]=h2,["stageLimits"]=l2,["inheritedLineage"]=inherited.DeepClone()};
            if(name=="samepath-inherited")
            {
                var altered=workspace.Load<JsonObject>(inheritedPath);var alternate=TaskRoot+"/"+name+"-different-old-authority.json";workspace.Save(alternate,new{synthetic=true,notHuman=true});altered["authorization"]=Ref(workspace,alternate);workspace.Save(inheritedPath,altered);
                chained["inheritedLineage"]=Ref(workspace,inheritedPath);grant["inheritedLineage"]=Ref(workspace,inheritedPath);
            }
            if(name=="wrong-grant")grant["maximumAttempts"]=4;
            workspace.Save(grantPath,grant);chained["authorization"]=Ref(workspace,grantPath);workspace.Save(f.LineagePath,chained);
            if(name=="stale-prior")File.AppendAllText(workspace.Resolve(f.MiddleState+"/manifest.json")," ");
            if(name=="add-original")Directory.CreateDirectory(workspace.Resolve(f.PriorArt+"/attempts/classify-added"));
            if(name=="remove-empty")Directory.Delete(workspace.Resolve(f.OriginalEmpty));
            if(name=="add-immediate")Directory.CreateDirectory(workspace.Resolve(f.MiddleArt+"/attempts/classify-added"));
            var context=workspace.Load<JsonObject>("strict-context.json");
            context["roots"]!["inputs"]!.AsArray().Add(f.MiddleState);context["roots"]!["inputs"]!.AsArray().Add(f.MiddleArt);
            var exact=new List<string>{"STYLE.md","rights.json","recipe.json","toolchain.json","profile.json","sources.json",auth,profilePath,inheritedPath,priorPath,f.MiddleState+"/manifest.json",f.LineagePath,freshProfile};
            if(name!="missing-grant")exact.Add(grantPath);
            if(name=="samepath-inherited")exact.Add(TaskRoot+"/"+name+"-different-old-authority.json");
            exact.AddRange(Enumerable.Range(0,6).Select(i=>"source-"+i+".png"));
            var currentRefs=new JsonArray();foreach(var file in exact.Distinct(StringComparer.Ordinal)){var r=Ref(workspace,file);r["kind"]=file=="STYLE.md"?"policy":"input";currentRefs.Add(r);}context["references"]=currentRefs;
            workspace.Save("strict-v2-context.json",context);f.W=new NativeWorkspace(root,Bubo.Host.HostExecutionContext.Load(root,"strict-v2-context.json"));f.Process.Context=f.W.HostContext;
            foreach(var option in new[]{"manifest","classification","proof-set","kind","reviewer","evidence"})f.Options.Remove(option);
            f.Options["version"]="cwd";f.Options["preprocessing-profile"]=freshProfile;f.Options["stage-lineage"]=f.LineagePath;
            beforeInit?.Invoke(f.Process);
            await f.Execute("init");f.Options["manifest"]=f.ManifestPath;return f;
        }
        static JsonObject RefPlaceholder(string path)=>new(){["path"]=path,["sha256"]=new string('a',64)};
        static JsonObject Ref(Workspace w,string path,bool sized=false){var r=new JsonObject{["path"]=path,["sha256"]=w.HashProjectFile(path)};if(sized)r["bytes"]=new FileInfo(w.Resolve(path)).Length;return r;}
        public async Task Register()
        {
            var c=TaskRoot+"/"+Name+"-"+Version+"-classification.json"; W.Save(c,PreparationTests.Classification(Data,State));Options["classification"]=c;await Execute("classify");
            var proof=PreparationTests.ProofSet(Data,State);proof["schemaVersion"]=2;proof["conditioningProfile"]="cardinal-four-v1";proof["viewCount"]=4;proof["views"]!.AsArray().RemoveAt(5);proof["views"]!.AsArray().RemoveAt(4);
            var p=TaskRoot+"/"+Name+"-"+Version+"-proofs.json";W.Save(p,proof);Options["proof-set"]=p;await Execute("prepare-proofs");
        }
        public async Task<JsonObject> Status(){var sw=new StringWriter();var original=Console.Out;try{Console.SetOut(sw);await Execute("status");}finally{Console.SetOut(original);}return JsonNode.Parse(sw.ToString())!.AsObject();}
        public async Task Approve(string kind)
        {
            var status=await Status(); var p=TaskRoot+"/reviews/"+Name+"-"+Version+"-"+kind+".json";
            W.Save(p,new{kind,reviewer="synthetic-human",decision="approved",human=true,reason="SIMULATED TEST APPROVAL ONLY; no real human approval",fingerprint=status["approvalFingerprints"]![kind=="proof-set"?"proofSet":kind]!.GetValue<string>()});
            Options["kind"]=kind;Options["reviewer"]="synthetic-human";Options["evidence"]=p;await Execute("record");
        }
    }
    sealed class SimulatedProcess(Workspace data) : INativeProcess
    {
        public int Mattes {get;private set;} public int Models {get;private set;} public Action? AfterMatte {get;set;} public Bubo.Host.HostExecutionContext? Context {get;set;}
        public async Task RunGpu(Hephaera.AssetProduction.GpuProcessSpec spec,CancellationToken cancellation=default,Func<CancellationToken,Task>? beforeStart=null)
        {var context=Context??throw new InvalidOperationException("Explicit real HostContext required by simulation");context.ValidateFresh();context.EnsureNativeWorkingDirectoryAllowed(spec.Executable,spec.WorkingDirectory);context.EnsureWriteAllowed(spec.Log);if(beforeStart is not null)await beforeStart(cancellation); await Run(spec.Executable,spec.Arguments,spec.WorkingDirectory,spec.Log,spec.TimeoutSeconds,cancellation,spec.MaximumLogBytes,spec.Environment);}
        public Task Run(string executable,IEnumerable<string> arguments,string cwd,string log,int seconds,CancellationToken cancellation=default,long maximumLogBytes=16777216,IReadOnlyDictionary<string,string>? environment=null)
        {
            cancellation.ThrowIfCancellationRequested();var args=arguments.ToArray();
            if(args.Contains("--bg-preserve-canvas"))
            {
                Mattes++;Demand(Path.GetFileName(executable)=="matting.bin" && Path.GetFullPath(cwd)==Path.GetDirectoryName(Path.GetFullPath(executable)),"separate matting executable and sealed CWD");
                Demand(args.Contains("--require-gpu") && args[Array.IndexOf(args,"--gpu")+1]=="0" && !args.Contains("--views"),"explicit matting-only GPU0 flags");
                var allowed=new[]{cwd,Environment.SystemDirectory}.Where(p=>!string.IsNullOrEmpty(p));Demand(environment is not null && environment["PATH"]==string.Join(Path.PathSeparator,allowed),"fixed PATH contains only bundle/system, not ambient PATH");
                var source=RgbaPng.Read(args[^2]);var pixels=(byte[])source.Pixels.Clone();for(var y=0;y<source.Height;y++)for(var x=0;x<source.Width;x++)pixels[4*(y*source.Width+x)+3]=x>0&&y>0&&x<source.Width-1&&y<source.Height-1?(byte)128:(byte)0;
                RgbaPng.Write(Path.ChangeExtension(args[^1],null)+"_cutout.png",new(source.Width,source.Height,pixels));AfterMatte?.Invoke();
            }
            else
            {
                Models++;Demand(Path.GetFileName(executable)=="simulated-engine.bin" && args.Contains("--views") && args[Array.IndexOf(args,"--num-views")+1]=="4" && !args.Contains("--bg-preserve-canvas"),"reconstruction stays original base engine MV4");
                var views=args[Array.IndexOf(args,"--views")+1];var transforms=data.Load<JsonObject>(Path.Combine(views,"transforms.json"));Demand(transforms["frames"]!.AsArray().Count==4,"reconstruction consumes exactly four frames");
                foreach(var frame in transforms["frames"]!.AsArray()){var image=RgbaPng.Read(Path.Combine(views,frame!["file_path"]!.GetValue<string>()));Demand(image.Width==8&&image.Height==8&&image.Pixels[36]==160&&image.Pixels[39]==128,"model sees full straight-RGB soft-alpha canvas");}
                WriteGlb(args[^1],false);
            }
            File.WriteAllText(log,"SYNTHETIC PROCESS PORT ONLY; no native/GPU/model execution or actual approval");return Task.CompletedTask;
        }
    }
    static void WriteGlb(string path, bool meshless)
    {
        var body = new JsonObject { ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "Hephaera deterministic process simulation; not inferred geometry" } };
        using var binary = new MemoryStream();
        using (var writer = new BinaryWriter(binary, Encoding.UTF8, true))
        {
            foreach (var value in new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }) writer.Write(value);
            foreach (var value in new ushort[] { 0, 1, 2 }) writer.Write(value);
            writer.Write((ushort)0);
        }
        if (!meshless)
        {
            body["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = 44 });
            body["bufferViews"] = new JsonArray(new JsonObject { ["buffer"] = 0, ["byteOffset"] = 0, ["byteLength"] = 36, ["target"] = 34962 }, new JsonObject { ["buffer"] = 0, ["byteOffset"] = 36, ["byteLength"] = 6, ["target"] = 34963 });
            body["accessors"] = new JsonArray(new JsonObject { ["bufferView"] = 0, ["componentType"] = 5126, ["count"] = 3, ["type"] = "VEC3", ["min"] = new JsonArray(0, 0, 0), ["max"] = new JsonArray(1, 1, 0) }, new JsonObject { ["bufferView"] = 1, ["componentType"] = 5123, ["count"] = 3, ["type"] = "SCALAR" });
            body["meshes"] = new JsonArray(new JsonObject { ["primitives"] = new JsonArray(new JsonObject { ["attributes"] = new JsonObject { ["POSITION"] = 0 }, ["indices"] = 1, ["mode"] = 4 }) });
            body["nodes"] = new JsonArray(new JsonObject { ["mesh"] = 0 });
            body["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(0) });
            body["scene"] = 0;
        }
        var json = Encoding.UTF8.GetBytes(body.ToJsonString());
        var padding = (4 - json.Length % 4) % 4;
        var binaryBytes = meshless ? [] : binary.ToArray();
        using var file = File.Create(path);
        using var output = new BinaryWriter(file);
        output.Write(0x46546c67u);
        output.Write(2u);
        output.Write((uint)(20 + json.Length + padding + (meshless ? 0 : 8 + binaryBytes.Length)));
        output.Write((uint)(json.Length + padding));
        output.Write(0x4e4f534au);
        output.Write(json);
        output.Write(Enumerable.Repeat((byte)32, padding).ToArray());
        if (!meshless)
        {
            output.Write((uint)binaryBytes.Length);
            output.Write(0x004e4942u);
            output.Write(binaryBytes);
        }
    }
}
