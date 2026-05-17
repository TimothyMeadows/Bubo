using Bubo.LocalAgent.Inference.CodexCli;

namespace Bubo.LocalAgent.Cli.Tests;

public sealed class CodexCliCommandBuilderTests
{
    [Fact]
    public void BuildExecArgumentsUsesNonInteractiveStdinInvocation()
    {
        var args = CodexCliCommandBuilder.BuildExecArguments(
            new CodexCliOptions
            {
                WorkingDirectory = "repo",
                Model = "gpt-test",
                Profile = "ci"
            },
            "last-message.md");

        Assert.Equal("exec", args[0]);
        Assert.Contains("--cd", args);
        Assert.Contains("repo", args);
        Assert.Contains("--output-last-message", args);
        Assert.Contains("last-message.md", args);
        Assert.Contains("--json", args);
        Assert.Contains("--ephemeral", args);
        Assert.Contains("--model", args);
        Assert.Contains("gpt-test", args);
        Assert.Equal("-", args[^1]);
    }

    [Fact]
    public void ComposePromptReturnsUserPromptWhenSystemPromptIsEmpty()
    {
        var prompt = CodexCliPromptBuilder.Compose(null, "user task");

        Assert.Equal("user task", prompt);
    }

    [Fact]
    public void ComposePromptWrapsOpenCawSystemPromptAndUserPrompt()
    {
        var prompt = CodexCliPromptBuilder.Compose("OpenCaw baseline", "user task");

        Assert.Contains("<system>", prompt);
        Assert.Contains("OpenCaw baseline", prompt);
        Assert.Contains("</system>", prompt);
        Assert.Contains("<user>", prompt);
        Assert.Contains("user task", prompt);
        Assert.Contains("</user>", prompt);
    }
}
