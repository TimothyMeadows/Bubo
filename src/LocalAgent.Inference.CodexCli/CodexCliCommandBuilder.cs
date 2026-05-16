namespace Bubo.LocalAgent.Inference.CodexCli;

public static class CodexCliCommandBuilder
{
    public static IReadOnlyList<string> BuildExecArguments(
        CodexCliOptions options,
        string outputLastMessagePath)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputLastMessagePath);

        var arguments = new List<string>
        {
            "exec",
            "--cd",
            options.WorkingDirectory,
            "--output-last-message",
            outputLastMessagePath,
            "--sandbox",
            "read-only",
            "--ask-for-approval",
            "never"
        };

        if (options.JsonEvents)
        {
            arguments.Add("--json");
        }

        if (options.Ephemeral)
        {
            arguments.Add("--ephemeral");
        }

        if (!string.IsNullOrWhiteSpace(options.Model))
        {
            arguments.Add("--model");
            arguments.Add(options.Model);
        }

        if (!string.IsNullOrWhiteSpace(options.Profile))
        {
            arguments.Add("--profile");
            arguments.Add(options.Profile);
        }

        arguments.Add("-");
        return arguments;
    }
}
