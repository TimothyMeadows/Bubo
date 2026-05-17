using Bubo.LocalAgent.Cli;

namespace Bubo.LocalAgent.Cli.Tests;

public sealed class SandboxCommandParserTests
{
    [Fact]
    public void ParseSandboxTestAcceptsWorkspaceOption()
    {
        var result = CommandLineParser.Parse(new[]
        {
            "sandbox",
            "test",
            "--workspace",
            "repo"
        });

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Options);
        Assert.Equal("sandbox-test", result.Options.Command);
        Assert.Equal("repo", result.Options.WorkspacePath);
    }

    [Fact]
    public void ParseSandboxTestAcceptsNvidiaGpuOption()
    {
        var result = CommandLineParser.Parse(new[]
        {
            "sandbox",
            "test",
            "--workspace",
            "repo",
            "--gpu",
            "nvidia"
        });

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Options);
        Assert.Equal("sandbox-test", result.Options.Command);
        Assert.Equal("repo", result.Options.WorkspacePath);
        Assert.Equal("nvidia", result.Options.SandboxGpu);
    }

    [Fact]
    public void ParseSandboxTestRejectsUnsupportedGpuOption()
    {
        var result = CommandLineParser.Parse(new[]
        {
            "sandbox",
            "test",
            "--gpu",
            "amd"
        });

        Assert.False(result.IsSuccess);
        Assert.Contains("Unsupported GPU mode", result.ErrorMessage);
    }
}
