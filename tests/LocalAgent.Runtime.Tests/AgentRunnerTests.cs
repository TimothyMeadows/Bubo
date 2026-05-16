using Bubo.LocalAgent.Abstractions;
using Bubo.LocalAgent.Runtime;

namespace Bubo.LocalAgent.Runtime.Tests;

public sealed class AgentRunnerTests
{
    [Fact]
    public async Task RunAsyncWritesOutputDebugAndTranscriptFiles()
    {
        var workspace = CreateWorkspace();
        var inputPath = Path.Combine(workspace, "INPUT.md");
        var outputPath = Path.Combine(workspace, "OUTPUT.md");
        await File.WriteAllTextAsync(inputPath, "# Task\n\nSay hello.");

        var runner = new AgentRunner(new FakeSandboxRunner());
        var result = await runner.RunAsync(
            new AgentRunRequest
            {
                WorkspacePath = workspace,
                InputPath = inputPath,
                OutputPath = outputPath,
                Mode = AgentMode.Local
            },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(File.Exists(outputPath));
        Assert.True(File.Exists(Path.Combine(workspace, "agent-debug.jsonl")));
        Assert.True(File.Exists(Path.Combine(workspace, "agent-transcript.md")));

        var output = await File.ReadAllTextAsync(outputPath);
        Assert.Contains("# Result", output);
        Assert.Contains("## Summary", output);
        Assert.Contains("## Next Steps", output);
    }

    [Fact]
    public async Task RunAsyncExecutesBuboActions()
    {
        var workspace = CreateWorkspace();
        var inputPath = Path.Combine(workspace, "INPUT.md");
        var outputPath = Path.Combine(workspace, "OUTPUT.md");
        await File.WriteAllTextAsync(
            inputPath,
            """
            # Task

            ```bubo-actions
            [
              {
                "tool": "write_file",
                "arguments": {
                  "path": "notes/result.txt",
                  "content": "Hello from Bubo\n"
                }
              },
              {
                "tool": "run_command",
                "arguments": {
                  "executable": "dotnet",
                  "arguments": ["--version"]
                }
              }
            ]
            ```
            """);

        var runner = new AgentRunner(new FakeSandboxRunner());
        var result = await runner.RunAsync(
            new AgentRunRequest
            {
                WorkspacePath = workspace,
                InputPath = inputPath,
                OutputPath = outputPath,
                Mode = AgentMode.Local
            },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("notes/result.txt", result.FilesChanged);
        Assert.Contains("dotnet --version", result.CommandsRun);
        Assert.Equal("Hello from Bubo\n", await File.ReadAllTextAsync(Path.Combine(workspace, "notes", "result.txt")));

        var output = await File.ReadAllTextAsync(outputPath);
        Assert.Contains("Bubo executed 2 action", output);
        Assert.Contains("dotnet --version", output);
    }

    [Fact]
    public async Task RunAsyncDeniesActionPathTraversal()
    {
        var workspace = CreateWorkspace();
        var inputPath = Path.Combine(workspace, "INPUT.md");
        var outputPath = Path.Combine(workspace, "OUTPUT.md");
        await File.WriteAllTextAsync(
            inputPath,
            """
            # Task

            ```bubo-actions
            [
              {
                "tool": "write_file",
                "arguments": {
                  "path": "../escape.txt",
                  "content": "nope"
                }
              }
            ]
            ```
            """);

        var runner = new AgentRunner();
        var result = await runner.RunAsync(
            new AgentRunRequest
            {
                WorkspacePath = workspace,
                InputPath = inputPath,
                OutputPath = outputPath,
                Mode = AgentMode.Local
            },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(File.Exists(Path.Combine(Directory.GetParent(workspace)!.FullName, "escape.txt")));

        var output = await File.ReadAllTextAsync(outputPath);
        Assert.Contains("Bubo stopped", output);
        Assert.Contains("write_file failed", output);
    }

    private static string CreateWorkspace()
    {
        var workspace = Path.Combine(
            Path.GetTempPath(),
            "bubo-runner-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        return Path.GetFullPath(workspace);
    }

    private sealed class FakeSandboxRunner : ISandboxRunner
    {
        public Task<ToolResult> RunCommandAsync(
            string command,
            IReadOnlyList<string> arguments,
            SandboxOptions options,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new ToolResult
            {
                Success = true,
                ExitCode = 0,
                Output = $"{command} {string.Join(" ", arguments)}"
            });
        }
    }
}
