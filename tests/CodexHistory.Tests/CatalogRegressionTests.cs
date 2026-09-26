using CodexHistory.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CodexHistory.Tests;

public sealed class CatalogRegressionTests
{
    private const string Meta = """{"timestamp":"2026-01-01T00:00:00Z","type":"session_meta","payload":{"id":"s1","cwd":"C:\\work"}}""";
    private const string Model = """{"timestamp":"2026-01-01T00:00:01Z","type":"turn_context","payload":{"model":"gpt-test"}}""";

    [Fact]
    public async Task Refresh_is_idempotent_and_does_not_modify_source_bytes()
    {
        using var w = new TestWorkspace();
        var log = w.WriteLog("sessions/2026/01/s1.jsonl", Meta, Model, Modern("r1", 100, 20, 30, 5));
        var before = TestWorkspace.Sha256(log);
        var catalog = w.Catalog();

        var first = await catalog.RefreshAsync(w.Source);
        var second = await catalog.RefreshAsync(w.Source);
        var session = Assert.Single(await catalog.ListSessionsAsync());

        Assert.Equal(before, TestWorkspace.Sha256(log));
        Assert.Equal((100L, 20L, 30L, 5L), (session.InputTokens, session.CachedInputTokens, session.OutputTokens, session.ReasoningOutputTokens));
        Assert.Equal(1, first.FilesIndexed);
        Assert.Equal(0, second.FilesIndexed);
        Assert.Equal(1, second.FilesSkipped);
    }

    [Fact]
    public async Task Modern_usage_and_matching_legacy_total_are_not_double_counted()
    {
        using var w = new TestWorkspace();
        w.WriteLog("sessions/s1.jsonl", Meta, Model,
            Modern("r1", 100, 20, 30, 5),
            Legacy(totalInput: 100, totalCached: 20, totalOutput: 30, totalReasoning: 5, lastInput: 100, lastCached: 20, lastOutput: 30, lastReasoning: 5));

        var catalog = w.Catalog();
        await catalog.RefreshAsync(w.Source);
        var session = Assert.Single(await catalog.ListSessionsAsync());

        Assert.Equal((100L, 20L, 30L, 5L), (session.InputTokens, session.CachedInputTokens, session.OutputTokens, session.ReasoningOutputTokens));
    }

    [Fact]
    public async Task Response_ids_are_deduplicated_across_copied_history_files()
    {
        using var w = new TestWorkspace();
        w.WriteLog("sessions/parent.jsonl", Meta.Replace("s1", "parent"), Model, Modern("shared-response", 80, 10, 12, 2));
        w.WriteLog("archived_sessions/copy.jsonl", Meta.Replace("s1", "copy"), Model, Modern("shared-response", 80, 10, 12, 2));

        var catalog = w.Catalog();
        await catalog.RefreshAsync(w.Source);
        var sessions = await catalog.ListSessionsAsync();

        Assert.Equal(2, sessions.Count);
        Assert.Equal(80, sessions.Sum(x => x.InputTokens));
        Assert.Equal(12, sessions.Sum(x => x.OutputTokens));
    }

    [Fact]
    public async Task Duplicate_active_and_archived_session_id_does_not_collide()
    {
        using var w = new TestWorkspace();
        w.WriteLog("sessions/live.jsonl", Meta, Model, Modern("r1", 10, 0, 2, 0));
        w.WriteLog("archived_sessions/archive.jsonl", Meta, Model, Modern("r1", 10, 0, 2, 0));

        var catalog = w.Catalog();
        var result = await catalog.RefreshAsync(w.Source);

        Assert.Equal(2, result.FilesScanned);
        Assert.Single(await catalog.ListSessionsAsync());
    }

