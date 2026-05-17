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
    public async Task PatchFileReplacesSingleMatch()
    {
        var workspace = CreateWorkspace();
        var path = Path.Combine(workspace, "notes.txt");
        await File.WriteAllTextAsync(path, "alpha\nold\nomega\n");
        var tool = new PatchFileTool();

        var result = await tool.InvokeAsync(
            new ToolRequest
            {
                Name = "patch_file",
                WorkspaceRoot = workspace,
                Arguments = new Dictionary<string, string>
                {
                    ["path"] = "notes.txt",
                    ["old"] = "old",
                    ["new"] = "new"
                }
            },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("notes.txt", result.Output);
        Assert.Equal("alpha\nnew\nomega\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task PatchFileRejectsAmbiguousMatch()
    {
        var workspace = CreateWorkspace();
        var path = Path.Combine(workspace, "notes.txt");
        await File.WriteAllTextAsync(path, "old\nold\n");
        var tool = new PatchFileTool();

        var result = await tool.InvokeAsync(
            new ToolRequest
            {
                Name = "patch_file",
                WorkspaceRoot = workspace,
                Arguments = new Dictionary<string, string>
                {
                    ["path"] = "notes.txt",
                    ["old"] = "old",
                    ["new"] = "new"
                }
            },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("more than once", result.Error);
        Assert.Equal("old\nold\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task PatchFileRejectsGitMetadataPath()
    {
        var workspace = CreateWorkspace();
        Directory.CreateDirectory(Path.Combine(workspace, ".git"));
        await File.WriteAllTextAsync(Path.Combine(workspace, ".git", "config"), "old");
        var tool = new PatchFileTool();

        var result = await tool.InvokeAsync(
            new ToolRequest
            {
                Name = "patch_file",
                WorkspaceRoot = workspace,
                Arguments = new Dictionary<string, string>
                {
                    ["path"] = ".git/config",
                    ["old"] = "old",
                    ["new"] = "new"
                }
            },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("reserved Git metadata", result.Error);
    }

    [Fact]
    public async Task GitApplyPatchUsesSandboxRunnerAndReportsFiles()
    {
        var workspace = CreateWorkspace();
        var runner = new RecordingSandboxRunner();
        var tool = new GitApplyPatchTool(runner);

        var patch = """
                   diff --git a/notes.txt b/notes.txt
                   new file mode 100644
                   index 0000000..f2ba8f8
                   --- /dev/null
                   +++ b/notes.txt
                   @@ -0,0 +1 @@
                   +Hello
                   """;

        var result = await tool.InvokeAsync(
            new ToolRequest
            {
                Name = "git_apply_patch",
                WorkspaceRoot = workspace,
                Arguments = new Dictionary<string, string>
                {
                    ["patch"] = patch
                }
            },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("notes.txt", result.Output);
        Assert.Equal(2, runner.Calls.Count);
        Assert.All(runner.Calls, call => Assert.Equal("git", call.Command));
        Assert.Equal("apply", runner.Calls[0].Arguments[0]);
        Assert.Equal("--check", runner.Calls[0].Arguments[1]);
        Assert.StartsWith("/workspace/.bubo/patches/", runner.Calls[0].Arguments[2], StringComparison.Ordinal);
        Assert.Equal(workspace, runner.Calls[0].Options.WorkspacePath);
    }

    [Fact]
    public async Task GitApplyPatchRejectsTraversal()
    {
        var workspace = CreateWorkspace();
        var runner = new RecordingSandboxRunner();
        var tool = new GitApplyPatchTool(runner);

        var patch = """
                   diff --git a/../outside.txt b/../outside.txt
                   --- a/../outside.txt
                   +++ b/../outside.txt
                   @@ -1 +1 @@
                   -old
                   +new
                   """;

        var result = await tool.InvokeAsync(
            new ToolRequest
            {
                Name = "git_apply_patch",
                WorkspaceRoot = workspace,
                Arguments = new Dictionary<string, string>
                {
                    ["patch"] = patch
                }
            },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("parent traversal", result.Error);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void ToolRegistryIncludesPatchTools()
    {
        var registry = ToolRegistry.CreateDefault(new RecordingSandboxRunner());

        Assert.Contains("patch_file", registry.ToolNames);
        Assert.Contains("git_apply_patch", registry.ToolNames);
    }

    [Fact]
    public void ModelSafeToolRegistryExcludesGenericRunCommand()
    {
        var registry = ToolRegistry.CreateModelSafe(new RecordingSandboxRunner());

        Assert.DoesNotContain("run_command", registry.ToolNames);
        Assert.Contains("git_apply_patch", registry.ToolNames);
    }

    [Fact]
    public async Task ListFilesSkipsNestedSymlinkDirectory()
    {
        var workspace = CreateWorkspace();
        var outside = CreateWorkspace();
        await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "secret");
        var link = Path.Combine(workspace, "linked");

        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var result = await new ListFilesTool().InvokeAsync(
            new ToolRequest
            {
                Name = "list_files",
                WorkspaceRoot = workspace,
                Arguments = new Dictionary<string, string> { ["path"] = "." }
            },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.DoesNotContain("secret.txt", result.Output);
    }

    [Fact]
    public async Task SearchTextSkipsNestedSymlinkDirectory()
    {
        var workspace = CreateWorkspace();
        var outside = CreateWorkspace();
        await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "needle");
        var link = Path.Combine(workspace, "linked");

        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var result = await new SearchTextTool().InvokeAsync(
            new ToolRequest
            {
                Name = "search_text",
                WorkspaceRoot = workspace,
                Arguments = new Dictionary<string, string>
                {
                    ["path"] = ".",
                    ["pattern"] = "needle"
                }
            },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.DoesNotContain("needle", result.Output);
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
        public List<SandboxCall> Calls { get; } = new();

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
            Calls.Add(new SandboxCall(command, arguments, options));

            return Task.FromResult(new ToolResult
            {
                Success = true,
                ExitCode = 0,
                Output = $"{command} {string.Join(" ", arguments)}"
            });
        }
    }

    private sealed record SandboxCall(
        string Command,
        IReadOnlyList<string> Arguments,
        SandboxOptions Options);
}
