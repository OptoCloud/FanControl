using System.Globalization;
using Vigil.Core.Protocol;

namespace Vigil.Core.Alerting;

/// <summary>What judging the UPS needs besides its state: how long things have lasted, and what is raised.</summary>
/// <param name="UnreadableFor">How long there has been no reading that names a power source.</param>
/// <param name="UnguardedFor">How long upsd has reported no monitor logged in.</param>
/// <param name="BatteryHotRaised">Whether <see cref="UpsConditions.BatteryHot"/> is raised, for its hysteresis.</param>
public sealed record UpsWatch(TimeSpan UnreadableFor, TimeSpan UnguardedFor, bool BatteryHotRaised);

/// <summary>The problems visible in the UPS's state.</summary>
public static class UpsConditions
{
    /// <summary>
    /// How long the UPS may go unread before that is a problem. A single dropped poll or an
    /// upsd restart is not worth an event; a lasting gap means a power cut would go unseen.
    /// </summary>
    public static readonly TimeSpan UnreadableGrace = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long upsd may report no monitor before that is a problem. Restarting upsmon, or upsd,
    /// logs it out for a moment, and UPS conditions are raised on the first poll; a zero that
    /// lasts a minute is a upsmon that is not coming back.
    /// </summary>
    public static readonly TimeSpan UnguardedGrace = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How far below the limit a hot battery has to cool before the alert clears, so a battery
    /// sitting at the limit does not raise and clear on every tenth of a degree of noise.
    /// </summary>
    public const double BatteryCoolingMargin = 2.0;

    /// <summary>The one condition with hysteresis, so the runtime has to say whether it is raised.</summary>
    public const string BatteryHot = "ups-battery-hot";

    /// <summary>
    /// The <c>ups.status</c> flags that say where the load's power comes from. Every other flag
    /// (CHRG, RB, ALARM, a driver's state mid-transfer) qualifies one of these, so a status
    /// without any of them says nothing about the mains.
    /// </summary>
    private static readonly string[] PowerSourceFlags = ["OL", "OB", "BYPASS", "OFF"];

    /// <summary>
    /// Whether <paramref name="reading"/> says where the power comes from. One that does not is
    /// treated like no reading at all: read as "not OB", it would clear a running on-battery
    /// alert and announce that mains power was back, about a UPS that said nothing of the sort.
    /// </summary>
    public static bool NamesPowerSource(UpsReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        return reading.Status.Any(flag => PowerSourceFlags.Contains(flag, StringComparer.Ordinal));
    }

    /// <summary>
    /// Every problem visible in <paramref name="ups"/>. Nothing, ever, when NUT is not configured.
    /// </summary>
    /// <remarks>
    /// Without a reading that names a power source only <c>ups-unreadable</c> can be judged. Feed
    /// the result to the debouncer as incomplete then, so the conditions already raised stay raised.
    /// </remarks>
    public static Conditions Of(UpsState ups, UpsWatch watch)
    {
        ArgumentNullException.ThrowIfNull(ups);
        ArgumentNullException.ThrowIfNull(watch);

        var conditions = new Conditions();
        if (!ups.Enabled)
        {
            return conditions;
        }

        if (ups.Reading is not { } reading || !NamesPowerSource(reading))
        {
            if (watch.UnreadableFor >= UnreadableGrace)
            {
                conditions["ups-unreadable"] = new Condition(
                    Severity.Warning,
                    $"The UPS can't be read ({WhyUnreadable(ups)}). A power cut would not show up here; orion's own upsmon is unaffected.",
                    "The UPS is readable again.");
            }

            return conditions;
        }

        AddStatusConditions(conditions, reading);
        AddProtectionConditions(conditions, ups.Limits, reading, watch);
        return conditions;
    }

