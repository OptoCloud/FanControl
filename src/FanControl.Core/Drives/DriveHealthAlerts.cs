namespace FanControl.Core.Drives;

/// <summary>
/// Decides which drive health changes are worth a log warning. smart_status.passed is a
/// lagging indicator (drives routinely die with it still true), so sector counts are
/// watched as well, but on *change* rather than on level: a drive with a long-standing,
/// stable reallocated count would otherwise produce the same warning every poll forever.
/// </summary>
public static class DriveHealthAlerts
{
    /// <param name="previous">The last good status for the same drive, or null if there is none yet.</param>
    /// <param name="current">This poll's status. Produces nothing if unavailable.</param>
    public static IReadOnlyList<string> Evaluate(DriveHealthStatus? previous, DriveHealthStatus current)
    {
        if (!current.IsAvailable)
        {
            return [];
        }

        var alerts = new List<string>();

        if (current.Passed == false)
        {
            alerts.Add("FAILED its SMART overall-health self-assessment");
        }

        if (current.PendingSectorCount is > 0 and var pending && pending != previous?.PendingSectorCount)
        {
            alerts.Add($"has {pending} pending (unreadable, not yet reallocated) sector(s)");
        }

        if (current.ReallocatedSectorCount is { } reallocated &&
            previous?.ReallocatedSectorCount is { } previousReallocated &&
            reallocated > previousReallocated)
        {
            alerts.Add($"reallocated sector count grew from {previousReallocated} to {reallocated}");
        }

        return alerts;
    }
}
