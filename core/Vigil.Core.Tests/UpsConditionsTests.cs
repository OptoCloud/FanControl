using Vigil.Core.Alerting;
using Vigil.Core.Protocol;
using Xunit;

namespace Vigil.Core.Tests;

/// <summary>Ported from the Rust core's alerts.rs tests: the tests are the specification.</summary>
public sealed class UpsConditionsTests
{
    internal static UpsState Ups(string[] status, double? charge, double? runtime) => new()
    {
        Enabled = true,
        Name = "apc",
        Reading = new UpsReading
        {
            TimestampUtc = "t",
            Name = "apc",
            Model = null,
            Status = status,
            BatteryCharge = charge,
            BatteryRuntimeSeconds = runtime,
            Load = 25.0,
            RealPower = null,
            InputVoltage = null,
            OutputVoltage = null,
            BatteryVoltage = null,
            Variables = new SortedDictionary<string, string>(StringComparer.Ordinal),
        },
        Error = null,
    };

    [Fact]
    public void AUpsOnlineIsFine()
    {
        Assert.Empty(UpsConditions.Of(Ups(["OL", "CHRG"], 100.0, 2000.0), TimeSpan.Zero));
    }

    [Fact]
    public void FlagsOnBatteryLowBatteryAndABatteryToReplace()
    {
        var conditions = UpsConditions.Of(Ups(["OB", "DISCHRG", "LB", "RB"], 9.0, 150.0), TimeSpan.Zero);

        Assert.Equal(["ups-low-battery", "ups-on-battery", "ups-replace-battery"], conditions.Keys);
        Assert.Equal(Severity.Critical, conditions["ups-low-battery"].Severity);

        // 150s is 2.5 minutes: "3" pins rounding away from zero, where .NET's default would say 2.
        Assert.Equal(
            "Mains power lost: the UPS is on battery. Battery at 9%, about 3 min of runtime left.",
            conditions["ups-on-battery"].Message);
    }

    [Fact]
    public void AnUnreadableUpsIsOnlyAProblemAfter30SecondsAndOnlyWhenConfigured()
    {
        var unreadable = new UpsState { Enabled = true, Name = "apc", Reading = null, Error = "upsd: DATA-STALE" };
        Assert.Empty(UpsConditions.Of(unreadable, TimeSpan.FromSeconds(10)));
        Assert.Contains(
            "DATA-STALE",
            UpsConditions.Of(unreadable, TimeSpan.FromSeconds(30))["ups-unreadable"].Message,
            StringComparison.Ordinal);

        var disabled = unreadable with { Enabled = false };
        Assert.Empty(UpsConditions.Of(disabled, TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void SaysWhichWayTheUpsIsNotProtectingAndHowOverloadedItIs()
    {
        var conditions = UpsConditions.Of(Ups(["OFF", "BYPASS", "OVER"], null, null), TimeSpan.Zero);

        Assert.Equal("The UPS is off: the load is not protected.", conditions["ups-not-protecting"].Message);
        Assert.Equal("The UPS is overloaded (25% load).", conditions["ups-overload"].Message);
    }
}
