namespace Bubo.LocalAgent.Abstractions;

public interface ISandboxRunner
{
    Task<ToolResult> RunCommandAsync(
        string command,
        IReadOnlyList<string> arguments,
        SandboxOptions options,
        CancellationToken cancellationToken);
}
