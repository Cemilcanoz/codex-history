namespace CodexHistory.Core;

/// <summary>
/// Calculates an explicitly configured, illustrative water scenario from token counts.
/// The coefficient is a user supplied assumption and is not a Codex measurement.
/// </summary>
public static class WaterFootprintEstimator
{
    /// <summary>
    /// Illustrative UI default. This value is an assumption, not a scientifically
    /// determined coefficient or a measurement of Codex water use.
    /// </summary>
    public const decimal IllustrativeDefaultMillilitersPerThousandTokens = 1.0m;

    /// <summary>
    /// Estimates milliliters from a session's billable input and output token counts.
    /// Cached input and reasoning output are not added because they are subsets of
    /// the input and output counts, respectively.
    /// </summary>
    public static decimal Estimate(
        SessionSummary summary,
        decimal millilitersPerThousandTokens)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return Estimate(summary.InputTokens, summary.OutputTokens, millilitersPerThousandTokens);
    }

    /// <summary>
    /// Estimates milliliters from input and output token counts using a caller supplied
    /// illustrative coefficient.
    /// </summary>
    public static decimal Estimate(
        long inputTokens,
        long outputTokens,
        decimal millilitersPerThousandTokens)
    {
        if (inputTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inputTokens), inputTokens, "Token counts cannot be negative.");
        }

        if (outputTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(outputTokens), outputTokens, "Token counts cannot be negative.");
        }

        if (millilitersPerThousandTokens < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(millilitersPerThousandTokens), millilitersPerThousandTokens, "The coefficient cannot be negative.");
        }

        // Convert before adding so long.MaxValue + long.MaxValue cannot wrap.
        decimal totalTokens = checked((decimal)inputTokens + (decimal)outputTokens);
        return checked(totalTokens / 1000m * millilitersPerThousandTokens);
    }
}
