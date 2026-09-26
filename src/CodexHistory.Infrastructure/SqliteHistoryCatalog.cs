using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexHistory.Core;
using Microsoft.Data.Sqlite;

namespace CodexHistory.Infrastructure;

public sealed class SqliteHistoryCatalog : IHistoryCatalog
{
    private static readonly (string Model, string EffectiveDate, string Input, string Cached, string Output)[] DefaultPrices =
    [
        ("gpt-6-astra", "1970-01-01T00:00:00.0000000+00:00", "10", "1", "50"),
        ("gpt-5.6-sol", "1970-01-01T00:00:00.0000000+00:00", "4", ".4", "20"),
        ("gpt-5.6-terra", "1970-01-01T00:00:00.0000000+00:00", "2", ".2", "12"),
        ("gpt-5.6-luna", "1970-01-01T00:00:00.0000000+00:00", ".2", ".02", "1.2"),
        ("gpt-5.5", "1970-01-01T00:00:00.0000000+00:00", "5", ".5", "30"),
        ("gpt-5.3-codex", "1970-01-01T00:00:00.0000000+00:00", "1.75", ".175", "14"),
        ("gpt-5.2-codex", "1970-01-01T00:00:00.0000000+00:00", "1.75", ".175", "14")
    ];

    private readonly string databasePath;

    public SqliteHistoryCatalog(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = Path.GetFullPath(databasePath);
        RejectObviouslyUnsafeDatabaseLocation(this.databasePath);
    }

    public async Task<RefreshResult> RefreshAsync(string sourceDirectory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = Path.GetFullPath(sourceDirectory ?? throw new ArgumentNullException(nameof(sourceDirectory)));
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
        if (HasReparsePoint(source)) throw new InvalidOperationException("Source directory must not contain symbolic links or junctions in its path.");
        if (IsWithin(databasePath, source)) throw new InvalidOperationException("İndeks veritabanı kaynak günlük klasörünün dışında olmalıdır.");

        var files = EnumerateHistoryFiles(source).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = Open();
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        var sameSource = await IsCatalogForSourceAsync(connection, source, cancellationToken);
        var previous = sameSource ? await ReadFingerprintsAsync(connection, cancellationToken) : new(StringComparer.OrdinalIgnoreCase);
        var parsed = new List<ParsedFile>(files.Length);
        var lockedFilesSkipped = 0;
        var filesScanned = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            filesScanned++;
            try
            {
                var relativePath = Path.GetRelativePath(source, file).Replace('\\', '/');
                var bytes = await ReadFileAsync(file, cancellationToken);
                var fingerprint = Convert.ToHexString(SHA256.HashData(bytes));
                var cached = sameSource && previous.TryGetValue(relativePath, out var old) && old == fingerprint
                    ? await ReadCachedFileAsync(connection, relativePath, file, fingerprint, cancellationToken)
                    : null;
                parsed.Add(cached ?? ParseFile(bytes, file, relativePath, fingerprint, cancellationToken));
            }
            catch (IOException exception) when (IsFileLock(exception))
            {
                lockedFilesSkipped++;
                var relativePath = Path.GetRelativePath(source, file).Replace('\\', '/');
                if (sameSource && previous.ContainsKey(relativePath))
                {
                    await RepriceAsync(connection, cancellationToken);
                    return new(filesScanned, 0, 0, 0, 0, lockedFilesSkipped) { RefreshDeferred = true };
                }
            }
        }

        var currentPaths = parsed.Select(x => x.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var indexed = parsed.Count(x => !previous.TryGetValue(x.RelativePath, out var old) || old != x.Fingerprint);
        var removed = previous.Keys.Count(x => !currentPaths.Contains(x));

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await ExecuteAsync(connection, transaction, "DELETE FROM events; DELETE FROM quotas; DELETE FROM cost_items; DELETE FROM sessions; DELETE FROM source_files; DELETE FROM file_cache;", cancellationToken);
            var seenResponses = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in parsed.GroupBy(x => x.SessionId, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var session = Aggregate(group, seenResponses);
                await InsertSessionAsync(connection, transaction, session, cancellationToken);
            }
            foreach (var file in parsed)
            {
                await InsertFingerprintAsync(connection, transaction, file, cancellationToken);
                await InsertCachedFileAsync(connection, transaction, file, cancellationToken);
            }
            await WriteSourceAsync(connection, transaction, source, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }

        await RepriceAsync(connection, cancellationToken);
        return new(
            files.Length,
            indexed,
            parsed.Count - indexed,
            removed,
            parsed.Sum(x => x.Malformed),
            lockedFilesSkipped);
    }

