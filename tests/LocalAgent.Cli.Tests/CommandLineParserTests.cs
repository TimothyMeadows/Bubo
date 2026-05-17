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
    public void ParseRunCommandAcceptsOpenCawOptions()
    {
        var result = CommandLineParser.Parse(new[]
        {
            "run",
            "--workspace",
            "repo",
            "--opencaw",
            "disabled",
            "--opencaw-path",
            ".cursor",
            "--opencaw-ref",
            "release/test",
            "--opencaw-update",
            "false",
            "--opencaw-bootstrap",
            "false"
        });

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Options);
        Assert.False(result.Options.OpenCawEnabled);
        Assert.Equal(".cursor", result.Options.OpenCawPath);
        Assert.Equal("release/test", result.Options.OpenCawRef);
        Assert.False(result.Options.OpenCawUpdateOnRun);
        Assert.False(result.Options.OpenCawExecuteBootstrap);
    }

    [Fact]
    public void ParseRunCommandAcceptsOpenCawDisableShortFlags()
    {
        var result = CommandLineParser.Parse(new[]
        {
            "run",
            "--no-opencaw",
            "--no-opencaw-update",
            "--no-opencaw-bootstrap"
        });

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Options);
        Assert.False(result.Options.OpenCawEnabled);
        Assert.False(result.Options.OpenCawUpdateOnRun);
        Assert.False(result.Options.OpenCawExecuteBootstrap);
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

    [Fact]
    public void ParseNativeTestAcceptsBaseDirectoryOption()
    {
        var result = CommandLineParser.Parse(new[]
        {
            "native",
            "test",
            "--base-directory",
            "src/LlamaCppSharp.Native",
            "--backend",
            "cuda",
            "--strict"
        });

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Options);
        Assert.Equal("native-test", result.Options.Command);
        Assert.Equal("src/LlamaCppSharp.Native", result.Options.NativeBaseDirectory);
        Assert.Equal("cuda", result.Options.NativeBackend);
        Assert.True(result.Options.NativeStrict);
    }

    [Fact]
    public void ParseNativeTestRejectsUnknownOption()
    {
        var result = CommandLineParser.Parse(new[]
        {
            "native",
            "test",
            "--rid",
            "linux-x64"
        });

        Assert.False(result.IsSuccess);
        Assert.Contains("Unknown option", result.ErrorMessage);
    }

    [Fact]
    public void ParseNativeTestAcceptsStrictWithoutBaseDirectory()
    {
        var result = CommandLineParser.Parse(new[]
        {
            "native",
            "test",
            "--strict"
        });

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Options);
        Assert.Equal("native-test", result.Options.Command);
        Assert.True(result.Options.NativeStrict);
        Assert.Null(result.Options.NativeBaseDirectory);
        Assert.Equal("cpu", result.Options.NativeBackend);
    }

    [Fact]
    public void ParseNativeTestRejectsUnsupportedBackend()
    {
        var result = CommandLineParser.Parse(new[]
        {
            "native",
            "test",
            "--backend",
            "quantum"
        });

        Assert.False(result.IsSuccess);
        Assert.Contains("Unsupported native backend", result.ErrorMessage);
    }
}
