namespace FanControl.Core.Sensors;

/// <summary>
/// A single point-in-time reading. <see cref="Id"/> is a stable logical name
/// (e.g. "cpu", "drive:sda") chosen at discovery time — never a raw hwmonN path,
/// since hwmon numbering is not stable across reboots or module load order.
/// </summary>
public sealed record SensorReading(
    string Id,
    SensorCategory Category,
    string Label,
    double? CelsiusOrNull,
    string SourcePath)
{
    public bool IsAvailable => CelsiusOrNull.HasValue;
}
