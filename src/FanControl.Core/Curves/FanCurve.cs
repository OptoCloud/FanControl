namespace FanControl.Core.Curves;

/// <summary>
/// Maps one or more sensor ids (aggregated by max — the hottest input drives the fan)
/// to a duty percentage via a piecewise-linear curve.
/// </summary>
/// <param name="FanChannelId">Matches a FanControl.Core.Fans.FanChannel.Id.</param>
/// <param name="SensorIds">
/// Sensor ids this curve responds to. For "drive" use every "drive:*" id present at
/// evaluation time — pass the literal prefix "drive:*" and CurveEngine expands it.
/// </param>
/// <param name="Points">Must be sorted ascending by temperature; at least one point.</param>
/// <param name="HysteresisCelsius">
/// Duty is allowed to rise immediately but only drops once the driving temperature has
/// fallen at least this many degrees below the point that produced the current duty —
/// prevents the classic hunting/oscillation at a curve breakpoint.
/// </param>
/// <param name="FailSafeDutyPercent">
/// Applied instead of the curve when every sensor in <see cref="SensorIds"/> is
/// unavailable (e.g. the HBA temperature isn't wired up yet). Deliberately per-curve
/// rather than a single global fail-safe: a curve fed by an always-missing sensor would
/// otherwise sit at whatever this value is forever, so how "safe" that number should be
/// depends on what the fan is actually protecting.
/// </param>
public sealed record FanCurve(
    string FanChannelId,
    IReadOnlyList<string> SensorIds,
    IReadOnlyList<CurvePoint> Points,
    double HysteresisCelsius = 3.0,
    int FailSafeDutyPercent = 100);
