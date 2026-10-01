using Vigil.Core.Alerting;
using Vigil.Core.Protocol;
using Xunit;

namespace Vigil.Core.Tests;

/// <summary>Ported from the Rust core's alerts.rs tests: the tests are the specification.</summary>
public sealed class UpsConditionsTests
{
    /// <summary>Nothing configured but the default battery limit, as a fresh install has.</summary>
    internal static readonly UpsLimits Limits = new() { HostShutdownSeconds = null, BatteryTemperatureWarn = 40 };

    /// <summary>A UPS read just now, with nothing raised.</summary>
    internal static readonly UpsWatch Fresh = new(TimeSpan.Zero, TimeSpan.Zero, BatteryHotRaised: false);

    private static UpsWatch Unread(int seconds) => Fresh with { UnreadableFor = TimeSpan.FromSeconds(seconds) };

    internal static UpsState Ups(
        string[] status, double? charge, double? runtime, double? temperature = null, int? monitors = 1, double? lowBatteryRuntime = null) => new()
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
                BatteryTemperature = temperature,
                BatteryDate = null,
                LowBatteryRuntimeSeconds = lowBatteryRuntime,
                LowBatteryCharge = null,
                TransferReason = null,
                OutputFrequency = null,
                OutputCurrent = null,
                Monitors = monitors,
                Variables = new SortedDictionary<string, string>(StringComparer.Ordinal),
            },
            Error = null,
            Limits = Limits,
        };

    [Fact]
    public void AUpsOnlineIsFine()
    {
        Assert.Empty(UpsConditions.Of(Ups(["OL", "CHRG"], 100.0, 2000.0), Fresh));
    }

    [Fact]
    public void FlagsOnBatteryLowBatteryAndABatteryToReplace()
    {
        var conditions = UpsConditions.Of(Ups(["OB", "DISCHRG", "LB", "RB"], 9.0, 150.0), Fresh);

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
        var unreadable = new UpsState { Enabled = true, Name = "apc", Reading = null, Error = "upsd: DATA-STALE", Limits = Limits };
        Assert.Empty(UpsConditions.Of(unreadable, Unread(10)));
        Assert.Contains(
            "DATA-STALE",
            UpsConditions.Of(unreadable, Unread(30))["ups-unreadable"].Message,
            StringComparison.Ordinal);

        var disabled = unreadable with { Enabled = false };
        Assert.Empty(UpsConditions.Of(disabled, Unread(60)));
    }

    [Fact]
    public void SaysWhichWayTheUpsIsNotProtectingAndHowOverloadedItIs()
    {
        var conditions = UpsConditions.Of(Ups(["OFF", "BYPASS", "OVER"], null, null), Fresh);

        Assert.Equal("The UPS is off: the load is not protected.", conditions["ups-not-protecting"].Message);
        Assert.Equal("The UPS is overloaded (25% load).", conditions["ups-overload"].Message);
    }

    [Theory]
    [InlineData(new string[0], "upsd reports no ups.status")]
    [InlineData(new[] { "CHRG" }, "upsd reports ups.status \"CHRG\", which names no power source")]
    [InlineData(new[] { "ALARM", "RB" }, "upsd reports ups.status \"ALARM RB\", which names no power source")]
    public void AStatusThatNamesNoPowerSourceIsUnreadableRatherThanOnMains(string[] status, string why)
    {
        var ups = Ups(status, 100.0, 2000.0);

        Assert.Empty(UpsConditions.Of(ups, Unread(10)));
        var unreadable = Assert.Single(UpsConditions.Of(ups, Unread(30)));
        Assert.Equal("ups-unreadable", unreadable.Key);
        Assert.Contains(why, unreadable.Value.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("OL")]
    [InlineData("OB")]
    [InlineData("BYPASS")]
    [InlineData("OFF")]
    public void EachPowerSourceFlagMakesAReadingUsable(string flag)
    {
        Assert.True(UpsConditions.NamesPowerSource(Ups([flag, "CHRG"], null, null).Reading!)); // Ups() always sets one.
    }

    [Fact]
    public void NoMonitorIsCriticalOnlyOnceItHasLasted()
    {
        var unguarded = Ups(["OL"], 100.0, 2000.0, monitors: 0);

        // Restarting upsmon or upsd logs it out for a moment; that is not a dead upsmon yet.
        Assert.Empty(UpsConditions.Of(unguarded, Fresh with { UnguardedFor = TimeSpan.FromSeconds(59) }));

        var condition = UpsConditions.Of(unguarded, Fresh with { UnguardedFor = TimeSpan.FromSeconds(60) })["ups-unguarded"];
        Assert.Equal(Severity.Critical, condition.Severity);
        Assert.Contains("no upsmon is logged in", condition.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownMonitorCountIsNotZero()
    {
        // An upsd that will not answer NUMLOGINS says nothing about upsmon.
        var unknown = Ups(["OL"], 100.0, 2000.0, monitors: null);

        Assert.Empty(UpsConditions.Of(unknown, Fresh with { UnguardedFor = TimeSpan.FromHours(1) }));
    }

    [Fact]
    public void AShutdownThatOutlastsTheLowBatteryWarningIsFlagged()
    {
        var tooLate = Ups(["OL"], 100.0, 2000.0, lowBatteryRuntime: 84) with { Limits = Limits with { HostShutdownSeconds = 150 } };
        Assert.Equal(
            "orion's shutdown would not finish on battery: upsmon starts it with 84 s of runtime left, and it takes 150 s. "
                + "Raise override.battery.runtime.low in ups.conf, or shorten the shutdown.",
            UpsConditions.Of(tooLate, Fresh)["ups-shutdown-too-late"].Message);

        // orion as configured: LB at 600 s of runtime left.
        var inTime = Ups(["OL"], 100.0, 2000.0, lowBatteryRuntime: 600) with { Limits = Limits with { HostShutdownSeconds = 150 } };
        Assert.Empty(UpsConditions.Of(inTime, Fresh));

        // Either number missing: nothing to compare, nothing to say.
        Assert.Empty(UpsConditions.Of(Ups(["OL"], 100.0, 2000.0, lowBatteryRuntime: 84), Fresh));
        Assert.Empty(UpsConditions.Of(Ups(["OL"], 100.0, 2000.0) with { Limits = Limits with { HostShutdownSeconds = 150 } }, Fresh));
    }

    [Theory]
    [InlineData(39.9, false, false)]
    [InlineData(40.0, false, true)]
    [InlineData(38.5, true, true)] // Raised, and not yet 2 °C under the limit: stays raised.
    [InlineData(38.0, true, false)] // Cooled the full margin: clears.
    public void AHotBatteryRaisesAtTheLimitAndClearsOnlyOnceItHasCooled(double temperature, bool raised, bool expected)
    {
        var conditions = UpsConditions.Of(Ups(["OL"], 100.0, 2000.0, temperature), Fresh with { BatteryHotRaised = raised });

        Assert.Equal(expected, conditions.ContainsKey(UpsConditions.BatteryHot));
    }

    [Fact]
    public void AHotBatterySaysHowHotAndWhereTheLimitIs()
    {
        var message = UpsConditions.Of(Ups(["OL"], 100.0, 2000.0, 41.6), Fresh)[UpsConditions.BatteryHot].Message;

        Assert.StartsWith("The UPS battery is at 42 °C (alert at 40 °C).", message, StringComparison.Ordinal);
    }
}
