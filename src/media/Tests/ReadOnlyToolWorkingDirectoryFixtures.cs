using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Hephaera.AssetProduction;
using HostExecutionContext = Bubo.Host.HostExecutionContext;

// Real HostNativeProcess admission and real GpuGuardian.Validate; no executable,
// guardian, GPU, provider or process is launched by these fixtures.
static class ReadOnlyToolWorkingDirectoryFixtures
{
    static string Hash(string file) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));
    sealed class CountingRunner : INativeProcess
    {
        public int Calls;
        public Task Run(string executable,IEnumerable<string> arguments,string cwd,string log,int seconds,
            CancellationToken cancellation=default,long maximumLogBytes=16777216,IReadOnlyDictionary<string,string>? environment=null)
        { Calls++; return Task.CompletedTask; }
    }
    static HostExecutionContext Context(string root,string bundleInput="tools/bundle")
    {
        foreach(var directory in new[]{"tools/bundle","tools/bundle/nested","tools/other","inputs","state/work","staging"})Directory.CreateDirectory(Path.Combine(root,directory));
        File.WriteAllText(Path.Combine(root,"media.json"),"SYNTHETIC READ-ONLY CWD GUARD FIXTURE; no native execution");
        foreach(var tool in new[]{"tools/bundle/tool.exe","tools/other/tool.exe"})File.WriteAllText(Path.Combine(root,tool),"synthetic non-executable selected tool");
        var value=new JsonObject
        {
            ["schemaVersion"]="bubo-host-context/v1",["projectRoot"]=root,["task"]="read-only-cwd-fixture",
            ["roots"]=new JsonObject{["state"]="state",["staging"]="staging",["tools"]=new JsonArray("tools"),["inputs"]=new JsonArray(bundleInput,"inputs"),["outputs"]=new JsonArray()},
            ["policy"]=new JsonObject{["purpose"]="local-media",["pipeline"]="LOCAL",["media"]=new JsonObject{["path"]="media.json",["sha256"]=Hash(Path.Combine(root,"media.json"))}},
            ["references"]=new JsonArray(new JsonObject{["path"]="media.json",["sha256"]=Hash(Path.Combine(root,"media.json")),["kind"]="policy"}),
            ["tools"]=new JsonArray(new[]{"tools/bundle/tool.exe","tools/other/tool.exe"}.Select(tool=>(JsonNode)new JsonObject{["path"]=Path.Combine(root,tool),["sha256"]=Hash(Path.Combine(root,tool)),["kind"]="tool"}).ToArray()),
            ["budgets"]=new JsonObject{["timeoutSeconds"]=20,["maxLogBytes"]=65536}
        };
        File.WriteAllText(Path.Combine(root,"context.json"),value.ToJsonString());return HostExecutionContext.Load(root,"context.json");
    }
    static void Guardian(HostExecutionContext context,string tool,string cwd,string? log=null,string? compatibilityLock=null)
    {
        var spec=new GpuProcessSpec(tool,[],cwd,log??Path.Combine(context.Root,"state/native.log"),10,65536,null,CompatibilityLock:compatibilityLock,MaximumWaitSeconds:5);
        var job=new GpuGuardianJob("bubo-gpu-guardian/v1","synthetic",context.Root,context.ContextPath,context.ContextSha256,0,0,spec);
        var validate=typeof(GpuGuardian).GetMethod("Validate",BindingFlags.Static|BindingFlags.NonPublic)??throw new InvalidOperationException("Real guardian admission unavailable");
        try{validate.Invoke(null,[context,job,true,CancellationToken.None]);}
        catch(TargetInvocationException exception) when(exception.InnerException is not null){throw exception.InnerException;}
    }
    public static async Task Run(Action<bool,string> check)
    {
        var root=Path.Combine(AppContext.BaseDirectory,"test-fixtures","readonly-cwd-"+Guid.NewGuid().ToString("N"));
        var context=Context(root);var tool=Path.Combine(root,"tools/bundle/tool.exe");var other=Path.Combine(root,"tools/other/tool.exe");var bundle=Path.Combine(root,"tools/bundle");
        var runner=new CountingRunner();var guarded=new HostNativeProcess(context,runner);
        void Denied(Action action,string name){try{action();throw new Exception("Expected refusal: "+name);}catch(InvalidOperationException){check(true,name);}}
        async Task HostDenied(string selected,string cwd,string name,string? log=null){var before=runner.Calls;try{await guarded.Run(selected,[],cwd,log??Path.Combine(root,"state/native.log"),10,maximumLogBytes:65536);throw new Exception("Expected refusal: "+name);}catch(InvalidOperationException){check(runner.Calls==before,name);}}
        await guarded.Run(tool,[],bundle,Path.Combine(root,"state/native.log"),10,maximumLogBytes:65536);
        check(runner.Calls==1,"real HostNativeProcess admits exact readonly tool-parent CWD to synthetic runner");
        Guardian(context,tool,bundle);check(true,"real guardian admission accepts same exact readonly tool-parent CWD without launch");
        Denied(()=>context.EnsureWriteAllowed(bundle),"readonly bundle remains unwritable after CWD admission");
        Denied(()=>context.EnsureWriteAllowed(Path.Combine(bundle,"new.dll")),"readonly bundle children remain unwritable");
        await HostDenied(other,bundle,"host rejects readonly root belonging to a different selected tool parent");
        Denied(()=>Guardian(context,other,bundle),"guardian rejects different selected tool parent");
        await HostDenied(tool,Path.Combine(bundle,"nested"),"host rejects arbitrary readonly descendant CWD");
        Denied(()=>Guardian(context,tool,Path.Combine(bundle,"nested")),"guardian rejects arbitrary readonly descendant CWD");
        await HostDenied(tool,Path.Combine(root,"inputs"),"host rejects unrelated exact readonly input root");
        Denied(()=>Guardian(context,tool,Path.Combine(root,"inputs")),"guardian rejects unrelated readonly input root");
        await HostDenied(tool,bundle,"host still refuses readonly process log",Path.Combine(bundle,"process.log"));
        Denied(()=>Guardian(context,tool,bundle,Path.Combine(bundle,"process.log")),"guardian still refuses readonly process log");
        Denied(()=>Guardian(context,tool,bundle,compatibilityLock:Path.Combine(bundle,"gpu.lock")),"guardian still refuses readonly compatibility lock");
        await guarded.Run(tool,[],root,Path.Combine(root,"state/root.log"),10,maximumLogBytes:65536);
        await guarded.Run(tool,[],Path.Combine(root,"state/work"),Path.Combine(root,"state/work.log"),10,maximumLogBytes:65536);
        check(runner.Calls==3,"project root and ordinary writable CWD admission preserved");
        Guardian(context,tool,root);Guardian(context,tool,Path.Combine(root,"state/work"));check(true,"guardian original root/writable CWD paths preserved");
        var broadRoot=root+"-broad";var broad=Context(broadRoot,"tools");
        Denied(()=>broad.EnsureNativeWorkingDirectoryAllowed(Path.Combine(broadRoot,"tools/bundle/tool.exe"),Path.Combine(broadRoot,"tools/bundle")),"containing readonly ancestor does not equal exact CWD input root");
        File.AppendAllText(tool,"changed");
        await HostDenied(tool,bundle,"host rejects selected tool hash drift before synthetic runner");
        Denied(()=>Guardian(context,tool,bundle),"guardian rejects selected tool hash drift before any process");
    }
}
