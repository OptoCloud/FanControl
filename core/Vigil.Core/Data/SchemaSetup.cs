using System.Data.Common;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Vigil.Core.Protocol;

namespace Vigil.Core.Data;

/// <summary>
/// vigil's schema, applied on every start: the base tables, then history's shape over time
/// handed to TimescaleDB. The raw sample tables are hypertables, continuous aggregates keep
/// 1-minute and 1-hour rollups, and background jobs compress and drop old data. vigil-core only
/// inserts raw samples; everything after that happens in the database on its own schedule.
/// Ported from the Rust core's timescale.rs.
/// </summary>
/// <remarks>
/// <code>
/// | Level  | Built by                                    | Kept                                              |
/// |--------|---------------------------------------------|---------------------------------------------------|
/// | raw    | vigil-core, every PERSIST_INTERVAL_SECONDS  | RAW_RETENTION_DAYS (30), compressed after 7 days  |
/// | 1 min  | *_1m, a continuous aggregate of raw         | 365 days                                          |
/// | 1 hour | *_1h, a continuous aggregate of *_1m        | forever                                           |
/// </code>
/// Every step is idempotent, which is why it runs on every start rather than as numbered
/// migrations. The extension itself is created by CT 300's recipe: it needs a superuser, and
/// vigil-core's role is not one.
///
/// The one-time work (converting tables, copying old history, filling the aggregates over all
/// time) can take far longer than the 15-second statement timeout, so it runs without one. A
/// finished fill is recorded in vigil_state; until that row exists every start fills again, so
/// an interrupted fill is resumed, never silently skipped.
/// </remarks>
public sealed class SchemaSetup(VigilDataSources databases, ILogger<SchemaSetup> logger)
{
    /// <summary>Minute rollups are kept this long; hourly ones forever.</summary>
    private const string MinuteRetention = "365 days";

    /// <summary>Raw chunks older than this are compressed (only the 1h and 6h charts read them).</summary>
    private const string CompressAfter = "7 days";

    /// <summary>One raw table and everything built over it. The SQL is in core/sql/aggregates/.</summary>
    /// <param name="Legacy">The pre-TimescaleDB minute table, copied into raw once if it exists.</param>
    internal sealed record Series(string Raw, string Key, string Legacy)
    {
        public string Prefix => Raw[..^"_samples".Length];

        public string NameOf(string level) => $"{Prefix}_{level}";

        public string Query(string part) => SqlText.Load($"aggregates.{Prefix}_{part}");
    }

    /// <summary>The closed set every interpolated identifier in this file comes from.</summary>
    internal static readonly Series[] AllSeries =
    [
        new("sensor_samples", "sensor_id", "sensor_minutes"),
        new("fan_samples", "fan_id", "fan_minutes"),
        new("ups_samples", "ups", "ups_minutes"),
    ];

    /// <exception cref="DatabaseSetupException">The database is reachable but cannot hold vigil's schema.</exception>
    public async Task ApplyAsync(TimeSpan rawRetention, CancellationToken cancellationToken)
    {
        await using var connection = await databases.Writer.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var database = new Session(connection, cancellationToken);

        if (!await database.ExistsAsync("select 1 from pg_extension where extname = $1", "timescaledb").ConfigureAwait(false))
        {
            throw new DatabaseSetupException(
                "the timescaledb extension is not installed in this database. CT 300's recipe creates it "
                + "(create extension timescaledb, as the superuser)");
        }

        await database.ExecuteAsync(SqlText.Load("schema")).ConfigureAwait(false);
        await database.ExecuteAsync(
            "create table if not exists vigil_state (key text primary key, value text not null, updated timestamptz not null default now())")
            .ConfigureAwait(false);

        var freshAggregates = false;
        foreach (var series in AllSeries)
        {
            freshAggregates |= await CreateSeriesAsync(database, series).ConfigureAwait(false);
        }

        if (freshAggregates || !await database.ExistsAsync("select 1 from vigil_state where key = $1", "aggregates_filled").ConfigureAwait(false))
        {
            await FillAggregatesAsync(database).ConfigureAwait(false);
        }

        var rawDays = Math.Max(1, (long)rawRetention.TotalDays);
        foreach (var series in AllSeries)
        {
            await SchedulePoliciesAsync(database, series, rawDays).ConfigureAwait(false);
        }
    }

