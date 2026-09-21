using FanControl.Core.Sensors;

namespace FanControl.Core.Curves;

/// <summary>
/// Stateful per-curve evaluator: applies the piecewise-linear curve plus asymmetric
/// hysteresis (instant rise, delayed fall). One instance is meant to live for the whole
/// daemon lifetime so hysteresis state persists across polls.
/// </summary>
public sealed class CurveEngine
{
    private readonly Dictionary<string, double> _lastAppliedTemperature = [];
    private readonly Dictionary<string, int> _lastAppliedDuty = [];

    /// <summary>
    /// Duty percent to apply for this poll. An unreadable input must never be silently
    /// treated as "cold":
    ///  - if none of the curve's sensors produced a reading, returns FailSafeDutyPercent;
    ///  - if a sensor the curve names explicitly (not via a "prefix:*" wildcard) is
    ///    unavailable while others still read, the curve still runs on what's left but
    ///    FailSafeDutyPercent becomes a floor, since the missing sensor could be the hot one.
    /// Wildcard members are exempt from the second rule: drives come and go (hot-swap,
    /// standby) and the ones still present are a fair stand-in for the group.
    /// </summary>
    public int Evaluate(FanCurve curve, IReadOnlyList<SensorReading> readings)
    {
        var (drivingTemperature, namedSensorUnavailable) = SelectDrivingTemperature(curve, readings);
        if (drivingTemperature is not { } temperature)
        {
            return curve.FailSafeDutyPercent;
        }

        var duty = ApplyHysteresis(curve, temperature);
        return namedSensorUnavailable ? Math.Max(duty, curve.FailSafeDutyPercent) : duty;
    }

    private int ApplyHysteresis(FanCurve curve, double temperature)
    {
        var target = Interpolate(curve.Points, temperature);

        if (!_lastAppliedDuty.TryGetValue(curve.FanChannelId, out var previousDuty))
        {
            Apply(curve.FanChannelId, temperature, target);
            return target;
        }

        if (target >= previousDuty)
        {
            Apply(curve.FanChannelId, temperature, target);
            return target;
        }

        var lastTemperature = _lastAppliedTemperature[curve.FanChannelId];
        if (temperature <= lastTemperature - curve.HysteresisCelsius)
        {
            Apply(curve.FanChannelId, temperature, target);
            return target;
        }

        return previousDuty;
    }

    private void Apply(string fanChannelId, double temperature, int duty)
    {
        _lastAppliedTemperature[fanChannelId] = temperature;
        _lastAppliedDuty[fanChannelId] = duty;
    }

    private static (double? DrivingTemperature, bool NamedSensorUnavailable) SelectDrivingTemperature(
        FanCurve curve, IReadOnlyList<SensorReading> readings)
    {
        var namedIds = curve.SensorIds.Where(id => !id.EndsWith(":*", StringComparison.Ordinal)).ToHashSet();
        var prefixes = curve.SensorIds
            .Where(id => id.EndsWith(":*", StringComparison.Ordinal))
            .Select(id => id[..^1])
            .ToList();

        var available = readings
            .Where(r => r.IsAvailable && (namedIds.Contains(r.Id) || prefixes.Any(p => r.Id.StartsWith(p, StringComparison.Ordinal))))
            .ToList();

        var namedSensorUnavailable = namedIds.Any(id => !available.Any(r => r.Id == id));

        return (available.Count > 0 ? available.Max(r => r.CelsiusOrNull!.Value) : null, namedSensorUnavailable);
    }

    private static int Interpolate(IReadOnlyList<CurvePoint> points, double temperature)
    {
        if (points.Count == 0)
        {
            return 100;
        }

        if (temperature <= points[0].TemperatureCelsius)
        {
            return points[0].DutyPercent;
        }

        for (var i = 0; i < points.Count - 1; i++)
        {
            var (lo, hi) = (points[i], points[i + 1]);
            if (temperature <= hi.TemperatureCelsius)
            {
                var span = hi.TemperatureCelsius - lo.TemperatureCelsius;
                if (span <= 0)
                {
                    return hi.DutyPercent;
                }

                var fraction = (temperature - lo.TemperatureCelsius) / span;
                return (int)Math.Round(lo.DutyPercent + fraction * (hi.DutyPercent - lo.DutyPercent));
            }
        }

        return points[^1].DutyPercent;
    }
}
