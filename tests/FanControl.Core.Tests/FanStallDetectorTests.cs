using FanControl.Core.Fans;
using Xunit;

namespace FanControl.Core.Tests;

public class FanStallDetectorTests
{
    [Fact]
    public void FlagsStallOnlyAfterEnoughConsecutiveZeroRpmPolls()
    {
        var detector = new FanStallDetector(consecutivePollsRequired: 3);
        var zero = new FanStatus("cpu", 40, 0, PwmEnableMode.Manual);

        Assert.False(detector.Apply(zero).Stalled);
        Assert.False(detector.Apply(zero).Stalled);
        Assert.True(detector.Apply(zero).Stalled);
        Assert.True(detector.Apply(zero).Stalled);
    }

    [Fact]
    public void ASingleSpinningPollResetsTheCount()
    {
        var detector = new FanStallDetector(consecutivePollsRequired: 2);
        var zero = new FanStatus("cpu", 40, 0, PwmEnableMode.Manual);

        detector.Apply(zero);
        Assert.False(detector.Apply(zero with { Rpm = 800 }).Stalled);
        Assert.False(detector.Apply(zero).Stalled);
    }

    [Fact]
    public void TracksChannelsIndependently()
    {
        var detector = new FanStallDetector(consecutivePollsRequired: 2);

        detector.Apply(new FanStatus("a", 40, 0, PwmEnableMode.Manual));
        Assert.False(detector.Apply(new FanStatus("b", 40, 0, PwmEnableMode.Manual)).Stalled);
        Assert.True(detector.Apply(new FanStatus("a", 40, 0, PwmEnableMode.Manual)).Stalled);
    }

    [Theory]
    [InlineData(null, 40, PwmEnableMode.Manual)]     // no tach reading: unknown, not stalled
    [InlineData(0, 0, PwmEnableMode.Manual)]         // commanded off
    [InlineData(0, 40, PwmEnableMode.SmartFanIV)]    // BIOS is driving it and may legitimately stop it
    public void NeverFlagsWhenZeroRpmIsNotEvidenceOfAFault(int? rpm, int dutyPercent, PwmEnableMode mode)
    {
        var detector = new FanStallDetector(consecutivePollsRequired: 1);

        Assert.False(detector.Apply(new FanStatus("cpu", dutyPercent, rpm, mode)).Stalled);
    }
}
