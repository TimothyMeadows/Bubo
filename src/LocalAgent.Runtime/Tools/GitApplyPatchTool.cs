using Bubo.LocalAgent.Abstractions;

namespace Bubo.LocalAgent.Runtime.Tools;

public sealed class GitApplyPatchTool : SandboxBackedToolBase
{
    private const int DefaultMaxPatchBytes = 262_144;
    private const int DefaultMaxFilesChanged = 25;

    public override string Name => "git_apply_patch";

    public override string Description =>
        "Apply a guarded unified diff through git inside the Docker sandbox.";

    public GitApplyPatchTool(
        ISandboxRunner? sandboxRunner = null,
        SandboxOptions? sandboxOptions = null)
        : base(sandboxRunner, sandboxOptions)
    {
    }

    protected override async Task<ToolResult> InvokeCoreAsync(
        ToolRequest request,
        WorkspaceGuard guard,
        CancellationToken cancellationToken)
    {
        var patch = GetArgument(request, "patch");
        var maxPatchBytes = GetPositiveInteger(request, "maxPatchBytes", DefaultMaxPatchBytes);
        if (patch.Length > maxPatchBytes)
        {
            throw new ArgumentException(
                $"git_apply_patch payload exceeds maxPatchBytes ({maxPatchBytes}).");
        }

        var maxFilesChanged = GetPositiveInteger(request, "maxFilesChanged", DefaultMaxFilesChanged);
        var changedFiles = GitPatchPreflight.Scan(patch, maxFilesChanged);
        foreach (var file in changedFiles)
        {
            guard.ResolveWritableFileInsideWorkspace(file);
        }

        var patchPath = guard.ResolveWritableFileInsideWorkspace(
            Path.Combine(".bubo", "patches", $"{Guid.NewGuid():N}.patch"));
        Directory.CreateDirectory(Path.GetDirectoryName(patchPath)!);

        try
        {
            await File.WriteAllTextAsync(patchPath, patch, cancellationToken);
            var containerPatchPath = ToContainerPath(guard, patchPath);
            var checkResult = await RunSandboxedCommandAsync(
                guard,
                "git",
                new[] { "apply", "--check", containerPatchPath },
                guard.WorkspaceRoot,
                cancellationToken);

            if (!checkResult.Success)
            {
                return checkResult;
            }

            var applyResult = await RunSandboxedCommandAsync(
                guard,
                "git",
                new[] { "apply", containerPatchPath },
                guard.WorkspaceRoot,
                cancellationToken);

            return applyResult.Success
                ? new ToolResult
                {
                    Success = true,
                    ExitCode = applyResult.ExitCode,
                    Output = string.Join(Environment.NewLine, changedFiles)
                }
                : applyResult;
        }
        finally
        {
            if (File.Exists(patchPath))
            {
                File.Delete(patchPath);
            }
        }
    }

    private static int GetPositiveInteger(ToolRequest request, string name, int defaultValue)
    {
        if (!request.Arguments.TryGetValue(name, out var raw) ||
            string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        if (!int.TryParse(raw, out var value) || value <= 0)
        {
            throw new ArgumentException($"{name} must be a positive integer.");
        }

        return value;
    }

    private static string ToContainerPath(WorkspaceGuard guard, string hostPath)
    {
        var relativePath = Path.GetRelativePath(guard.WorkspaceRoot, hostPath).Replace('\\', '/');
        return $"/workspace/{relativePath}";
    }
}
