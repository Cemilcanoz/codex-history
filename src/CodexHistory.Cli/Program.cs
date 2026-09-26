using System.Text.Json;
using System.Text.Json.Serialization;
using CodexHistory.Cli;
using CodexHistory.Infrastructure;

const string help = """
Codex History — local, read-only session metadata indexer

Usage:
  codex-history --source <session-directory> [--db <index.db>]

Options:
  --source <path>  Directory to scan. Required; never inferred from credentials.
  --db <path>      SQLite index path. Defaults to LocalApplicationData/CodexHistory/index.db.
  -h, --help       Show this help.

The command prints one metadata JSON document. It never reads auth.json and never stores
or prints conversation content.
""";

var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};

try
{
    var parsed = CliOptions.Parse(args);
    if (parsed.ShowHelp)
    {
        Console.WriteLine(help);
        return 0;
    }

    if (parsed.Error is not null)
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(new { error = parsed.Error }, jsonOptions));
        Console.Error.WriteLine("Use --help for usage.");
        return 2;
    }

    var options = parsed.Options!;
    if (!Directory.Exists(options.Source))
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(new { error = "Source directory does not exist." }, jsonOptions));
        return 2;
    }

    if (IsSameOrDescendant(options.DatabasePath, options.Source))
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(
            new { error = "Database path must be outside the source directory." },
            jsonOptions));
        return 2;
    }

    var catalog = new SqliteHistoryCatalog(options.DatabasePath);
    var databaseDirectory = Path.GetDirectoryName(options.DatabasePath);
    if (!string.IsNullOrEmpty(databaseDirectory))
    {
        Directory.CreateDirectory(databaseDirectory);
    }

    var refresh = await catalog.RefreshAsync(options.Source);
    var sessions = await catalog.ListSessionsAsync();
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        source = options.Source,
        database = options.DatabasePath,
        refresh,
        sessions
    }, jsonOptions));
    return 0;
}

catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { error = exception.Message }, jsonOptions));
    return 2;
}
catch (Exception exception)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { error = "Index operation failed.", detail = exception.Message }, jsonOptions));
    return 1;
}

static bool IsSameOrDescendant(string candidate, string directory)
{
    var candidatePath = Path.GetFullPath(candidate);
    var directoryPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
    return candidatePath.Equals(directoryPath, StringComparison.OrdinalIgnoreCase)
        || candidatePath.StartsWith(directoryPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
