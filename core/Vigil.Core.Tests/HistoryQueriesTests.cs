using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using Vigil.Core.Data;
using Vigil.Core.Protocol;
using Xunit;

namespace Vigil.Core.Tests;

[Collection(DatabaseTestGroup.Name)]
public sealed class HistoryQueriesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheResponseHasExactlyThePropertiesTheBrowserReads()
    {
        // HistoryResponse is not yet in core/contract.json, so its shape, a contract with the
        // browser all the same, is pinned here against what web/src/lib/types.ts declares.
        var empty = new Dictionary<string, IReadOnlyList<double[]>>(StringComparer.Ordinal);
        var response = new HistoryResponse
        {
            Range = RangeKey.OneDay,
            From = 1,
            To = 2,
            BucketSeconds = 300,
            Temperatures = empty,
            Duties = empty,
            Rpms = empty,
            Ups = empty,
        };

        var json = JsonNode.Parse(JsonSerializer.Serialize(response, VigilJson.Options))!.AsObject();

        Assert.Equal(
            ["bucketSeconds", "duties", "from", "range", "rpms", "temperatures", "to", "ups"],
            json.Select(property => property.Key).OrderBy(key => key, StringComparer.Ordinal));
        // The range is the string the dashboard's buttons use, not an ordinal.
        Assert.Equal("24h", json["range"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(RangeKey.OneHour, "1h", 10)]
    [InlineData(RangeKey.SixHours, "6h", 60)]
    [InlineData(RangeKey.OneDay, "24h", 300)]
    [InlineData(RangeKey.SevenDays, "7d", 1_800)]
    [InlineData(RangeKey.ThirtyDays, "30d", 7_200)]
    [InlineData(RangeKey.OneYear, "1y", 86_400)]
    public void EveryRangeKeepsItsWireNameAndBucketWidth(RangeKey range, string wire, int bucketSeconds)
    {
        // These are the values vigil-web has always used; changing one silently rescales a chart.
        Assert.Equal($"\"{wire}\"", JsonSerializer.Serialize(range, VigilJson.Options));
        Assert.Equal(bucketSeconds, HistoryQueries.BucketSecondsFor(range));
    }

    [Fact]
    public async Task ReadsRawSamplesAndChartsTheHottestOfEachGroup()
    {
        var token = TestContext.Current.CancellationToken;
        await using var databases = await DatabaseFixture.ConnectAsync(token);
        await DatabaseFixture.TruncateAsync(databases.Writer, token);

        // Two drives and two DIMMs, so the grouped series have something to choose between.
        await InsertSensorAsync(databases.Writer, "cpu", Now.AddMinutes(-5), 38.25, token);
        await InsertSensorAsync(databases.Writer, "drive:naa.1", Now.AddMinutes(-5), 35.5, token);
        await InsertSensorAsync(databases.Writer, "drive:naa.2", Now.AddMinutes(-5), 41.125, token);
        await InsertSensorAsync(databases.Writer, "dimm:0", Now.AddMinutes(-5), 30, token);
        await InsertSensorAsync(databases.Writer, "dimm:1", Now.AddMinutes(-5), 33, token);
        await InsertFanAsync(databases.Writer, "drive-cage", Now.AddMinutes(-5), 65, 1211, token);
        await InsertFanAsync(databases.Writer, "lsi-cooling", Now.AddMinutes(-5), 40, null, token);

        var history = await new HistoryQueries(databases).QueryAsync(RangeKey.OneHour, upsName: null, Now, token);

        Assert.Equal(RangeKey.OneHour, history.Range);
        Assert.Equal(10, history.BucketSeconds);
        Assert.Equal(Now.ToUnixTimeMilliseconds(), history.To);
        Assert.Equal(Now.AddHours(-1).ToUnixTimeMilliseconds(), history.From);

        // Rounded to two decimals, as the TypeScript did.
        Assert.Equal(38.25, Assert.Single(history.Temperatures["cpu"])[1]);

        // The hottest of each group, not an average of it.
        Assert.Equal(41.13, Assert.Single(history.Temperatures["drives:max"])[1]);
        Assert.Equal(33, Assert.Single(history.Temperatures["memory:max"])[1]);

        Assert.Equal(65, Assert.Single(history.Duties["drive-cage"])[1]);
        Assert.Equal(1211, Assert.Single(history.Rpms["drive-cage"])[1]);
        // A header whose tach is unreadable has a duty but must not appear as 0 rpm.
        Assert.True(history.Duties.ContainsKey("lsi-cooling"));
        Assert.False(history.Rpms.ContainsKey("lsi-cooling"));

        // No NUT configured: the UPS section is empty rather than absent.
        Assert.Empty(history.Ups);
    }

    [Fact]
    public async Task ExcludesAnythingOlderThanTheRange()
    {
        var token = TestContext.Current.CancellationToken;
        await using var databases = await DatabaseFixture.ConnectAsync(token);
        await DatabaseFixture.TruncateAsync(databases.Writer, token);

        await InsertSensorAsync(databases.Writer, "cpu", Now.AddMinutes(-30), 40, token);
        await InsertSensorAsync(databases.Writer, "cpu", Now.AddHours(-2), 99, token);

        var history = await new HistoryQueries(databases).QueryAsync(RangeKey.OneHour, upsName: null, Now, token);

        // The two-hour-old reading is outside a one-hour range.
        Assert.Equal(40, Assert.Single(history.Temperatures["cpu"])[1]);
    }

    [Fact]
    public async Task ReadsTheUpsWhenOneIsNamed()
    {
        var token = TestContext.Current.CancellationToken;
        await using var databases = await DatabaseFixture.ConnectAsync(token);
        await DatabaseFixture.TruncateAsync(databases.Writer, token);

        await using (var insert = databases.Writer.CreateCommand(
            "insert into ups_samples (ts, ups, status, charge, load, runtime_seconds, input_voltage)"
            + " values ($1, 'apc', 'OL', 87, 25, 2310, 231.4)"))
        {
            insert.Parameters.Add(new NpgsqlParameter { Value = Now.AddMinutes(-5), NpgsqlDbType = NpgsqlDbType.TimestampTz });
            await insert.ExecuteNonQueryAsync(token);
        }

        var history = await new HistoryQueries(databases).QueryAsync(RangeKey.OneHour, "apc", Now, token);

        Assert.Equal(87, Assert.Single(history.Ups["charge"])[1]);
        Assert.Equal(25, Assert.Single(history.Ups["load"])[1]);
        // Minutes, because that is what the chart's axis is in: 2310s is 38.5 min.
        Assert.Equal(38.5, Assert.Single(history.Ups["runtime"])[1]);
        Assert.Equal(231.4, Assert.Single(history.Ups["inputVoltage"])[1]);

        // A different UPS's history must not leak in.
        var other = await new HistoryQueries(databases).QueryAsync(RangeKey.OneHour, "not-this-one", Now, token);
        Assert.Empty(other.Ups);
    }

    [Fact]
    public async Task ReadsARollupWeightedByItsSampleCounts()
    {
        var token = TestContext.Current.CancellationToken;
        await using var databases = await DatabaseFixture.ConnectAsync(token);
        await RequireAggregatesAsync(databases.Writer, token);
        await DatabaseFixture.TruncateAsync(databases.Writer, token);

        // Two minutes, deliberately lopsided: one sample at 10°C and thirty at 40°C. A plain
        // average of the two minute-averages would be 25; weighted by the counts it is 39.03.
        // The 24h range reads sensor_1m, so this is what that arithmetic has to produce.
        var firstMinute = Now.AddHours(-3);
        await InsertSensorAsync(databases.Writer, "cpu", firstMinute, 10, token);
        for (var i = 0; i < 30; i++)
        {
            await InsertSensorAsync(databases.Writer, "cpu", firstMinute.AddMinutes(1).AddSeconds(i), 40, token);
        }

        await RefreshAggregateAsync(databases.Writer, "sensor_1m", token);

        var history = await new HistoryQueries(databases).QueryAsync(RangeKey.OneDay, upsName: null, Now, token);

        // One 300-second bucket covers both minutes.
        var point = Assert.Single(history.Temperatures["cpu"]);
        Assert.Equal(39.03, point[1]);
    }

    private static async Task InsertSensorAsync(
        NpgsqlDataSource source, string id, DateTimeOffset at, double celsius, CancellationToken token)
    {
        await using var command = source.CreateCommand("insert into sensor_samples (ts, sensor_id, celsius) values ($1, $2, $3)");
        command.Parameters.Add(new NpgsqlParameter { Value = at, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        command.Parameters.Add(new NpgsqlParameter { Value = id, NpgsqlDbType = NpgsqlDbType.Text });
        command.Parameters.Add(new NpgsqlParameter { Value = (float)celsius, NpgsqlDbType = NpgsqlDbType.Real });
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task InsertFanAsync(
        NpgsqlDataSource source, string id, DateTimeOffset at, short duty, int? rpm, CancellationToken token)
    {
        await using var command = source.CreateCommand("insert into fan_samples (ts, fan_id, duty_percent, rpm) values ($1, $2, $3, $4)");
        command.Parameters.Add(new NpgsqlParameter { Value = at, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        command.Parameters.Add(new NpgsqlParameter { Value = id, NpgsqlDbType = NpgsqlDbType.Text });
        command.Parameters.Add(new NpgsqlParameter { Value = duty, NpgsqlDbType = NpgsqlDbType.Smallint });
        command.Parameters.Add(new NpgsqlParameter { Value = rpm is null ? DBNull.Value : rpm.Value, NpgsqlDbType = NpgsqlDbType.Integer });
        await command.ExecuteNonQueryAsync(token);
    }

    /// <summary>
    /// The continuous aggregates are still the Rust core's to create (timescale.rs), so this
    /// skips rather than duplicating that setup.
    /// </summary>
    private static async Task RequireAggregatesAsync(NpgsqlDataSource source, CancellationToken token)
    {
        await using var command = source.CreateCommand(
            "select count(*) from timescaledb_information.continuous_aggregates where view_name = 'sensor_1m'");
        var found = Convert.ToInt64(await command.ExecuteScalarAsync(token), System.Globalization.CultureInfo.InvariantCulture);

        Assert.SkipWhen(found == 0, "run `cargo run -p vigil-core` against this database once to create the rollups");
    }

    private static async Task RefreshAggregateAsync(NpgsqlDataSource source, string name, CancellationToken token)
    {
        // CALL cannot run inside a transaction block, which is why this is its own statement,
        // and refresh_continuous_aggregate takes its view by name, so there is nothing bindable.
        // sql-literal-ok: `name` is a literal passed by this file, never anything from outside.
        await using var command = source.CreateCommand($"call refresh_continuous_aggregate('{name}', null, null)");
        await command.ExecuteNonQueryAsync(token);
    }
}
