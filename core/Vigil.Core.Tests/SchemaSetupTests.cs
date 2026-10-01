using Vigil.Core.Data;
using Xunit;

namespace Vigil.Core.Tests;

/// <summary>Ported from the Rust core's timescale.rs tests. The database half runs in DataLayerTests.</summary>
public sealed class SchemaSetupTests
{
    [Fact]
    public void TheRefreshWindowStartsAtATimestampThatCannotCarrySql()
    {
        Assert.Equal(
            "call refresh_continuous_aggregate('sensor_1m', '2026-09-21T20:39:27.482Z'::timestamptz, null)",
            SchemaSetup.RefreshStatement("sensor_1m", 1_790_023_167_482));

        // Whatever the instant, the interpolated literal is only ever these characters, so there
        // is nothing in it a SQL parser could read as syntax.
        foreach (var millis in new long[] { 0, 1, 999, 1_790_023_167_482, 253_402_300_799_999 })
        {
            var literal = SchemaSetup.RefreshStatement("x", millis).Split('\'')[3];
            Assert.True(literal.All(c => char.IsAsciiDigit(c) || c is '-' or ':' or '.' or 'Z' or 'T'), $"{millis}: {literal}");
        }
    }

    [Fact]
    public void AggregateNamesFollowTheRawTable()
    {
        Assert.Equal("sensor_1m", SchemaSetup.AllSeries[0].NameOf("1m"));
        Assert.Equal("fan_1h", SchemaSetup.AllSeries[1].NameOf("1h"));
        Assert.Equal("ups_1m", SchemaSetup.AllSeries[2].NameOf("1m"));
    }

    [Fact]
    public void EachHourlyAggregateReadsItsOwnMinuteOneAndEachBackfillItsLegacyTable()
    {
        foreach (var series in SchemaSetup.AllSeries)
        {
            Assert.Contains($"from {series.NameOf("1m")}", series.Query("1h"), StringComparison.Ordinal);
            Assert.Contains($"from {series.Raw}", series.Query("1m"), StringComparison.Ordinal);
            Assert.Contains($"from {series.Legacy}", series.Query("backfill"), StringComparison.Ordinal);
        }
    }
}
