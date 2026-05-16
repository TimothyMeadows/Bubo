using Bubo.LocalAgent.Abstractions;

namespace Bubo.LocalAgent.Runtime.Tools;

public sealed class ReadFileTool : WorkspaceToolBase
{
    public override string Name => "read_file";

    public override string Description => "Read a UTF-8 text file inside the workspace.";

    protected override async Task<ToolResult> InvokeCoreAsync(
        ToolRequest request,
        WorkspaceGuard guard,
        CancellationToken cancellationToken)
    {
        var path = guard.ResolveInsideWorkspace(GetArgument(request, "path"));
        var text = await File.ReadAllTextAsync(path, cancellationToken);
        return new ToolResult
        {
            Success = true,
            Output = text
        };
    }
}
