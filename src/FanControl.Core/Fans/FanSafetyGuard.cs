namespace FanControl.Core.Fans;

/// <summary>
/// Owns the "never leave fans stuck on manual" guarantee. Manual PWM is sticky in the
/// chip — if the process dies without releasing a channel, it holds its last duty forever,
/// which is a real thermal incident if that duty was low during e.g. a resilver.
///
/// Two independent backstops:
///  - Dispose() releases every taken channel to auto — covers normal shutdown and any
///    exception path that unwinds through a using/try-finally.
///  - The deadman timer releases everything if <see cref="Kick"/> isn't called often
///    enough — covers a hung control loop that never reaches the Dispose path (e.g.
///    deadlocked, or killed with SIGKILL leaves no chance to run Dispose at all, but a
///    watchdog *external* to this process would be needed for that case; this timer only
///    protects against the loop silently stalling while the process is still alive).
/// </summary>
public sealed class FanSafetyGuard : IDisposable
{
    private readonly ISysfsFanController _controller;
    private readonly IReadOnlyList<FanChannel> _channels;
    private readonly Timer _deadman;
    private readonly Lock _lock = new();
    private bool _released;

    public FanSafetyGuard(ISysfsFanController controller, IReadOnlyList<FanChannel> channels, TimeSpan deadmanTimeout)
    {
        _controller = controller;
        _channels = channels;

        foreach (var channel in _channels)
        {
            _controller.TakeManualControl(channel);
        }

        _deadman = new Timer(_ => ReleaseAll(), state: null, deadmanTimeout, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Call once per successful control-loop iteration to prove the loop is still alive.</summary>
    public void Kick(TimeSpan deadmanTimeout)
    {
        lock (_lock)
        {
            if (!_released)
            {
                _deadman.Change(deadmanTimeout, Timeout.InfiniteTimeSpan);
            }
        }
    }

    public void Dispose()
    {
        _deadman.Dispose();
        ReleaseAll();
    }

    private void ReleaseAll()
    {
        lock (_lock)
        {
            if (_released)
            {
                return;
            }

            foreach (var channel in _channels)
            {
                _controller.ReleaseToAuto(channel);
            }

            _released = true;
        }
    }
}
