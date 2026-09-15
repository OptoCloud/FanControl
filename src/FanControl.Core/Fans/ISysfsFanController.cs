namespace FanControl.Core.Fans;

public interface ISysfsFanController
{
    /// <summary>Takes manual control of the channel (pwmN_enable=1). Idempotent.</summary>
    void TakeManualControl(FanChannel channel);

    /// <summary>Writes a duty cycle 0-100%. Channel must already be under manual control.</summary>
    void SetDutyPercent(FanChannel channel, int dutyPercent);

    /// <summary>Hands the channel back to the BIOS's Smart Fan IV curve. Always safe to call, including on channels never taken.</summary>
    void ReleaseToAuto(FanChannel channel);

    FanStatus ReadStatus(FanChannel channel);
}
