using Bubo.LocalAgent.Abstractions;
using Bubo.LocalAgent.Runtime.Tools;

namespace Bubo.LocalAgent.Runtime.Tests;

public sealed class ToolTests
{
    [Fact]
    public async Task WriteAndReadFileToolsStayInsideWorkspace()
    {
        var workspace = CreateWorkspace();
        var write = new WriteFileTool();
        var read = new ReadFileTool();

        var writeResult = await write.InvokeAsync(
            new ToolRequest
            {
                Name = "write_file",
                WorkspaceRoot = workspace,
                Arguments = new Dictionary<string, string>
                {
                    ["path"] = "notes/result.txt",
                    ["content"] = "hello from Bubo"
                }
            },
            CancellationToken.None);

        var readResult = await read.InvokeAsync(
            new ToolRequest
            {
                Name = "read_file",
                WorkspaceRoot = workspace,
                Arguments = new Dictionary<string, string>
                {
                    ["path"] = "notes/result.txt"
                }
            },
            CancellationToken.None);

        Assert.True(writeResult.Success);
        Assert.True(readResult.Success);
        Assert.Equal("hello from Bubo", readResult.Output);
    }

    [Fact]
    public async Task ReadFileToolRejectsTraversal()
    {
        var workspace = CreateWorkspace();
        var read = new ReadFileTool();

        var result = await read.InvokeAsync(
            new ToolRequest
            {
                Name = "read_file",
                WorkspaceRoot = workspace,
                Arguments = new Dictionary<string, string>
                {
                    ["path"] = "../outside.txt"
                }
            },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("outside the workspace", result.Error);
    }

    [Fact]
    public async Task SearchTextToolFindsLiteralMatches()
    {
        var workspace = CreateWorkspace();
        Directory.CreateDirectory(Path.Combine(workspace, "src"));
        await File.WriteAllTextAsync(Path.Combine(workspace, "src", "app.txt"), "alpha\nBubo\nomega");

        var search = new SearchTextTool();
        var result = await search.InvokeAsync(
            new ToolRequest
            {
                Name = "search_text",
                WorkspaceRoot = workspace,
                Arguments = new Dictionary<string, string>
                {
                    ["path"] = ".",
                    ["pattern"] = "bubo"
                }
            },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("src/app.txt:2:Bubo", result.Output);
    }

    [Fact]
    public async Task RunCommandAllowsDotnetWithoutShell()
    {
        var workspace = CreateWorkspace();
        var tool = new RunCommandTool();

        var result = await tool.InvokeAsync(
            new ToolRequest
            {
                Name = "run_command",
                WorkspaceRoot = workspace,
                Arguments = new Dictionary<string, string>
                {
                    ["executable"] = "dotnet",
                    ["arguments"] = "--version"
                }
            },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Output));
    }

    [Fact]
    public async Task RunCommandRejectsUnlistedExecutable()
    {
        var workspace = CreateWorkspace();
        var tool = new RunCommandTool();

        var result = await tool.InvokeAsync(
            new ToolRequest
            {
                Name = "run_command",
                WorkspaceRoot = workspace,
                Arguments = new Dictionary<string, string>
                {
                    ["executable"] = "powershell"
                }
            },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("not allowlisted", result.Error);
    }

    private static string CreateWorkspace()
    {
        var workspace = Path.Combine(
            Path.GetTempPath(),
            "bubo-tool-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        return Path.GetFullPath(workspace);
    }
}
