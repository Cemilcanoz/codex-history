namespace CodexHistory.Cli;

internal sealed record CliOptions(string Source, string DatabasePath)
{
    public static ParseResult Parse(string[] args)
    {
        if (args.Any(arg => arg is "--help" or "-h"))
        {
            return ParseResult.Help();
        }

        string? source = null;
        string? database = null;
        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            if (option is not ("--source" or "--db"))
            {
                return ParseResult.Fail($"Unknown argument: {option}");
            }

            if (++index >= args.Length
                || string.IsNullOrWhiteSpace(args[index])
                || args[index].StartsWith("--", StringComparison.Ordinal))
            {
                return ParseResult.Fail($"Missing value for {option}.");
            }

            if (option == "--source")
            {
                source = args[index];
            }
            else
            {
                database = args[index];
            }
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            return ParseResult.Fail("--source is required.");
        }

        database ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexHistory",
            "index.db");

        return ParseResult.Success(new CliOptions(
            Path.GetFullPath(source),
            Path.GetFullPath(database)));
    }
}

internal sealed record ParseResult(CliOptions? Options, bool ShowHelp, string? Error)
{
    public static ParseResult Success(CliOptions options) => new(options, false, null);
    public static ParseResult Help() => new(null, true, null);
    public static ParseResult Fail(string message) => new(null, false, message);
}