    public async Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(databasePath)) return [];
        await using var connection = Open();
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await RepriceAsync(connection, cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,title,project,model,started_at,updated_at,input_tokens,cached_tokens,output_tokens,
                   reasoning_tokens,tool_count,estimated_cost,cost_partial,malformed_count,source_path,usage_uncertain
            FROM sessions ORDER BY updated_at DESC;
            """;
        var result = new List<SessionSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadSummary(reader));
        return result;
    }

    public async Task<SessionDetail?> GetSessionAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(databasePath)) return null;
        await using var connection = Open();
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await RepriceAsync(connection, cancellationToken);
        var summaries = connection.CreateCommand();
        summaries.CommandText = """SELECT id,title,project,model,started_at,updated_at,input_tokens,cached_tokens,output_tokens,reasoning_tokens,tool_count,estimated_cost,cost_partial,malformed_count,source_path,usage_uncertain FROM sessions WHERE id=$id""";
        summaries.Parameters.AddWithValue("$id", id);
        SessionSummary summary;
        await using (var reader = await summaries.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return null;
            summary = ReadSummary(reader);
        }
        var events = new List<HistoryEvent>();
        var eventCommand = connection.CreateCommand();
        eventCommand.CommandText = "SELECT timestamp,kind,name,model,input_tokens,cached_tokens,output_tokens,reasoning_tokens,response_id,usage_uncertain FROM events WHERE session_id=$id ORDER BY timestamp";
        eventCommand.Parameters.AddWithValue("$id", id);
        await using (var reader = await eventCommand.ExecuteReaderAsync(cancellationToken))
        while (await reader.ReadAsync(cancellationToken))
            events.Add(new(ParseDate(reader.GetString(0)), reader.GetString(1), NullableString(reader, 2), NullableString(reader, 3), NullableInt64(reader, 4), NullableInt64(reader, 5), NullableInt64(reader, 6), NullableInt64(reader, 7)) { ResponseId = NullableString(reader, 8), UsageIsUncertain = reader.GetInt64(9) != 0 });

        var quotas = new List<QuotaSnapshot>();
        var quotaCommand = connection.CreateCommand();
        quotaCommand.CommandText = "SELECT timestamp,limit_name,used_percent,resets_at FROM quotas WHERE session_id=$id ORDER BY timestamp";
        quotaCommand.Parameters.AddWithValue("$id", id);
        await using (var reader = await quotaCommand.ExecuteReaderAsync(cancellationToken))
        while (await reader.ReadAsync(cancellationToken))
            quotas.Add(new(ParseDate(reader.GetString(0)), NullableString(reader, 1), reader.IsDBNull(2) ? null : reader.GetDouble(2), reader.IsDBNull(3) ? null : ParseDate(reader.GetString(3))));
        return new(summary, events, quotas);
    }

    public async Task<PriceImportResult> ImportPricesAsync(string path, CancellationToken cancellationToken = default)
    {
        var rows = await File.ReadAllLinesAsync(path, cancellationToken);
        if (rows.Length == 0) return new(0, 0);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = Open();
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        var imported = 0; var rejected = 0;
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var line in rows.Skip(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cells = ParseCsvLine(line);
            if (cells.Count < 5 || string.IsNullOrWhiteSpace(cells[0]) || !DateTimeOffset.TryParse(cells[1], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) || !Decimal(cells[2], out var input) || !Decimal(cells[3], out var cached) || !Decimal(cells[4], out var output) || input < 0 || cached < 0 || output < 0)
            { rejected++; continue; }
            var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO prices(model,effective_date,input_price,cached_price,output_price) VALUES($m,$d,$i,$c,$o) ON CONFLICT(model,effective_date) DO UPDATE SET input_price=$i,cached_price=$c,output_price=$o";
            command.Parameters.AddWithValue("$m", cells[0]); command.Parameters.AddWithValue("$d", FormatDate(date)); command.Parameters.AddWithValue("$i", input.ToString(CultureInfo.InvariantCulture)); command.Parameters.AddWithValue("$c", cached.ToString(CultureInfo.InvariantCulture)); command.Parameters.AddWithValue("$o", output.ToString(CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken); imported++;
        }
        await transaction.CommitAsync(cancellationToken);
        await RepriceAsync(connection, cancellationToken);
        return new(imported, rejected);
    }

    private static async Task<byte[]> ReadFileAsync(string path, CancellationToken token)
    {
        if (HasReparsePoint(path)) throw new InvalidOperationException("History file must not contain symbolic links or junctions in its path.");
        await using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var content = new MemoryStream();
        await input.CopyToAsync(content, token);
        return content.ToArray();
    }

    private static ParsedFile ParseFile(byte[] bytes, string path, string relativePath, string fingerprint, CancellationToken token)
    {
        var text = Encoding.UTF8.GetString(bytes);
        var lines = text.Split('\n');
        var count = lines.Length - 1; // A live writer may not have finished the final line yet.
        var file = new ParsedFile(relativePath, path, fingerprint);
        string? currentModel = null;
        for (var i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            var line = lines[i].TrimEnd('\r'); if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement; var type = String(root, "type"); var payload = Object(root, "payload");
                var timestamp = Date(root, "timestamp") ?? File.GetLastWriteTimeUtc(path);
                file.Started ??= timestamp; file.Updated = timestamp > file.Updated ? timestamp : file.Updated;
                if (type == "session_meta")
                {
                    file.SessionId = String(payload, "id") ?? file.SessionId;
                    file.Title = String(payload, "title") ?? String(payload, "task_title") ?? file.Title;
                    file.Project = String(payload, "cwd") ?? String(payload, "project") ?? file.Project;
                }
                else if (type == "turn_context") { currentModel = String(payload, "model") ?? currentModel; file.Model = currentModel; }
                else if (type == "token_usage_record")
                {
                    var usage = Object(payload, "usage");
                    if (usage.ValueKind == JsonValueKind.Object)
                    {
                        var u = Usage(usage); var response = String(payload, "response_id");
                        file.Modern.Add(new(timestamp, currentModel, response, u));
                        file.Events.Add(new(timestamp, "usage", null, currentModel, u.Input, u.Cached, u.Output, u.Reasoning) { ResponseId = response });
                    }
                    ParseQuota(payload, timestamp, file.Quotas);
                }
                else if (type == "event_msg")
                {
                    var eventType = String(payload, "type");
                    if (eventType == "token_count")
                    {
                        var info = Object(payload, "info"); var total = Object(info, "total_token_usage");
                        if (total.ValueKind == JsonValueKind.Object)
                        {
                            var legacyUsage = Usage(total);
                            file.LegacyTotals.Add(new(timestamp, currentModel, legacyUsage));
                            file.Events.Add(new(
                                timestamp, "legacy usage snapshot", "token_count", currentModel,
                                legacyUsage.Input, legacyUsage.Cached, legacyUsage.Output, legacyUsage.Reasoning)
                            { UsageIsUncertain = true });
                        }
                        ParseQuota(info, timestamp, file.Quotas); ParseQuota(payload, timestamp, file.Quotas);
                    }
                }
                else if (type == "response_item")
                {
                    var itemType = String(payload, "type");
                    if (itemType is "function_call" or "custom_tool_call" or "web_search_call" or "local_shell_call" or "tool_search_call")
                    { file.ToolCount++; file.Events.Add(new(timestamp, "tool", String(payload, "name") ?? itemType, currentModel, null, null, null, null)); }
                }
            }
            catch (JsonException) { file.Malformed++; }
        }
        file.SessionId ??= Path.GetFileNameWithoutExtension(path);
        file.Started ??= File.GetCreationTimeUtc(path); if (file.Updated == default) file.Updated = File.GetLastWriteTimeUtc(path);
        return file;
    }

    private static AggregatedSession Aggregate(IEnumerable<ParsedFile> input, HashSet<string> seenResponses)
    {
        var files = input.DistinctBy(x => x.Fingerprint).OrderBy(x => x.RelativePath.StartsWith("archived_sessions/", StringComparison.OrdinalIgnoreCase) ? 1 : 0).ThenBy(x => x.RelativePath).ToArray();
        var result = new AggregatedSession(files[0].SessionId!, files[0].Path)
        {
            Title = files.Select(x => x.Title).LastOrDefault(x => x is not null), Project = files.Select(x => x.Project).LastOrDefault(x => x is not null), Model = files.Select(x => x.Model).LastOrDefault(x => x is not null),
            Started = files.Min(x => x.Started!.Value), Updated = files.Max(x => x.Updated), ToolCount = files.Sum(x => x.ToolCount), Malformed = files.Sum(x => x.Malformed)
        };
        foreach (var file in files)
        {
            var acceptedModern = new List<ModernUsage>();
            foreach (var modern in file.Modern)
            {
                if (modern.ResponseId is not null && !seenResponses.Add(modern.ResponseId)) continue;
                acceptedModern.Add(modern); result.Add(modern.Usage); result.CostItems.Add((modern.Timestamp, modern.Model, modern.Usage));
            }
            var acceptedIds = acceptedModern.Where(x => x.ResponseId is not null)
                .Select(x => x.ResponseId!).ToHashSet(StringComparer.Ordinal);
            result.Events.AddRange(file.Events.Where(e => e.ResponseId is null || acceptedIds.Remove(e.ResponseId)));
            result.Quotas.AddRange(file.Quotas);
        }
        // Legacy snapshots are cumulative for the session, including copies in archives.
        // Reconcile once against unique raw responses, including globally deduplicated copies.
        var legacy = files.SelectMany(x => x.LegacyTotals).OrderBy(x => x.Timestamp).LastOrDefault();
        if (legacy is not null)
        {
            var localResponses = new HashSet<string>(StringComparer.Ordinal);
            var rawModern = files.SelectMany(x => x.Modern)
                .Where(x => x.ResponseId is null || localResponses.Add(x.ResponseId))
                .Aggregate(UsageValue.Zero, (sum, x) => sum + x.Usage);
            var delta = UsageValue.PositiveDifference(legacy.Usage, rawModern);
            if (!delta.IsZero)
            {
                result.Add(delta);
                result.CostItems.Add((legacy.Timestamp, legacy.Model, delta));
                result.UsageUncertain = files.Any(x => x.Modern.Count > 0);
            }
        }
        return result;
    }

    private async Task RepriceAsync(SqliteConnection connection, CancellationToken token)
    {
        var sessions = await ListCostItemsAsync(connection, token);
        var prices = await ReadPricesAsync(connection, token);
        foreach (var (id, items) in sessions)
        {
            decimal total = 0; var known = 0; var unknown = 0;
            foreach (var item in items)
            {
                var price = prices.Where(x => string.Equals(x.Model, item.Model, StringComparison.OrdinalIgnoreCase) && x.Date <= item.Timestamp).OrderByDescending(x => x.Date).FirstOrDefault();
                if (price is null) { unknown++; continue; }
                var uncached = Math.Max(0, item.Usage.Input - item.Usage.Cached);
                total += (uncached * price.Input + item.Usage.Cached * price.Cached + item.Usage.Output * price.Output) / 1_000_000m; known++;
            }
            var command = connection.CreateCommand(); command.CommandText = "UPDATE sessions SET estimated_cost=$cost,cost_partial=$partial WHERE id=$id";
            command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$cost", known == 0 ? DBNull.Value : total); command.Parameters.AddWithValue("$partial", unknown > 0 ? 1 : 0); await command.ExecuteNonQueryAsync(token);
        }
    }

    private static async Task InsertSessionAsync(SqliteConnection c, SqliteTransaction t, AggregatedSession s, CancellationToken token)
    {
        var cmd = c.CreateCommand(); cmd.Transaction = t;
        cmd.CommandText = "INSERT INTO sessions VALUES($id,$title,$project,$model,$started,$updated,$input,$cached,$output,$reasoning,$tools,NULL,1,$malformed,$path,$uncertain)";
        Add(cmd,"$id",s.Id); Add(cmd,"$title",s.Title); Add(cmd,"$project",s.Project); Add(cmd,"$model",s.Model); Add(cmd,"$started",FormatDate(s.Started)); Add(cmd,"$updated",FormatDate(s.Updated)); Add(cmd,"$input",s.Usage.Input); Add(cmd,"$cached",s.Usage.Cached); Add(cmd,"$output",s.Usage.Output); Add(cmd,"$reasoning",s.Usage.Reasoning); Add(cmd,"$tools",s.ToolCount); Add(cmd,"$malformed",s.Malformed); Add(cmd,"$path",s.SourcePath); Add(cmd,"$uncertain",s.UsageUncertain ? 1:0); await cmd.ExecuteNonQueryAsync(token);
        foreach (var e in s.Events)
        { var ec=c.CreateCommand(); ec.Transaction=t; ec.CommandText="INSERT INTO events(session_id,timestamp,kind,name,model,input_tokens,cached_tokens,output_tokens,reasoning_tokens,response_id,usage_uncertain) VALUES($sid,$ts,$kind,$name,$model,$i,$c,$o,$r,$response,$uncertain)"; Add(ec,"$sid",s.Id);Add(ec,"$ts",FormatDate(e.Timestamp));Add(ec,"$kind",e.Kind);Add(ec,"$name",e.Name);Add(ec,"$model",e.Model);Add(ec,"$i",e.InputTokens);Add(ec,"$c",e.CachedInputTokens);Add(ec,"$o",e.OutputTokens);Add(ec,"$r",e.ReasoningOutputTokens);Add(ec,"$response",e.ResponseId);Add(ec,"$uncertain",e.UsageIsUncertain?1:0);await ec.ExecuteNonQueryAsync(token); }
        foreach (var q in s.Quotas)
        { var qc=c.CreateCommand();qc.Transaction=t;qc.CommandText="INSERT INTO quotas(session_id,timestamp,limit_name,used_percent,resets_at) VALUES($sid,$ts,$name,$used,$reset)";Add(qc,"$sid",s.Id);Add(qc,"$ts",FormatDate(q.Timestamp));Add(qc,"$name",q.LimitName);Add(qc,"$used",q.UsedPercent);Add(qc,"$reset",q.ResetsAt is null?null:FormatDate(q.ResetsAt.Value));await qc.ExecuteNonQueryAsync(token); }
        foreach (var item in s.CostItems)
        { var cc=c.CreateCommand();cc.Transaction=t;cc.CommandText="INSERT INTO cost_items(session_id,timestamp,model,input_tokens,cached_tokens,output_tokens) VALUES($sid,$ts,$model,$i,$c,$o)";Add(cc,"$sid",s.Id);Add(cc,"$ts",FormatDate(item.Timestamp));Add(cc,"$model",item.Model);Add(cc,"$i",item.Usage.Input);Add(cc,"$c",item.Usage.Cached);Add(cc,"$o",item.Usage.Output);await cc.ExecuteNonQueryAsync(token); }
    }

    private static async Task EnsureSchemaAsync(SqliteConnection c, CancellationToken token)
    {
        await ExecuteAsync(c,null,"""
            CREATE TABLE IF NOT EXISTS sessions(id TEXT PRIMARY KEY,title TEXT,project TEXT,model TEXT,started_at TEXT NOT NULL,updated_at TEXT NOT NULL,input_tokens INTEGER NOT NULL,cached_tokens INTEGER NOT NULL,output_tokens INTEGER NOT NULL,reasoning_tokens INTEGER NOT NULL,tool_count INTEGER NOT NULL,estimated_cost TEXT,cost_partial INTEGER NOT NULL,malformed_count INTEGER NOT NULL,source_path TEXT NOT NULL,usage_uncertain INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS events(id INTEGER PRIMARY KEY,session_id TEXT NOT NULL,timestamp TEXT NOT NULL,kind TEXT NOT NULL,name TEXT,model TEXT,input_tokens INTEGER,cached_tokens INTEGER,output_tokens INTEGER,reasoning_tokens INTEGER,response_id TEXT,usage_uncertain INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS quotas(id INTEGER PRIMARY KEY,session_id TEXT NOT NULL,timestamp TEXT NOT NULL,limit_name TEXT,used_percent REAL,resets_at TEXT);
            CREATE TABLE IF NOT EXISTS source_files(path TEXT PRIMARY KEY,fingerprint TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS file_cache(path TEXT PRIMARY KEY,fingerprint TEXT NOT NULL,payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS prices(model TEXT NOT NULL,effective_date TEXT NOT NULL,input_price TEXT NOT NULL,cached_price TEXT NOT NULL,output_price TEXT NOT NULL,PRIMARY KEY(model,effective_date));
            CREATE TABLE IF NOT EXISTS cost_items(id INTEGER PRIMARY KEY,session_id TEXT NOT NULL,timestamp TEXT NOT NULL,model TEXT,input_tokens INTEGER NOT NULL,cached_tokens INTEGER NOT NULL,output_tokens INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS catalog_metadata(key TEXT PRIMARY KEY,value TEXT NOT NULL);
            """,token);

        await using var transaction = (SqliteTransaction)await c.BeginTransactionAsync(token);
        foreach (var price in DefaultPrices)
        {
            var command = c.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO prices(model,effective_date,input_price,cached_price,output_price)
                VALUES($model,$date,$input,$cached,$output);
                """;
            Add(command, "$model", price.Model);
            Add(command, "$date", price.EffectiveDate);
            Add(command, "$input", price.Input);
            Add(command, "$cached", price.Cached);
            Add(command, "$output", price.Output);
            await command.ExecuteNonQueryAsync(token);
        }

        var metadata = c.CreateCommand();
        metadata.Transaction = transaction;
        metadata.CommandText = """
            INSERT INTO catalog_metadata(key,value) VALUES
            ('default_price_catalog_version','2026-09-22-current-price-fallback-v2'),
            ('default_price_catalog_source','OpenAI standard API list prices verified 2026-09-22; current-price historical comparison')
            ON CONFLICT(key) DO UPDATE SET value=excluded.value;
            """;
        await metadata.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
    }

    private static async Task ExecuteAsync(SqliteConnection c, SqliteTransaction? t, string sql, CancellationToken token){var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText=sql;await cmd.ExecuteNonQueryAsync(token);}
    private SqliteConnection Open() => new(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
    private static IEnumerable<string> EnumerateHistoryFiles(string source)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false
        };
        foreach (var name in new[] { "sessions", "archived_sessions" })
        {
            var directory = Path.Combine(source, name);
            if (!Directory.Exists(directory) || HasReparsePoint(directory)) continue;
            foreach (var file in Directory.EnumerateFiles(directory, "*.jsonl", options))
                if (!HasReparsePoint(file)) yield return file;
        }
    }

    private static bool HasReparsePoint(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        return false;
    }
    private static async Task<Dictionary<string,string>> ReadFingerprintsAsync(SqliteConnection c,CancellationToken token){var d=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);var cmd=c.CreateCommand();cmd.CommandText="SELECT path,fingerprint FROM source_files";await using var r=await cmd.ExecuteReaderAsync(token);while(await r.ReadAsync(token))d[r.GetString(0)]=r.GetString(1);return d;}
    private static async Task InsertFingerprintAsync(SqliteConnection c,SqliteTransaction t,ParsedFile f,CancellationToken token){var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText="INSERT INTO source_files VALUES($p,$f)";Add(cmd,"$p",f.RelativePath);Add(cmd,"$f",f.Fingerprint);await cmd.ExecuteNonQueryAsync(token);}
    private static async Task<string?> ReadSourceAsync(SqliteConnection c,CancellationToken token){var cmd=c.CreateCommand();cmd.CommandText="SELECT value FROM catalog_metadata WHERE key='source_root'";return await cmd.ExecuteScalarAsync(token) as string;}
    private static async Task<bool> IsCatalogForSourceAsync(SqliteConnection c,string source,CancellationToken token)
    {
        var recorded=await ReadSourceAsync(c,token);if(recorded is not null)return string.Equals(recorded,source,StringComparison.OrdinalIgnoreCase);
        var cmd=c.CreateCommand();cmd.CommandText="SELECT source_path FROM sessions";var found=false;await using var reader=await cmd.ExecuteReaderAsync(token);
        while(await reader.ReadAsync(token)){found=true;if(!IsWithin(Path.GetFullPath(reader.GetString(0)),source))return false;}return found;
    }
    private static async Task WriteSourceAsync(SqliteConnection c,SqliteTransaction t,string source,CancellationToken token){var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText="INSERT INTO catalog_metadata(key,value) VALUES('source_root',$source) ON CONFLICT(key) DO UPDATE SET value=$source";Add(cmd,"$source",source);await cmd.ExecuteNonQueryAsync(token);}
    private static async Task<ParsedFile?> ReadCachedFileAsync(SqliteConnection c,string relativePath,string path,string fingerprint,CancellationToken token)
    {
        var cmd=c.CreateCommand();cmd.CommandText="SELECT payload FROM file_cache WHERE path=$path AND fingerprint=$fingerprint";Add(cmd,"$path",relativePath);Add(cmd,"$fingerprint",fingerprint);
        var json=await cmd.ExecuteScalarAsync(token) as string;if(json is null)return null;
        CachePayload? cached;try{cached=JsonSerializer.Deserialize<CachePayload>(json);}catch(JsonException){return null;}if(cached is null)return null;
        var file=new ParsedFile(relativePath,path,fingerprint){SessionId=cached.SessionId,Title=cached.Title,Project=cached.Project,Model=cached.Model,Started=cached.Started,Updated=cached.Updated,ToolCount=cached.ToolCount,Malformed=cached.Malformed};
        file.Modern.AddRange(cached.Modern);file.LegacyTotals.AddRange(cached.LegacyTotals);file.Events.AddRange(cached.Events);file.Quotas.AddRange(cached.Quotas);return file;
    }
    private static async Task InsertCachedFileAsync(SqliteConnection c,SqliteTransaction t,ParsedFile f,CancellationToken token)
    {
        var payload=new CachePayload(f.SessionId,f.Title,f.Project,f.Model,f.Started,f.Updated,f.ToolCount,f.Malformed,f.Modern,f.LegacyTotals,f.Events,f.Quotas);
        var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText="INSERT INTO file_cache VALUES($path,$fingerprint,$payload)";Add(cmd,"$path",f.RelativePath);Add(cmd,"$fingerprint",f.Fingerprint);Add(cmd,"$payload",JsonSerializer.Serialize(payload));await cmd.ExecuteNonQueryAsync(token);
    }
    private static SessionSummary ReadSummary(SqliteDataReader r)=>new(r.GetString(0),NullableString(r,1),NullableString(r,2),NullableString(r,3),ParseDate(r.GetString(4)),ParseDate(r.GetString(5)),r.GetInt64(6),r.GetInt64(7),r.GetInt64(8),r.GetInt64(9),r.GetInt32(10),r.IsDBNull(11)?null:decimal.Parse(r.GetString(11),CultureInfo.InvariantCulture),r.GetInt64(12)!=0,r.GetInt32(13),r.GetString(14)){UsageIsUncertain=r.GetInt64(15)!=0};
    private static async Task<List<(string Id,List<(DateTimeOffset Timestamp,string? Model,UsageValue Usage)> Items)>> ListCostItemsAsync(SqliteConnection c,CancellationToken token){var map=new Dictionary<string,List<(DateTimeOffset,string?,UsageValue)>>();var cmd=c.CreateCommand();cmd.CommandText="SELECT session_id,timestamp,model,input_tokens,cached_tokens,output_tokens FROM cost_items";await using var r=await cmd.ExecuteReaderAsync(token);while(await r.ReadAsync(token)){var id=r.GetString(0);if(!map.TryGetValue(id,out var list))map[id]=list=[];list.Add((ParseDate(r.GetString(1)),NullableString(r,2),new(r.GetInt64(3),r.GetInt64(4),r.GetInt64(5),0)));}return map.Select(x=>(x.Key,x.Value)).ToList();}
    private static async Task<List<Price>> ReadPricesAsync(SqliteConnection c,CancellationToken token){var list=new List<Price>();var cmd=c.CreateCommand();cmd.CommandText="SELECT model,effective_date,input_price,cached_price,output_price FROM prices";await using var r=await cmd.ExecuteReaderAsync(token);while(await r.ReadAsync(token))list.Add(new(r.GetString(0),ParseDate(r.GetString(1)),decimal.Parse(r.GetString(2),CultureInfo.InvariantCulture),decimal.Parse(r.GetString(3),CultureInfo.InvariantCulture),decimal.Parse(r.GetString(4),CultureInfo.InvariantCulture)));return list;}
    private static void ParseQuota(JsonElement payload, DateTimeOffset timestamp, List<QuotaSnapshot> target)
    {
        foreach (var containerName in new[] { "rate_limits", "rate_limit", "quota", "limit" })
        {
            var quota = Object(payload, containerName);
            if (quota.ValueKind != JsonValueKind.Object) continue;

            var baseName = String(quota, "limit_name")
                ?? String(quota, "limit_id")
                ?? String(quota, "name")
                ?? containerName;
            var nestedFound = false;
            foreach (var (propertyName, displayName) in new[] { ("primary", "Birincil"), ("secondary", "İkincil"), ("individual_limit", "Bireysel") })
            {
                var window = Object(quota, propertyName);
                if (window.ValueKind != JsonValueKind.Object) continue;
                nestedFound = true;
                AddQuotaWindow(target, timestamp, baseName, displayName, window);
            }

            if (!nestedFound)
                AddQuotaWindow(target, timestamp, baseName, null, quota);
        }
    }

    private static void AddQuotaWindow(
        List<QuotaSnapshot> target,
        DateTimeOffset timestamp,
        string baseName,
        string? windowName,
        JsonElement window)
    {
        var used = Double(window, "used_percent") ?? Double(window, "usedPercent");
        var reset = Date(window, "resets_at") ?? Date(window, "reset_at");
        var minutes = Double(window, "window_minutes");
        if (used is null && reset is null && minutes is null) return;

        var label = windowName is null ? baseName : $"{baseName} · {windowName}";
        if (minutes is not null) label += $" ({FormatWindow(minutes.Value)})";
        target.Add(new(timestamp, label, used, reset));
    }

    private static string FormatWindow(double minutes)
    {
        if (minutes >= 1440 && minutes % 1440 == 0) return $"{minutes / 1440:N0} gün";
        if (minutes >= 60 && minutes % 60 == 0) return $"{minutes / 60:N0} saat";
        return $"{minutes:N0} dk";
    }
    private static UsageValue Usage(JsonElement e)=>new(Long(e,"input_tokens"),Long(e,"cached_input_tokens"),Long(e,"output_tokens"),Long(e,"reasoning_output_tokens"));
    private static JsonElement Object(JsonElement e,string name)=>e.ValueKind==JsonValueKind.Object&&e.TryGetProperty(name,out var v)?v:default;
    private static string? String(JsonElement e,string name)=>Object(e,name).ValueKind==JsonValueKind.String?Object(e,name).GetString():null;
    private static long Long(JsonElement e,string name){var v=Object(e,name);return v.ValueKind==JsonValueKind.Number&&v.TryGetInt64(out var n)?Math.Max(0,n):0;}
    private static double? Double(JsonElement e,string name){var v=Object(e,name);return v.ValueKind==JsonValueKind.Number&&v.TryGetDouble(out var n)?n:null;}
    private static DateTimeOffset? Date(JsonElement e,string name){var s=String(e,name);if(s is not null&&DateTimeOffset.TryParse(s,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var d))return d;var v=Object(e,name);if(v.ValueKind==JsonValueKind.Number&&v.TryGetInt64(out var unix)&&unix >= -62135596800L&&unix <= 253402300799L)return DateTimeOffset.FromUnixTimeSeconds(unix);return null;}
    private static bool Decimal(string s,out decimal d)=>decimal.TryParse(s,NumberStyles.Number,CultureInfo.InvariantCulture,out d);
    private static string FormatDate(DateTimeOffset d)=>d.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseDate(string s)=>DateTimeOffset.Parse(s,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind);
    private static string? NullableString(SqliteDataReader r,int i)=>r.IsDBNull(i)?null:r.GetString(i);
    private static long? NullableInt64(SqliteDataReader r,int i)=>r.IsDBNull(i)?null:r.GetInt64(i);
    private static void Add(SqliteCommand c,string name,object? value)=>c.Parameters.AddWithValue(name,value??DBNull.Value);
    private static bool IsWithin(string child,string parent){var rel=Path.GetRelativePath(parent,child);return rel!=".."&&!rel.StartsWith(".."+Path.DirectorySeparatorChar,StringComparison.Ordinal)&&!Path.IsPathRooted(rel);}
    private static bool IsFileLock(IOException exception) => (exception.HResult & 0xFFFF) is 32 or 33;
    private static void RejectObviouslyUnsafeDatabaseLocation(string path){for(var d=Directory.GetParent(path);d is not null;d=d.Parent){if(d.Name.Equals(".codex",StringComparison.OrdinalIgnoreCase)||d.Name.Equals("source",StringComparison.OrdinalIgnoreCase)||Directory.Exists(Path.Combine(d.FullName,"sessions"))||Directory.Exists(Path.Combine(d.FullName,"archived_sessions")))throw new InvalidOperationException("İndeks veritabanı kaynak günlük klasörünün dışında olmalıdır.");}}
    private static List<string> ParseCsvLine(string line){var result=new List<string>();var b=new StringBuilder();var quoted=false;for(var i=0;i<line.Length;i++){var ch=line[i];if(ch=='\"'){if(quoted&&i+1<line.Length&&line[i+1]=='\"'){b.Append('\"');i++;}else quoted=!quoted;}else if(ch==','&&!quoted){result.Add(b.ToString().Trim());b.Clear();}else b.Append(ch);}result.Add(b.ToString().Trim());return result;}

    private sealed class ParsedFile(string relativePath,string path,string fingerprint){public string RelativePath{get;}=relativePath;public string Path{get;}=path;public string Fingerprint{get;}=fingerprint;public string? SessionId;public string? Title;public string? Project;public string? Model;public DateTimeOffset? Started;public DateTimeOffset Updated;public int ToolCount;public int Malformed;public List<ModernUsage> Modern{get;}=[];public List<LegacyUsage> LegacyTotals{get;}=[];public List<HistoryEvent> Events{get;}=[];public List<QuotaSnapshot> Quotas{get;}=[];}
    private sealed record ModernUsage(DateTimeOffset Timestamp,string? Model,string? ResponseId,UsageValue Usage);
    private sealed record LegacyUsage(DateTimeOffset Timestamp,string? Model,UsageValue Usage);
    private sealed record CachePayload(string? SessionId,string? Title,string? Project,string? Model,DateTimeOffset? Started,DateTimeOffset Updated,int ToolCount,int Malformed,List<ModernUsage> Modern,List<LegacyUsage> LegacyTotals,List<HistoryEvent> Events,List<QuotaSnapshot> Quotas);
    private readonly record struct UsageValue(long Input,long Cached,long Output,long Reasoning){public static UsageValue Zero=>new(0,0,0,0);public bool IsZero=>Input==0&&Cached==0&&Output==0&&Reasoning==0;public static UsageValue operator +(UsageValue a,UsageValue b)=>new(a.Input+b.Input,a.Cached+b.Cached,a.Output+b.Output,a.Reasoning+b.Reasoning);public static UsageValue PositiveDifference(UsageValue a,UsageValue b)=>new(Math.Max(0,a.Input-b.Input),Math.Max(0,a.Cached-b.Cached),Math.Max(0,a.Output-b.Output),Math.Max(0,a.Reasoning-b.Reasoning));}
    private sealed class AggregatedSession(string id,string sourcePath){public string Id{get;}=id;public string SourcePath{get;}=sourcePath;public string? Title;public string? Project;public string? Model;public DateTimeOffset Started;public DateTimeOffset Updated;public int ToolCount;public int Malformed;public bool UsageUncertain;public UsageValue Usage;public List<HistoryEvent> Events{get;}=[];public List<QuotaSnapshot> Quotas{get;}=[];public List<(DateTimeOffset Timestamp,string? Model,UsageValue Usage)> CostItems{get;}=[];public void Add(UsageValue u)=>Usage+=u;}
    private sealed record Price(string Model,DateTimeOffset Date,decimal Input,decimal Cached,decimal Output);
}
