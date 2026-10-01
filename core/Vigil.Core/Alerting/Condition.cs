using Vigil.Core.Protocol;

namespace Vigil.Core.Alerting;

// Turns vigild's snapshots and the UPS's readings into discrete events ("drive-cage stalled",
// "reallocated sectors grew", "on battery"). vigild deliberately reports only the present;
// noticing that something CHANGED needs memory, and that lives here. Everything in this
// namespace is pure, so it is fully unit-tested; the runtime wires it to the database and
// notifications.
//
// Ported from the Rust core's alerts.rs, split along the four jobs that file held (docs/STYLE.md §2.2):
// snapshot conditions, UPS conditions, the debouncer, and the drive health diff.

/// <summary>An event to record and announce: a condition raised or cleared, or a drive change.</summary>
public sealed record NewEvent(Severity Severity, string Kind, string Message);

/// <summary>A problem that holds right now.</summary>
/// <param name="Cleared">What to log when it goes away.</param>
public sealed record Condition(Severity Severity, string Message, string Cleared);

/// <summary>
/// Every condition visible at once, keyed by a stable id for the condition.
/// </summary>
/// <remarks>
/// Ordinal-sorted, as the Rust side's <c>BTreeMap</c> is: the debouncer raises and clears in
/// key order, so the order events reach the log and ntfy is the same in both implementations.
/// </remarks>
public sealed class Conditions : SortedDictionary<string, Condition>
{
    public Conditions()
        : base(StringComparer.Ordinal)
    {
    }
}
