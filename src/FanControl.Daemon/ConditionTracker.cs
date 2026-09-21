namespace FanControl.Daemon;

/// <summary>
/// Remembers which named conditions (a channel failing, a fan stalled, ...) are currently
/// active, so a condition that persists is logged when it starts and when it clears rather
/// than on every 2-second poll for as long as it lasts.
/// </summary>
internal sealed class ConditionTracker
{
    private readonly HashSet<string> _active = [];

    /// <summary>Records the condition's current state. True only if that differs from the last recorded state.</summary>
    public bool Changed(string condition, bool active) =>
        active ? _active.Add(condition) : _active.Remove(condition);

    public bool IsActive(string condition) => _active.Contains(condition);
}
