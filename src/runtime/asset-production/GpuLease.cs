using System.Net.Sockets;
using System.Diagnostics;

namespace OpenCaw.AssetProduction;

/// <summary>Conservative cooperative exclusion for all Bubo GPU jobs in this OS namespace.</summary>
public static class GpuLease
{
    // Do not version these names: independent protocol versions must still exclude each other.
    public const string WindowsName="Global\\Bubo.Gpu.All";
    public const string LinuxName="\0bubo.gpu.all";

    public static async Task<IDisposable> Acquire(int maximumWaitSeconds,CancellationToken cancellation)
    {
        if (maximumWaitSeconds is <1 or >86400) throw new InvalidOperationException("GPU ownership wait must be1..86400seconds");
        if (OperatingSystem.IsWindows()) return await WindowsLease.Acquire(maximumWaitSeconds,cancellation);
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("GPU ownership is supported only by Windows and Linux; no fallback");
        var waiting=Stopwatch.StartNew();
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var socket=new Socket(AddressFamily.Unix,SocketType.Stream,ProtocolType.Unspecified);
            try
            {
                socket.Bind(new UnixDomainSocketEndPoint(LinuxName));
                return new SocketLease(socket);
            }
            catch (SocketException e) when (e.SocketErrorCode==SocketError.AddressAlreadyInUse)
            {
                socket.Dispose();
                if (waiting.Elapsed.TotalSeconds>=maximumWaitSeconds) throw new TimeoutException("Another Bubo guardian owns the GPU; no model loaded");
                await Task.Delay(100,cancellation);
            }
            catch {socket.Dispose();throw;}
        }
    }

    sealed class SocketLease(Socket socket):IDisposable
    {
        public void Dispose()=>socket.Dispose();
    }

    sealed class WindowsLease:IDisposable
    {
        readonly ManualResetEvent release=new(false);
        readonly TaskCompletionSource<WindowsLease> ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly Thread holder;
        Exception? releaseError;
        int disposed;
        WindowsLease(int wait,CancellationToken cancellation)
        {
            holder=new Thread(()=>Hold(wait,cancellation)) {IsBackground=true,Name="Bubo GPU mutex owner"};
            holder.Start();
        }
        public static async Task<WindowsLease> Acquire(int wait,CancellationToken cancellation)
        {
            var lease=new WindowsLease(wait,cancellation);
            try {return await lease.ready.Task;}
            catch {lease.release.Set();lease.holder.Join();lease.release.Dispose();throw;}
        }
        void Hold(int wait,CancellationToken cancellation)
        {
            var acquired=false;
            Mutex? mutex=null;
            try
            {
                mutex=new Mutex(false,WindowsName);
                var index=WaitHandle.WaitAny([mutex,cancellation.WaitHandle],TimeSpan.FromSeconds(wait));
                if (index==1) throw new OperationCanceledException(cancellation);
                if (index==WaitHandle.WaitTimeout) throw new TimeoutException("Another Bubo guardian owns the GPU; no model loaded");
                acquired=true;
                cancellation.ThrowIfCancellationRequested();
                ready.TrySetResult(this);
                // Cancellation cannot release ownership before native cleanup finishes.
                release.WaitOne();
            }
            catch (AbandonedMutexException)
            {
                acquired=true;
                ready.TrySetException(new InvalidOperationException("Abandoned GPU guardian ownership; verify native cleanup before retrying. Abandonment does not prove the GPU idle."));
            }
            catch (Exception e) {ready.TrySetException(e);}
            finally
            {
                if (acquired)
                    try {mutex!.ReleaseMutex();} catch (Exception e) {releaseError=e;}
                mutex?.Dispose();
            }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed,1)!=0) return;
            release.Set();
            if (!holder.Join(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("GPU holder did not retire; cleanup is uncertain");
            release.Dispose();
            if (releaseError is not null) throw new InvalidOperationException("GPU ownership release failed",releaseError);
        }
    }
}
