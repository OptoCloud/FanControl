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
    /// Duty percent to apply for this poll. Returns curve.FailSafeDutyPercent if none of
    /// the curve's sensors produced a reading — an unreadable input must never be
    /// silently treated as "cold".
    /// </summary>
    public int Evaluate(FanCurve curve, IReadOnlyList<SensorReading> readings)
    {
        var drivingTemperature = SelectDrivingTemperature(curve, readings);
        if (drivingTemperature is not { } temperature)
        {
            return curve.FailSafeDutyPercent;
        }

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

    private static double? SelectDrivingTemperature(FanCurve curve, IReadOnlyList<SensorReading> readings)
    {
        var matchingIds = curve.SensorIds.Where(id => !id.EndsWith(":*", StringComparison.Ordinal)).ToHashSet();
        var prefixes = curve.SensorIds
            .Where(id => id.EndsWith(":*", StringComparison.Ordinal))
            .Select(id => id[..^1])
            .ToList();

        var values = readings
            .Where(r => r.IsAvailable && (matchingIds.Contains(r.Id) || prefixes.Any(p => r.Id.StartsWith(p, StringComparison.Ordinal))))
            .Select(r => r.CelsiusOrNull!.Value)
            .ToList();

        return values.Count > 0 ? values.Max() : null;
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
