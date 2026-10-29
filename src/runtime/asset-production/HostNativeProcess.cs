using Bubo.Host;
using HostExecutionContext = Bubo.Host.HostExecutionContext;
namespace OpenCaw.AssetProduction;

/// <summary>Applies the caller's exact tool identities and process ceilings.</summary>
public sealed class HostNativeProcess(HostExecutionContext context, INativeProcess inner) : INativeProcess
{
    public bool SupportsPrivateMemoryLimit => inner.SupportsPrivateMemoryLimit;

    void Admit(string executable, string cwd, string log, int seconds, long logBytes, long? privateBytes)
    {
        context.ValidateFresh();
        context.EnsureToolAllowed(executable);
        var fullCwd = Path.GetFullPath(cwd);
        if (!fullCwd.Equals(context.Root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            context.EnsureWriteAllowed(fullCwd);
        context.EnsureWriteAllowed(log);
        if (seconds < 1 || seconds > context.TimeoutSeconds || logBytes < 1 || logBytes > context.MaxLogBytes)
            throw new InvalidOperationException("Native request exceeds the host timeout/log budget; nothing launched");
        if (privateBytes is <= 0 || (context.MaxPrivateBytes is { } maximum && privateBytes > maximum))
            throw new InvalidOperationException("Native request exceeds the host memory budget; nothing launched");
    }

    public Task Run(string executable, IEnumerable<string> arguments, string cwd, string log,
        int seconds, CancellationToken cancellation = default, long maximumLogBytes = 16777216,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        Admit(executable, cwd, log, seconds, maximumLogBytes, context.MaxPrivateBytes);
        if (context.MaxPrivateBytes is { } memory)
            return inner.RunWithLimits(executable, arguments, cwd, log, seconds, memory, cancellation, maximumLogBytes, environment);
        return inner.Run(executable, arguments, cwd, log, seconds, cancellation, maximumLogBytes, environment);
    }

    public Task RunWithLimits(string executable, IEnumerable<string> arguments, string cwd, string log,
        int seconds, long maximumPrivateBytes, CancellationToken cancellation = default,
        long maximumLogBytes = 16777216, IReadOnlyDictionary<string, string>? environment = null)
    {
        Admit(executable, cwd, log, seconds, maximumLogBytes, maximumPrivateBytes);
        return inner.RunWithLimits(executable, arguments, cwd, log, seconds, maximumPrivateBytes,
            cancellation, maximumLogBytes, environment);
    }
    public Task RunGpu(GpuProcessSpec request,CancellationToken cancellation=default,Func<CancellationToken,Task>? beforeStart=null)
    {
        var selected=request with {MaximumPrivateBytes=request.MaximumPrivateBytes??context.MaxPrivateBytes};
        Admit(selected.Executable,selected.WorkingDirectory,selected.Log,selected.TimeoutSeconds,selected.MaximumLogBytes,selected.MaximumPrivateBytes);
        if(selected.MaximumPrivateBytes is not null && !inner.SupportsPrivateMemoryLimit)
            throw new PlatformNotSupportedException("Selected GPU memory ceiling is unavailable; no unbounded launch");
        return GpuGuardian.Run(context,selected,cancellation,beforeStart);
    }
}
