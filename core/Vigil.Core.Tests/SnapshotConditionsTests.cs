using Vigil.Core.Alerting;
using Vigil.Core.Protocol;
using Xunit;

namespace Vigil.Core.Tests;

/// <summary>Ported from the Rust core's alerts.rs tests: the tests are the specification.</summary>
public sealed class SnapshotConditionsTests
{
    private static FanStatus Fan(string id, PwmMode? mode, bool stalled) =>
        new() { Id = id, DutyPercent = 65, Rpm = stalled ? 0u : 1200u, Mode = mode, Stalled = stalled };

    private static SensorReading Sensor(string id, SensorCategory category, double? celsius) => new()
    {
        Id = id,
        Category = category,
        Label = "label",
        CelsiusOrNull = celsius,
        SourcePath = "path",
        IsAvailable = celsius is not null,
    };

    private static Snapshot Snapshot(FanStatus[] fans, SensorReading[] sensors, bool healthy) => new()
    {
        TimestampUtc = "t",
        Sensors = sensors,
        Fans = fans,
        DriveHealth = [],
        ControlLoopHealthy = healthy,
    };

    private static HashSet<string> Known(params string[] ids) => new(ids, StringComparer.Ordinal);

    [Fact]
    public void FindsNothingWrongWithAHealthySnapshot()
    {
        var healthy = Snapshot([Fan("drive-cage", PwmMode.Manual, false)], [Sensor("cpu", SensorCategory.Cpu, 40.0)], true);

        Assert.Empty(SnapshotConditions.In(healthy, Known("cpu")));
    }

    [Fact]
    public void FlagsAnUnhealthyLoopAStalledFanAndFansNotOnManual()
    {
        FanStatus[] fans = [Fan("drive-cage", PwmMode.Manual, true), Fan("intake", PwmMode.SmartFanIV, false), Fan("rear", null, false)];
        var conditions = SnapshotConditions.In(Snapshot(fans, [], false), Known());

        Assert.Equal(["fan-mode:intake", "fan-mode:rear", "fan-stalled:drive-cage", "loop-unhealthy"], conditions.Keys);
        Assert.Equal(Severity.Critical, conditions["fan-stalled:drive-cage"].Severity);
        Assert.Contains("smartFanIV", conditions["fan-mode:intake"].Message, StringComparison.Ordinal);
        Assert.Contains("unreadable", conditions["fan-mode:rear"].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASleepingDriveIsNotAProblemButAnUnreadableSensorOrAVanishedOneIs()
    {
        SensorReading[] sensors = [Sensor("drive:naa.1", SensorCategory.Drive, null), Sensor("gpu", SensorCategory.Gpu, null)];
        var conditions = SnapshotConditions.In(Snapshot([], sensors, true), Known("drive:naa.1", "gpu", "hba"));

        Assert.Equal(["sensor-missing:hba", "sensor-unavailable:gpu"], conditions.Keys);
    }

    [Fact]
    public void ConditionsAreOrderedOrdinallyAsTheRustSideOrdersThem()
    {
        // Culture-aware comparison would put "fan-mode:Z" after "fan-mode:a"; ordinal puts
        // capitals first, as Rust's BTreeMap does, so both cores raise events in one order.
        var conditions = SnapshotConditions.In(Snapshot([Fan("a", null, false), Fan("Z", null, false)], [], true), Known());

        Assert.Equal(["fan-mode:Z", "fan-mode:a"], conditions.Keys);
    }
}
