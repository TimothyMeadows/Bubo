using Bubo.LocalAgent.Abstractions;

namespace Bubo.LocalAgent.Runtime.Tools;

public sealed class GitDiffTool : WorkspaceToolBase
{
    public override string Name => "git_diff";

    public override string Description => "Run `git diff --stat` or `git diff` inside the workspace.";

    protected override Task<ToolResult> InvokeCoreAsync(
        ToolRequest request,
        WorkspaceGuard guard,
        CancellationToken cancellationToken)
    {
        var statOnly = !request.Arguments.TryGetValue("stat", out var stat) ||
                       !string.Equals(stat, "false", StringComparison.OrdinalIgnoreCase);

        return ProcessToolRunner.RunAsync(
            "git",
            statOnly ? new[] { "diff", "--stat" } : new[] { "diff" },
            guard.WorkspaceRoot,
            cancellationToken);
    }
}
