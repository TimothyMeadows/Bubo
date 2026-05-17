using Bubo.LocalAgent.Abstractions;

namespace Bubo.LocalAgent.Inference.CodexCli;

public sealed class CodexCliInferenceProvider : IInferenceProvider
{
    private readonly CodexCliOptions _options;

    public CodexCliInferenceProvider()
        : this(new CodexCliOptions())
    {
    }

    public CodexCliInferenceProvider(CodexCliOptions options)
    {
        _options = options;
    }

    public string Name => "codex-cli";

    public async Task<InferenceResponse> GenerateAsync(
        InferenceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var outputFile = Path.Combine(
            Path.GetTempPath(),
            $"bubo-codex-{Guid.NewGuid():N}.md");
        var arguments = CodexCliCommandBuilder.BuildExecArguments(_options, outputFile);

        var result = await CodexCliProcess.RunAsync(
            _options.Executable,
            arguments,
            request.Prompt,
            _options.WorkingDirectory,
            cancellationToken);

        var finalText = File.Exists(outputFile)
            ? await File.ReadAllTextAsync(outputFile, cancellationToken)
            : result.Output;

        return new InferenceResponse
        {
            Success = result.Success,
            Text = finalText,
            Events = new[]
            {
                new TranscriptEvent
                {
                    Type = result.Success ? "codex-cli.completed" : "codex-cli.failed",
                    Message = result.Success
                        ? "codex-cli completed successfully."
                        : result.Error
                }
            }
        };
    }
}
