namespace FanControl.Core.Sensors;

/// <summary>A sensor spec bound to a concrete sysfs path at a point in time. Re-resolved if the path stops existing.</summary>
/// <param name="Id">Stable identity — for drives, the WWN (wwid), not the sdX letter, so it survives being moved to a different port/slot.</param>
/// <param name="Label">Human-facing label; same stable value as <see cref="Id"/>'s suffix for drives.</param>
/// <param name="DeviceName">
/// The live kernel block device name (e.g. "sdc"), only populated for drives. This is NOT
/// stable across reboots/port changes — it exists purely so callers that need to actually
/// operate on the device right now (e.g. running smartctl against /dev/{DeviceName}) have
/// a way to do so, without that live-but-unstable name leaking into anything meant to
/// track a physical drive's identity over time.
/// </param>
public sealed record ResolvedSensor(
    string Id,
    SensorCategory Category,
    string Label,
    string TempInputPath,
    string? DeviceName = null);
