using Bubo.Host;
using HostExecutionContext = Bubo.Host.HostExecutionContext;
namespace OpenCaw.AssetProduction;

public interface IWorkspace
{
    string Root { get; }
    HostExecutionContext? HostContext => null;
    string Resolve(string path);
    string ResolveWrite(string path)
    {
        HostContext?.EnsureWriteAllowed(path);
        return Resolve(path);
    }
    string Relative(string path);
    string HashFile(string absolutePath);
    string HashProjectFile(string path) => HashFile(Resolve(path));
    void Save<T>(string path, T value);
    T Load<T>(string path);
    FileStream Lock(string path);
    FileStream LockRead(string path)
        => new(Resolve(path), FileMode.Open, FileAccess.Read, FileShare.None);
}
public interface INativeProcess
{
    Task Run(string executable, IEnumerable<string> arguments, string cwd, string log, int seconds, CancellationToken cancellation = default, long maximumLogBytes = 16777216, IReadOnlyDictionary<string, string>? environment = null);
    bool SupportsPrivateMemoryLimit => false;
    Task RunWithLimits(string executable, IEnumerable<string> arguments, string cwd, string log, int seconds, long maximumPrivateBytes, CancellationToken cancellation = default, long maximumLogBytes = 16777216, IReadOnlyDictionary<string, string>? environment = null)
        => throw new PlatformNotSupportedException("This native runner does not implement the explicitly selected owned-job committed-memory ceiling; nothing was launched");
}
