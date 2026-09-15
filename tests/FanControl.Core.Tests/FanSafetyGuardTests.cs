using FanControl.Core.Fans;
using Xunit;

namespace FanControl.Core.Tests;

public class FanSafetyGuardTests
{
    private static readonly FanChannel Channel = new("cpu", "/sys/class/hwmon/hwmon3", Index: 1);

    [Fact]
    public void TakesManualControlOfAllChannelsOnConstruction()
    {
        var sysFs = new FakeSysFs().AddFile(Channel.EnablePath, "5");
        var controller = new SysfsFanController(sysFs);

        using var guard = new FanSafetyGuard(controller, [Channel], TimeSpan.FromMinutes(5));

        Assert.Equal("1", sysFs.ReadAllText(Channel.EnablePath));
    }

    [Fact]
    public void DisposeReleasesAllChannelsToAuto()
    {
        var sysFs = new FakeSysFs().AddFile(Channel.EnablePath, "5");
        var controller = new SysfsFanController(sysFs);
        var guard = new FanSafetyGuard(controller, [Channel], TimeSpan.FromMinutes(5));

        guard.Dispose();

        Assert.Equal("5", sysFs.ReadAllText(Channel.EnablePath));
    }

    [Fact]
    public async Task DeadmanTimerReleasesChannelsWhenNotKicked()
    {
        var sysFs = new FakeSysFs().AddFile(Channel.EnablePath, "5");
        var controller = new SysfsFanController(sysFs);
        using var guard = new FanSafetyGuard(controller, [Channel], TimeSpan.FromMilliseconds(50));

        Assert.Equal("1", sysFs.ReadAllText(Channel.EnablePath));

        await Task.Delay(TimeSpan.FromMilliseconds(300));

        Assert.Equal("5", sysFs.ReadAllText(Channel.EnablePath));
    }

    [Fact]
    public async Task KickPostponesTheDeadman()
    {
        var sysFs = new FakeSysFs().AddFile(Channel.EnablePath, "5");
        var controller = new SysfsFanController(sysFs);
        using var guard = new FanSafetyGuard(controller, [Channel], TimeSpan.FromMilliseconds(150));

        for (var i = 0; i < 5; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50));
            guard.Kick(TimeSpan.FromMilliseconds(150));
        }

        Assert.Equal("1", sysFs.ReadAllText(Channel.EnablePath));
    }
}
