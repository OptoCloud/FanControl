using FanControl.Core.Fans;
using Xunit;

namespace FanControl.Core.Tests;

public class SysfsFanControllerTests
{
    private static readonly FanChannel Channel = new("cpu", "/sys/class/hwmon/hwmon3", Index: 1);

    [Fact]
    public void TakeManualControlWritesEnableModeOne()
    {
        var sysFs = new FakeSysFs().AddFile(Channel.EnablePath, "5");
        var controller = new SysfsFanController(sysFs);

        controller.TakeManualControl(Channel);

        Assert.Equal("1", sysFs.ReadAllText(Channel.EnablePath));
    }

    [Fact]
    public void ReleaseToAutoWritesEnableModeFive()
    {
        var sysFs = new FakeSysFs().AddFile(Channel.EnablePath, "1");
        var controller = new SysfsFanController(sysFs);

        controller.ReleaseToAuto(Channel);

        Assert.Equal("5", sysFs.ReadAllText(Channel.EnablePath));
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(50, "128")]
    [InlineData(100, "255")]
    public void SetDutyPercentConvertsToRawPwmScale(int dutyPercent, string expectedRaw)
    {
        var sysFs = new FakeSysFs().AddFile(Channel.PwmPath, "0");
        var controller = new SysfsFanController(sysFs);

        controller.SetDutyPercent(Channel, dutyPercent);

        Assert.Equal(expectedRaw, sysFs.ReadAllText(Channel.PwmPath));
    }

    [Fact]
    public void SetDutyPercentRejectsOutOfRangeValues()
    {
        var sysFs = new FakeSysFs().AddFile(Channel.PwmPath, "0");
        var controller = new SysfsFanController(sysFs);

        Assert.Throws<ArgumentOutOfRangeException>(() => controller.SetDutyPercent(Channel, 101));
    }

    [Fact]
    public void ReadStatusReportsZeroRpmWithoutMisreportingWhenStalledAtLowDuty()
    {
        var sysFs = new FakeSysFs()
            .AddFile(Channel.PwmPath, "51") // ~20%
            .AddFile(Channel.TachPath, "0")
            .AddFile(Channel.EnablePath, "1");
        var controller = new SysfsFanController(sysFs);

        var status = controller.ReadStatus(Channel);

        Assert.Equal(0, status.Rpm);
        Assert.Equal(20, status.DutyPercent);
        Assert.Equal(PwmEnableMode.Manual, status.Mode);
    }
}
