using FanControl.Core.Fans;
using Xunit;

namespace FanControl.Core.Tests;

public class FanSafetyGuardTests
{
    private static readonly FanChannel Channel = new("cpu", "/sys/class/hwmon/hwmon3", Index: 1);
    private static readonly FanChannel SecondChannel = new("case", "/sys/class/hwmon/hwmon3", Index: 2);
    private static readonly FanChannel ThirdChannel = new("rear", "/sys/class/hwmon/hwmon3", Index: 3);

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

    [Fact]
    public async Task KickAfterDeadmanFiredReArmsIt()
    {
        var sysFs = new FakeSysFs().AddFile(Channel.EnablePath, "5");
        var controller = new SysfsFanController(sysFs);
        using var guard = new FanSafetyGuard(controller, [Channel], TimeSpan.FromMilliseconds(50));

        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Assert.Equal("5", sysFs.ReadAllText(Channel.EnablePath));

        // The stalled loop recovers: it re-asserts manual mode and kicks, exactly as a normal poll does.
        controller.TakeManualControl(Channel);
        guard.Kick(TimeSpan.FromMilliseconds(50));

        // ...and then stalls a second time. The deadman has to catch that one too.
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Assert.Equal("5", sysFs.ReadAllText(Channel.EnablePath));
    }

    [Fact]
    public async Task DisposeStillReleasesChannelsRetakenAfterTheDeadmanFired()
    {
        var sysFs = new FakeSysFs().AddFile(Channel.EnablePath, "5");
        var controller = new SysfsFanController(sysFs);
        var guard = new FanSafetyGuard(controller, [Channel], TimeSpan.FromMilliseconds(50));

        await Task.Delay(TimeSpan.FromMilliseconds(300));
        controller.TakeManualControl(Channel);

        guard.Dispose();

        Assert.Equal("5", sysFs.ReadAllText(Channel.EnablePath));
    }

    [Fact]
    public void ConstructorFailureReleasesChannelsAlreadyTaken()
    {
        var sysFs = new FakeSysFs()
            .AddFile(Channel.EnablePath, "5")
            .AddFile(SecondChannel.EnablePath, "5");
        var controller = new FailingFanController(new SysfsFanController(sysFs)) { FailTakeFor = SecondChannel.Id };

        Assert.Throws<IOException>(() => new FanSafetyGuard(controller, [Channel, SecondChannel], TimeSpan.FromMinutes(5)));

        Assert.Equal("5", sysFs.ReadAllText(Channel.EnablePath));
    }

    [Fact]
    public void OneChannelFailingToReleaseDoesNotStopTheOthersOrThrow()
    {
        var sysFs = new FakeSysFs()
            .AddFile(Channel.EnablePath, "5")
            .AddFile(SecondChannel.EnablePath, "5")
            .AddFile(ThirdChannel.EnablePath, "5");
        var controller = new FailingFanController(new SysfsFanController(sysFs)) { FailReleaseFor = SecondChannel.Id };
        var guard = new FanSafetyGuard(controller, [Channel, SecondChannel, ThirdChannel], TimeSpan.FromMinutes(5));

        guard.Dispose();

        Assert.Equal("5", sysFs.ReadAllText(Channel.EnablePath));
        Assert.Equal("5", sysFs.ReadAllText(ThirdChannel.EnablePath));
    }

    [Fact]
    public async Task ReleaseFailureInsideTheDeadmanCallbackDoesNotEscape()
    {
        var sysFs = new FakeSysFs()
            .AddFile(Channel.EnablePath, "5")
            .AddFile(SecondChannel.EnablePath, "5");
        var controller = new FailingFanController(new SysfsFanController(sysFs)) { FailReleaseFor = Channel.Id };
        using var guard = new FanSafetyGuard(controller, [Channel, SecondChannel], TimeSpan.FromMilliseconds(50));

        // An exception escaping a timer callback is unhandled and kills the process (and
        // with it this test run), so simply getting past the delay is the assertion.
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        Assert.Equal("5", sysFs.ReadAllText(SecondChannel.EnablePath));
    }

    [Fact]
    public void KickAfterDisposeIsHarmless()
    {
        var sysFs = new FakeSysFs().AddFile(Channel.EnablePath, "5");
        var guard = new FanSafetyGuard(new SysfsFanController(sysFs), [Channel], TimeSpan.FromMinutes(5));

        guard.Dispose();
        guard.Kick(TimeSpan.FromMinutes(5));
    }

    private sealed class FailingFanController(ISysfsFanController inner) : ISysfsFanController
    {
        public string? FailTakeFor { get; init; }
        public string? FailReleaseFor { get; init; }

        public void TakeManualControl(FanChannel channel)
        {
            if (channel.Id == FailTakeFor)
            {
                throw new IOException("simulated sysfs write failure");
            }

            inner.TakeManualControl(channel);
        }

        public void SetDutyPercent(FanChannel channel, int dutyPercent) => inner.SetDutyPercent(channel, dutyPercent);

        public void ReleaseToAuto(FanChannel channel)
        {
            if (channel.Id == FailReleaseFor)
            {
                throw new IOException("simulated sysfs write failure");
            }

            inner.ReleaseToAuto(channel);
        }

        public FanStatus ReadStatus(FanChannel channel) => inner.ReadStatus(channel);
    }
}
