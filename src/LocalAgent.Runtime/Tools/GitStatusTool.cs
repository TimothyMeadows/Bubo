using Bubo.LocalAgent.Abstractions;

namespace Bubo.LocalAgent.Runtime.Tools;

public sealed class GitStatusTool : SandboxBackedToolBase
{
    public override string Name => "git_status";

    public override string Description => "Run `git status --short --branch` inside the Docker sandbox.";

    public GitStatusTool(
        ISandboxRunner? sandboxRunner = null,
        SandboxOptions? sandboxOptions = null)
        : base(sandboxRunner, sandboxOptions)
    {
    }

    protected override Task<ToolResult> InvokeCoreAsync(
        ToolRequest request,
        WorkspaceGuard guard,
        CancellationToken cancellationToken)
    {
        return RunSandboxedCommandAsync(
            guard,
            "git",
            new[] { "status", "--short", "--branch" },
            guard.WorkspaceRoot,
            cancellationToken);
    }
}
