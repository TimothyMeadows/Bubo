using Bubo.LocalAgent.Abstractions;

namespace Bubo.LocalAgent.Runtime.Tools;

public abstract class WorkspaceToolBase : IAgentTool
{
    public abstract string Name { get; }

    public abstract string Description { get; }

    public async Task<ToolResult> InvokeAsync(
        ToolRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var guard = new WorkspaceGuard(request.WorkspaceRoot);
            return await InvokeCoreAsync(request, guard, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new ToolResult
            {
                Success = false,
                Error = exception.Message
            };
        }
    }

    protected abstract Task<ToolResult> InvokeCoreAsync(
        ToolRequest request,
        WorkspaceGuard guard,
        CancellationToken cancellationToken);

    protected static string GetArgument(ToolRequest request, string name)
    {
        if (!request.Arguments.TryGetValue(name, out var value) ||
            string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Missing required tool argument: {name}");
        }

        return value;
    }
}
