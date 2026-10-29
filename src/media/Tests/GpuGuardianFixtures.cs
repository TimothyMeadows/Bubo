using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenCaw.AssetProduction;
using HostExecutionContext = Bubo.Host.HostExecutionContext;

static class GpuGuardianFixtures
{
    static string Self=>Environment.ProcessPath!;
    static string Hash(string path)=>Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    public static async Task<int> Child(string[] args)
    {
        if(args[0]=="--owned-client")
        {
            var ctx=HostExecutionContext.Load(args[1],"context.json");
            await new HostNativeProcess(ctx,new NativeProcess()).RunGpu(Spec(ctx,"hold","client-death"));
            return 0;
        }
        var mode=args[1];var root=args[2];Directory.CreateDirectory(root);
        if(mode=="leaf") {await Task.Delay(TimeSpan.FromMinutes(3));return 0;}
        File.WriteAllText(Path.Combine(root,"native.pid"),Environment.ProcessId.ToString());
        if(mode=="touch")return 0;
        var start=new ProcessStartInfo(Self) {UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true};
        foreach(var arg in new[]{"--owned-native","leaf",root})start.ArgumentList.Add(arg);
        using var leaf=Process.Start(start)!;leaf.StandardInput.Close();
        File.WriteAllText(Path.Combine(root,"child.pid"),leaf.Id.ToString());
        if(mode=="exit-with-child")return 0;
        if(mode=="failure")return 7;
        if(mode=="overlog") {for(var i=0;i<2048;i++)Console.WriteLine(new string('x',1024));}
        await Task.Delay(TimeSpan.FromMinutes(2));return 0;
    }
    static HostExecutionContext Context(string root,int seconds=20)
    {
        Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"media.json"),"OWNED PROCESS FIXTURE POLICY; no models or generation");
        Directory.CreateDirectory(Path.Combine(root,"inputs"));File.WriteAllText(Path.Combine(root,"inputs/pinned.bin"),"original");
        var json=new JsonObject
        {
            ["schemaVersion"]="bubo-host-context/v1",["projectRoot"]=root,["task"]="gpu-lifecycle-fixture",
            ["roots"]=new JsonObject { ["state"]="state",["staging"]="staging",["tools"]=new JsonArray("tools"),["inputs"]=new JsonArray("inputs"),["outputs"]=new JsonArray() },
            ["policy"]=new JsonObject { ["purpose"]="local-media",["pipeline"]="LOCAL",["media"]=new JsonObject { ["path"]="media.json",["sha256"]=Hash(Path.Combine(root,"media.json")) } },
            ["references"]=new JsonArray(new JsonObject { ["path"]="media.json",["sha256"]=Hash(Path.Combine(root,"media.json")),["kind"]="policy" }),
            ["tools"]=new JsonArray(new JsonObject { ["path"]=Self,["sha256"]=Hash(Self),["kind"]="tool" }),
            ["budgets"]=new JsonObject { ["timeoutSeconds"]=seconds,["maxLogBytes"]=65536 }
        };
        File.WriteAllText(Path.Combine(root,"context.json"),json.ToJsonString());return HostExecutionContext.Load(root,"context.json");
    }
    static GpuProcessSpec Spec(HostExecutionContext ctx,string mode,string name,int seconds=10,long logBytes=65536)
    {
        var directory=Path.Combine(ctx.Root,"state",name);
        return new(Self,["--owned-native",mode,directory],ctx.Root,Path.Combine(directory,"native.log"),seconds,logBytes,null,MaximumWaitSeconds:Math.Min(5,ctx.TimeoutSeconds));
    }
    static async Task Wait(Func<bool> condition,int seconds=15)
    {
        var clock=Stopwatch.StartNew();while(!condition()) {if(clock.Elapsed.TotalSeconds>seconds)throw new InvalidOperationException("Fixture condition timed out");await Task.Delay(25);}
    }
    static bool Alive(int id)
    {
        try
        {
            using var process=Process.GetProcessById(id);if(process.HasExited)return false;
            if(OperatingSystem.IsLinux())
            {
                var text=File.ReadAllText("/proc/"+id+"/stat");if(text[(text.LastIndexOf(')')+2)] is 'Z' or 'X')return false;
            }
            return true;
        }
        catch(Exception e) when(e is ArgumentException or FileNotFoundException or DirectoryNotFoundException){return false;}
    }
    static int Pid(string root,string name,string file)=>int.Parse(File.ReadAllText(Path.Combine(root,"state",name,file)));
    static async Task Failed(Task task)
    {
        if(await Task.WhenAny(task,Task.Delay(TimeSpan.FromSeconds(30)))!=task)
            throw new InvalidOperationException("Fixture exceeded its cleanup deadline");
        try {await task;}
        catch(Exception e) when(e is InvalidOperationException or OperationCanceledException or IOException or TimeoutException){return;}
        throw new InvalidOperationException("Expected owned GPU job failure");
    }
    public static async Task<int> Run(Action<bool,string> check)
    {
        var count=0;
        void Check(bool value,string name) {check(value,name);count++;}
        var root=Path.Combine(AppContext.BaseDirectory,"test-fixtures","gpu-"+Guid.NewGuid().ToString("N"));
        var one=Context(Path.Combine(root,"one"));var two=Context(Path.Combine(root,"two"));
        var a=new HostNativeProcess(one,new NativeProcess());var b=new HostNativeProcess(two,new NativeProcess());
        using var firstCancel=new CancellationTokenSource();
        var first=a.RunGpu(Spec(one,"hold","first"),firstCancel.Token);
        try
        {
            await Wait(()=>first.IsCompleted || File.Exists(Path.Combine(one.Root,"state/first/child.pid")));
            if(first.IsCompleted)
            {
                await first;
                throw new InvalidOperationException("Owned guardian fixture completed before child startup");
            }
            Check(Alive(Pid(one.Root,"first","native.pid")) && Alive(Pid(one.Root,"first","child.pid")),"guardian starts real owned parent and descendant fixtures");
            using var waitingCancel=new CancellationTokenSource();var callback=false;
            var waiting=b.RunGpu(Spec(two,"touch","waiting"),waitingCancel.Token,_=>{callback=true;return Task.CompletedTask;});
            await Task.Delay(250);
            Check(!callback && !File.Exists(Path.Combine(two.Root,"state/waiting/native.pid")),"different project roots cannot overlap GPU ownership");
            waitingCancel.Cancel();await Failed(waiting);
            Check(!callback && !first.IsCompleted,"waiting cancellation neither starts model work nor retires another owner");
            var pin=Path.Combine(two.Root,"inputs/pinned.bin");
            var changed=Spec(two,"touch","changed") with {Inputs=[new("inputs/pinned.bin",Hash(pin))]};
            var queued=b.RunGpu(changed);
            await Task.Delay(250);File.WriteAllText(pin,"changed while queued");
            firstCancel.Cancel();await Failed(first);await Failed(queued);
            Check(!File.Exists(Path.Combine(two.Root,"state/changed/native.pid")),"queued input mutation fails before native launch");
        }
        finally {firstCancel.Cancel();try{await first;}catch(Exception) { }}
        Check(!Alive(Pid(one.Root,"first","native.pid")) && !Alive(Pid(one.Root,"first","child.pid")),"cancellation acknowledges actual parent and descendant retirement");
        var clock=Stopwatch.StartNew();await b.RunGpu(Spec(two,"exit-with-child","success"));
        Check(clock.Elapsed.TotalSeconds<10 && !Alive(Pid(two.Root,"success","child.pid")),"successful root exit retires its surviving descendant before return");
        await Failed(b.RunGpu(Spec(two,"failure","failure")));
        Check(!Alive(Pid(two.Root,"failure","child.pid")),"worker failure retires owned descendants");
        await Failed(b.RunGpu(Spec(two,"hold","timeout",1)));
        Check(!Alive(Pid(two.Root,"timeout","child.pid")),"native timeout retires owned descendants");
        await Failed(b.RunGpu(Spec(two,"overlog","overlog",10,1024)));
        Check(!Alive(Pid(two.Root,"overlog","child.pid")),"native log overflow retires owned descendants");
        var shortContext=Context(Path.Combine(root,"short"),2);var shortRunner=new HostNativeProcess(shortContext,new NativeProcess());
        await Failed(shortRunner.RunGpu(Spec(shortContext,"touch","stalled",1),default,_=>Task.Delay(Timeout.InfiniteTimeSpan)));
        Check(!File.Exists(Path.Combine(shortContext.Root,"state/stalled/native.pid")),"stalled before-start hook is bounded and starts no model work");
        var hookFinished=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateWrite=Path.Combine(shortContext.Root,"state/late-claim.txt");
        await Failed(shortRunner.RunGpu(Spec(shortContext,"touch","cancel-hook",1),default,async token=>
        {
            try {await Task.Delay(2500,token);File.WriteAllText(lateWrite,"must not be written after timeout");}
            finally {hookFinished.TrySetResult();}
        }));
        await hookFinished.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check(!File.Exists(lateWrite)&&!File.Exists(Path.Combine(shortContext.Root,"state/cancel-hook/native.pid")),"timed-out cooperative callback is canceled before a late claim/write");
        using(var alreadyCanceled=new CancellationTokenSource())
        {
            alreadyCanceled.Cancel();var noLog=Path.Combine(two.Root,"state/no-memory-canceled.log");
            await Failed(new NativeProcess().Run(Self,["--owned-worker","touch",Path.Combine(two.Root,"state/no-memory-canceled")],two.Root,noLog,3,alreadyCanceled.Token));
            Check(!File.Exists(noLog)&&!File.Exists(Path.Combine(two.Root,"state/no-memory-canceled/native.pid")),"already-canceled unbounded-memory native run creates no child or log");
        }
        var workspace=new Workspace(two.Root,two);var legacy="state/legacy.lock";var claimed=false;
        using(var locked=workspace.Lock(legacy))
            await Failed(b.RunGpu(Spec(two,"touch","legacy") with {CompatibilityLock=legacy},default,_=>{claimed=true;return Task.CompletedTask;}));
        Check(!claimed,"legacy lock conflict is detected before acceptance reservation");
        var crash=Context(Path.Combine(root,"crash"));
        var childStart=new ProcessStartInfo(Self) {UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        childStart.ArgumentList.Add("--owned-client");childStart.ArgumentList.Add(crash.Root);
        using var client=Process.Start(childStart)!;client.StandardInput.Close();
        await Wait(()=>File.Exists(Path.Combine(crash.Root,"state/client-death/child.pid")));
        var nativePid=Pid(crash.Root,"client-death","native.pid");var leafPid=Pid(crash.Root,"client-death","child.pid");
        client.Kill(entireProcessTree:false);await client.WaitForExitAsync();
        await Wait(()=>!Alive(nativePid) && !Alive(leafPid));
        Check(true,"client death leaves guardian alive long enough to retire actual descendants");
        await b.RunGpu(Spec(two,"touch","after-crash"));Check(true,"next project acquires ownership after client-death cleanup");
        var receipts=Directory.EnumerateFiles(root,"result.json",SearchOption.AllDirectories).Select(path=>JsonNode.Parse(File.ReadAllText(path))!.AsObject()).ToArray();
        Check(receipts.Length>=10 && receipts.All(x=>x["nativeRetired"]!.GetValue<bool>()),"terminal guardian receipts report observed native retirement");
        Console.WriteLine("GPU fixtures use owned CPU processes only; no GPU/model generation or guardian-death resilience is certified.");
        return count;
    }
}
