using CodexHistory.Core;
using Xunit;

namespace CodexHistory.Tests;

public sealed class WaterFootprintEstimatorTests
{
    [Fact]
    public void Estimate_uses_input_and_output_only()
    {
        var summary = CreateSummary(input: 1_000, cachedInput: 900, output: 2_000, reasoningOutput: 1_800);

        var result = WaterFootprintEstimator.Estimate(summary, 2.5m);

        Assert.Equal(7.5m, result);
    }

    [Fact]
    public void Estimate_accepts_large_long_counts_without_integer_sum_overflow()
    {
        var result = WaterFootprintEstimator.Estimate(long.MaxValue, long.MaxValue, 1m);

        Assert.Equal((decimal)long.MaxValue * 2m / 1000m, result);
    }

    [Theory]
    [InlineData(-1L, 0L, 1.0)]
    [InlineData(0L, -1L, 1.0)]
    public void Estimate_rejects_negative_token_counts(long input, long output, double coefficient)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WaterFootprintEstimator.Estimate(input, output, (decimal)coefficient));
    }

    [Fact]
    public void Estimate_rejects_negative_coefficient()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WaterFootprintEstimator.Estimate(1, 2, -0.1m));
    }

    [Fact]
    public void Estimate_rejects_null_summary()
    {
        Assert.Throws<ArgumentNullException>(() =>
            WaterFootprintEstimator.Estimate(null!, 1m));
    }

    private static SessionSummary CreateSummary(
        long input,
        long cachedInput,
        long output,
        long reasoningOutput) => new(
            "id",
            "title",
            null,
            "model",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            input,
            cachedInput,
            output,
            reasoningOutput,
            0,
            null,
            false,
            0,
            "source");
}
