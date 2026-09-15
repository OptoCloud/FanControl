using FanControl.Core.IO;

namespace FanControl.Core.Fans;

public sealed class SysfsFanController(ISysFs sysFs) : ISysfsFanController
{
    public void TakeManualControl(FanChannel channel) =>
        sysFs.WriteAllText(channel.EnablePath, ((int)PwmEnableMode.Manual).ToString());

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
        sysFs.WriteAllText(channel.EnablePath, ((int)PwmEnableMode.SmartFanIV).ToString());

    public FanStatus ReadStatus(FanChannel channel)
    {
        var pwmRaw = sysFs.TryReadAllText(channel.PwmPath);
        var dutyPercent = int.TryParse(pwmRaw, out var pwm) ? (int)Math.Round(pwm / 255.0 * 100) : 0;

        var rpmRaw = sysFs.TryReadAllText(channel.TachPath);
        int? rpm = int.TryParse(rpmRaw, out var parsedRpm) ? parsedRpm : null;

        var enableRaw = sysFs.TryReadAllText(channel.EnablePath);
        var mode = int.TryParse(enableRaw, out var enableValue) && Enum.IsDefined(typeof(PwmEnableMode), enableValue)
            ? (PwmEnableMode)enableValue
            : PwmEnableMode.SmartFanIV;

        return new FanStatus(channel.Id, dutyPercent, rpm, mode);
    }
}
