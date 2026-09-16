namespace FanControl.Core.Drives;

public interface IDriveHealthProvider
{
    /// <param name="liveDeviceName">Current kernel block device name (e.g. "sdc"), used to actually reach the drive right now (/dev/{liveDeviceName}). Not stable across reboots or a port/slot change.</param>
    /// <param name="stableId">Stable identity (e.g. WWN) to report in the returned status, so a drive's history stays attached to it even if its live device name changes later.</param>
    Task<DriveHealthStatus> ReadAsync(string liveDeviceName, string stableId, CancellationToken cancellationToken);
}
