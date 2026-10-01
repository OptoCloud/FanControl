using Vigil.Core.Protocol;

namespace Vigil.Core.Alerting;

/// <summary>The problems visible in one vigild snapshot.</summary>
public static class SnapshotConditions
{
    /// <summary>
    /// Every problem visible in <paramref name="snapshot"/>. <paramref name="knownSensorIds"/> is
    /// every sensor ever seen, so one that has vanished from the host is noticed.
    /// </summary>
    public static Conditions In(Snapshot snapshot, IReadOnlySet<string> knownSensorIds)
    {
        var conditions = new Conditions();

        if (!snapshot.ControlLoopHealthy)
        {
            conditions["loop-unhealthy"] = new Condition(
                Severity.Critical,
                "The control loop reports unhealthy: a fan channel could not be driven, or the loop has stopped polling.",
                "The control loop is healthy again.");
        }

        foreach (var fan in snapshot.Fans)
        {
            if (fan.Stalled)
            {
                conditions[$"fan-stalled:{fan.Id}"] = new Condition(
                    Severity.Critical,
                    $"Fan '{fan.Id}' reads 0 RPM at {fan.DutyPercent}% duty: dead, jammed or unplugged?",
                    $"Fan '{fan.Id}' is spinning again.");
            }

            if (fan.Mode != PwmMode.Manual)
            {
                var mode = fan.Mode is { } known ? ModeName(known) : "unreadable";
                conditions[$"fan-mode:{fan.Id}"] = new Condition(
                    Severity.Warning,
                    $"Fan '{fan.Id}' is not under vigild's control (pwm mode: {mode}).",
                    $"Fan '{fan.Id}' is back under vigild's control.");
            }
        }

        foreach (var sensor in snapshot.Sensors)
        {
            // A drive with no temperature is usually just asleep, which is not a problem.
            if (!sensor.IsAvailable && sensor.Category != SensorCategory.Drive)
            {
                conditions[$"sensor-unavailable:{sensor.Id}"] = new Condition(
                    Severity.Warning,
                    $"Sensor '{sensor.Id}' ({sensor.Label}) is unreadable. Curves that name it run at no less than their fail-safe duty.",
                    $"Sensor '{sensor.Id}' is readable again.");
            }
        }

        var present = snapshot.Sensors.Select(sensor => sensor.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in knownSensorIds.Where(id => !present.Contains(id)))
        {
            conditions[$"sensor-missing:{id}"] = new Condition(
                Severity.Warning,
                $"Sensor '{id}' has disappeared from the host.",
                $"Sensor '{id}' is back.");
        }

        return conditions;
    }

    /// <summary>
    /// The wire spelling, written out rather than taken from the JSON attribute: this text goes
    /// into the event log, which must not change wording if the wire name ever does.
    /// </summary>
    private static string ModeName(PwmMode mode) => mode switch
    {
        PwmMode.Disabled => "disabled",
        PwmMode.Manual => "manual",
        PwmMode.ThermalCruise => "thermalCruise",
        PwmMode.SpeedCruise => "speedCruise",
        PwmMode.SmartFanIII => "smartFanIII",
        PwmMode.SmartFanIV => "smartFanIV",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "unknown pwm mode"),
    };
}
