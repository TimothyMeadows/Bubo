namespace Bubo.LocalAgent.Cli;

public sealed record ParseResult
{
    private ParseResult(CommandLineOptions? options, string? errorMessage, bool showHelp)
    {
        Options = options;
        ErrorMessage = errorMessage;
        ShowHelp = showHelp;
    }

    public CommandLineOptions? Options { get; }

    public string? ErrorMessage { get; }

    public bool ShowHelp { get; }

    public bool IsSuccess => Options is not null;

    public static ParseResult Success(CommandLineOptions options)
    {
        return new ParseResult(options, null, showHelp: false);
    }

    public static ParseResult Failure(string errorMessage)
    {
        return new ParseResult(null, errorMessage, showHelp: false);
    }

    public static ParseResult Help()
    {
        return new ParseResult(null, null, showHelp: true);
    }
}
