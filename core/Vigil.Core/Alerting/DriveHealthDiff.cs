using Vigil.Core.Protocol;

namespace Vigil.Core.Alerting;

/// <summary>What one drive's health poll means against its last good state.</summary>
/// <param name="State">The drive's new last-good state, or null if this poll had nothing usable (asleep, smartctl failed).</param>
/// <param name="Changed">True if anything other than power-on hours moved, i.e. worth a history row.</param>
public sealed record DriveUpdate(DriveState? State, bool Changed, IReadOnlyList<NewEvent> Events);

public static class DriveHealthDiff
{
    /// <summary>
    /// Compares one drive's fresh health poll with its last good state.
    /// </summary>
    /// <remarks>
    /// <c>smart_status.passed</c> is a lagging indicator (drives routinely die with it still
    /// true), so sector counts are watched too, but on CHANGE rather than level: a drive with a
    /// long-standing, stable reallocated count would otherwise alert forever.
    /// </remarks>
    public static DriveUpdate Diff(DriveState? previous, DriveHealth current)
    {
        if (!current.IsAvailable)
        {
            return new DriveUpdate(null, false, []);
        }

        var state = new DriveState
        {
            Wwn = current.DeviceName,
            Port = current.Port,
            Passed = current.Passed,
            ReallocatedSectorCount = current.ReallocatedSectorCount,
            PendingSectorCount = current.PendingSectorCount,
            PowerOnHours = current.PowerOnHours,
            SourcePath = current.SourcePath,
            AsOf = current.AsOf,
        };

        var events = new List<NewEvent>();
        var name = $"Drive {current.DeviceName} ({current.SourcePath})";
        NewEvent Event(Severity severity, string kind, string message) => new(severity, $"{kind}:{state.Wwn}", message);

        var previousPassed = previous?.Passed;
        if (current.Passed == false && previousPassed != false)
        {
            events.Add(Event(Severity.Critical, "drive-failed", $"{name} FAILED its SMART overall-health self-assessment."));
        }
        else if (current.Passed == true && previousPassed == false)
        {
            events.Add(Event(Severity.Info, "drive-failed", $"{name} passes its SMART self-assessment again."));
        }

        // Growth only, and only against a known previous count: a first sighting of a drive
        // with old reallocations is history, not news.
        if (current.ReallocatedSectorCount is { } reallocated
            && previous?.ReallocatedSectorCount is { } previousReallocated
            && reallocated > previousReallocated)
        {
            events.Add(Event(
                Severity.Warning,
                "drive-reallocated",
                $"{name}: reallocated sector count grew from {previousReallocated} to {reallocated}."));
        }

        // Pending sectors are different: any nonzero count is news, because they either get
        // reallocated or read back, and both are worth knowing.
        var previousPending = previous?.PendingSectorCount ?? 0;
        if (current.PendingSectorCount is { } pending && pending != previousPending)
        {
            events.Add(pending > 0
                ? Event(
                    Severity.Warning,
                    "drive-pending",
                    $"{name}: {pending} pending (unreadable, not yet reallocated) sector(s), was {previousPending}.")
                : Event(Severity.Info, "drive-pending", $"{name}: no pending sectors any more (was {previousPending})."));
        }

        var changed = previous is null
            || previous.Passed != state.Passed
            || previous.ReallocatedSectorCount != state.ReallocatedSectorCount
            || previous.PendingSectorCount != state.PendingSectorCount;

        return new DriveUpdate(state, changed, events);
    }
}
