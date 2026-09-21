using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FanControl.Core.Fans;

/// <summary>
/// Owns the "never leave fans stuck on manual" guarantee. Manual PWM is sticky in the
/// chip: if the process dies without releasing a channel, it holds its last duty forever,
/// which is a real thermal incident if that duty was low during e.g. a resilver.
///
/// Two independent backstops:
///  - Dispose() releases every channel to auto. Covers normal shutdown and any
///    exception path that unwinds through a using/try-finally. Unconditional: it releases
///    even if the deadman already did, because the control loop re-asserts manual mode
///    every poll and may well have taken the channels back since.
///  - The deadman timer releases everything if <see cref="Kick"/> isn't called often
///    enough. Covers a hung control loop that never reaches the Dispose path while the
///    process is still alive. It is not a one-shot: the next Kick after it fires re-arms
///    it, so a loop that stalls, recovers, and stalls again is protected the second time
///    too.
///
/// Neither covers the process dying outright (SIGKILL, SIGBUS, OOM kill), which leaves no
/// chance to run anything in-process. That case needs a watchdog *external* to this
/// process: see deploy/release-fans.sh, wired up as the systemd unit's ExecStopPost.
///
/// Releasing is always best-effort per channel: one channel's sysfs write failing must
/// neither stop the remaining channels from being released nor escape the timer callback
/// (an unhandled exception there would take the whole process down with every channel
/// still on manual, the exact outcome this class exists to prevent).
/// </summary>
public sealed class FanSafetyGuard : IDisposable
{
    private readonly ISysfsFanController _controller;
    private readonly IReadOnlyList<FanChannel> _channels;
    private readonly ILogger<FanSafetyGuard> _logger;
    private readonly Timer _deadman;
    private readonly Lock _lock = new();
    private bool _disposed;

    public FanSafetyGuard(
        ISysfsFanController controller,
        IReadOnlyList<FanChannel> channels,
        TimeSpan deadmanTimeout,
        ILogger<FanSafetyGuard>? logger = null)
    {
        _controller = controller;
        _channels = channels;
        _logger = logger ?? NullLogger<FanSafetyGuard>.Instance;

        try
        {
            foreach (var channel in _channels)
            {
                _controller.TakeManualControl(channel);
            }
        }
        catch
        {
            // No guard instance will exist for the caller to dispose, so any channel
            // already taken above has to be handed back right here or it never will be.
            ReleaseAll();
            throw;
        }

        _deadman = new Timer(_ => OnDeadmanExpired(), state: null, deadmanTimeout, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Call once per completed control-loop iteration to prove the loop is still alive.</summary>
    public void Kick(TimeSpan deadmanTimeout)
    {
        lock (_lock)
        {
            if (!_disposed)
            {
                _deadman.Change(deadmanTimeout, Timeout.InfiniteTimeSpan);
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _deadman.Dispose();
            ReleaseAll();
        }
    }

    private void OnDeadmanExpired()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _logger.LogCritical("Deadman expired: the control loop has not completed a poll in time. Releasing every fan channel back to automatic control.");
            ReleaseAll();
        }
    }

    private void ReleaseAll()
    {
        foreach (var channel in _channels)
        {
            try
            {
                _controller.ReleaseToAuto(channel);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Failed to release fan channel '{FanChannelId}' back to automatic control; it may be stuck on manual.", channel.Id);
            }
        }
    }
}
