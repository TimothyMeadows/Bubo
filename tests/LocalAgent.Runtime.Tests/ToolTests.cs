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
        var runner = new RecordingSandboxRunner();
        var tool = new RunCommandTool(runner);

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
        Assert.Equal("dotnet", runner.Command);
        Assert.Equal(new[] { "--version" }, runner.Arguments);
        Assert.Equal(workspace, runner.Options?.WorkspacePath);
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

    [Fact]
    public async Task RunCommandRequiresSandboxRunner()
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

        Assert.False(result.Success);
        Assert.Contains("Docker sandbox runner", result.Error);
    }

    [Fact]
    public async Task GitToolsUseSandboxRunner()
    {
        var workspace = CreateWorkspace();
        var runner = new RecordingSandboxRunner();
        var status = new GitStatusTool(runner);

        var result = await status.InvokeAsync(
            new ToolRequest
            {
                Name = "git_status",
                WorkspaceRoot = workspace
            },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("git", runner.Command);
        Assert.Equal(new[] { "status", "--short", "--branch" }, runner.Arguments);
        Assert.Equal(workspace, runner.Options?.WorkspacePath);
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

    private sealed class RecordingSandboxRunner : ISandboxRunner
    {
        public string? Command { get; private set; }

        public IReadOnlyList<string>? Arguments { get; private set; }

        public SandboxOptions? Options { get; private set; }

        public Task<ToolResult> RunCommandAsync(
            string command,
            IReadOnlyList<string> arguments,
            SandboxOptions options,
            CancellationToken cancellationToken)
        {
            Command = command;
            Arguments = arguments;
            Options = options;

            return Task.FromResult(new ToolResult
            {
                Success = true,
                ExitCode = 0,
                Output = $"{command} {string.Join(" ", arguments)}"
            });
        }
    }
}
