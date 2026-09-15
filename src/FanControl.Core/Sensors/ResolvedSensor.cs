namespace FanControl.Core.Sensors;

/// <summary>A sensor spec bound to a concrete sysfs path at a point in time. Re-resolved if the path stops existing.</summary>
public sealed record ResolvedSensor(
    string Id,
    SensorCategory Category,
    string Label,
    string TempInputPath);