    [Fact]
    public async Task Pricing_charges_uncached_input_cached_input_and_output_separately()
    {
        using var w = new TestWorkspace();
        w.WriteLog("sessions/s1.jsonl", Meta, Model, Modern("r1", 1_000_000, 250_000, 500_000, 0));
        var prices = w.WriteText("prices.csv", "model,effective_date,input_per_million,cached_per_million,output_per_million\ngpt-test,2025-01-01,2,0.5,8\n");
        var catalog = w.Catalog();

        await catalog.ImportPricesAsync(prices);
        await catalog.RefreshAsync(w.Source);
        var session = Assert.Single(await catalog.ListSessionsAsync());

        // (1,000,000 - 250,000) * 2/M + 250,000 * .5/M + 500,000 * 8/M = 5.625
        Assert.Equal(5.625m, session.EstimatedCost);
        Assert.False(session.CostIsPartial);
    }

    [Fact]
    public async Task Importing_prices_reprices_unchanged_files()
    {
        using var w = new TestWorkspace();
        w.WriteLog("sessions/s1.jsonl", Meta, Model, Modern("r1", 1_000_000, 0, 0, 0));
        var catalog = w.Catalog();
        await catalog.RefreshAsync(w.Source);
        Assert.Null(Assert.Single(await catalog.ListSessionsAsync()).EstimatedCost);
        var prices = w.WriteText("prices.csv", "model,effective_date,input_per_million,cached_per_million,output_per_million\ngpt-test,2025-01-01,3,1,9\n");

        await catalog.ImportPricesAsync(prices);
        var refresh = await catalog.RefreshAsync(w.Source);
        var session = Assert.Single(await catalog.ListSessionsAsync());

        Assert.Equal(0, refresh.FilesIndexed);
        Assert.Equal(3m, session.EstimatedCost);
    }

    [Fact]
    public async Task Default_price_catalog_uses_current_prices_for_older_supported_usage()
    {
        using var w = new TestWorkspace();
        var model = Model.Replace("gpt-test", "gpt-5.6-sol");
        var usage = Modern("r1", 1_000_000, 250_000, 500_000, 0);
        w.WriteLog("sessions/s1.jsonl", Meta, model, usage);
        var catalog = w.Catalog();

        await catalog.RefreshAsync(w.Source);
        var session = Assert.Single(await catalog.ListSessionsAsync());

        Assert.Equal(13.1m, session.EstimatedCost);
        Assert.False(session.CostIsPartial);
    }

    [Fact]
    public async Task Existing_database_with_only_old_catalog_seed_receives_historical_fallback()
    {
        using var w = new TestWorkspace();
        var model = Model.Replace("gpt-test", "gpt-5.6-sol");
        w.WriteLog("sessions/s1.jsonl", Meta, model, Modern("r1", 1_000_000, 0, 0, 0));
        var catalog = w.Catalog();
        await catalog.RefreshAsync(w.Source);

        await using (var connection = new SqliteConnection($"Data Source={w.Database}"))
        {
            await connection.OpenAsync();
            var simulateOldSeed = connection.CreateCommand();
            simulateOldSeed.CommandText = """
                DELETE FROM prices WHERE model='gpt-5.6-sol';
                INSERT INTO prices(model,effective_date,input_price,cached_price,output_price)
                VALUES('gpt-5.6-sol','2026-09-22T00:00:00.0000000+00:00','4','.4','20');
                UPDATE sessions SET estimated_cost=NULL,cost_partial=1;
                """;
            await simulateOldSeed.ExecuteNonQueryAsync();
        }

        var reopened = w.Catalog();
        var session = Assert.Single(await reopened.ListSessionsAsync());

        Assert.Equal(4m, session.EstimatedCost);
        Assert.False(session.CostIsPartial);
    }

    [Fact]
    public async Task Csv_price_at_same_model_and_date_overrides_default_without_being_reseeded()
    {
        using var w = new TestWorkspace();
        var model = Model.Replace("gpt-test", "gpt-5.6-sol");
        var usage = Modern("r1", 1_000_000, 0, 0, 0)
            .Replace("2026-01-01T00:00:02Z", "2026-09-23T00:00:02Z");
        w.WriteLog("sessions/s1.jsonl", Meta, model, usage);
        var prices = w.WriteText(
            "override.csv",
            "model,effective_date,input_per_million,cached_per_million,output_per_million\n" +
            "gpt-5.6-sol,2026-09-22,7,0.7,25\n");
        var catalog = w.Catalog();

        await catalog.RefreshAsync(w.Source);
        await catalog.ImportPricesAsync(prices);
        var session = Assert.Single(await catalog.ListSessionsAsync());

        Assert.Equal(7m, session.EstimatedCost);
        Assert.False(session.CostIsPartial);
    }

