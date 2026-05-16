using Bubo.LocalAgent.Abstractions;
using Bubo.LocalAgent.Cli;

namespace Bubo.LocalAgent.Cli.Tests;

public sealed class CommandLineParserTests
{
    [Fact]
    public void ParseRunCommandAcceptsExplicitOptions()
    {
        var result = CommandLineParser.Parse(new[]
        {
            "run",
            "--workspace",
            "repo",
            "--input",
            "repo/INPUT.md",
            "--output",
            "repo/OUTPUT.md",
            "--mode",
            "cloud"
        });

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Options);
        Assert.Equal("repo", result.Options.WorkspacePath);
        Assert.Equal("repo/INPUT.md", result.Options.InputPath);
        Assert.Equal("repo/OUTPUT.md", result.Options.OutputPath);
        Assert.Equal(AgentMode.Cloud, result.Options.Mode);
    }

    [Fact]
    public void ParseRunCommandRejectsUnknownMode()
    {
        var result = CommandLineParser.Parse(new[]
        {
            "run",
            "--mode",
            "remote"
        });

        Assert.False(result.IsSuccess);
        Assert.Contains("Unsupported mode", result.ErrorMessage);
    }
}
