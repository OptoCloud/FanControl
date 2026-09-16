namespace FanControl.Core.Drives;

/// <summary>
/// SMART health summary for one drive. <see cref="Passed"/> mirrors smartctl's normalized
/// "SMART overall-health self-assessment" flag, which works the same way across both ATA
/// and SCSI/SAS drives — everything else is ATA-attribute-specific and will be null for
/// SAS drives (behind the LSI HBA) that don't report them.
/// </summary>
/// <param name="DeviceName">
/// Stable drive identity (WWN), NOT the live sdX letter — this is what a dashboard should
/// key history/graphs on, since it survives reboots and even a physical port/slot change.
/// See <see cref="SourcePath"/> for the live /dev path this particular read actually used.
/// </param>
/// <param name="SourcePath">The live /dev/sdX path smartctl was actually run against for this read — not stable, informational only.</param>
public sealed record DriveHealthStatus(
    string DeviceName,
    bool? Passed,
    ulong? ReallocatedSectorCount,
    ulong? PendingSectorCount,
    ulong? PowerOnHours,
    string SourcePath)
{
    public bool IsAvailable => Passed.HasValue;
}
