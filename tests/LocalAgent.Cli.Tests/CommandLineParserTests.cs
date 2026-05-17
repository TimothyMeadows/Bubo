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
    public void ParseRunCommandAcceptsFolderOption()
    {
        var result = CommandLineParser.Parse(new[]
        {
            "run",
            "--folder",
            "repo"
        });

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Options);
        Assert.Equal("repo", result.Options.WorkspacePath);
        Assert.Equal(Path.Combine("repo", "INPUT.md"), result.Options.InputPath);
        Assert.Equal(Path.Combine("repo", ".ai", "artifacts", "OUTPUT.md"), result.Options.OutputPath);
    }

    [Fact]
    public void ParseRunCommandAcceptsExternalInputAndFolderOutputPath()
    {
        var result = CommandLineParser.Parse(new[]
        {
            "run",
            "--folder",
            "repo",
            "--input",
            "prompts/INPUT.md",
            "--output",
            "reports/OUTPUT.md"
        });

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Options);
        Assert.Equal("repo", result.Options.WorkspacePath);
        Assert.Equal("prompts/INPUT.md", result.Options.InputPath);
        Assert.Equal("reports/OUTPUT.md", result.Options.OutputPath);
    }

    [Fact]
    public void ParseRunCommandAcceptsFolderAndWorkspaceWhenTheyResolveToSamePath()
    {
        var folder = Path.Combine(Path.GetTempPath(), "bubo-cli-parser", Guid.NewGuid().ToString("N"));
        var result = CommandLineParser.Parse(new[]
        {
            "run",
            "--folder",
            folder,
            "--workspace",
            Path.Combine(folder, ".")
        });

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Options);
        Assert.Equal(folder, result.Options.WorkspacePath);
    }

    [Fact]
    public void ParseRunCommandRejectsFolderAndWorkspaceWhenTheyResolveDifferently()
    {
        var result = CommandLineParser.Parse(new[]
        {
            "run",
            "--folder",
            "repo-a",
            "--workspace",
            "repo-b"
        });

        Assert.False(result.IsSuccess);
        Assert.Contains("--folder and --workspace", result.ErrorMessage);
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
            "--opencaw-path",
            ".cursor",
            "--opencaw-ref",
            "release/test",
            "--opencaw-update",
            "false"
        });

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Options);
        Assert.Equal(".cursor", result.Options.OpenCawPath);
        Assert.Equal("release/test", result.Options.OpenCawRef);
        Assert.False(result.Options.OpenCawUpdateOnRun);
    }

    [Fact]
    public void ParseRunCommandRejectsOpenCawDisableOptions()
    {
        var removedOptions = new[]
        {
            string.Concat("--no-", "opencaw"),
            string.Concat("--no-", "opencaw", "-update"),
            string.Concat("--no-", "opencaw", "-bootstrap"),
            string.Concat("--", "opencaw"),
            string.Concat("--", "opencaw", "-bootstrap")
        };

        foreach (var option in removedOptions)
        {
            var args = option.StartsWith("--no-", StringComparison.Ordinal)
                ? new[] { "run", option }
                : new[] { "run", option, "false" };

            var result = CommandLineParser.Parse(args);

            Assert.False(result.IsSuccess);
            Assert.Contains(option, result.ErrorMessage);
        }
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