    [Fact]
    public async Task Legacy_token_count_is_exposed_in_timeline_without_double_counting()
    {
        using var w = new TestWorkspace();
        w.WriteLog("sessions/s1.jsonl", Meta, Model,
            Modern("r1", 100, 20, 30, 5),
            Legacy(100, 20, 30, 5, 100, 20, 30, 5));
        var catalog = w.Catalog();

        await catalog.RefreshAsync(w.Source);
        var detail = await catalog.GetSessionAsync("s1");
        Assert.NotNull(detail);
        var legacy = Assert.Single(detail.Timeline, item => item.Kind == "legacy usage snapshot");

        Assert.Equal((100L, 20L, 30L, 5L),
            (detail.Summary.InputTokens, detail.Summary.CachedInputTokens,
             detail.Summary.OutputTokens, detail.Summary.ReasoningOutputTokens));
        Assert.Equal(100, legacy.InputTokens);
        Assert.True(legacy.UsageIsUncertain);
    }

    [Fact]
    public async Task Real_rate_limit_shape_creates_primary_and_secondary_snapshots()
    {
        using var w = new TestWorkspace();
        var quota = System.Text.Json.JsonSerializer.Serialize(new
        {
            timestamp = "2026-09-23T00:00:03Z",
            type = "event_msg",
            payload = new
            {
                type = "token_count",
                info = new
                {
                    total_token_usage = new { input_tokens = 10, cached_input_tokens = 2, output_tokens = 3, reasoning_output_tokens = 1 },
                    last_token_usage = new { input_tokens = 10, cached_input_tokens = 2, output_tokens = 3, reasoning_output_tokens = 1 },
                    model_context_window = 200_000
                },
                rate_limits = new
                {
                    limit_id = "codex",
                    limit_name = (string?)null,
                    primary = new { used_percent = 41, window_minutes = 300, resets_at = 1_789_672_228 },
                    secondary = new { used_percent = 66, window_minutes = 10_080, resets_at = 1_789_993_965 }
                }
            }
        });
        w.WriteLog("sessions/s1.jsonl", Meta, Model, quota);
        var catalog = w.Catalog();

        await catalog.RefreshAsync(w.Source);
        var detail = await catalog.GetSessionAsync("s1");
        Assert.NotNull(detail);

        Assert.Collection(
            detail.QuotaSnapshots,
            primary =>
            {
                Assert.Contains("Birincil", primary.LimitName);
                Assert.Contains("5 saat", primary.LimitName);
                Assert.Equal(41, primary.UsedPercent);
            },
            secondary =>
            {
                Assert.Contains("İkincil", secondary.LimitName);
                Assert.Contains("7 gün", secondary.LimitName);
                Assert.Equal(66, secondary.UsedPercent);
            });
    }

    [Fact]
    public async Task Malformed_line_is_counted_without_dropping_later_valid_lines()
    {
        using var w = new TestWorkspace();
        w.WriteLog("sessions/s1.jsonl", Meta, "{ definitely not json", Model, Modern("r1", 7, 2, 3, 1));
        var catalog = w.Catalog();

        var result = await catalog.RefreshAsync(w.Source);
        var session = Assert.Single(await catalog.ListSessionsAsync());

        Assert.Equal(1, result.MalformedLines);
        Assert.Equal(1, session.MalformedLineCount);
        Assert.Equal(7, session.InputTokens);
    }

    [Fact]
    public async Task Incomplete_final_line_is_deferred_until_terminated()
    {
        using var w = new TestWorkspace();
        var line = Modern("r1", 7, 2, 3, 1);
        var path = w.WriteText("source/sessions/s1.jsonl", Meta + Environment.NewLine + Model + Environment.NewLine + line[..^5]);
        var catalog = w.Catalog();

        await catalog.RefreshAsync(w.Source);
        var first = Assert.Single(await catalog.ListSessionsAsync());
        Assert.Equal(0, first.MalformedLineCount);
        Assert.Equal(0, first.InputTokens);

        File.AppendAllText(path, line[^5..] + Environment.NewLine);
        await catalog.RefreshAsync(w.Source);
        Assert.Equal(7, Assert.Single(await catalog.ListSessionsAsync()).InputTokens);
    }

