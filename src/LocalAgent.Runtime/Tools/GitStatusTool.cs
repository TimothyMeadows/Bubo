using Bubo.LocalAgent.Abstractions;

namespace Bubo.LocalAgent.Runtime.Tools;

public sealed class GitStatusTool : WorkspaceToolBase
{
    public override string Name => "git_status";

    public override string Description => "Run `git status --short --branch` inside the workspace.";

    protected override Task<ToolResult> InvokeCoreAsync(
        ToolRequest request,
        WorkspaceGuard guard,
        CancellationToken cancellationToken)
    {
        return ProcessToolRunner.RunAsync(
            "git",
            new[] { "status", "--short", "--branch" },
            guard.WorkspaceRoot,
            cancellationToken);
    }
}
