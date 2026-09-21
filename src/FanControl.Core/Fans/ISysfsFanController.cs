namespace FanControl.Core.Fans;

public interface ISysfsFanController
{
    /// <summary>Takes manual control of the channel (pwmN_enable=1). Idempotent.</summary>
    void TakeManualControl(FanChannel channel);

    /// <summary>Writes a duty cycle 0-100%. Channel must already be under manual control.</summary>
    void SetDutyPercent(FanChannel channel, int dutyPercent);

    /// <summary>
    /// Hands the channel back to whichever automatic mode it was in before it was first
    /// taken (Smart Fan IV if that isn't known, or wasn't an automatic mode). Always safe
    /// to call, including on channels never taken.
    /// </summary>
    void ReleaseToAuto(FanChannel channel);

    FanStatus ReadStatus(FanChannel channel);
}