    [Fact]
    public async Task Actively_written_log_can_be_read_with_shared_access()
    {
        using var w = new TestWorkspace();
        var path = w.WriteLog("sessions/active.jsonl", Meta, Model, Modern("r1", 7, 2, 3, 1));
        await using var activeWriter = new FileStream(
            path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        var catalog = w.Catalog();

        var result = await catalog.RefreshAsync(w.Source);

        Assert.Equal(0, result.LockedFilesSkipped);
        Assert.Equal(7, Assert.Single(await catalog.ListSessionsAsync()).InputTokens);
    }

    [Fact]
    public async Task Exclusively_locked_log_is_skipped_without_aborting_other_files()
    {
        using var w = new TestWorkspace();
        var lockedPath = w.WriteLog("sessions/locked.jsonl", Meta, Model);
        w.WriteLog(
            "sessions/readable.jsonl",
            Meta.Replace("s1", "readable"),
            Model,
            Modern("r-readable", 11, 0, 2, 0));
        await using var exclusiveLock = new FileStream(
            lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var catalog = w.Catalog();

        var result = await catalog.RefreshAsync(w.Source);
        var session = Assert.Single(await catalog.ListSessionsAsync());

        Assert.Equal(2, result.FilesScanned);
        Assert.Equal(1, result.LockedFilesSkipped);
        Assert.Equal("readable", session.Id);
        Assert.Equal(11, session.InputTokens);
    }

    [Fact]
    public async Task Deleted_files_are_removed_from_catalog()
    {
        using var w = new TestWorkspace();
        var path = w.WriteLog("sessions/s1.jsonl", Meta, Model);
        var catalog = w.Catalog();
        await catalog.RefreshAsync(w.Source);
        File.Delete(path);

        var result = await catalog.RefreshAsync(w.Source);

        Assert.Equal(1, result.FilesRemoved);
        Assert.Empty(await catalog.ListSessionsAsync());
    }

    [Fact]
    public async Task Nested_direct_sessions_path_is_accepted_but_unrelated_jsonl_is_ignored()
    {
        using var w = new TestWorkspace();
        w.WriteLog("sessions/2026/09/18/deep.jsonl", Meta, Model);
        w.WriteLog("cache/ignored.jsonl", Meta.Replace("s1", "ignored"), Model);
        var catalog = w.Catalog();

        var result = await catalog.RefreshAsync(w.Source);

        Assert.Equal(1, result.FilesScanned);
        Assert.Equal("s1", Assert.Single(await catalog.ListSessionsAsync()).Id);
    }

    [Fact]
    public void Database_inside_source_is_rejected_before_creating_database_or_directories()
    {
        using var w = new TestWorkspace();
        var database = Path.Combine(w.Source, "new", "history.db");

        Assert.Throws<InvalidOperationException>(() => new SqliteHistoryCatalog(database));
        Assert.False(Directory.Exists(Path.GetDirectoryName(database)));
        Assert.False(File.Exists(database));
    }

    [Fact]
    public async Task Cancellation_preserves_previous_good_catalog_state()
    {
        using var w = new TestWorkspace();
        var path = w.WriteLog("sessions/s1.jsonl", Meta, Model, Modern("r1", 4, 0, 1, 0));
        var catalog = w.Catalog();
        await catalog.RefreshAsync(w.Source);
        File.AppendAllText(path, Modern("r2", 100, 0, 20, 0) + Environment.NewLine);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.RefreshAsync(w.Source, cancellation.Token));
        Assert.Equal(4, Assert.Single(await catalog.ListSessionsAsync()).InputTokens);
    }

    [Fact]
    public async Task Sensitive_content_and_auth_file_are_not_indexed()
    {
        using var w = new TestWorkspace();
        const string secret = "SUPER-SECRET-PROMPT-AND-TOOL-OUTPUT";
        w.WriteLog("sessions/s1.jsonl", Meta,
            System.Text.Json.JsonSerializer.Serialize(new { timestamp = "2026-01-01T00:00:01Z", type = "response_item", payload = new { type = "message", content = secret } }),
            System.Text.Json.JsonSerializer.Serialize(new { timestamp = "2026-01-01T00:00:02Z", type = "response_item", payload = new { type = "function_call", name = "shell", arguments = secret, output = secret } }));
        w.WriteText("source/auth.json", System.Text.Json.JsonSerializer.Serialize(new { token = secret }));

        var catalog = w.Catalog();
        var refresh = await catalog.RefreshAsync(w.Source);
        var session = Assert.Single(await catalog.ListSessionsAsync());

        Assert.Equal(1, refresh.FilesScanned);
        Assert.Equal(1, session.ToolCallCount);
        SqliteConnection.ClearAllPools();
        Assert.DoesNotContain(secret, System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(w.Database)));
    }