    /// <summary>What the UPS says about itself in <c>ups.status</c>.</summary>
    private static void AddStatusConditions(Conditions conditions, UpsReading reading)
    {
        var flags = reading.Status.ToHashSet(StringComparer.Ordinal);
        var charge = reading.BatteryCharge is { } percent ? $" Battery at {Whole(percent)}%" : string.Empty;
        var runtime = reading.BatteryRuntimeSeconds is { } seconds ? $", about {Whole(seconds / 60.0)} min of runtime left" : string.Empty;

        if (flags.Contains("OB"))
        {
            conditions["ups-on-battery"] = new Condition(
                Severity.Warning,
                $"Mains power lost: the UPS is on battery.{charge}{runtime}.",
                "Mains power is back: the UPS is online again.");
        }

        if (flags.Contains("LB"))
        {
            conditions["ups-low-battery"] = new Condition(
                Severity.Critical,
                $"The UPS battery is LOW.{charge}. orion's upsmon shuts the host down on this.",
                "The UPS battery is no longer low.");
        }

        if (flags.Contains("FSD"))
        {
            conditions["ups-forced-shutdown"] = new Condition(
                Severity.Critical,
                "The UPS is in forced shutdown (FSD): the load is about to lose power.",
                "Forced shutdown is over.");
        }

        if (flags.Contains("RB"))
        {
            conditions["ups-replace-battery"] = new Condition(
                Severity.Warning,
                "The UPS reports its battery needs replacing.",
                "The UPS no longer reports a battery to replace.");
        }

        if (flags.Contains("OVER"))
        {
            var load = reading.Load is { } percentLoad ? $" ({Whole(percentLoad)}% load)" : string.Empty;
            conditions["ups-overload"] = new Condition(
                Severity.Warning,
                $"The UPS is overloaded{load}.",
                "The UPS is no longer overloaded.");
        }

        if (flags.Contains("BYPASS") || flags.Contains("OFF"))
        {
            var what = flags.Contains("OFF") ? "off" : "on bypass";
            conditions["ups-not-protecting"] = new Condition(
                Severity.Warning,
                $"The UPS is {what}: the load is not protected.",
                "The UPS is protecting the load again.");
        }
    }

    /// <summary>Whether the protection would work when it is needed: the shutdown, and the battery.</summary>
    private static void AddProtectionConditions(Conditions conditions, UpsLimits limits, UpsReading reading, UpsWatch watch)
    {
        if (reading.Monitors == 0 && watch.UnguardedFor >= UnguardedGrace)
        {
            conditions["ups-unguarded"] = new Condition(
                Severity.Critical,
                "Nothing will shut orion down on a power cut: no upsmon is logged in to upsd. Check nut-monitor on orion.",
                "upsmon is watching the UPS again.");
        }

        if (reading.LowBatteryRuntimeSeconds is { } startsAt && limits.HostShutdownSeconds is { } takes && startsAt < takes)
        {
            conditions["ups-shutdown-too-late"] = new Condition(
                Severity.Warning,
                $"orion's shutdown would not finish on battery: upsmon starts it with {Whole(startsAt)} s of runtime left, "
                    + $"and it takes {Whole(takes)} s. Raise override.battery.runtime.low in ups.conf, or shorten the shutdown.",
                "The low-battery warning leaves orion enough time to shut down again.");
        }

        var limit = limits.BatteryTemperatureWarn;
        if (reading.BatteryTemperature is { } temperature
            && (temperature >= limit || (watch.BatteryHotRaised && temperature > limit - BatteryCoolingMargin)))
        {
            conditions[BatteryHot] = new Condition(
                Severity.Warning,
                $"The UPS battery is at {Whole(temperature)} °C (alert at {Whole(limit)} °C). Heat ages a lead-acid battery "
                    + "faster than anything else: check the UPS's airflow and the room.",
                "The UPS battery has cooled down.");
        }
    }

    private static string WhyUnreadable(UpsState ups) => ups.Reading switch
    {
        null => ups.Error ?? "no reason given",
        { Status.Count: 0 } => "upsd reports no ups.status",
        { Status: var status } => $"upsd reports ups.status \"{string.Join(' ', status)}\", which names no power source",
    };

    /// <summary>
    /// Rounded away from zero, as Rust's <c>f64::round</c> is. .NET's default is banker's
    /// rounding, which would tell the operator 2 minutes of runtime where the Rust core said 3.
    /// </summary>
    private static string Whole(double value) =>
        Math.Round(value, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);
}
