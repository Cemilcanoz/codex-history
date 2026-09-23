using CodexHistory.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CodexHistory.Tests;

public sealed class IndexRefreshTests
{
    private const string Model = "{\"timestamp\":\"2026-01-01T00:00:01Z\",\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-test\"}}";

    [Fact]
    public async Task Unchanged_parse_cache_survives_catalog_recreation()
    {
        using var w = new TestWorkspace();
        w.WriteLog("sessions/s1.jsonl", Meta("s1"), Model, Usage("r1", 7));
        await w.Catalog().RefreshAsync(w.Source);

        var result = await w.Catalog().RefreshAsync(w.Source);

        Assert.Equal(0, result.FilesIndexed);
        Assert.Equal(1, result.FilesSkipped);
        Assert.Equal(7, Assert.Single(await w.Catalog().ListSessionsAsync()).InputTokens);
    }

    [Fact]
    public async Task Same_size_rewrite_is_detected_by_content_hash()
    {
        using var w = new TestWorkspace();
        var path = w.WriteLog("sessions/s1.jsonl", Meta("s1"), Model, Usage("r1", 7));
        var catalog = w.Catalog();
        await catalog.RefreshAsync(w.Source);
        var beforeLength = new FileInfo(path).Length;
        File.WriteAllText(path, string.Join(Environment.NewLine, Meta("s1"), Model, Usage("r1", 9)) + Environment.NewLine);

        var result = await w.Catalog().RefreshAsync(w.Source);

        Assert.Equal(beforeLength, new FileInfo(path).Length);
        Assert.Equal(1, result.FilesIndexed);
        Assert.Equal(9, Assert.Single(await catalog.ListSessionsAsync()).InputTokens);
    }

    [Fact]
    public async Task Previously_indexed_locked_file_preserves_catalog_after_restart()
    {
        using var w = new TestWorkspace();
        var path = w.WriteLog("sessions/s1.jsonl", Meta("s1"), Model, Usage("r1", 7));
        await w.Catalog().RefreshAsync(w.Source);
        await using (var connection = new SqliteConnection($"Data Source={w.Database}"))
        {
            await connection.OpenAsync();
            var simulateLegacyCatalog = connection.CreateCommand();
            simulateLegacyCatalog.CommandText = "DELETE FROM catalog_metadata WHERE key='source_root'";
            await simulateLegacyCatalog.ExecuteNonQueryAsync();
        }
        await using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = await w.Catalog().RefreshAsync(w.Source);

        Assert.Equal(1, result.LockedFilesSkipped);
        Assert.True(result.RefreshDeferred);
        Assert.Equal(1, result.FilesScanned);
        Assert.Equal(7, Assert.Single(await w.Catalog().ListSessionsAsync()).InputTokens);
    }

    [Fact]
    public async Task Switching_source_does_not_reuse_same_relative_path()
    {
        using var w = new TestWorkspace();
        w.WriteLog("sessions/s1.jsonl", Meta("first"), Model, Usage("r1", 7));
        var catalog = w.Catalog();
        await catalog.RefreshAsync(w.Source);
        var second = Path.Combine(w.Root, "other-source");
        Directory.CreateDirectory(Path.Combine(second, "sessions"));
        File.WriteAllText(Path.Combine(second, "sessions", "s1.jsonl"), string.Join(Environment.NewLine, Meta("other"), Model, Usage("r2", 9)) + Environment.NewLine);

        var result = await catalog.RefreshAsync(second);
        var session = Assert.Single(await catalog.ListSessionsAsync());

        Assert.Equal(1, result.FilesIndexed);
        Assert.Equal("other", session.Id);
        Assert.Equal(9, session.InputTokens);
    }

    private static string Meta(string id) => $"{{\"timestamp\":\"2026-01-01T00:00:00Z\",\"type\":\"session_meta\",\"payload\":{{\"id\":\"{id}\",\"cwd\":\"C:\\\\work\"}}}}";
    private static string Usage(string responseId, long input) => $"{{\"timestamp\":\"2026-01-01T00:00:02Z\",\"type\":\"token_usage_record\",\"payload\":{{\"response_id\":\"{responseId}\",\"usage\":{{\"input_tokens\":{input},\"cached_input_tokens\":0,\"output_tokens\":1,\"reasoning_output_tokens\":0}}}}}}";
}
