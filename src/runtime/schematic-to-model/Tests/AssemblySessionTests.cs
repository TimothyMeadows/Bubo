using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bubo.Schematic.Domain;
using Bubo.Schematic.Infrastructure;
using Hephaera.AssetProduction;
using static Bubo.Schematic.Tests.DesignExamples;

namespace Bubo.Schematic.Tests;

/// <summary>SIMULATED owned native protocol and synthetic approval fixtures; no actual Blender or human acceptance.</summary>
public static class AssemblySessionTests
{
    const string Manifest = "state/task/schematic-to-model/fixture/v1/manifest.json";
    static readonly string[] Scripts = ["parts_author.py", "parts_verify.py", "parts_geometry.py", "parts_admission.py", "assembly_admission.py", "assembly_author.py", "assembly_geometry.py", "assembly_motion.py", "assembly_verify.py"];
    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    static string Digest<T>(T value) => Hash(JsonSerializer.SerializeToUtf8Bytes(value, StrictDesignJson.Options));
    static JsonObject View(object value) => JsonSerializer.SerializeToNode(value, StrictDesignJson.Options)!.AsObject();
    static AssemblyDesign Source(bool v2 = true)
    {
        var part = Part("block-definition", V(1,1,1), Box(1,1,1)) with { Interfaces=[Port("mount",V()),Port("slider",V(1))] };
        return Base("assembly-lifecycle-fixture",[part],[Instance("first",part.Id),Instance("second",part.Id,x:1)]) with
        {
            SchemaVersion=v2?"bubo-assembly-design/v2":"bubo-assembly-design/v1", AssemblyProfile=v2?FlexibleTests.Profile() with {MaxGeometryChecks=10_000_000,Contacts=[new(){Id="slider-rest-mate",FirstInstanceId="first",SecondInstanceId="second",Frame=F(.999,-.001,-.001),Size=V(.002,1.002,1.002),Reason="Synthetic approved shared rest face of the sliding pair"}]}:null,
            Joints=[Joint("slide",JointKind.Slider,"first","second","slider","mount") with {Axis=V(1),Minimum=0,Maximum=.5,Rest=0,Clearance=0}]
        };
    }
    sealed class Fixture
    {
        public Workspace Workspace { get; }
        public DesignSession Design { get; }
        public PartSession Parts { get; }
        public SimulatedProcess Process { get; }
        public string Bundle { get; }
        public string CatalogHash { get; }
        public string Tool { get; }
        public AssemblySession Session(long? memory=268_435_456,int timeout=60,long logs=65536) => new(Workspace,Design,Parts,Process,Bundle,timeout,logs,memory,CatalogHash);
        public Fixture(bool approved=true,bool buildParts=true,bool v2=true,AssemblyDesign? source=null)
        {
            var repository=AppContext.BaseDirectory;
            while(!File.Exists(Path.Combine(repository,"Bubo.slnx"))) repository=Directory.GetParent(repository)?.FullName??throw new InvalidOperationException("Fixture repository missing");
            var root=Path.Combine(repository,".bubo/schematic-assembly-session-tests",Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(root,"inputs")); Workspace=new(root);
            Bundle=Workspace.Resolve("resources/schematic-to-model/blender"); Directory.CreateDirectory(Bundle); var catalog=new SortedDictionary<string,string>(StringComparer.Ordinal);
            foreach(var name in Scripts)
            {
                var bytes=Encoding.UTF8.GetBytes("# SIMULATED owned-native lifecycle fixture, never actual execution: "+name+"\n");
                File.WriteAllBytes(Path.Combine(Bundle,name),bytes); catalog.Add("resources/schematic-to-model/blender/"+name,Hash(bytes));
            }
            Workspace.Save("resources/catalog.json",new{files=catalog}); CatalogHash=Workspace.HashProjectFile("resources/catalog.json");
            Tool=Workspace.Resolve("inputs/simulated-blender.exe"); File.WriteAllText(Tool,"SIMULATED registered tool identity; this file is never executed");
            File.WriteAllBytes(Workspace.Resolve("inputs/design.json"),JsonSerializer.SerializeToUtf8Bytes(source??Source(v2),StrictDesignJson.Options));
            Design=new(Workspace,"test","state/task","staging",new('b',64),new('c',64),new('a',64)); Design.Initialize("inputs/design.json","fixture","v1");
            if(approved)
            {
                var preview=View(Design.Preview(Manifest)); var review=new WholeAssemblyReview
                {
                    SchemaVersion="bubo-schematic-review/v1",Kind="whole-assembly-design",Decision="approved",Human=true,Reviewer="synthetic-lifecycle-fixture",
                    Reason="Synthetic review protocol only; no real human asset acceptance is asserted.",Asset="fixture",Version="v1",DesignSha256=preview["designSha256"]!.GetValue<string>(),
                    DesignFingerprint=preview["designFingerprint"]!.GetValue<string>(),ReviewedArtifacts=preview["artifacts"]!.AsObject().ToDictionary(p=>p.Key,p=>p.Value!.GetValue<string>())
                };
                Workspace.Save("state/task/review.json",review); Design.Record(Manifest,review.Kind,review.Reviewer,"state/task/review.json");
            }
            Process=new(Workspace); Parts=new(Workspace,Design,Process,Bundle,60,65536,268_435_456,CatalogHash);
            if(buildParts&&approved) Parts.BuildParts(Manifest,Tool).GetAwaiter().GetResult(); Process.Clear();
        }
        public JsonObject Assemble()=>View(Session().Assemble(Manifest,Tool).GetAwaiter().GetResult());
        public JsonObject Prove()=>View(Session().ProveMotion(Manifest,Tool).GetAwaiter().GetResult());
        public JsonObject Status()=>View(Session().Status(Manifest));
        public JsonObject State()=>JsonNode.Parse(File.ReadAllText(Workspace.Resolve("state/task/schematic-to-model/fixture/v1/assembly.json")))!.AsObject();
        public AssemblyBuildJob LastJob()=>Process.Jobs.Last();
    }
    sealed class SimulatedProcess(Workspace workspace):INativeProcess
    {
        public bool MemorySupported=true;
        public bool SupportsPrivateMemoryLimit=>MemorySupported;
        public List<string> Launches {get;}=[];
        public List<AssemblyBuildJob> Jobs {get;}=[];
        public List<int> Timeouts {get;}=[];
        public string? FailStage;
        public string Version="Blender 4.5.3 LTS";
        public int LogPadding;
        public Action<string,AssemblyBuildJob?>? After;
        public void Clear(){Launches.Clear();Jobs.Clear();Timeouts.Clear();}
        public Task Run(string executable,IEnumerable<string> arguments,string cwd,string log,int seconds,CancellationToken cancellation=default,long maximumLogBytes=16777216,IReadOnlyDictionary<string,string>? environment=null)
            =>throw new InvalidOperationException("Unbounded process launch is forbidden in this simulated fixture");
        public Task RunWithLimits(string executable,IEnumerable<string> arguments,string cwd,string log,int seconds,long maximumPrivateBytes,CancellationToken cancellation=default,long maximumLogBytes=16777216,IReadOnlyDictionary<string,string>? environment=null)
        {
            cancellation.ThrowIfCancellationRequested(); Check.True(MemorySupported&&maximumPrivateBytes>=67_108_864); var args=arguments.ToArray();
            var stage=args is ["--version"]?"version":Path.GetFileName(args[Array.IndexOf(args,"--python")+1]); Launches.Add(stage); Timeouts.Add(seconds);
            Directory.CreateDirectory(Path.GetDirectoryName(log)!); if(stage==FailStage) throw new IOException("SIMULATED owned native failure at "+stage);
            if(stage=="version") {File.WriteAllText(log,Version+new string(' ',LogPadding));After?.Invoke(stage,null);cancellation.ThrowIfCancellationRequested();return Task.CompletedTask;}
            Check.True(args.Contains("--disable-autoexec")&&args.Contains("--offline-mode")); var index=Array.IndexOf(args,"--"); var path=args[index+1];var hash=args[index+2];
            Check.True(Hash(File.ReadAllBytes(path))==hash); var script=args[Array.IndexOf(args,"--python")+1];
            Check.True(script.Contains("/bundle/",StringComparison.Ordinal)||script.Contains("\\bundle\\",StringComparison.Ordinal)); AssemblyBuildJob? job=null;
            if(stage is "parts_author.py" or "parts_verify.py")
            {
                var partJob=StrictDesignJson.Read<PartBuildJob>(path);
                if(stage=="parts_author.py") ProducePart(partJob,hash);
                else File.Copy(Path.Combine(partJob.Output,"parts-report.json"),Path.Combine(partJob.Output,"parts-verification.json"));
            }
            else
            {
                job=StrictDesignJson.Read<AssemblyBuildJob>(path); Jobs.Add(job);
                if(stage is "assembly_author.py" or "assembly_motion.py") AssemblyOutputTests.Produce(workspace,job,hash);
                else
                {
                    Check.True(stage=="assembly_verify.py"); var stem=job.Operation=="assemble"?"assembly":"motion";
                    File.Copy(Path.Combine(job.Output,stem+"-report.json"),Path.Combine(job.Output,stem+"-verification.json"));
                }
            }
            File.WriteAllText(log,"SIMULATED owned native protocol, not actual Blender execution.\n"+new string(' ',LogPadding));
            After?.Invoke(stage,job); cancellation.ThrowIfCancellationRequested();return Task.CompletedTask;
        }
        void ProducePart(PartBuildJob job,string jobHash)
        {
            Directory.CreateDirectory(job.Output);var artifacts=new List<PartArtifact>();long bytes=0;
            foreach(var part in job.Design.Parts)
            {
                var size=part.Size; Vec3[] points=[V(),V(size.X),V(size.X,size.Y),V(0,size.Y),V(z:size.Z),V(size.X,z:size.Z),V(size.X,size.Y,size.Z),V(0,size.Y,size.Z)];
                // All six outward cube faces; measured by the real native verifier.
                int[] triangles=[0,2,1,0,3,2,4,5,6,4,6,7,0,1,5,0,5,4,1,2,6,1,6,5,2,3,7,2,7,6,3,0,4,3,4,7];
                var binary=Path.Combine(job.Output,part.Id+".geometry.bin");using(var writer=new BinaryWriter(File.Create(binary)))
                {writer.Write("BUBOPRT1"u8);writer.Write((uint)points.Length);writer.Write((uint)(triangles.Length/3));foreach(var p in points){writer.Write(p.X);writer.Write(p.Y);writer.Write(p.Z);}foreach(var t in triangles)writer.Write((uint)t);}
                var blend=Path.Combine(job.Output,part.Id+".blend");File.WriteAllText(blend,"SIMULATED editable prototype; not an actual Blender scene");
                bytes+=new FileInfo(binary).Length+new FileInfo(blend).Length;var material=job.Design.Materials.Single(m=>m.Id==part.MaterialId);
                double Channel(int start){var n=Convert.ToInt32(material.Color.Substring(start,2),16)/255d;return n<=.04045?n/12.92:Math.Pow((n+.055)/1.055,2.4);}
                artifacts.Add(new(){Id=part.Id,DefinitionSha256=job.PartIdentities[part.Id],MaterialId=part.MaterialId,BlendFile=part.Id+".blend",BlendSha256=workspace.HashFile(blend),GeometryFile=part.Id+".geometry.bin",GeometrySha256=workspace.HashFile(binary),
                    VertexCount=8,TriangleCount=12,Minimum=Vec3.Zero,Maximum=size,Volume=size.X*size.Y*size.Z,SurfaceArea=2*(size.X*size.Y+size.X*size.Z+size.Y*size.Z),Components=1,Manifold=true,
                    MaterialColorLinear=V(Channel(1),Channel(3),Channel(5)),MaterialRoughness=material.Roughness,MaterialMetallic=material.Metallic});
            }
            var report=new PartsReport{SchemaVersion="bubo-schematic-parts/v1",JobSha256=jobHash,Identity=job.Identity,BlenderVersion="4.5.3 LTS",Parts=artifacts,Instances=job.Design.Instances,
                WeightedTriangles=12L*job.Design.Instances.Count,TotalBytes=bytes};
            File.WriteAllBytes(Path.Combine(job.Output,"parts-report.json"),JsonSerializer.SerializeToUtf8Bytes(report,StrictDesignJson.Options));
        }
    }
    public static void Run()
    {
        Check.Case("assembly lifecycle SIMULATED: doctor is read-only and v1/unapproved/missing parts remain unavailable",()=>
        {
            foreach(var f in new[]{new Fixture(v2:false),new Fixture(approved:false,buildParts:false),new Fixture(buildParts:false)})
            {
                var doctor=View(f.Session().Doctor(Manifest));Check.True(!doctor["assemblyAvailable"]!.GetValue<bool>()&&!doctor["toolProbePerformed"]!.GetValue<bool>());
                Check.True(f.Process.Launches.Count==0&&!File.Exists(f.Workspace.Resolve("state/task/schematic-to-model/fixture/v1/assembly.json")));
                var status=f.Status();Check.True(!status["currentAssemblyValid"]!.GetValue<bool>()&&!status["currentMotionProofValid"]!.GetValue<bool>());
            }
        });
        Check.Case("assembly lifecycle SIMULATED: exact full design plan rigs and seven-field identities bind assembly and proof",()=>
        {
            var f=new Fixture();var assembled=f.Assemble();var assembly=f.LastJob();Check.True(assembled["currentAssemblyValid"]!.GetValue<bool>()&&!assembled["currentMotionProofValid"]!.GetValue<bool>());
            Check.True(assembly.Plan.Clips.Count==1&&assembly.Plan.Sampling.TotalSamples==4&&assembly.Limits.MaxFiles==assembly.Design.Instances.Count+3);
            var proved=f.Prove();var proof=f.LastJob();Check.True(proved["currentAssemblyValid"]!.GetValue<bool>()&&proved["currentMotionProofValid"]!.GetValue<bool>());
            Check.True(Digest(assembly.Design)==Digest(proof.Design)&&Digest(assembly.Plan)==Digest(proof.Plan)&&Digest(assembly.Rigs)==Digest(proof.Rigs)&&assembly.Identity==proof.Identity);
            Check.True(proof.Limits.MaxFiles==proof.Design.Instances.Count*proof.Plan.Sampling.TotalSamples+3&&proof.SourceAssembly==f.Workspace.Resolve(assembled["assemblySource"]!.GetValue<string>()));
            Check.True(File.Exists(f.Workspace.Resolve(proved["motionNativeVerification"]!.GetValue<string>()))&&f.Status()["currentMotionProofValid"]!.GetValue<bool>());
            foreach(var key in new[]{"modelExportAvailable","humanModelAccepted","runtimeAcceptance","continuousCollisionCertified"})Check.True(!proved[key]!.GetValue<bool>());
            Check.True(f.Process.Launches.Count==6);
        });
        Check.Case("assembly lifecycle SIMULATED: one canonical asset lock spans every owned native stage",()=>
        {
            var f=new Fixture();f.Process.After=(stage,_)=>{if(stage=="version")Check.Throws<InvalidOperationException>(()=>f.Design.Status(Manifest));};
            f.Assemble();f.Prove();Check.True(f.Status()["currentMotionProofValid"]!.GetValue<bool>());
        });
        Check.Case("assembly lifecycle SIMULATED: latest failed assembly invalidates assembly and proof without fallback",()=>
        {
            var f=new Fixture();f.Assemble();var old=f.Prove();f.Process.FailStage="assembly_author.py";Check.Throws<IOException>(()=>f.Assemble());
            var status=f.Status();Check.True(!status["currentAssemblyValid"]!.GetValue<bool>()&&!status["currentMotionProofValid"]!.GetValue<bool>()&&status["lastAssemblyStatus"]!.GetValue<string>()=="failed");
            var launches=f.Process.Launches.Count;Check.Throws<InvalidOperationException>(()=>f.Prove());Check.True(f.Process.Launches.Count==launches);
            Check.True(File.Exists(f.Workspace.Resolve(old["diagnosticSource"]!.GetValue<string>()))&&f.State()["attempts"]!.AsArray().Count==3);
        });
        Check.Case("assembly lifecycle SIMULATED: failed proof retains valid assembly and new assembly invalidates prior proof",()=>
        {
            var f=new Fixture();f.Assemble();f.Prove();f.Process.FailStage="assembly_motion.py";Check.Throws<IOException>(()=>f.Prove());
            var status=f.Status();Check.True(status["currentAssemblyValid"]!.GetValue<bool>()&&!status["currentMotionProofValid"]!.GetValue<bool>()&&status["lastMotionStatus"]!.GetValue<string>()=="failed");
            f.Process.FailStage=null;f.Prove();f.Assemble();status=f.Status();Check.True(status["currentAssemblyValid"]!.GetValue<bool>()&&!status["currentMotionProofValid"]!.GetValue<bool>());
        });
        Check.Case("assembly lifecycle SIMULATED: new verified part attempt invalidates older assembly and proof ancestry",()=>
        {
            var f=new Fixture();f.Assemble();f.Prove();f.Parts.BuildParts(Manifest,f.Tool).GetAwaiter().GetResult();
            var status=f.Status();Check.True(!status["currentAssemblyValid"]!.GetValue<bool>()&&!status["currentMotionProofValid"]!.GetValue<bool>());
            Check.Throws<InvalidOperationException>(()=>f.Prove());Check.True(f.State()["attempts"]!.AsArray().Count==2);
        });
        Check.Case("assembly lifecycle SIMULATED: mismatched tool and unenforceable memory stop before launch",()=>
        {
            var f=new Fixture();var other=f.Workspace.Resolve("inputs/other-blender.exe");File.WriteAllText(other,"another simulated identity");
            Check.Throws<InvalidOperationException>(()=>f.Session().Assemble(Manifest,other).GetAwaiter().GetResult());
            Check.Throws<InvalidOperationException>(()=>f.Session(memory:null).Assemble(Manifest,f.Tool).GetAwaiter().GetResult());
            f.Process.MemorySupported=false;Check.Throws<PlatformNotSupportedException>(()=>f.Assemble());Check.True(f.Process.Launches.Count==0);
        });
        Check.Case("assembly lifecycle SIMULATED: source tool reviewed bundle copied bundle and job mutations retain failed attempts",()=>
        {
            foreach(var mode in new[]{"source","tool","reviewed","copy","job"})
            {
                var f=new Fixture();f.Process.After=(stage,job)=>
                {
                    if(stage!="assembly_author.py")return;var directory=Directory.GetParent(job!.Output)!.FullName;
                    var path=mode switch{"source"=>f.Workspace.Resolve("inputs/design.json"),"tool"=>f.Tool,"reviewed"=>Path.Combine(f.Bundle,"assembly_geometry.py"),"copy"=>Path.Combine(directory,"bundle/assembly_geometry.py"),_=>Path.Combine(directory,"job.json")};
                    File.AppendAllText(path," ");
                };
                Check.Throws<InvalidOperationException>(()=>f.Assemble());Check.True(f.State()["attempts"]![0]!["status"]!.GetValue<string>()=="failed"&&f.Process.Launches.Count==2);
            }
        });
        Check.Case("assembly lifecycle SIMULATED: retained scene job reports native evidence and geometry tampering fail status",()=>
        {
            foreach(var mode in new[]{"scene","job","report","verification","native","geometry"})
            {
                var f=new Fixture();var status=f.Assemble();var job=f.LastJob();var directory=Directory.GetParent(job.Output)!.FullName;
                var path=mode switch{"scene"=>Path.Combine(job.Output,"assembly.blend"),"job"=>Path.Combine(directory,"job.json"),"report"=>Path.Combine(job.Output,"assembly-report.json"),
                    "verification"=>Path.Combine(job.Output,"assembly-verification.json"),"native"=>Path.Combine(directory,"native-verification.json"),_=>Path.Combine(job.Output,"rest.000000.geometry.bin")};
                File.AppendAllText(path," ");Check.Throws<InvalidOperationException>(()=>f.Status());
            }
        });
        Check.Case("assembly lifecycle SIMULATED: source assembly scene and full provenance remain fresh across motion stages",()=>
        {
            foreach(var mode in new[]{"scene","job","report","verification","native","geometry","bundle","extra"})
            {
                var f=new Fixture();var status=f.Assemble();var output=f.LastJob().Output;var directory=Directory.GetParent(output)!.FullName;
                var source=mode switch{"scene"=>Path.Combine(output,"assembly.blend"),"job"=>Path.Combine(directory,"job.json"),"report"=>Path.Combine(output,"assembly-report.json"),
                    "verification"=>Path.Combine(output,"assembly-verification.json"),"native"=>Path.Combine(directory,"native-verification.json"),"geometry"=>Path.Combine(output,"rest.000000.geometry.bin"),"extra"=>Path.Combine(output,"extra.blend"),_=>Path.Combine(directory,"bundle/assembly_geometry.py")};
                f.Process.After=(stage,_)=>{if(stage=="version")File.AppendAllText(source," ");};Check.Throws<InvalidOperationException>(()=>f.Prove());
                Check.True(f.State()["attempts"]!.AsArray().Count==2&&f.State()["attempts"]![1]!["status"]!.GetValue<string>()=="failed");
            }
        });
        Check.Case("assembly lifecycle SIMULATED: cancellation and cumulative stage deadline or log exhaustion preserve failures",()=>
        {
            var f=new Fixture();using var cancel=new CancellationTokenSource();f.Process.After=(stage,_)=>{if(stage=="assembly_author.py")cancel.Cancel();};
            Check.Throws<OperationCanceledException>(()=>f.Session().Assemble(Manifest,f.Tool,cancel.Token).GetAwaiter().GetResult());Check.True(f.Status()["lastAssemblyStatus"]!.GetValue<string>()=="failed");
            var g=new Fixture();g.Process.LogPadding=800;Check.Throws<InvalidOperationException>(()=>g.Session(logs:2048).Assemble(Manifest,g.Tool).GetAwaiter().GetResult());Check.True(g.Process.Launches.Count==2);
            var h=new Fixture();h.Process.After=(stage,_)=>{if(stage is "version" or "assembly_author.py")Thread.Sleep(600);};
            var error=Check.Throws<Exception>(()=>h.Session(timeout:1).Assemble(Manifest,h.Tool).GetAwaiter().GetResult());Check.True(error is OperationCanceledException or TimeoutException);
            Check.True(h.State()["attempts"]![0]!["status"]!.GetValue<string>()=="failed");
        });
        Check.Case("assembly lifecycle SIMULATED: actual native interference witnesses are retained with exact failed-attempt hashes",()=>
        {
            var source=Source();source=source with {Joints=[],Instances=[source.Instances[0],source.Instances[1] with {Frame=F(.5)}],AssemblyProfile=source.AssemblyProfile! with {Contacts=[]}};
            var f=new Fixture(source:source);Check.Throws<AssemblyOutputVerificationException>(()=>f.Assemble());
            var attempt=f.State()["attempts"]![0]!;var evidence=attempt["directory"]!.GetValue<string>()+"/native-failure.json";
            Check.True(f.Workspace.HashProjectFile(evidence)==attempt["nativeFailureSha256"]!.GetValue<string>());
            var clearance=StrictDesignJson.Read<ClearanceReport>(f.Workspace.Resolve(evidence));Check.True(!clearance.IsValid&&clearance.Issues.Count>0&&clearance.Issues.Any(i=>i.Witness is not null));
            Check.True(!f.Status()["currentAssemblyValid"]!.GetValue<bool>());File.AppendAllText(f.Workspace.Resolve(evidence)," ");Check.Throws<InvalidOperationException>(()=>f.Status());
        });
        Check.Case("assembly lifecycle SIMULATED: finite synthetic retained history cannot be reset or overwritten",()=>
        {
            var f=new Fixture();f.Process.FailStage="assembly_author.py";Check.Throws<IOException>(()=>f.Assemble());var state=f.State();var template=state["attempts"]![0]!.DeepClone();
            var attempts=new JsonArray();for(var i=0;i<128;i++){var entry=template.DeepClone();entry["directory"]="staging/schematic-to-model/fixture/v1/attempts/assembly-"+Guid.NewGuid().ToString("N");attempts.Add(entry);}
            state["attempts"]=attempts;File.WriteAllBytes(f.Workspace.Resolve("state/task/schematic-to-model/fixture/v1/assembly.json"),JsonSerializer.SerializeToUtf8Bytes(state,StrictDesignJson.Options));
            f.Process.FailStage=null;f.Process.Clear();Check.Throws<InvalidOperationException>(()=>f.Assemble());Check.True(f.Process.Launches.Count==0&&f.State()["attempts"]!.AsArray().Count==128);
        });
        Check.Case("assembly lifecycle SIMULATED: doctor applies the exact finite full-plan job admission before readiness",()=>
        {
            var source=Source();source=source with {Instances=[..source.Instances,..Enumerable.Range(0,5).Select(i=>Instance("extra-"+i+"-"+new string('a',72),source.Parts[0].Id,x:3+3*i))],
                Subassemblies=source.Subassemblies.Select(group=>group.Id=="assembly"?group with {Frame=F() with {RotationDegrees=V(17,23,31)}}:group).ToList(),
                Budgets=source.Budgets with {MaxValidationChecks=10_000_000},AssemblyProfile=source.AssemblyProfile! with {SamplesPerRange=4097}};
            var f=new Fixture(source:source);var doctor=View(f.Session().Doctor(Manifest));
            Check.True(!doctor["assemblyAvailable"]!.GetValue<bool>()&&doctor["unavailable"]!.AsArray().Any(v=>v!.GetValue<string>().Contains("8MiB",StringComparison.Ordinal)));
            Check.True(f.Process.Launches.Count==0);Check.Throws<InvalidOperationException>(()=>f.Assemble());Check.True(f.Process.Launches.Count==0&&f.State()["attempts"]![0]!["status"]!.GetValue<string>()=="failed");
        });
    }
}
