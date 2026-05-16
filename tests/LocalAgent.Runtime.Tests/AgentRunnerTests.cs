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

        Assert.True(result.Success);
        Assert.True(File.Exists(outputPath));
        Assert.True(File.Exists(Path.Combine(workspace, "agent-debug.jsonl")));
        Assert.True(File.Exists(Path.Combine(workspace, "agent-transcript.md")));

        var output = await File.ReadAllTextAsync(outputPath);
        Assert.Contains("# Result", output);
        Assert.Contains("## Summary", output);
        Assert.Contains("## Next Steps", output);
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
}
