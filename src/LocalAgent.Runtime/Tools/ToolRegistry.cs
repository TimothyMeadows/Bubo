using Bubo.LocalAgent.Abstractions;

namespace Bubo.LocalAgent.Runtime.Tools;

public sealed class ToolRegistry
{
    private readonly Dictionary<string, IAgentTool> _tools;

    public ToolRegistry(IEnumerable<IAgentTool> tools)
    {
        _tools = tools.ToDictionary(tool => tool.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<string> ToolNames => _tools.Keys;

    public bool TryGet(string name, out IAgentTool tool)
    {
        return _tools.TryGetValue(name, out tool!);
    }

    public static ToolRegistry CreateDefault()
    {
        return new ToolRegistry(new IAgentTool[]
        {
            new ReadFileTool(),
            new WriteFileTool(),
            new ListFilesTool(),
            new SearchTextTool(),
            new RunCommandTool(),
            new GitStatusTool(),
            new GitDiffTool()
        });
    }
}
