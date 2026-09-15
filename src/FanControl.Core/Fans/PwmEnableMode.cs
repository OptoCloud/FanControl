namespace FanControl.Core.Fans;

/// <summary>Values accepted by nct6775/nct6798's pwmN_enable sysfs attribute.</summary>
public enum PwmEnableMode
{
    /// <summary>Fans jump to full speed. Never written by this daemon.</summary>
    Disabled = 0,
    Manual = 1,
    ThermalCruise = 2,
    SpeedCruise = 3,

    /// <summary>BIOS "Smart Fan IV" — the multi-slope curve mode the board ships in.</summary>
    SmartFanIV = 5,
}
