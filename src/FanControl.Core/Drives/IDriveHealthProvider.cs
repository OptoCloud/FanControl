namespace FanControl.Core.Drives;

public interface IDriveHealthProvider
{
    Task<DriveHealthStatus> ReadAsync(string deviceName, CancellationToken cancellationToken);
}
