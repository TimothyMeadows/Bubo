using Bubo.LocalAgent.Abstractions;

namespace Bubo.LocalAgent.Runtime.Tools;

public sealed class WriteFileTool : WorkspaceToolBase
{
    public override string Name => "write_file";

    public override string Description => "Write a UTF-8 text file inside the workspace.";

    protected override async Task<ToolResult> InvokeCoreAsync(
        ToolRequest request,
        WorkspaceGuard guard,
        CancellationToken cancellationToken)
    {
        var path = guard.ResolveInsideWorkspace(GetArgument(request, "path"));
        var content = GetArgument(request, "content");
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? guard.WorkspaceRoot);
        await File.WriteAllTextAsync(path, content, cancellationToken);
        return new ToolResult
        {
            Success = true,
            Output = Path.GetRelativePath(guard.WorkspaceRoot, path)
        };
    }
}
