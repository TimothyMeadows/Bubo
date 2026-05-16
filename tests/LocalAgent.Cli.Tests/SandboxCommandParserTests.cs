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
}
