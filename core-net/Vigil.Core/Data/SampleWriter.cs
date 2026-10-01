using Npgsql;
using NpgsqlTypes;
using Vigil.Core.Protocol;

namespace Vigil.Core.Data;

/// <summary>
/// The sample writes: one row per sensor per poll, the fans, the UPS, and the inventory of what
/// exists.
/// </summary>
/// <remarks>
/// A whole snapshot goes in as one statement per table, with the columns passed as parallel
/// arrays and expanded by <c>unnest</c> — thirteen drives cost one round trip, not thirteen. The
/// queries are the same .sql files the Rust implementation reads.
/// </remarks>
public sealed class SampleWriter(VigilDataSources databases)
{
    private static readonly string InsertSensorSamples = SqlText.Load("insert_sensor_samples");
    private static readonly string InsertFanSamples = SqlText.Load("insert_fan_samples");
    private static readonly string UpsertSensors = SqlText.Load("upsert_sensors");
    private static readonly string UpsertBayOccupants = SqlText.Load("upsert_bay_occupants");
    private static readonly string UpsertFans = SqlText.Load("upsert_fans");
    private static readonly string InsertUpsSample = SqlText.Load("insert_ups_sample");

    /// <summary>
    /// The history keys one reading is stored under: its own id, plus its bay for a drive with a
    /// known port.
    /// </summary>
    /// <remarks>
    /// A drive's temperature is stored twice. <c>drive:&lt;wwn&gt;</c> follows the disk — its own
    /// trend, wherever it is plugged in. <c>port:&lt;by-path&gt;</c> follows the bay — the trend
    /// of that spot in the case, whichever disk is in it. Two questions, two keys (ADR-009).
    /// </remarks>
    public static IReadOnlyList<string> SeriesIdsOf(SensorReading sensor)
    {
        ArgumentNullException.ThrowIfNull(sensor);

        return sensor.Port is { Length: > 0 } port ? [sensor.Id, $"port:{port}"] : [sensor.Id];
    }

    public async Task InsertSamplesAsync(Snapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var ids = new List<string>();
        var celsius = new List<float>();
        foreach (var sensor in snapshot.Sensors)
        {
            if (sensor.CelsiusOrNull is not { } value)
            {
                continue;
            }

            foreach (var id in SeriesIdsOf(sensor))
            {
                ids.Add(id);
                celsius.Add((float)value);
            }
        }

        if (ids.Count > 0)
        {
            await ExecuteAsync(
                InsertSensorSamples,
                cancellationToken,
                Text(snapshot.TimestampUtc),
                Array(ids.ToArray()),
                Array(celsius.ToArray(), NpgsqlDbType.Real));
        }

        if (snapshot.Fans.Count > 0)
        {
            await ExecuteAsync(
                InsertFanSamples,
                cancellationToken,
                Text(snapshot.TimestampUtc),
                Array(snapshot.Fans.Select(fan => fan.Id).ToArray()),
                Array(snapshot.Fans.Select(fan => (short)fan.DutyPercent).ToArray(), NpgsqlDbType.Smallint),
                Array(snapshot.Fans.Select(fan => (int?)fan.Rpm).ToArray(), NpgsqlDbType.Integer));
        }
    }

    /// <summary>
    /// Records which sensors, bays and fans exist, when each was last seen, and which disk is in
    /// which bay.
    /// </summary>
    public async Task TouchInventoryAsync(Snapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var (ids, categories, labels) = (new List<string>(), new List<string>(), new List<string>());
        foreach (var sensor in snapshot.Sensors)
        {
            ids.Add(sensor.Id);
            categories.Add(CategoryName(sensor.Category));
            labels.Add(sensor.Label);

            if (sensor.Port is { Length: > 0 } port)
            {
                ids.Add($"port:{port}");
                categories.Add("bay");
                labels.Add(port);
            }
        }

        if (ids.Count > 0)
        {
            await ExecuteAsync(UpsertSensors, cancellationToken, Array(ids.ToArray()), Array(categories.ToArray()), Array(labels.ToArray()));
        }

        var (ports, wwns) = (new List<string>(), new List<string>());
        foreach (var sensor in snapshot.Sensors)
        {
            if (sensor.Port is { Length: > 0 } port && sensor.Id.StartsWith("drive:", StringComparison.Ordinal))
            {
                ports.Add(port);
                wwns.Add(sensor.Id["drive:".Length..]);
            }
        }

        if (ports.Count > 0)
        {
            await ExecuteAsync(UpsertBayOccupants, cancellationToken, Array(ports.ToArray()), Array(wwns.ToArray()));
        }

        if (snapshot.Fans.Count > 0)
        {
            await ExecuteAsync(UpsertFans, cancellationToken, Array(snapshot.Fans.Select(fan => fan.Id).ToArray()));
        }
    }

    public async Task InsertUpsSampleAsync(UpsReading reading, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reading);

        // The columns are `real`: a tenth of a volt is well inside what the hardware measures.
        static object Real(double? value) => value is null ? DBNull.Value : (float)value.Value;

        await ExecuteAsync(
            InsertUpsSample,
            cancellationToken,
            Text(reading.TimestampUtc),
            Text(reading.Name),
            Text(string.Join(' ', reading.Status)),
            new NpgsqlParameter { Value = Real(reading.BatteryCharge), NpgsqlDbType = NpgsqlDbType.Real },
            new NpgsqlParameter
            {
                Value = reading.BatteryRuntimeSeconds is null
                    ? DBNull.Value
                    : (int)Math.Round(reading.BatteryRuntimeSeconds.Value, MidpointRounding.AwayFromZero),
                NpgsqlDbType = NpgsqlDbType.Integer,
            },
            new NpgsqlParameter { Value = Real(reading.Load), NpgsqlDbType = NpgsqlDbType.Real },
            new NpgsqlParameter { Value = Real(reading.RealPower), NpgsqlDbType = NpgsqlDbType.Real },
            new NpgsqlParameter { Value = Real(reading.InputVoltage), NpgsqlDbType = NpgsqlDbType.Real },
            new NpgsqlParameter { Value = Real(reading.OutputVoltage), NpgsqlDbType = NpgsqlDbType.Real },
            new NpgsqlParameter { Value = Real(reading.BatteryVoltage), NpgsqlDbType = NpgsqlDbType.Real });
    }

    /// <summary>
    /// The stored name of a sensor's category. Spelled out rather than reusing the JSON name,
    /// because this value is stored and queried: a rename on the wire must not silently
    /// repartition history.
    /// </summary>
    private static string CategoryName(SensorCategory category) => category switch
    {
        SensorCategory.Cpu => "cpu",
        SensorCategory.BoardAmbient => "boardAmbient",
        SensorCategory.Drive => "drive",
        SensorCategory.Gpu => "gpu",
        SensorCategory.Memory => "memory",
        SensorCategory.Hba => "hba",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "unknown sensor category"),
    };

    private static NpgsqlParameter Text(string value) => new() { Value = value, NpgsqlDbType = NpgsqlDbType.Text };

    private static NpgsqlParameter Array<T>(T[] values, NpgsqlDbType element = NpgsqlDbType.Text) =>
        new() { Value = values, NpgsqlDbType = NpgsqlDbType.Array | element };

    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken, params NpgsqlParameter[] parameters)
    {
        await using var command = databases.Writer.CreateCommand(sql);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(parameter);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
