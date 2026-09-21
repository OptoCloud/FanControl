namespace FanControl.Core.Fans;

/// <summary>
/// Flags a fan as stalled once its tach has read 0 RPM for several consecutive polls while
/// under manual control. Consecutive rather than instantaneous because a fan legitimately
/// reads 0 for a moment while spinning up from rest, e.g. right after the daemon takes over.
/// A missing tach reading (null) is "unknown", never "stalled".
/// </summary>
public sealed class FanStallDetector(int consecutivePollsRequired = 5)
{
    private readonly Dictionary<string, int> _zeroRpmPolls = [];

    public FanStatus Apply(FanStatus status)
    {
        var zeroWhileDriven = status is { Mode: PwmEnableMode.Manual, Rpm: 0, DutyPercent: > 0 };
        var count = zeroWhileDriven ? _zeroRpmPolls.GetValueOrDefault(status.Id) + 1 : 0;
        _zeroRpmPolls[status.Id] = Math.Min(count, consecutivePollsRequired);

        return status with { Stalled = count >= consecutivePollsRequired };
    }
}
