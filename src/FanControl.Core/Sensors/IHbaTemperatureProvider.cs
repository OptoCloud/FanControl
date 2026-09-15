namespace FanControl.Core.Sensors;

/// <summary>
/// mpt3sas has no hwmon temperature exposure in mainline (IOC/board temp only reachable
/// via an ioctl to /dev/mpt3ctl, e.g. what lsiutil/storcli do). This is a seam for that:
/// ship <see cref="UnavailableHbaTemperatureProvider"/> until an ioctl- or storcli-backed
/// implementation exists, rather than blocking the rest of the daemon on it.
/// </summary>
public interface IHbaTemperatureProvider
{
    Task<SensorReading> ReadAsync(CancellationToken cancellationToken);
}
