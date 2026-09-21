using System.Collections.Concurrent;
using FanControl.Core.IO;

namespace FanControl.Core.Fans;

public sealed class SysfsFanController(ISysFs sysFs) : ISysfsFanController
{
    // Whatever automatic mode each channel was in before this daemon first touched it, so
    // release hands it back to that rather than assuming Smart Fan IV. Concurrent because
    // FanSafetyGuard's deadman releases from a timer thread.
    private readonly ConcurrentDictionary<string, PwmEnableMode> _originalModes = new();

    public void TakeManualControl(FanChannel channel)
    {
        if (!_originalModes.ContainsKey(channel.EnablePath) && ReadMode(channel) is { } original)
        {
            _originalModes.TryAdd(channel.EnablePath, original);
        }

        sysFs.WriteAllText(channel.EnablePath, ((int)PwmEnableMode.Manual).ToString());
    }

    public void SetDutyPercent(FanChannel channel, int dutyPercent)
    {
        if (dutyPercent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(dutyPercent), dutyPercent, "Duty must be 0-100.");
        }

        var raw = (int)Math.Round(dutyPercent / 100.0 * 255);
        sysFs.WriteAllText(channel.PwmPath, raw.ToString());
    }

    public void ReleaseToAuto(FanChannel channel) =>
        sysFs.WriteAllText(channel.EnablePath, ((int)ReleaseMode(channel)).ToString());

    public FanStatus ReadStatus(FanChannel channel)
    {
        var pwmRaw = sysFs.TryReadAllText(channel.PwmPath);
        var dutyPercent = int.TryParse(pwmRaw, out var pwm) ? (int)Math.Round(pwm / 255.0 * 100) : 0;

        var rpmRaw = sysFs.TryReadAllText(channel.TachPath);
        int? rpm = int.TryParse(rpmRaw, out var parsedRpm) ? parsedRpm : null;

        return new FanStatus(channel.Id, dutyPercent, rpm, ReadMode(channel));
    }

    private PwmEnableMode? ReadMode(FanChannel channel)
    {
        var enableRaw = sysFs.TryReadAllText(channel.EnablePath);
        return int.TryParse(enableRaw, out var enableValue) && Enum.IsDefined(typeof(PwmEnableMode), enableValue)
            ? (PwmEnableMode)enableValue
            : null;
    }

    // Only ever restore a mode where the chip itself regulates the fan. If the channel was
    // found already on Manual (a previous instance died without releasing it) or Disabled,
    // restoring that would recreate the stuck-fan situation release exists to prevent, so
    // fall back to the board's shipping default instead.
    private PwmEnableMode ReleaseMode(FanChannel channel) =>
        _originalModes.TryGetValue(channel.EnablePath, out var original)
        && original is PwmEnableMode.ThermalCruise or PwmEnableMode.SpeedCruise or PwmEnableMode.SmartFanIII or PwmEnableMode.SmartFanIV
            ? original
            : PwmEnableMode.SmartFanIV;
}