    /// <summary>The hypertable and both aggregates. True if anything new needs filling.</summary>
    private async Task<bool> CreateSeriesAsync(Session database, Series series)
    {
        // sql-literal-ok: series.Raw comes from AllSeries, a compile-time closed set, and
        // create_hypertable takes its table by name.
        await database.ExecuteAsync(
            $"select create_hypertable('{series.Raw}', by_range('ts', interval '1 day'), if_not_exists => true, migrate_data => true)")
            .ConfigureAwait(false);

        var fresh = false;
        foreach (var level in new[] { "1m", "1h" })
        {
            var name = series.NameOf(level);
            if (!await database.ExistsAsync("select 1 from timescaledb_information.continuous_aggregates where view_name = $1", name)
                    .ConfigureAwait(false))
            {
                // sql-literal-ok: the view name derives from AllSeries, and the query is an
                // embedded file of ours; a view definition cannot be a parameter.
                await database.ExecuteAsync(
                    $"create materialized view {name} with (timescaledb.continuous) as {series.Query(level)} with no data")
                    .ConfigureAwait(false);
                fresh = true;
            }
        }

        // Once: the old minute table becomes raw history, then is renamed out of the way (kept, not dropped).
        if (await database.ExistsAsync("select 1 where to_regclass($1) is not null", series.Legacy).ConfigureAwait(false))
        {
            var copied = await database.ExecuteAsync(series.Query("backfill")).ConfigureAwait(false);

            // sql-literal-ok: series.Legacy comes from AllSeries.
            await database.ExecuteAsync($"alter table {series.Legacy} rename to legacy_{series.Legacy}").ConfigureAwait(false);
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "carried {Rows} rows of {Legacy} over into {Raw}; the old table is now legacy_{Legacy}",
                    copied, series.Legacy, series.Raw, series.Legacy);
            }
            fresh = true;
        }

        return fresh;
    }

    /// <summary>
    /// Fills every aggregate over everything its source still holds, minutes before hours.
    /// Refreshing is idempotent, so a repeat only costs time. The window starts at the source's
    /// oldest row, never earlier: a refresh over a period whose source rows retention has already
    /// dropped DELETES the rollups for that period, which are then the only copy left.
    /// </summary>
    private async Task FillAggregatesAsync(Session database)
    {
        logger.LogInformation("filling the rollups over all history (once; this can take a while)");
        var started = TimeProvider.System.GetTimestamp();

        foreach (var level in new[] { "1m", "1h" })
        {
            foreach (var series in AllSeries)
            {
                // Aligned down to a whole bucket, so the first partial bucket is filled too.
                var (source, oldestBucket) = level == "1m"
                    ? (series.Raw, "time_bucket('1 minute', min(ts))")
                    : (series.NameOf("1m"), "time_bucket('1 hour', min(bucket))");

                // Read back as epoch MILLISECONDS, not as text: a timestamp read as text and pasted
                // into the next statement would be a database-controlled value inside SQL.
                // sql-literal-ok: source derives from AllSeries; oldestBucket is one of two literals above.
                var oldest = await database.ScalarAsync<long?>($"select (extract(epoch from {oldestBucket}) * 1000)::int8 from {source}")
                    .ConfigureAwait(false);

                // Negative would mean a pre-1970 sample, which is EARLIER than the oldest row:
                // the one thing this window must never be.
                if (oldest is { } millis and >= 0)
                {
                    await database.ExecuteAsync(RefreshStatement(series.NameOf(level), millis)).ConfigureAwait(false);
                }
            }
        }

        await database.ExecuteAsync(
            "insert into vigil_state (key, value) values ('aggregates_filled', now()::text) "
            + "on conflict (key) do update set value = excluded.value, updated = now()").ConfigureAwait(false);
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("rollups filled in {Seconds:0}s", TimeProvider.System.GetElapsedTime(started).TotalSeconds);
        }
    }

    private static async Task SchedulePoliciesAsync(Session database, Series series, long rawDays)
    {
        var (minute, hour) = (series.NameOf("1m"), series.NameOf("1h"));

        // sql-literal-ok: every interpolation is an identifier derived from AllSeries, a const
        // above, or rawDays, a long. TimescaleDB's policy functions take their table by name, so
        // nothing here is bindable even in principle.
        await database.ExecuteAsync($"""
            select add_continuous_aggregate_policy('{minute}', start_offset => interval '2 hours',
                end_offset => interval '1 minute', schedule_interval => interval '1 minute', if_not_exists => true);
            select add_continuous_aggregate_policy('{hour}', start_offset => interval '2 days',
                end_offset => interval '1 hour', schedule_interval => interval '30 minutes', if_not_exists => true);
            select add_retention_policy('{minute}', drop_after => interval '{MinuteRetention}', if_not_exists => true);
            select remove_retention_policy('{series.Raw}', if_exists => true);
            select add_retention_policy('{series.Raw}', drop_after => interval '{rawDays} days');
            """).ConfigureAwait(false);

        var compressed = await database.ScalarAsync<bool>(
            "select coalesce(bool_or(compression_enabled), false) from timescaledb_information.hypertables where hypertable_name = $1",
            series.Raw).ConfigureAwait(false);
        if (!compressed)
        {
            // sql-literal-ok: series.Raw and series.Key come from AllSeries.
            await database.ExecuteAsync(
                $"alter table {series.Raw} set (timescaledb.compress, timescaledb.compress_segmentby = '{series.Key}', "
                + "timescaledb.compress_orderby = 'ts')").ConfigureAwait(false);
        }

        // sql-literal-ok: series.Raw comes from AllSeries and CompressAfter is a const.
        await database.ExecuteAsync(
            $"select add_compression_policy('{series.Raw}', compress_after => interval '{CompressAfter}', if_not_exists => true)")
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The one-off fill of a continuous aggregate, from <paramref name="oldestMillis"/> to now.
    /// </summary>
    /// <remarks>
    /// The only statement here that interpolates a value, so it is separate and unit-tested.
    /// <c>CALL</c> cannot run inside a transaction block and takes no subquery, so the window is
    /// written into the text; it is formatted by us from an integer, so it can only ever contain
    /// digits, '-', ':', '.', 'T' and 'Z'.
    /// </remarks>
    internal static string RefreshStatement(string name, long oldestMillis) =>
        // sql-literal-ok: name derives from AllSeries; the timestamp is formatted from a long.
        $"call refresh_continuous_aggregate('{name}', '{Rfc3339.From(DateTimeOffset.FromUnixTimeMilliseconds(oldestMillis))}'::timestamptz, null)";

    /// <summary>One connection with no statement timeout: the one-time work outlasts any sensible one.</summary>
    private sealed class Session(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        public async Task<int> ExecuteAsync(string sql)
        {
            await using var command = Command(sql);
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task<bool> ExistsAsync(string sql, string argument)
        {
            await using var command = Command(sql, argument);
            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
        }

        public async Task<T> ScalarAsync<T>(string sql, string? argument = null)
        {
            await using var command = Command(sql, argument);
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return value is null or DBNull ? default! : (T)value;
        }

        private NpgsqlCommand Command(string sql, string? argument = null)
        {
            var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 0 };
            if (argument is not null)
            {
                command.Parameters.Add(new NpgsqlParameter { Value = argument, NpgsqlDbType = NpgsqlDbType.Text });
            }

            return command;
        }
    }
}

/// <summary>
/// The database answered but cannot hold vigil's schema. A <see cref="DbException"/>, because it
/// is the database's state that is wrong, so the runtime logs it and retries like any other
/// database failure rather than stopping.
/// </summary>
public sealed class DatabaseSetupException(string message) : DbException(message);
