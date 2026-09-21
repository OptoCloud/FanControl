using FanControl.Core.Drives;
using FanControl.Core.Status;
using Xunit;

namespace FanControl.Core.Tests;

public class DriveHealthTests
{
    private static readonly DateTimeOffset FirstPoll = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SecondPoll = FirstPoll.AddMinutes(15);

    private static DriveHealthStatus Healthy(string id, ulong reallocated = 0, ulong pending = 0, string source = "/dev/sda") =>
        new(id, true, reallocated, pending, 1000, source);

    [Fact]
    public void StoreStampsAvailableResultsWithThePollTime()
    {
        var store = new DriveHealthStore();

        store.Update([Healthy("wwn-1")], FirstPoll);

        Assert.Equal(FirstPoll, Assert.Single(store.Latest).AsOf);
    }

    [Fact]
    public void StoreKeepsLastGoodResultForADriveThatIsUnavailableThisPoll()
    {
        var store = new DriveHealthStore();
        store.Update([Healthy("wwn-1", reallocated: 7)], FirstPoll);

        // Asleep: smartctl -n standby skipped it. Also came back under a different sdX letter.
        store.Update([SmartctlJsonParser.Unavailable("wwn-1", "/dev/sdq")], SecondPoll);

        var status = Assert.Single(store.Latest);
        Assert.True(status.IsAvailable);
        Assert.Equal(7UL, status.ReallocatedSectorCount);
        Assert.Equal(FirstPoll, status.AsOf);
        Assert.Equal("/dev/sdq", status.SourcePath);
    }

    [Fact]
    public void StoreReportsNeverReadDriveAsUnavailableWithNoAsOf()
    {
        var store = new DriveHealthStore();

        store.Update([SmartctlJsonParser.Unavailable("wwn-1", "/dev/sda")], FirstPoll);

        var status = Assert.Single(store.Latest);
        Assert.False(status.IsAvailable);
        Assert.Null(status.AsOf);
    }

    [Fact]
    public void StoreDropsDrivesThatAreNoLongerPresent()
    {
        var store = new DriveHealthStore();
        store.Update([Healthy("wwn-1"), Healthy("wwn-2")], FirstPoll);

        store.Update([Healthy("wwn-2")], SecondPoll);

        Assert.Equal("wwn-2", Assert.Single(store.Latest).DeviceName);
    }

    [Fact]
    public void AlertsOnFailedSelfAssessment()
    {
        var alerts = DriveHealthAlerts.Evaluate(null, Healthy("wwn-1") with { Passed = false });

        Assert.Contains(alerts, a => a.Contains("FAILED"));
    }

    [Fact]
    public void StableReallocatedCountIsNotAnAlertButGrowthIs()
    {
        var previous = Healthy("wwn-1", reallocated: 1048);

        Assert.Empty(DriveHealthAlerts.Evaluate(null, previous));
        Assert.Empty(DriveHealthAlerts.Evaluate(previous, Healthy("wwn-1", reallocated: 1048)));
        Assert.Contains(
            DriveHealthAlerts.Evaluate(previous, Healthy("wwn-1", reallocated: 1056)),
            a => a.Contains("1048") && a.Contains("1056"));
    }

    [Fact]
    public void PendingSectorsAlertWhenTheyAppearOrChangeButNotWhileUnchanged()
    {
        var clean = Healthy("wwn-1");
        var pending = Healthy("wwn-1", pending: 8);

        Assert.Single(DriveHealthAlerts.Evaluate(clean, pending));
        Assert.Single(DriveHealthAlerts.Evaluate(null, pending));
        Assert.Empty(DriveHealthAlerts.Evaluate(pending, pending));
        Assert.Single(DriveHealthAlerts.Evaluate(pending, Healthy("wwn-1", pending: 16)));
    }

    [Fact]
    public void UnavailableStatusProducesNoAlerts()
    {
        Assert.Empty(DriveHealthAlerts.Evaluate(Healthy("wwn-1"), SmartctlJsonParser.Unavailable("wwn-1", "/dev/sda")));
    }
}
