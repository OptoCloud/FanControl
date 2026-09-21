using FanControl.Core.Drives;

namespace FanControl.Core.Status;

/// <summary>Thread-safe holder for the latest drive health poll: written by DriveHealthService, read by the status snapshot.</summary>
public sealed class DriveHealthStore
{
    private volatile IReadOnlyList<DriveHealthStatus> _latest = [];

    public IReadOnlyList<DriveHealthStatus> Latest => _latest;

    /// <summary>
    /// Replaces the stored results with this poll's. A drive that was unavailable this
    /// poll (asleep, so smartctl -n standby skipped it, or smartctl timed out) keeps its
    /// last good result, with that result's older AsOf, instead of being blanked out.
    /// Drives absent from <paramref name="statuses"/> altogether are dropped.
    /// </summary>
    public void Update(IReadOnlyList<DriveHealthStatus> statuses, DateTimeOffset now)
    {
        var previous = _latest;

        _latest = statuses
            .Select(status => status.IsAvailable
                ? status with { AsOf = now }
                : previous.FirstOrDefault(p => p.DeviceName == status.DeviceName && p.IsAvailable) is { } lastGood
                    ? lastGood with { SourcePath = status.SourcePath }
                    : status)
            .ToList();
    }
}
