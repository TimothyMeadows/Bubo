using Bubo.LocalAgent.Abstractions;

namespace Bubo.LocalAgent.Runtime.Tools;

public sealed class GitDiffTool : SandboxBackedToolBase
{
    public override string Name => "git_diff";

    public override string Description => "Run `git diff --stat` or `git diff` inside the Docker sandbox.";

    public GitDiffTool(
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
        var statOnly = !request.Arguments.TryGetValue("stat", out var stat) ||
                       !string.Equals(stat, "false", StringComparison.OrdinalIgnoreCase);

        return RunSandboxedCommandAsync(
            guard,
            "git",
            statOnly ? new[] { "diff", "--stat" } : new[] { "diff" },
            guard.WorkspaceRoot,
            cancellationToken);
    }
}
