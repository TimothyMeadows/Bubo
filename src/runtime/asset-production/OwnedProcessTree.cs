using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
namespace OpenCaw.AssetProduction;
// Lifetime containment for owned native subprocesses; not a security sandbox.
internal sealed class OwnedProcessTree : IDisposable
{
    IntPtr job;
    bool retired;
    public OwnedProcessTree(Process process, long? maximumPrivateBytes = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            if (maximumPrivateBytes is not null) throw new PlatformNotSupportedException("Owned-job aggregate committed-memory ceiling requires Windows; no unbounded fallback");
            return;
        }
        job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
            throw new Win32Exception();
        var info = new ExtendedLimit { BasicLimitInformation = new BasicLimit { LimitFlags = 0x2000 } };
        if (maximumPrivateBytes is { } memory)
        {
            // JOB_OBJECT_LIMIT_JOB_MEMORY caps aggregate committed memory charged
            // to the owned job, including descendants. It is not resident RAM,
            // GPU memory or an OS sandbox. Process.Start precedes assignment;
            // an assignment failure kills the launched tree in the caller.
            info.BasicLimitInformation.LimitFlags |= 0x200;
            info.JobMemoryLimit = new UIntPtr(checked((ulong)memory));
        }
        if (!SetInformationJobObject(job, 9, ref info, (uint)Marshal.SizeOf<ExtendedLimit>()) || !AssignProcessToJobObject(job, process.Handle))
        {
            var error = Marshal.GetLastWin32Error();
            CloseHandle(job);
            job = IntPtr.Zero;
            throw new Win32Exception(error, "Cannot establish owned native process lifetime");
        }
    }
    public void Dispose()
    {
        if (job != IntPtr.Zero)
        {
            CloseHandle(job);
            job = IntPtr.Zero;
        }
    }
    public async Task Retire(Process process,bool processGroup)
    {
        if(retired)return;
        var clock=Stopwatch.StartNew();
        if(OperatingSystem.IsWindows())
        {
            if(job==IntPtr.Zero || !TerminateJobObject(job,1))throw new Win32Exception(Marshal.GetLastWin32Error(),"Cannot terminate owned native job");
            while(true)
            {
                if(!QueryInformationJobObject(job,1,out var accounting,(uint)Marshal.SizeOf<BasicAccounting>(),IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(),"Cannot verify owned native job retirement");
                if(accounting.ActiveProcesses==0)break;
                if(clock.Elapsed>TimeSpan.FromSeconds(10))throw new InvalidOperationException("Owned native job retirement is uncertain");
                await Task.Delay(25);
            }
        }
        else if(processGroup)
        {
            var stable=0;
            while(stable<2)
            {
                kill(-process.Id,9);
                if(!process.HasExited)process.Kill(entireProcessTree:true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                var living=false;
                foreach(var entry in Directory.EnumerateDirectories("/proc"))
                {
                    if(!int.TryParse(Path.GetFileName(entry),out _))continue;
                    string text;
                    try{text=File.ReadAllText(Path.Combine(entry,"stat"));}
                    catch(FileNotFoundException){continue;}
                    catch(DirectoryNotFoundException){continue;}
                    var fields=text[(text.LastIndexOf(')')+2)..].Split(' ');
                    if(fields.Length<3)throw new InvalidOperationException("Cannot verify native process-group metadata");
                    if(int.Parse(fields[2],System.Globalization.CultureInfo.InvariantCulture)==process.Id && fields[0] is not ("Z" or "X")) {living=true;break;}
                }
                stable=living?0:stable+1;
                if(clock.Elapsed>TimeSpan.FromSeconds(10))throw new InvalidOperationException("Owned native process-group retirement is uncertain");
                await Task.Delay(25);
            }
        }
        else throw new PlatformNotSupportedException("Native retirement cannot be verified on this platform");
        retired=true;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct BasicLimit
    {
        public long PerProcessUserTimeLimit; public long PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize; public UIntPtr MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass; public uint SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct ExtendedLimit
    {
        public BasicLimit BasicLimitInformation; public IoCounters IoInfo; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [StructLayout(LayoutKind.Sequential)]struct BasicAccounting
    {
        public long TotalUserTime,TotalKernelTime,ThisPeriodUserTime,ThisPeriodKernelTime;
        public uint TotalPageFaultCount,TotalProcesses,ActiveProcesses,TotalTerminatedProcesses;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimit info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateJobObject(IntPtr job,uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool QueryInformationJobObject(IntPtr job,int infoClass,out BasicAccounting info,uint length,IntPtr returnedLength);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("libc", SetLastError = true)] internal static extern int kill(int pid, int signal);
}
