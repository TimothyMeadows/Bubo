using Bubo.LocalAgent.Abstractions;

namespace Bubo.LocalAgent.Sandbox.Docker;

public sealed class DockerSandboxRunner : ISandboxRunner
{
    public Task<ToolResult> RunCommandAsync(
        string command,
        IReadOnlyList<string> arguments,
        SandboxOptions options,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new ToolResult
        {
            Success = false,
            Error = "Docker sandbox execution is implemented in a later goal task."
        });
    }
}
