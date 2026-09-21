namespace FanControl.Core.Configuration;

/// <summary>
/// Catches config mistakes at startup, before any fan is touched, instead of letting them
/// surface as a poll that throws every 2 seconds. Every rule here guards against something
/// that would otherwise fail at runtime in a way that leaves a fan uncontrolled:
///  - an out-of-range duty makes the sysfs write throw on every poll;
///  - a channel with no curve is taken to manual and then never given a duty;
///  - two curves on one channel fight over it and share one hysteresis state;
///  - a curve naming an unknown channel silently does nothing;
///  - unsorted points make interpolation return nonsense.
/// </summary>
public static class FanControlOptionsValidator
{
    /// <returns>Every problem found (empty if the config is valid), so they can all be fixed in one pass.</returns>
    public static IReadOnlyList<string> Validate(FanControlOptions options)
    {
        var errors = new List<string>();

        if (options.PollInterval <= TimeSpan.Zero)
        {
            errors.Add($"PollInterval must be positive (is {options.PollInterval}).");
        }
        else if (options.DeadmanTimeout < options.PollInterval * 2)
        {
            errors.Add($"DeadmanTimeout ({options.DeadmanTimeout}) must be at least twice PollInterval ({options.PollInterval}), or a single slow poll trips it.");
        }

        if (options.SensorRescanInterval <= TimeSpan.Zero)
        {
            errors.Add($"SensorRescanInterval must be positive (is {options.SensorRescanInterval}).");
        }

        if (options.Gpu.Timeout <= TimeSpan.Zero)
        {
            errors.Add($"Gpu.Timeout must be positive (is {options.Gpu.Timeout}).");
        }

        if (options.DriveHealth.Enabled && options.DriveHealth.PollInterval <= TimeSpan.Zero)
        {
            errors.Add($"DriveHealth.PollInterval must be positive (is {options.DriveHealth.PollInterval}).");
        }

        if (options.DriveHealth.Timeout <= TimeSpan.Zero)
        {
            errors.Add($"DriveHealth.Timeout must be positive (is {options.DriveHealth.Timeout}).");
        }

        foreach (var channel in options.Channels)
        {
            if (string.IsNullOrWhiteSpace(channel.Id))
            {
                errors.Add("A fan channel has an empty Id.");
            }

            if (channel.Index < 1)
            {
                errors.Add($"Channel '{channel.Id}': Index must be 1 or greater (is {channel.Index}).");
            }

            if (!IsDuty(channel.MinimumDutyPercent))
            {
                errors.Add($"Channel '{channel.Id}': MinimumDutyPercent must be 0-100 (is {channel.MinimumDutyPercent}).");
            }
        }

        foreach (var duplicate in options.Channels.GroupBy(c => c.Id).Where(g => g.Count() > 1))
        {
            errors.Add($"Channel Id '{duplicate.Key}' is defined {duplicate.Count()} times.");
        }

        foreach (var duplicate in options.Channels.GroupBy(c => (c.ChipName, c.Index)).Where(g => g.Count() > 1))
        {
            errors.Add($"Channels {string.Join(", ", duplicate.Select(c => $"'{c.Id}'"))} all map to {duplicate.Key.ChipName} pwm{duplicate.Key.Index}.");
        }

        var channelIds = options.Channels.Select(c => c.Id).ToHashSet();

        foreach (var curve in options.Curves)
        {
            var name = $"Curve for '{curve.FanChannelId}'";

            if (!channelIds.Contains(curve.FanChannelId))
            {
                errors.Add($"{name}: no fan channel with that Id exists.");
            }

            if (curve.SensorIds.Count == 0)
            {
                errors.Add($"{name}: SensorIds is empty.");
            }

            if (curve.Points.Count == 0)
            {
                errors.Add($"{name}: Points is empty.");
            }

            if (curve.Points.Any(p => !IsDuty(p.DutyPercent)))
            {
                errors.Add($"{name}: every point's DutyPercent must be 0-100.");
            }

            if (curve.Points.Zip(curve.Points.Skip(1)).Any(pair => pair.Second.TemperatureCelsius < pair.First.TemperatureCelsius))
            {
                errors.Add($"{name}: Points must be sorted by ascending TemperatureCelsius.");
            }

            if (!IsDuty(curve.FailSafeDutyPercent))
            {
                errors.Add($"{name}: FailSafeDutyPercent must be 0-100 (is {curve.FailSafeDutyPercent}).");
            }

            if (curve.HysteresisCelsius < 0)
            {
                errors.Add($"{name}: HysteresisCelsius must not be negative (is {curve.HysteresisCelsius}).");
            }
        }

        foreach (var duplicate in options.Curves.GroupBy(c => c.FanChannelId).Where(g => g.Count() > 1))
        {
            errors.Add($"Fan channel '{duplicate.Key}' has {duplicate.Count()} curves; exactly one is required.");
        }

        var curveChannelIds = options.Curves.Select(c => c.FanChannelId).ToHashSet();
        foreach (var channel in options.Channels.Where(c => !curveChannelIds.Contains(c.Id)))
        {
            errors.Add($"Channel '{channel.Id}' has no curve: it would be taken to manual control and never given a duty. Add a curve or remove the channel.");
        }

        return errors;
    }

    private static bool IsDuty(int percent) => percent is >= 0 and <= 100;
}
