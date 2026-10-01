using Vigil.Core.Protocol;

namespace Vigil.Core.Alerting;

/// <summary>
/// Debounces conditions into raise/clear events. A condition has to hold for <c>threshold</c>
/// consecutive updates before it is raised, and be absent for as many before it clears, so a
/// single odd poll (nvidia-smi timing out once) never produces a notification.
/// </summary>
/// <remarks>
/// Not thread-safe, deliberately: it has one owner, the loop that feeds it updates in the
/// order they happened.
/// </remarks>
public sealed class ConditionDebouncer(uint threshold)
{
    private readonly uint _threshold = Math.Max(threshold, 1);

    /// <summary>How many consecutive updates each not-yet-raised condition has held for.</summary>
    private readonly SortedDictionary<string, uint> _pending = new(StringComparer.Ordinal);

    /// <summary>Raised conditions, with how many consecutive updates each has been absent for.</summary>
    private readonly SortedDictionary<string, (Condition Condition, uint AbsentFor)> _raised = new(StringComparer.Ordinal);

    /// <summary>Feeds one update's conditions in; returns what was raised, then what cleared.</summary>
    public List<NewEvent> Update(Conditions conditions)
    {
        var events = new List<NewEvent>();

        foreach (var (kind, condition) in conditions)
        {
            if (_raised.TryGetValue(kind, out var held))
            {
                _raised[kind] = held with { AbsentFor = 0 };
                continue;
            }

            var seen = _pending.GetValueOrDefault(kind) + 1;
            if (seen >= _threshold)
            {
                _pending.Remove(kind);
                _raised[kind] = (condition, 0);
                events.Add(new NewEvent(condition.Severity, kind, condition.Message));
            }
            else
            {
                _pending[kind] = seen;
            }
        }

        // A condition must hold for consecutive updates; one gap starts the count again.
        foreach (var kind in _pending.Keys.Where(kind => !conditions.ContainsKey(kind)).ToList())
        {
            _pending.Remove(kind);
        }

        foreach (var (kind, (condition, absentFor)) in _raised.Where(entry => !conditions.ContainsKey(entry.Key)).ToList())
        {
            var absent = absentFor + 1;
            if (absent >= _threshold)
            {
                _raised.Remove(kind);
                events.Add(new NewEvent(Severity.Info, kind, condition.Cleared));
            }
            else
            {
                _raised[kind] = (condition, absent);
            }
        }

        return events;
    }
}
