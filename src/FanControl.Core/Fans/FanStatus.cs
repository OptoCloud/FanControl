namespace FanControl.Core.Fans;

/// <param name="Mode">Null if pwmN_enable couldn't be read or held a value this daemon doesn't know.</param>
/// <param name="Stalled">
/// True once the tach has read 0 RPM for several consecutive polls while under manual
/// control (see FanStallDetector). Duty is always held at or above the channel's
/// MinimumDutyPercent, so a sustained 0 RPM means a dead, jammed or unplugged fan.
/// </param>
public sealed record FanStatus(string Id, int DutyPercent, int? Rpm, PwmEnableMode? Mode, bool Stalled = false);