    [Fact]
    public async Task Legacy_cumulative_totals_are_reconciled_once_across_session_files()
    {
        using var w = new TestWorkspace();
        w.WriteLog("sessions/live.jsonl", Meta, Model, Legacy(120, 20, 30, 5, 20, 0, 5, 1));
        w.WriteLog("archived_sessions/earlier.jsonl", Meta, Model,
            Legacy(100, 20, 25, 4, 100, 20, 25, 4).Replace("00:00:03Z", "00:00:02Z"));
        var catalog = w.Catalog();

        await catalog.RefreshAsync(w.Source);
        var session = Assert.Single(await catalog.ListSessionsAsync());

        Assert.Equal((120L, 20L, 30L, 5L),
            (session.InputTokens, session.CachedInputTokens, session.OutputTokens, session.ReasoningOutputTokens));
    }

    [Fact]
    public async Task Exact_archive_copy_does_not_duplicate_tool_calls_or_legacy_usage()
    {
        using var w = new TestWorkspace();
        string[] lines = [Meta, Model, Legacy(100, 20, 25, 4, 100, 20, 25, 4),
            """{"timestamp":"2026-01-01T00:00:04Z","type":"response_item","payload":{"type":"function_call","name":"shell"}}"""];
        w.WriteLog("sessions/live.jsonl", lines);
        w.WriteLog("archived_sessions/copy.jsonl", lines);
        var catalog = w.Catalog();

        await catalog.RefreshAsync(w.Source);
        var session = Assert.Single(await catalog.ListSessionsAsync());

        Assert.Equal(100, session.InputTokens);
        Assert.Equal(1, session.ToolCallCount);
        Assert.Single((await catalog.GetSessionAsync("s1"))!.Timeline, x => x.Kind == "tool");
    }

    [Fact]
    public async Task Out_of_range_unix_reset_is_ignored_without_losing_valid_usage()
    {
        using var w = new TestWorkspace();
        w.WriteLog("sessions/s1.jsonl", Meta, Model, Modern("r1", 7, 2, 3, 1),
            """{"timestamp":"2026-01-01T00:00:03Z","type":"event_msg","payload":{"type":"token_count","rate_limits":{"used_percent":40,"resets_at":9223372036854775807}}}""");
        var catalog = w.Catalog();

        await catalog.RefreshAsync(w.Source);
        var detail = (await catalog.GetSessionAsync("s1"))!;

        Assert.Equal(7, detail.Summary.InputTokens);
        Assert.Null(Assert.Single(detail.QuotaSnapshots).ResetsAt);
    }

    [Fact]
    public async Task Database_filename_with_connection_string_characters_is_literal()
    {
        using var w = new TestWorkspace();
        w.WriteLog("sessions/s1.jsonl", Meta, Model, Modern("r1", 7, 2, 3, 1));
        var path = Path.Combine(w.Root, "catalog", "history;Mode=ReadOnly.db");
        var catalog = new SqliteHistoryCatalog(path);

        await catalog.RefreshAsync(w.Source);

        Assert.True(File.Exists(path));
        Assert.Equal(7, Assert.Single(await catalog.ListSessionsAsync()).InputTokens);
    }

