using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using HostExecutionContext = Bubo.Host.HostExecutionContext;

namespace OpenCaw.AssetProduction;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GpuGuardianJob(string SchemaVersion,string Id,string Root,string ContextPath,string ContextSha256,int ClientPid,
    long ClientStartIdentity,GpuProcessSpec Process);
public sealed record GpuGuardianOutcome(string SchemaVersion,string Id,string Status,bool NativeRetired,string? Error);

/// <summary>A short-lived process owns both the global lease and its native descendants.</summary>
public static class GpuGuardian
{
    const string Protocol="bubo-gpu-guardian/v1";
    static readonly JsonSerializerOptions Json=new() {PropertyNamingPolicy=JsonNamingPolicy.CamelCase,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,MaxDepth=32};

    public static async Task Run(HostExecutionContext context,GpuProcessSpec spec,CancellationToken cancellation,Func<CancellationToken,Task>? beforeStart=null)
    {
        cancellation.ThrowIfCancellationRequested();
        var apphost=Apphost();
        var workspace=new Workspace(context.Root,context);
        var id=Guid.NewGuid().ToString("N");var state=context.StateRoot+"/gpu-guardian/"+id;
        var jobPath=state+"/job.json";
        using var self=Process.GetCurrentProcess();
        var job=new GpuGuardianJob(Protocol,id,context.Root,context.ContextPath,context.ContextSha256,self.Id,StartIdentity(self),spec);
        Validate(context,job,cancellation:cancellation);
        workspace.Save(jobPath,job);
        var jobSha=workspace.HashProjectFile(jobPath);
        var linux=OperatingSystem.IsLinux();
        if (!OperatingSystem.IsWindows() && !linux) throw new PlatformNotSupportedException("GPU guardian supports Windows and Linux only");
        var info=new ProcessStartInfo(linux?"/usr/bin/setsid":apphost) {WorkingDirectory=context.Root,UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        if(linux) info.ArgumentList.Add(apphost);
        foreach(var arg in new[]{"gpu-guardian","run","--api-version","1","--root",context.Root,"--host-context",context.ContextPath,"--job",jobPath,"--job-sha256",jobSha}) info.ArgumentList.Add(arg);
        using var process=Process.Start(info) ?? throw new InvalidOperationException("GPU guardian did not start");
        using var sending=new SemaphoreSlim(1,1);
        using var stopped=new CancellationTokenSource();
        var inputClosed=0;
        void CloseInput()
        {
            if(Interlocked.Exchange(ref inputClosed,1)!=0)return;
            try {process.StandardInput.BaseStream.Dispose();}
            catch(Exception e) when(e is IOException or ObjectDisposedException or InvalidOperationException) { }
        }
        async Task Send(string value)
        {
            await sending.WaitAsync(stopped.Token);
            try {if(Volatile.Read(ref inputClosed)!=0)throw new IOException("Guardian control input closed");await process.StandardInput.WriteLineAsync(value);await process.StandardInput.FlushAsync();}
            finally {sending.Release();}
        }
        var log=PumpBounded(process.StandardError,context.MaxLogBytes,CloseInput);
        async Task Heartbeat()
        {
            try {while(!stopped.IsCancellationRequested) {await Send("heartbeat "+id);await Task.Delay(2000,stopped.Token);}}
            catch (Exception e) when(e is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException) { }
        }
        var heartbeat=Heartbeat();
        using var cancel=cancellation.Register(CloseInput);
        try
        {
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(spec.MaximumWaitSeconds+spec.TimeoutSeconds+30));
            var ready=await ReadLine(process.StandardOutput,deadline.Token);
            var greeting=JsonSerializer.Deserialize<GpuGuardianOutcome>(ready,Json) ?? throw new InvalidOperationException("Missing guardian handshake");
            if(greeting.SchemaVersion!=Protocol || greeting.Id!=id || greeting.Status!="leased") throw new InvalidOperationException("GPU guardian refused ownership: "+greeting.Error);
            // The guardian has just checked the complete queued identity set under
            // its lease. Avoid repeating a multi-minute inventory in the bounded
            // start handshake; it rechecks the inputs after the callback as well.
            context.ValidateFresh();Validate(context,job,validateInputs:false);
            if(beforeStart is not null)
            {
                using var callbackCancellation=CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                callbackCancellation.CancelAfter(TimeSpan.FromSeconds(Math.Min(30,context.TimeoutSeconds)));
                var callback=Task.Run(()=>beforeStart(callbackCancellation.Token),callbackCancellation.Token);
                try {await callback.WaitAsync(TimeSpan.FromSeconds(Math.Min(30,context.TimeoutSeconds)),deadline.Token);}
                finally
                {
                    callbackCancellation.Cancel();
                    _=callback.ContinueWith(t=>_ = t.Exception,TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously);
                }
            }
            deadline.Token.ThrowIfCancellationRequested();
            await Send("start "+id);
            var text=await ReadLine(process.StandardOutput,deadline.Token);
            var outcome=JsonSerializer.Deserialize<GpuGuardianOutcome>(text,Json) ?? throw new InvalidOperationException("Missing guardian outcome");
            await process.WaitForExitAsync(deadline.Token);
            await log;
            if(outcome.SchemaVersion!=Protocol || outcome.Id!=id || outcome.Status!="succeeded" || !outcome.NativeRetired || process.ExitCode!=0)
                throw new InvalidOperationException("GPU batch failed: "+outcome.Error);
        }
        catch
        {
            // Closing control requests cleanup. Killing the guardian would release its
            // lease before it can retire native descendants, so never kill its tree.
            CloseInput();
            try {await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));}
            catch(TimeoutException) {throw new InvalidOperationException("Guardian cleanup is still pending; no automatic retry or GPU-idle claim. Inspect "+state);}
            throw;
        }
        finally
        {
            stopped.Cancel();
            CloseInput();
            try {await heartbeat.WaitAsync(TimeSpan.FromSeconds(5));}catch(TimeoutException) {_=heartbeat.ContinueWith(t=>_ = t.Exception,TaskContinuationOptions.OnlyOnFaulted);}
            if(process.HasExited)
                try {await log;}catch(Exception e) when(e is IOException or InvalidOperationException) { }
            else _=log.ContinueWith(t=>_ = t.Exception,TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously);
        }
    }

    public static async Task<int> Serve(string[] args)
    {
        GpuGuardianJob? job=null;Workspace? workspace=null;string? state=null;var retired=true;IDisposable? lease=null;IDisposable? compatibility=null;
        using var cancel=new CancellationTokenSource();
        ConsoleCancelEventHandler signal=(_,e)=>{e.Cancel=true;cancel.Cancel();};Console.CancelKeyPress+=signal;
        try
        {
            var options=new Dictionary<string,string>(StringComparer.Ordinal);
            for(var i=0;i<args.Length;i+=2)
                if(i+1>=args.Length || !args[i].StartsWith("--",StringComparison.Ordinal) || !options.TryAdd(args[i][2..],args[i+1])) throw new InvalidOperationException("Invalid guardian options");
            if(options.Count!=5 || options.GetValueOrDefault("api-version")!="1") throw new InvalidOperationException("Explicit guardian APIv1 and exact job arguments required");
            var context=HostExecutionContext.Load(options["root"],options["host-context"]);
            workspace=new Workspace(context.Root,context);
            var path=workspace.Resolve(options["job"]);
            byte[] snapshot;
            using(var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                if(input.Length is <2 or >16777216)throw new InvalidOperationException("Guardian job must contain2bytes..16MiB");
                snapshot=new byte[checked((int)input.Length)];input.ReadExactly(snapshot);
                if(input.ReadByte()!=-1)throw new InvalidOperationException("Guardian job changed while reading");
            }
            if(Convert.ToHexStringLower(SHA256.HashData(snapshot))!=options["job-sha256"])throw new InvalidOperationException("Guardian job hash changed");
            job=JsonSerializer.Deserialize<GpuGuardianJob>(snapshot,Json) ?? throw new InvalidOperationException("Missing guardian job");
            if(job.SchemaVersion!=Protocol || job.Root!=context.Root || job.ContextPath!=context.ContextPath || job.ContextSha256!=context.ContextSha256
                || job.Id.Length!=32 || !job.Id.All(char.IsAsciiHexDigit)) throw new InvalidOperationException("Guardian job/context identity mismatch");
            state=context.StateRoot+"/gpu-guardian/"+job.Id;
            if(workspace.Relative(path)!=state+"/job.json")throw new InvalidOperationException("Guardian job must use its canonical task state");
            Parent(job);
            Validate(context,job,validateInputs:false);
            var start=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var control=Control(job,start,cancel);
            lease=await GpuLease.Acquire(job.Process.MaximumWaitSeconds,cancel.Token);
            context.ValidateFresh();Parent(job);Validate(context,job,cancellation:cancel.Token);
            if(workspace.HashFile(path)!=options["job-sha256"])throw new InvalidOperationException("Queued guardian job changed");
            compatibility=job.Process.CompatibilityLock is { } legacy?workspace.Lock(legacy):null;
            await Emit(new(Protocol,job.Id,"leased",true,null));
            await start.Task.WaitAsync(TimeSpan.FromSeconds(Math.Min(30,context.TimeoutSeconds)),cancel.Token);
            context.ValidateFresh();Parent(job);Validate(context,job,cancellation:cancel.Token);
            if(workspace.HashFile(path)!=options["job-sha256"])throw new InvalidOperationException("Guardian job changed before launch");
            var runner=new NativeProcess();
            var native=new HostNativeProcess(context,runner);
            cancel.Token.ThrowIfCancellationRequested();
            retired=false;
            try
            {
                if(job.Process.MaximumPrivateBytes is { } memory)
                    await native.RunWithLimits(job.Process.Executable,job.Process.Arguments,job.Process.WorkingDirectory,job.Process.Log,job.Process.TimeoutSeconds,memory,cancel.Token,job.Process.MaximumLogBytes,job.Process.Environment);
                else
                    await native.Run(job.Process.Executable,job.Process.Arguments,job.Process.WorkingDirectory,job.Process.Log,job.Process.TimeoutSeconds,cancel.Token,job.Process.MaximumLogBytes,job.Process.Environment);
            }
            finally {retired=runner.LastRetirementConfirmed;}
            var result=new GpuGuardianOutcome(Protocol,job.Id,"succeeded",retired,null);
            SaveOutcome(workspace,state,result);
            try {await Emit(result);}catch(IOException) {workspace.Save(state+"/delivery-failure.json",new {schemaVersion=Protocol,error="Client result pipe closed after native retirement",nativeOutcome="succeeded"});}
            cancel.Cancel();
            return 0;
        }
        catch(Exception error)
        {
            cancel.Cancel();
            var result=new GpuGuardianOutcome(Protocol,job?.Id??"unknown","failed",retired,error.Message);
            if(workspace is not null && state is not null)
                try {SaveOutcome(workspace,state,result);}catch(Exception e) when(e is IOException or UnauthorizedAccessException or InvalidOperationException) { }
            try {await Emit(result);}catch(IOException) { }
            if(!retired && lease is not null)
            {
                // Fail closed on an OS cleanup failure. An operator must reconcile
                // the owned native job/group; an uncertain guardian must not
                // announce idle hardware or permit another model to start.
                await Task.Delay(Timeout.InfiniteTimeSpan,CancellationToken.None);
            }
            return 1;
        }
        finally {compatibility?.Dispose();lease?.Dispose();Console.CancelKeyPress-=signal;}
    }

    static async Task Control(GpuGuardianJob job,TaskCompletionSource start,CancellationTokenSource cancel)
    {
        var messages=Channel.CreateBounded<string>(new BoundedChannelOptions(16) {SingleReader=true,SingleWriter=true});
        // Console.In performs synchronous reads even through ReadAsync. Isolate
        // the blocking reader so ownership, EOF and heartbeat watchdogs run.
        var reader=new Thread(()=>
        {
            try
            {
                using var input=new StreamReader(Console.OpenStandardInput(),new UTF8Encoding(false,true));
                while(true)
                {
                    var text=new StringBuilder();int c;
                    while((c=input.Read())!=-1 && c!='\n') {if(text.Length>=128)throw new IOException("Control line exceeds128bytes");text.Append((char)c);}
                    if(c==-1)throw new IOException("Client control pipe closed");
                    if(!messages.Writer.TryWrite(text.ToString().TrimEnd('\r')))throw new IOException("Control queue exceeded its bound");
                }
            }
            catch(Exception e) {messages.Writer.TryComplete(e);}
        }) {IsBackground=true,Name="Bubo guardian control reader"};
        reader.Start();
        try
        {
            while(!cancel.IsCancellationRequested)
            {
                var line=await messages.Reader.ReadAsync(cancel.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(15),cancel.Token);
                Parent(job);
                if(line=="start "+job.Id)start.TrySetResult();
                else if(line!="heartbeat "+job.Id)throw new InvalidOperationException("Unknown guardian control message");
            }
        }
        catch(Exception) {cancel.Cancel();}
    }

    static void SaveOutcome(Workspace workspace,string state,GpuGuardianOutcome value)
    {
        var path=workspace.ResolveWrite(state+"/result.json");
        using var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read);
        JsonSerializer.Serialize(output,value,Json);output.Flush(true);
    }

    static void Validate(HostExecutionContext context,GpuGuardianJob job,bool validateInputs=true,CancellationToken cancellation=default)
    {
        context.ValidateFresh();
        if(context.InspectionOnly)throw new InvalidOperationException("Read-only inspection cannot claim GPU production ownership");
        var spec=job.Process;
        context.EnsureToolAllowed(spec.Executable);
        if(!Path.GetFullPath(spec.WorkingDirectory).Equals(context.Root,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal)) context.EnsureWriteAllowed(spec.WorkingDirectory);
        context.EnsureWriteAllowed(spec.Log);
        if(spec.TimeoutSeconds is <1 or >86400 || spec.TimeoutSeconds>context.TimeoutSeconds || spec.MaximumLogBytes is <1024 or >1073741824 || spec.MaximumLogBytes>context.MaxLogBytes
            || spec.MaximumPrivateBytes is <=0 || context.MaxPrivateBytes is { } maximum && spec.MaximumPrivateBytes>maximum
            || spec.MaximumWaitSeconds<1 || spec.MaximumWaitSeconds>Math.Min(86400,context.TimeoutSeconds)) throw new InvalidOperationException("GPU job exceeds host budgets");
        if(spec.Arguments is null || spec.Arguments.Length>4096 || spec.Arguments.Any(x=>x is null || x.Length>32768 || x.Contains('\0')))throw new InvalidOperationException("Unbounded GPU arguments");
        if(spec.CompatibilityLock is { } legacy)context.EnsureWriteAllowed(legacy);
        if(spec.Inputs is {Count:>65536})throw new InvalidOperationException("GPU input identity set exceeds its bound");
        if(!validateInputs)return;
        foreach(var file in spec.Inputs??[])
        {
            cancellation.ThrowIfCancellationRequested();
            if(file.Sha256.Length!=64 || !file.Sha256.All(char.IsAsciiHexDigit) || InputHash(context,file.Path)!=file.Sha256)
                throw new InvalidOperationException("GPU input changed while waiting for ownership");
        }
        cancellation.ThrowIfCancellationRequested();
    }

    static string InputHash(HostExecutionContext context,string path)
    {
        var full=Path.GetFullPath(path,context.Root);
        var comparison=OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal;
        bool Within(string child,string root)=>child.Equals(Path.TrimEndingDirectorySeparator(root),comparison)
            || child.StartsWith(Path.TrimEndingDirectorySeparator(root)+Path.DirectorySeparatorChar,comparison);
        if(Within(full,context.Root))return Workspace.Hash(context.ResolveReadPath(path));
        var bundle=Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        if(Within(full,bundle))
        {
            // Fixed runtime resources live beside this exact built apphost,
            // outside a caller's project. No sibling-source search is allowed.
            var resources=new Workspace(bundle);
            return resources.HashFile(resources.Resolve(Path.GetRelativePath(bundle,full)));
        }
        return context.HashToolFile(full);
    }

    static string Apphost()
    {
        var path=Environment.ProcessPath ?? throw new InvalidOperationException("An explicit built apphost is required");
        if(Path.GetFileNameWithoutExtension(path).Equals("dotnet",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("GPU execution requires the built apphost; do not invoke the DLL through dotnet");
        return Path.GetFullPath(path);
    }
    static void Parent(GpuGuardianJob job)
    {
        if(ParentPid()!=job.ClientPid)throw new InvalidOperationException("Guardian must be a direct child of its bound Bubo apphost");
        using var parent=Process.GetProcessById(job.ClientPid);
        var image=parent.MainModule?.FileName ?? throw new InvalidOperationException("Client image unavailable");
        if(parent.HasExited || StartIdentity(parent)!=job.ClientStartIdentity || !Path.GetFullPath(image).Equals(Apphost(),OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))
            throw new InvalidOperationException("Guardian client identity changed or is not the same built apphost");
    }
    static long StartIdentity(Process process)
    {
        if(!OperatingSystem.IsLinux())return process.StartTime.ToUniversalTime().Ticks;
        // Linux Process.StartTime uses a boot-time estimate cached separately by
        // each .NET process. Bind the kernel start counter, not derived UTC time.
        var stat=File.ReadAllText("/proc/"+process.Id+"/stat");
        var end=stat.LastIndexOf(')');
        var fields=end>=0?stat[(end+1)..].Split(' ',StringSplitOptions.RemoveEmptyEntries):[];
        if(fields.Length<=19 || !long.TryParse(fields[19],System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out var start) || start<=0)
            throw new InvalidOperationException("Cannot verify guardian client kernel start identity");
        return start;
    }
    static async Task Emit(GpuGuardianOutcome value)=>await Console.Out.WriteLineAsync(JsonSerializer.Serialize(value,Json));
    static async Task<string> ReadLine(TextReader input,CancellationToken cancellation)
    {
        var buffer=new char[1];var text=new StringBuilder();
        while(true)
        {
            var count=await input.ReadAsync(buffer.AsMemory(),cancellation);
            if(count==0)throw new IOException("Guardian control/result pipe closed");
            if(buffer[0]=='\n')return text.ToString().TrimEnd('\r');
            if(text.Length>=65536)throw new InvalidOperationException("Guardian control/result line exceeds64KiB");
            text.Append(buffer[0]);
        }
    }
    static async Task PumpBounded(StreamReader input,long maximum,Action overflow)
    {
        var buffer=new char[4096];long bytes=0;int count;
        while((count=await input.ReadAsync(buffer))>0)
        {
            bytes+=Encoding.UTF8.GetByteCount(buffer.AsSpan(0,count));
            if(bytes>maximum){overflow();throw new InvalidOperationException("Guardian log budget exceeded");}
            await Console.Error.WriteAsync(buffer.AsMemory(0,count));
        }
    }
    static int ParentPid()
    {
        if(OperatingSystem.IsLinux())
        {
            var stat=File.ReadAllText("/proc/self/stat");return int.Parse(stat[(stat.LastIndexOf(')')+2)..].Split(' ')[1],System.Globalization.CultureInfo.InvariantCulture);
        }
        if(OperatingSystem.IsWindows())
        {
            using var self=Process.GetCurrentProcess();var info=new BasicInformation();
            if(NtQueryInformationProcess(self.Handle,0,ref info,Marshal.SizeOf<BasicInformation>(),out _)!=0)throw new InvalidOperationException("Cannot verify guardian parent");
            return checked((int)info.Parent);
        }
        throw new PlatformNotSupportedException("Guardian parent identity unsupported");
    }
    [StructLayout(LayoutKind.Sequential)]struct BasicInformation {public nint ExitStatus,Peb,Affinity,Priority,Id,Parent;}
    [DllImport("ntdll.dll")]static extern int NtQueryInformationProcess(nint handle,int kind,ref BasicInformation info,int size,out int returned);
}
