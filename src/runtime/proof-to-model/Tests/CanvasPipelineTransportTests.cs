using System.Text;
using System.Text.Json.Nodes;
using Hephaera.ProofToModel.Application;
using Hephaera.ProofToModel.Domain;
using Hephaera.ProofToModel.Infrastructure;
using NativeWorkspace = Hephaera.ProofToModel.Infrastructure.Workspace;

/// <summary>CPU-only synthetic process port. Never launches a DLL/GPU/native engine, and asserts no real approvals.</summary>
static class CanvasPipelineTransportTests
{
    const string TaskName = "canvas-transport";
    const string TaskRoot = ".hephaera/.cache/tasks/" + TaskName;
    const string ModelDirectory = ".local/tools/proof-to-model/models/synthetic-mv";
    static readonly string[] ModelNames = ["dinov3.gguf", "pixal3d_naf.gguf", "pixal3d_ss_flow_mv.gguf", "pixal3d_shape_flow_512_mv.gguf", "pixal3d_shape_flow_1024_mv.gguf", "pixal3d_tex_flow_1024_mv.gguf", "ss_dec.gguf", "shape_dec.gguf", "tex_dec.gguf"];
    static void Demand(bool condition, string message) { if (!condition) throw new InvalidOperationException("SYNTHETIC TRANSPORT: " + message); }
    public static async Task Run(Workspace parent, Action<bool,string> check, Func<Func<Task>,string,Task> reject)
    {
        var good = await Fixture.Create(parent, "good");
        await good.Register();
        check(good.State.Approvals.Count == 0, "Fresh canvas version inherits no predecessor decisions");
        await reject(() => good.Execute("preprocess"), "Whole proof-set approval still required before simulated matting");
        check(good.Process.Mattes == 0, "Missing proof-set decision launches zero simulated children");
        await good.Approve("proof-set");
        await good.Execute("preprocess");
        check(good.Process.Mattes == 4 && good.Process.Models == 0, "Exactly four explicit simulated neural matte children precede any reconstruction");
        var dataset = good.W.Load<JsonObject>(good.State.Stages["preprocess"].Artifacts["dataset"]);
        check(dataset["views"]!.AsArray().All(v => v!["normalization"]!["operation"]!.GetValue<string>() == "birefnet-preserve-canvas-and-common-square-pad"), "Every source takes real pipeline neural-matting branch rather than preserve-alpha shortcut");
        foreach(var view in dataset["views"]!.AsArray())
        {
            var n = view!["normalization"]!;
            var rgba = RgbaPng.Read(good.W.Resolve(n["cutout"]!.GetValue<string>()));
            check(rgba.Width == 8 && rgba.Height == 8 && rgba.Pixels[4*9] == 160 && rgba.Pixels[4*9+3] == 128, "Simulated soft-alpha cutout preserves full source canvas and straight original RGB, no premultiplication/crop");
        }
        await reject(() => good.Execute("reconstruct"), "Separate preprocessing record is still required before reconstruction");
        check(good.Process.Models == 0, "No reconstruction emitted before synthetic preprocessing approval");
        await good.Approve("preprocessing");
        await good.Execute("reconstruct");
        check(good.Process.Models == 1 && good.Process.Mattes == 4, "One simulated reconstruction uses original engine after four separate matting children");
        var status = await good.Status();
        check(status["stageLineage"]!["consumed"]!["preprocess"]!.GetValue<int>() == 2 && status["stageLineage"]!["consumed"]!["reconstruct"]!.GetValue<int>() == 1, "Old failed plus new complete preprocessing exhaust global2; one reconstruction remains global1");
        await reject(() => good.Execute("preprocess"), "Consumed cumulative preprocessing allowance cannot restart");
        check(good.Process.Mattes == 4 && good.Process.Models == 1, "Exhausted stage cannot launch extra children or models");
        await reject(() => Task.Run(() => good.W.HostContext!.EnsureWriteAllowed(good.PriorState)), "Actual prior state is readonly");
        await reject(() => Task.Run(() => good.W.HostContext!.EnsureWriteAllowed(good.PriorArt)), "Actual prior staging is readonly");
        await reject(() => Task.Run(() => good.W.HostContext!.EnsureWriteAllowed(".local/tools/canvas")), "Actual separate matting bundle is readonly");
        foreach(var mutation in new[]{"bundle", "lineage"})
        {
            var bad = await Fixture.Create(parent, mutation); await bad.Register(); await bad.Approve("proof-set");
            bad.Process.AfterMatte = () => File.AppendAllText(bad.W.Resolve(mutation == "bundle" ? ".local/tools/canvas/matting.bin" : bad.LineagePath), "SYNTHETIC STALE INPUT");
            await reject(() => bad.Execute("preprocess"), "Mid-child " + mutation + " mutation is refused in same invocation");
            check(bad.Process.Mattes == 1 && bad.Process.Models == 0 && bad.State.Stages["preprocess"].Status == "failed", "Mid-child stale input stops before second matte or any model and retains failed stage");
            await reject(() => bad.Execute("reconstruct"), "Stale " + mutation + " cannot launch reconstruction");
            check(bad.Process.Models == 0, "No extra model on stale immutable dependency");
        }
    }
    sealed class Fixture
    {
        public Workspace Data {get;}
        public NativeWorkspace W {get; private set;} = null!;
        public Dictionary<string,string> Options {get;} = new();
        public SimulatedProcess Process {get;}
        public string Name {get;}
        public string ManifestPath => TaskRoot + "/proof-to-model/" + Name + "/canvas/manifest.json";
        public string PriorState => TaskRoot + "/proof-to-model/" + Name + "/old";
        public string PriorArt => ".art-staging/proof-to-model/" + Name + "/old";
        public string LineagePath => TaskRoot + "/" + Name + "-lineage.json";
        public Manifest State => W.Load<Manifest>(ManifestPath);
        Fixture(string root,string name) { Data = new Workspace(root); Name=name; Process=new SimulatedProcess(Data); }
        public Task Execute(string command) => new Pipeline(W,Options,Process,new AlphaProcessor()).Execute(command,default);
        public static async Task<Fixture> Create(Workspace parent,string name)
        {
            var root=parent.Resolve("canvas-flow-" + name + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
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
            var priorPath=f.PriorState+"/manifest.json"; var prior=workspace.Load<Manifest>(priorPath);
            var failed=f.PriorArt+"/attempts/preprocess-failed";Directory.CreateDirectory(workspace.Resolve(failed));
            prior.History.Add(new JsonObject{["event"]="stage-start",["stage"]="preprocess",["attempt"]=failed});workspace.Save(priorPath,prior);
            Directory.CreateDirectory(workspace.Resolve(".local/tools/canvas")); File.WriteAllText(workspace.Resolve(".local/tools/canvas/matting.bin"),"SYNTHETIC MATTER; never executable");
            var auth=TaskRoot+"/"+name+"-synthetic-authority.json";workspace.Save(auth,new {synthetic=true,decision="approved",scope="CPU fixture only, not a user or production grant"});
            var profilePath=TaskRoot+"/"+name+"-matting.json";
            workspace.Save(profilePath,new JsonObject{["schemaVersion"]=1,["id"]=CanvasMattingProfile.Id,["mode"]="multi-view-four",["baseToolchain"]=Ref(workspace,"toolchain.json"),["executable"]=Ref(workspace,".local/tools/canvas/matting.bin",true),["files"]=new JsonArray(Ref(workspace,".local/tools/canvas/matting.bin",true)),["maxMattingChildren"]=4});
            var attempts=new JsonObject();var dirs=new JsonObject();var counts=new JsonObject();var limits=new JsonObject();
            foreach(var stage in CanvasStageLineage.Stages){attempts[stage]=stage=="preprocess"?new JsonArray(failed):new JsonArray();dirs[stage]=attempts[stage]!.DeepClone();counts[stage]=stage=="preprocess"?1:0;limits[stage]=stage is "classify" or "prepare-proofs"?4:stage=="preprocess"?2:1;}
            workspace.Save(f.LineagePath,new JsonObject{["schemaVersion"]=1,["kind"]="proof-to-model-stage-lineage",["decision"]="approved",["task"]=TaskName,["asset"]=name,["sourceVersion"]="old",["targetVersion"]="canvas",["authorization"]=Ref(workspace,auth),["inputApproval"]=Ref(workspace,auth),["priorManifest"]=Ref(workspace,priorPath),["priorStateRoot"]=f.PriorState,["priorStaging"]=f.PriorArt,["priorAttempts"]=attempts,["priorDirectories"]=dirs,["priorHistoryCounts"]=counts,["stageLimits"]=limits});
            var refs=new JsonArray(); foreach(var file in new[]{"STYLE.md","rights.json","recipe.json","toolchain.json","profile.json","sources.json",auth,profilePath,f.LineagePath,priorPath}.Concat(Enumerable.Range(0,6).Select(i=>"source-"+i+".png"))) {var r=Ref(workspace,file);r["kind"]=file=="STYLE.md"?"policy":"input";refs.Add(r);}
            var tools=new JsonArray(); foreach(var file in new[]{"simulated-engine.bin",".local/tools/canvas/matting.bin"})tools.Add(new JsonObject{["path"]=workspace.Resolve(file),["sha256"]=workspace.HashProjectFile(file)});
            workspace.Save("strict-context.json",new JsonObject{
                ["schemaVersion"]="bubo-host-context/v1",["projectRoot"]=root,["task"]=TaskName,
                ["roots"]=new JsonObject{["state"]=".hephaera/.cache/tasks",["staging"]=".art-staging",["tools"]=new JsonArray(".local/tools"),["inputs"]=new JsonArray(f.PriorState,f.PriorArt,".local/tools/canvas"),["outputs"]=new JsonArray("assets/models")},
                ["policy"]=new JsonObject{["pipeline"]="PROOF_TO_MODEL",["styleSha256"]=workspace.HashProjectFile("STYLE.md"),["engine"]=new JsonObject{["effectiveEngine"]="THREEJS"},["libraries"]=new JsonArray()},
                ["references"]=refs,["tools"]=tools,["budgets"]=new JsonObject{["timeoutSeconds"]=30,["maxLogBytes"]=100000},["permissions"]=new JsonObject{["provisioning"]=false,["promotion"]=false}});
            f.W=new NativeWorkspace(root,Bubo.Host.HostExecutionContext.Load(root,"strict-context.json"));
            f.Options["version"]="canvas";f.Options["preprocessing-profile"]=profilePath;f.Options["stage-lineage"]=f.LineagePath;
            await f.Execute("init");f.Options["manifest"]=f.ManifestPath;return f;
        }
        static JsonObject Ref(Workspace w,string path,bool sized=false){var r=new JsonObject{["path"]=path,["sha256"]=w.HashProjectFile(path)};if(sized)r["bytes"]=new FileInfo(w.Resolve(path)).Length;return r;}
        public async Task Register()
        {
            var c=TaskRoot+"/"+Name+"-classification.json"; W.Save(c,PreparationTests.Classification(Data,State));Options["classification"]=c;await Execute("classify");
            var proof=PreparationTests.ProofSet(Data,State);proof["schemaVersion"]=2;proof["conditioningProfile"]="cardinal-four-v1";proof["viewCount"]=4;proof["views"]!.AsArray().RemoveAt(5);proof["views"]!.AsArray().RemoveAt(4);
            var p=TaskRoot+"/"+Name+"-proofs.json";W.Save(p,proof);Options["proof-set"]=p;await Execute("prepare-proofs");
        }
        public async Task<JsonObject> Status(){var sw=new StringWriter();var original=Console.Out;try{Console.SetOut(sw);await Execute("status");}finally{Console.SetOut(original);}return JsonNode.Parse(sw.ToString())!.AsObject();}
        public async Task Approve(string kind)
        {
            var status=await Status(); var p=TaskRoot+"/reviews/"+Name+"-"+kind+".json";
            W.Save(p,new{kind,reviewer="synthetic-human",decision="approved",human=true,reason="SIMULATED TEST APPROVAL ONLY; no real human approval",fingerprint=status["approvalFingerprints"]![kind=="proof-set"?"proofSet":kind]!.GetValue<string>()});
            Options["kind"]=kind;Options["reviewer"]="synthetic-human";Options["evidence"]=p;await Execute("record");
        }
    }
    sealed class SimulatedProcess(Workspace data) : INativeProcess
    {
        public int Mattes {get;private set;} public int Models {get;private set;} public Action? AfterMatte {get;set;}
        public async Task RunGpu(Hephaera.AssetProduction.GpuProcessSpec spec,CancellationToken cancellation=default,Func<CancellationToken,Task>? beforeStart=null)
        {if(beforeStart is not null)await beforeStart(cancellation); await Run(spec.Executable,spec.Arguments,spec.WorkingDirectory,spec.Log,spec.TimeoutSeconds,cancellation,spec.MaximumLogBytes,spec.Environment);}
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
