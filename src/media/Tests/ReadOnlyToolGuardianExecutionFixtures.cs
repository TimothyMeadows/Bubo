using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Hephaera.AssetProduction;
using HostExecutionContext = Bubo.Host.HostExecutionContext;

/// <summary>Actual owned CPU child; never a GPU/model/tool-help invocation.</summary>
static class ReadOnlyToolGuardianExecutionFixtures
{
    const long MaximumBundleBytes=67108864;
    static string Self=>Environment.ProcessPath??throw new InvalidOperationException("Built test apphost required");
    static string Hash(string file){using var stream=File.OpenRead(file);return Convert.ToHexStringLower(SHA256.HashData(stream));}
    static void Ordinary(string path)
    {
        for(var current=Path.GetFullPath(path);;current=Path.GetDirectoryName(current)!)
        {
            if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("Fixture cannot traverse linked apphost/input components");
            var parent=Path.GetDirectoryName(current);if(parent is null||parent==current)break;
        }
    }
    public static int Child(string root)
    {
        // The actual copied test entry point handles only this early branch.
        // Report observed CWD to the writable sibling state, never to tool-bin.
        root=Path.GetFullPath(root);var expected=Path.Combine(root,"tool-bin");
        var comparison=OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal;
        if(!Directory.GetCurrentDirectory().Equals(expected,comparison)||!Path.GetDirectoryName(Self)!.Equals(expected,comparison))return 17;
        var output=Path.Combine(root,"state/observed-child.json");
        using var stream=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.Read);
        var value=new JsonObject{["schemaVersion"]="synthetic-readonly-cwd-child/v1",["pid"]=Environment.ProcessId,["cwd"]=Directory.GetCurrentDirectory(),["executable"]=Self,["cpuOnly"]=true};
        var bytes=System.Text.Encoding.UTF8.GetBytes(value.ToJsonString());stream.Write(bytes);stream.Flush(true);
        Console.WriteLine("Owned CPU fixture observed exact readonly tool-parent CWD; no GPU/model work");return 0;
    }
    static Dictionary<string,(long Bytes,string Sha256)> Snapshot(string directory)
    {
        var result=new Dictionary<string,(long,string)>(StringComparer.Ordinal);long total=0;
        foreach(var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if(result.Count>=128||!File.Exists(entry)||Directory.Exists(entry))throw new InvalidOperationException("Unexpected/unbounded tool bundle entry");
            Ordinary(entry);var size=new FileInfo(entry).Length;if(size<1||size>16777216||(total+=size)>MaximumBundleBytes)throw new InvalidOperationException("Fixture bundle budget exceeded before hash");
            result.Add(Path.GetFileName(entry),(size,Hash(entry)));
        }
        return result;
    }
    public static async Task Run(Action<bool,string> check)
    {
        if(Path.GetFileNameWithoutExtension(Self).Equals("dotnet",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Actual guardian fixture requires built apphost, no DLL/source-run fallback");
        var entry=Assembly.GetEntryAssembly()?.GetName().Name??throw new InvalidOperationException("Current test entry assembly required");
        var source=Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);Ordinary(source);Ordinary(Self);
        if(!Path.GetDirectoryName(Self)!.Equals(source,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))throw new InvalidOperationException("Test apphost must belong to its exact entry bundle");
        var root=Path.Combine(source,"test-fixtures","readonly-actual-"+Guid.NewGuid().ToString("N"));var bundle=Path.Combine(root,"tool-bin");
        Directory.CreateDirectory(bundle);Directory.CreateDirectory(Path.Combine(root,"state"));Directory.CreateDirectory(Path.Combine(root,"staging"));
        // Copy only current entry-point runtime metadata, its apphost and bounded
        // top-level assembly dependencies. No recursion or environment lookup.
        var names=new HashSet<string>(StringComparer.Ordinal){Path.GetFileName(Self),entry+".deps.json",entry+".runtimeconfig.json",entry+".dll"};
        var copied=0;long copiedBytes=0;
        foreach(var file in Directory.EnumerateFiles(source,"*",SearchOption.TopDirectoryOnly))
        {
            var name=Path.GetFileName(file);if(!names.Contains(name)&&!name.EndsWith(".dll",StringComparison.OrdinalIgnoreCase))continue;
            Ordinary(file);var size=new FileInfo(file).Length;if(++copied>128||size<1||size>16777216||(copiedBytes+=size)>MaximumBundleBytes)throw new InvalidOperationException("Bounded current test runtime bundle required");
            var target=Path.Combine(bundle,name);File.Copy(file,target,false);
            if(!OperatingSystem.IsWindows())File.SetUnixFileMode(target,File.GetUnixFileMode(file));
            if(Hash(target)!=Hash(file))throw new InvalidOperationException("Copied test runtime bytes differ");
        }
        foreach(var name in names)if(!File.Exists(Path.Combine(bundle,name)))throw new InvalidOperationException("Required current test apphost dependency missing: "+name);
        var executable=Path.Combine(bundle,Path.GetFileName(Self));var before=Snapshot(bundle);var policy=Path.Combine(root,"media.json");
        File.WriteAllText(policy,"SYNTHETIC ACTUAL OWNED CPU FIXTURE ONLY; no GPU, image or model production");
        var references=new JsonArray(new JsonObject{["path"]="media.json",["sha256"]=Hash(policy),["kind"]="policy"});
        foreach(var row in before.Where(row=>row.Key!=Path.GetFileName(Self)))references.Add(new JsonObject{["path"]="tool-bin/"+row.Key,["sha256"]=row.Value.Sha256,["kind"]="input"});
        var realRunner=new NativeProcess();long? privateBytes=realRunner.SupportsPrivateMemoryLimit?536870912L:null;
        var budgets=new JsonObject{["timeoutSeconds"]=15,["maxLogBytes"]=65536};if(privateBytes is not null)budgets["maxPrivateBytes"]=privateBytes.Value;
        var contextValue=new JsonObject{["schemaVersion"]="bubo-host-context/v1",["projectRoot"]=root,["task"]="readonly-actual-cpu-fixture",["roots"]=new JsonObject{["state"]="state",["staging"]="staging",["tools"]=new JsonArray("tool-bin"),["inputs"]=new JsonArray("tool-bin"),["outputs"]=new JsonArray()},["policy"]=new JsonObject{["purpose"]="local-media",["pipeline"]="LOCAL",["media"]=new JsonObject{["path"]="media.json",["sha256"]=Hash(policy)}},["references"]=references,["tools"]=new JsonArray(new JsonObject{["path"]=executable,["sha256"]=Hash(executable),["kind"]="tool"}),["budgets"]=budgets,["permissions"]=new JsonObject{["provisioning"]=false,["promotion"]=false}};
        File.WriteAllText(Path.Combine(root,"context.json"),contextValue.ToJsonString());var context=HostExecutionContext.Load(root,"context.json");
        var request=new GpuProcessSpec(executable,["--readonly-tool-cwd-child",root],bundle,Path.Combine(root,"state/process.log"),10,65536,privateBytes,MaximumWaitSeconds:3);
        using var cancellation=new CancellationTokenSource(TimeSpan.FromSeconds(15));var clock=Stopwatch.StartNew();
        await new HostNativeProcess(context,realRunner).RunGpu(request,cancellation.Token);
        check(clock.Elapsed.TotalSeconds<15,"real HostNativeProcess→guardian→copied owned CPU child completes under15seconds");
        var child=JsonNode.Parse(File.ReadAllText(Path.Combine(root,"state/observed-child.json")))!.AsObject();
        check(child["cwd"]!.GetValue<string>()==bundle&&child["executable"]!.GetValue<string>()==executable&&child["cpuOnly"]!.GetValue<bool>(),"actual owned child observes exact readonly registered tool-parent CWD");
        var outcomes=Directory.EnumerateFiles(Path.Combine(root,"state/gpu-guardian"),"result.json",SearchOption.AllDirectories).Select(file=>JsonNode.Parse(File.ReadAllText(file))!.AsObject()).ToArray();
        check(outcomes.Length==1&&outcomes[0]["status"]!.GetValue<string>()=="succeeded"&&outcomes[0]["nativeRetired"]!.GetValue<bool>(),"actual guardian terminal receipt confirms exit0 success and owned child retirement");
        var after=Snapshot(bundle);check(before.Count==after.Count&&before.All(row=>after.TryGetValue(row.Key,out var value)&&value==row.Value),"readonly tool bundle file inventory and all bytes unchanged by actual child");
        check(new FileInfo(Path.Combine(root,"state/process.log")).Length<=65536,"actual child log remains within64KiB outside readonly bundle");
        var writeDenied=false;try{context.EnsureWriteAllowed(Path.Combine(bundle,"must-not-write.bin"));}catch(InvalidOperationException){writeDenied=true;}check(writeDenied,"CWD admission does not grant bundle writes after actual successful launch");
        Console.WriteLine("PASS: actual readonly-CWD owned CPU child; no GPU/model generation or OS sandbox claimed.");
    }
}
