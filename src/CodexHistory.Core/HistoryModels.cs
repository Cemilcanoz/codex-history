namespace CodexHistory.Core;

public sealed record SessionSummary(
    string Id,
    string? Title,
    string? Project,
    string? Model,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long ReasoningOutputTokens,
    int ToolCallCount,
    decimal? EstimatedCost,
    bool CostIsPartial,
    int MalformedLineCount,
    string SourcePath)
{
    public bool UsageIsUncertain { get; init; }
}

public sealed record SessionDetail(
    SessionSummary Summary,
    IReadOnlyList<HistoryEvent> Timeline,
    IReadOnlyList<QuotaSnapshot> QuotaSnapshots);

public sealed record HistoryEvent(
    DateTimeOffset Timestamp,
    string Kind,
    string? Name,
    string? Model,
    long? InputTokens,
    long? CachedInputTokens,
    long? OutputTokens,
    long? ReasoningOutputTokens)
{
    public string? ResponseId { get; init; }
    public bool UsageIsUncertain { get; init; }
}

public sealed record QuotaSnapshot(
    DateTimeOffset Timestamp,
    string? LimitName,
    double? UsedPercent,
    DateTimeOffset? ResetsAt);

public sealed record RefreshResult(
    int FilesScanned,
    int FilesIndexed,
    int FilesSkipped,
    int FilesRemoved,
    int MalformedLines,
    int LockedFilesSkipped)
{
    public bool RefreshDeferred { get; init; }
}

public sealed record PriceImportResult(int ImportedRows, int RejectedRows);

public interface IHistoryCatalog
{
    Task<RefreshResult> RefreshAsync(string sourceDirectory, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(CancellationToken cancellationToken = default);
    Task<SessionDetail?> GetSessionAsync(string id, CancellationToken cancellationToken = default);
    Task<PriceImportResult> ImportPricesAsync(string path, CancellationToken cancellationToken = default);
}
