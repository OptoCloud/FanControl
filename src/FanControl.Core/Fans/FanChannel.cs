using FanControl.Core.IO;

namespace FanControl.Core.Fans;

/// <summary>
/// One physical fan header, addressed by its sysfs pwmN attribute set. The mapping from
/// "pwmN" to a silkscreen header (CPU_FAN1, CHA_FAN2, ...) is board-specific and NOT
/// guessable — it must be verified by walking each channel in manual mode and watching
/// which tach responds, then recorded here in config.
/// </summary>
/// <param name="Id">Logical name chosen after verification, e.g. "cpu", "case-rear".</param>
/// <param name="ChipDirectory">hwmon chip directory containing this pwmN/fanN pair, e.g. /sys/class/hwmon/hwmon3.</param>
/// <param name="Index">The N in pwmN / fanN for this header.</param>
/// <param name="MinimumDutyPercent">
/// Floor below which this fan stalls and its tach reads 0 RPM — below this, a 0 RPM
/// reading means "stalled/off by design", not "dead fan".
/// </param>
public sealed record FanChannel(string Id, string ChipDirectory, int Index, int MinimumDutyPercent = 20)
{
    public string PwmPath => SysFsPath.Combine(ChipDirectory, $"pwm{Index}");
    public string EnablePath => SysFsPath.Combine(ChipDirectory, $"pwm{Index}_enable");
    public string TachPath => SysFsPath.Combine(ChipDirectory, $"fan{Index}_input");
}
