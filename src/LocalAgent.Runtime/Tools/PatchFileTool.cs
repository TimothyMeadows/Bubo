using Bubo.LocalAgent.Abstractions;

namespace Bubo.LocalAgent.Runtime.Tools;

public sealed class PatchFileTool : WorkspaceToolBase
{
    private const int DefaultMaxPatchBytes = 262_144;

    public override string Name => "patch_file";

    public override string Description =>
        "Apply a bounded old/new text replacement inside the workspace.";

    protected override async Task<ToolResult> InvokeCoreAsync(
        ToolRequest request,
        WorkspaceGuard guard,
        CancellationToken cancellationToken)
    {
        var relativePath = GetArgument(request, "path");
        var oldText = GetArgument(request, "old");
        if (string.IsNullOrEmpty(oldText))
        {
            throw new ArgumentException("patch_file old text must not be empty.");
        }

        var newText = request.Arguments.TryGetValue("new", out var replacement)
            ? replacement
            : string.Empty;
        var maxPatchBytes = GetMaxPatchBytes(request);
        if (oldText.Length + newText.Length > maxPatchBytes)
        {
            throw new ArgumentException(
                $"patch_file payload exceeds maxPatchBytes ({maxPatchBytes}).");
        }

        var path = guard.ResolveWritableFileInsideWorkspace(relativePath);
        var content = await File.ReadAllTextAsync(path, cancellationToken);
        var matchCount = CountMatches(content, oldText);
        if (matchCount == 0)
        {
            return new ToolResult
            {
                Success = false,
                Error = "patch_file old text was not found."
            };
        }

        if (matchCount > 1)
        {
            return new ToolResult
            {
                Success = false,
                Error = "patch_file old text matched more than once."
            };
        }

        var updated = content.Replace(oldText, newText, StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, updated, cancellationToken);

        return new ToolResult
        {
            Success = true,
            Output = Path.GetRelativePath(guard.WorkspaceRoot, path).Replace('\\', '/')
        };
    }

    private static int GetMaxPatchBytes(ToolRequest request)
    {
        if (!request.Arguments.TryGetValue("maxPatchBytes", out var raw) ||
            string.IsNullOrWhiteSpace(raw))
        {
            return DefaultMaxPatchBytes;
        }

        if (!int.TryParse(raw, out var value) || value <= 0)
        {
            throw new ArgumentException("patch_file maxPatchBytes must be a positive integer.");
        }

        return value;
    }

    private static int CountMatches(string content, string oldText)
    {
        var count = 0;
        var index = 0;
        while ((index = content.IndexOf(oldText, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += oldText.Length;
        }

        return count;
    }
}
