using Npgsql;
using Vigil.Core.Data;
using Vigil.Core.Protocol;
using Xunit;

namespace Vigil.Core.Tests;

[Collection(DatabaseTestGroup.Name)]
public sealed class DataLayerTests
{
    private static Snapshot SnapshotWith(params SensorReading[] sensors) => new()
    {
        TimestampUtc = Rfc3339.From(DateTimeOffset.UtcNow),
        Sensors = sensors,
        Fans =
        [
            new FanStatus { Id = "drive-cage", DutyPercent = 65, Rpm = 1211, Mode = PwmMode.Manual, Stalled = false },
            new FanStatus { Id = "lsi-cooling", DutyPercent = 40, Rpm = null, Mode = null, Stalled = true },
        ],
        DriveHealth = [],
        ControlLoopHealthy = true,
    };

    private static SensorReading Sensor(string id, SensorCategory category, double? celsius, string? port = null) => new()
    {
        Id = id,
        Category = category,
        Label = id,
        CelsiusOrNull = celsius,
        SourcePath = "/sys/class/hwmon/hwmon0/temp1_input",
        IsAvailable = celsius is not null,
        Port = port,
    };

    private static async Task<long> ScalarAsync(NpgsqlDataSource source, string sql, CancellationToken token)
    {
        await using var command = source.CreateCommand(sql);
        return Convert.ToInt64(await command.ExecuteScalarAsync(token), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void ADriveIsStoredUnderItsOwnIdAndUnderItsBay()
    {
        var drive = Sensor("drive:naa.5000c500bae40598", SensorCategory.Drive, 35, "pci-0000:01:00.1-ata-3");

        Assert.Equal(["drive:naa.5000c500bae40598", "port:pci-0000:01:00.1-ata-3"], SampleWriter.SeriesIdsOf(drive));
    }

    [Fact]
    public void EverythingElseAndADriveWithoutAPortGoUnderTheirIdAlone()
    {
        Assert.Equal(["cpu"], SampleWriter.SeriesIdsOf(Sensor("cpu", SensorCategory.Cpu, 40)));
        Assert.Equal(["drive:naa.1"], SampleWriter.SeriesIdsOf(Sensor("drive:naa.1", SensorCategory.Drive, null)));
    }

    [Fact]
    public async Task WritesASnapshotAsOneRowPerSeries()
    {
        var token = TestContext.Current.CancellationToken;
        await using var databases = await DatabaseFixture.ConnectAsync(token);
        await DatabaseFixture.TruncateAsync(databases.Writer, token);

        var writer = new SampleWriter(databases);
        var snapshot = SnapshotWith(
            Sensor("cpu", SensorCategory.Cpu, 38.25),
            Sensor("drive:naa.1", SensorCategory.Drive, 35.5, "pci-0000:01:00.1-ata-3"),
            // Unreadable: no sample row at all, rather than a zero that would read as cold.
            Sensor("gpu", SensorCategory.Gpu, null));

        await writer.InsertSamplesAsync(snapshot, token);

        // cpu, drive:naa.1 and its bay — three rows from two readable sensors.
        Assert.Equal(3, await ScalarAsync(databases.Writer, "select count(*) from sensor_samples", token));
        Assert.Equal(2, await ScalarAsync(databases.Writer, "select count(*) from fan_samples", token));
        Assert.Equal(1, await ScalarAsync(databases.Writer, "select count(*) from fan_samples where rpm is null", token));
    }

    [Fact]
    public async Task WritingTheSameSnapshotTwiceIsHarmless()
    {
        var token = TestContext.Current.CancellationToken;
        await using var databases = await DatabaseFixture.ConnectAsync(token);
        await DatabaseFixture.TruncateAsync(databases.Writer, token);

        var writer = new SampleWriter(databases);
        var snapshot = SnapshotWith(Sensor("cpu", SensorCategory.Cpu, 38.25));

        await writer.InsertSamplesAsync(snapshot, token);
        await writer.InsertSamplesAsync(snapshot, token);

        // `on conflict do nothing`: a retry after a database blip must not double-count.
        Assert.Equal(1, await ScalarAsync(databases.Writer, "select count(*) from sensor_samples", token));
    }

    [Fact]
    public async Task RecordsTheInventoryIncludingWhichDiskIsInWhichBay()
    {
        var token = TestContext.Current.CancellationToken;
        await using var databases = await DatabaseFixture.ConnectAsync(token);
        await DatabaseFixture.TruncateAsync(databases.Writer, token);

        var snapshot = SnapshotWith(
            Sensor("cpu", SensorCategory.Cpu, 38.25),
            Sensor("drive:naa.1", SensorCategory.Drive, 35.5, "pci-0000:01:00.1-ata-3"));

        await new SampleWriter(databases).TouchInventoryAsync(snapshot, token);

        // cpu, the drive, and the bay the drive sits in.
        Assert.Equal(3, await ScalarAsync(databases.Writer, "select count(*) from sensors", token));
        Assert.Equal(1, await ScalarAsync(databases.Writer, "select count(*) from sensors where category = 'bay'", token));
        Assert.Equal(1, await ScalarAsync(databases.Writer, "select count(*) from bay_occupants where wwn = 'naa.1'", token));
        Assert.Equal(2, await ScalarAsync(databases.Writer, "select count(*) from fans", token));
    }

    [Fact]
    public async Task WritesAUpsReadingWithEveryMetricOptional()
    {
        var token = TestContext.Current.CancellationToken;
        await using var databases = await DatabaseFixture.ConnectAsync(token);
        await DatabaseFixture.TruncateAsync(databases.Writer, token);

        var reading = NutProtocolTestsSupport.Reading(Rfc3339.From(DateTimeOffset.UtcNow));

        await new SampleWriter(databases).InsertUpsSampleAsync(reading, token);

        Assert.Equal(1, await ScalarAsync(databases.Writer, "select count(*) from ups_samples where ups = 'apc'", token));
        Assert.Equal(1, await ScalarAsync(databases.Writer, "select count(*) from ups_samples where status = 'OL CHRG'", token));
        // A UPS that does not report battery volts stores null, not zero.
        Assert.Equal(1, await ScalarAsync(databases.Writer, "select count(*) from ups_samples where battery_voltage is null", token));
    }

    [Fact]
    public async Task RoundTripsADriveAndRecordsHistoryOnlyWhenSomethingMoved()
    {
        var token = TestContext.Current.CancellationToken;
        await using var databases = await DatabaseFixture.ConnectAsync(token);
        await DatabaseFixture.TruncateAsync(databases.Writer, token);

        var store = new DriveStore(databases);
        var drive = new DriveState
        {
            Wwn = "naa.5000c500bae40598",
            Port = "pci-0000:01:00.1-ata-3",
            Passed = true,
            ReallocatedSectorCount = 0,
            PendingSectorCount = null,
            PowerOnHours = 8760,
            SourcePath = "/dev/sdc",
            AsOf = Rfc3339.From(DateTimeOffset.UtcNow),
        };

        await store.SaveAsync(drive, changed: true, token);
        await store.SaveAsync(drive with { PowerOnHours = 8761 }, changed: false, token);

        var loaded = Assert.Single(await store.LoadAsync(token));
        Assert.Equal(drive.Wwn, loaded.Wwn);
        Assert.Equal(drive.Port, loaded.Port);
        Assert.True(loaded.Passed);
        Assert.Equal(0ul, loaded.ReallocatedSectorCount);
        // A drive that does not report attribute 197 stores null, not zero.
        Assert.Null(loaded.PendingSectorCount);
        Assert.Equal(8761ul, loaded.PowerOnHours);
        // The timestamp survives the epoch-milliseconds round trip in the wire format.
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", loaded.AsOf);

        // Only the first save counted as a change; power-on hours alone must not add a row.
        Assert.Equal(1, await ScalarAsync(databases.Writer, "select count(*) from drive_health_history", token));
    }

    [Fact]
    public async Task InsertsAnEventAndReturnsWhatTheDatabaseAssigned()
    {
        var token = TestContext.Current.CancellationToken;
        await using var databases = await DatabaseFixture.ConnectAsync(token);
        await DatabaseFixture.TruncateAsync(databases.Writer, token);

        var recorded = await new EventStore(databases).InsertAsync(
            Severity.Warning, "fan-stalled:drive-cage", "drive-cage is being driven at 65% but reads 0 RPM", token);

        Assert.True(recorded.Id > 0);
        Assert.Equal(Severity.Warning, recorded.Severity);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", recorded.Ts);
        Assert.Equal(1, await ScalarAsync(databases.Writer, "select count(*) from events where severity = 'warning'", token));
    }

    [Fact]
    public async Task ReadsTheNewestEventsFirstAndOnlyAsManyAsAsked()
    {
        var token = TestContext.Current.CancellationToken;
        await using var databases = await DatabaseFixture.ConnectAsync(token);
        await DatabaseFixture.TruncateAsync(databases.Writer, token);

        var store = new EventStore(databases);
        foreach (var (severity, kind) in new[] { (Severity.Critical, "first"), (Severity.Info, "second"), (Severity.Warning, "third") })
        {
            await store.InsertAsync(severity, kind, $"{kind} event", token);
        }

        var recent = await store.RecentAsync(2, token);

        // Same-millisecond inserts are ordered by id, which is why the query breaks ties on it.
        Assert.Equal(["third", "second"], recent.Select(e => e.Kind));
        Assert.Equal([Severity.Warning, Severity.Info], recent.Select(e => e.Severity));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", recent[0].Ts);
    }

    [Fact]
    public async Task EveryEmbeddedQueryLoads()
    {
        // A query whose file is renamed or dropped should fail here, not the first time the one
        // code path that uses it runs in production.
        await Task.CompletedTask;
        foreach (var name in SqlText.Names)
        {
            Assert.False(string.IsNullOrWhiteSpace(SqlText.Load(name)), name);
        }

        Assert.Contains("insert_sensor_samples", SqlText.Names);
    }
}

/// <summary>A UPS reading with a representative mix of present and absent metrics.</summary>
internal static class NutProtocolTestsSupport
{
    public static UpsReading Reading(string at) => new()
    {
        TimestampUtc = at,
        Name = "apc",
        Model = "American Power Conversion Smart-UPS 1000",
        Status = ["OL", "CHRG"],
        BatteryCharge = 87,
        BatteryRuntimeSeconds = 2310.4,
        Load = 25,
        RealPower = 168,
        InputVoltage = 231.4,
        OutputVoltage = 230,
        BatteryVoltage = null,
        Variables = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["ups.status"] = "OL CHRG" },
    };
}
