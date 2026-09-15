namespace FanControl.Core.Configuration;

public sealed class CurvePointOptions
{
    public required double TemperatureCelsius { get; init; }
    public required int DutyPercent { get; init; }
}

public sealed class FanCurveOptions
{
    public required string FanChannelId { get; init; }
    public required IReadOnlyList<string> SensorIds { get; init; }
    public required IReadOnlyList<CurvePointOptions> Points { get; init; }
    public double HysteresisCelsius { get; init; } = 3.0;
}
