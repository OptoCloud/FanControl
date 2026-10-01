using System.Globalization;
using Vigil.Core.Protocol;

namespace Vigil.Core.Alerting;

/// <summary>The problems visible in the UPS's state.</summary>
public static class UpsConditions
{
    /// <summary>
    /// How long the UPS may go unread before that is a problem. A single dropped poll or an
    /// upsd restart is not worth an event; a lasting gap means a power cut would go unseen.
    /// </summary>
    public static readonly TimeSpan UnreadableGrace = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Every problem visible in <paramref name="ups"/>. <paramref name="unreadableFor"/> is how
    /// long there has been no reading. Nothing, ever, when NUT is not configured.
    /// </summary>
    public static Conditions Of(UpsState ups, TimeSpan unreadableFor)
    {
        var conditions = new Conditions();
        if (!ups.Enabled)
        {
            return conditions;
        }

        if (ups.Reading is not { } reading)
        {
            if (unreadableFor >= UnreadableGrace)
            {
                conditions["ups-unreadable"] = new Condition(
                    Severity.Warning,
                    $"The UPS can't be read ({ups.Error ?? "no reason given"}). A power cut would not show up here; orion's own upsmon is unaffected.",
                    "The UPS is readable again.");
            }

            return conditions;
        }

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

        return conditions;
    }

    /// <summary>
    /// Rounded away from zero, as Rust's <c>f64::round</c> is. .NET's default is banker's
    /// rounding, which would tell the operator 2 minutes of runtime where the Rust core said 3.
    /// </summary>
    private static string Whole(double value) =>
        Math.Round(value, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);
}
