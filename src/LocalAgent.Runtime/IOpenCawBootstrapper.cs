using Bubo.LocalAgent.Abstractions;

namespace Bubo.LocalAgent.Runtime;

public interface IOpenCawBootstrapper
{
    Task<OpenCawBootstrapResult> BootstrapAsync(
        WorkspaceGuard guard,
        OpenCawOptions options,
        CancellationToken cancellationToken);
}
