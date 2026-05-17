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
            "cloud",
            "--config",
            "repo/bubo.config.json"
        });

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Options);
        Assert.Equal("repo", result.Options.WorkspacePath);
        Assert.Equal("repo/INPUT.md", result.Options.InputPath);
        Assert.Equal("repo/OUTPUT.md", result.Options.OutputPath);
        Assert.Equal(AgentMode.Cloud, result.Options.Mode);
        Assert.True(result.Options.ModeWasSpecified);
        Assert.Equal("repo/bubo.config.json", result.Options.ConfigPath);
    }

    [Fact]
    public void ParseRunCommandLeavesModeUnspecifiedWhenModeIsOmitted()
    {
        var result = CommandLineParser.Parse(new[]
        {
            "run",
            "--workspace",
            "repo"
        });

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Options);
        Assert.Equal(AgentMode.Local, result.Options.Mode);
        Assert.False(result.Options.ModeWasSpecified);
        Assert.Null(result.Options.ConfigPath);
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

    [Theory]
    [InlineData("doctor", null, "doctor")]
    [InlineData("models", "list", "models-list")]
    [InlineData("native", "test", "native-test")]
    public void ParseUtilityCommands(string command, string? subcommand, string expected)
    {
        var args = subcommand is null
            ? new[] { command }
            : new[] { command, subcommand };

        var result = CommandLineParser.Parse(args);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Options);
        Assert.Equal(expected, result.Options.Command);
    }
}