    [Fact]
    public async Task Repeated_response_id_does_not_duplicate_usage_timeline()
    {
        using var w = new TestWorkspace();
        w.WriteLog("sessions/s1.jsonl", Meta, Model, Modern("r1", 7, 2, 3, 1), Modern("r1", 7, 2, 3, 1));
        var catalog = w.Catalog();

        await catalog.RefreshAsync(w.Source);
        var detail = (await catalog.GetSessionAsync("s1"))!;

        Assert.Equal(7, detail.Summary.InputTokens);
        Assert.Single(detail.Timeline, x => x.Kind == "usage");
    }

    [Fact]
    public async Task Linked_source_or_ancestor_is_rejected_before_database_creation()
    {
        using var w = new TestWorkspace();
        w.WriteLog("sessions/s1.jsonl", Meta, Model);
        var link = Path.Combine(w.Root, "linked-source");
        CreateTestLink(link, w.Source, directory: true);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => w.Catalog().RefreshAsync(link));
            await Assert.ThrowsAsync<InvalidOperationException>(() => w.Catalog().RefreshAsync(Path.Combine(link, "sessions")));
            Assert.False(File.Exists(w.Database));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task Linked_history_directories_and_auth_alias_files_are_not_scanned()
    {
        using var w = new TestWorkspace();
        w.WriteLog("sessions/real.jsonl", Meta, Model, Modern("r1", 7, 0, 1, 0));
        var external = w.WriteText("outside/auth.json", Meta.Replace("s1", "secret") + Environment.NewLine + Modern("secret-response", 900, 0, 1, 0) + Environment.NewLine);
        w.WriteText("outside/secret.jsonl", Meta.Replace("s1", "secret") + Environment.NewLine);
        var fileLink = Path.Combine(w.Source, "sessions", "auth-alias.jsonl");
        var directoryLink = Path.Combine(w.Source, "archived_sessions");
        CreateTestLink(fileLink, external, directory: false);
        try
        {
            CreateTestLink(directoryLink, Path.GetDirectoryName(external)!, directory: true);
            try
            {
                var nestedLink = Path.Combine(w.Source, "sessions", "external");
                CreateTestLink(nestedLink, Path.GetDirectoryName(external)!, directory: true);
                try
                {
                var result = await w.Catalog().RefreshAsync(w.Source);
                Assert.Equal(1, result.FilesScanned);
                Assert.Equal("s1", Assert.Single(await w.Catalog().ListSessionsAsync()).Id);
                }
                finally { Directory.Delete(nestedLink); }
            }
            finally { Directory.Delete(directoryLink); }
        }
        finally { File.Delete(fileLink); }
    }

    private static void CreateTestLink(string link, string target, bool directory)
    {
        try
        {
            if (directory) Directory.CreateSymbolicLink(link, target);
            else File.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException
            || exception is IOException && (exception.HResult & 0xFFFF) == 1314)
        {
            Assert.Skip("Symbolic link creation is unavailable on this machine: " + exception.Message);
        }
    }

    private static string Modern(string responseId, long input, long cached, long output, long reasoning) =>
        System.Text.Json.JsonSerializer.Serialize(new { timestamp = "2026-01-01T00:00:02Z", type = "token_usage_record", payload = new { response_id = responseId, usage = new { input_tokens = input, cached_input_tokens = cached, output_tokens = output, reasoning_output_tokens = reasoning } } });

    private static string Legacy(long totalInput, long totalCached, long totalOutput, long totalReasoning, long lastInput, long lastCached, long lastOutput, long lastReasoning) =>
        System.Text.Json.JsonSerializer.Serialize(new { timestamp = "2026-01-01T00:00:03Z", type = "event_msg", payload = new { type = "token_count", info = new { total_token_usage = new { input_tokens = totalInput, cached_input_tokens = totalCached, output_tokens = totalOutput, reasoning_output_tokens = totalReasoning }, last_token_usage = new { input_tokens = lastInput, cached_input_tokens = lastCached, output_tokens = lastOutput, reasoning_output_tokens = lastReasoning } } } });
}
