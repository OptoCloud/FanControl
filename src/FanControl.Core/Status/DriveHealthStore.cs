using FanControl.Core.Drives;

namespace FanControl.Core.Status;

/// <summary>Thread-safe holder for the latest drive health poll: written by DriveHealthService, read by the status snapshot.</summary>
public sealed class DriveHealthStore
{
    private volatile IReadOnlyList<DriveHealthStatus> _latest = [];

    public IReadOnlyList<DriveHealthStatus> Latest => _latest;

    public void Update(IReadOnlyList<DriveHealthStatus> statuses) => _latest = statuses;
}
