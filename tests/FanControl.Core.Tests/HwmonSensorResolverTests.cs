using FanControl.Core.Sensors;
using Xunit;

namespace FanControl.Core.Tests;

public class HwmonSensorResolverTests
{
    [Fact]
    public void ResolvesCpuSensorByChipAndLabel()
    {
        var sysFs = new FakeSysFs()
            .AddFile("/sys/class/hwmon/hwmon0/name", "k10temp")
            .AddFile("/sys/class/hwmon/hwmon0/temp1_label", "Tctl")
            .AddFile("/sys/class/hwmon/hwmon0/temp1_input", "44125");

        var resolver = new HwmonSensorResolver(sysFs);

        var resolved = resolver.Resolve([new SensorSpec("cpu", SensorCategory.Cpu, "k10temp", Label: "Tctl")]);

        var sensor = Assert.Single(resolved);
        Assert.Equal("cpu", sensor.Id);
        Assert.Equal("/sys/class/hwmon/hwmon0/temp1_input", sensor.TempInputPath);
    }

    [Fact]
    public void ResolvesOneSensorPerDrivetempInstanceNamedByBackingBlockDevice()
    {
        var sysFs = new FakeSysFs()
            .AddFile("/sys/class/hwmon/hwmon5/name", "drivetemp")
            .AddFile("/sys/class/hwmon/hwmon5/temp1_input", "31000")
            .AddDirectory("/sys/class/hwmon/hwmon5/device/block/sda")
            .AddFile("/sys/class/hwmon/hwmon6/name", "drivetemp")
            .AddFile("/sys/class/hwmon/hwmon6/temp1_input", "33000")
            .AddDirectory("/sys/class/hwmon/hwmon6/device/block/sdb");

        var resolver = new HwmonSensorResolver(sysFs);

        var resolved = resolver.Resolve([new SensorSpec("drive", SensorCategory.Drive, "drivetemp", AllInstancesOfChip: true)]);

        Assert.Equal(2, resolved.Count);
        Assert.Contains(resolved, r => r.Id == "drive:sda");
        Assert.Contains(resolved, r => r.Id == "drive:sdb");
    }

    [Fact]
    public void IgnoresChipsNotInWhitelist()
    {
        var sysFs = new FakeSysFs()
            .AddFile("/sys/class/hwmon/hwmon2/name", "nct6798")
            .AddFile("/sys/class/hwmon/hwmon2/temp1_label", "AUXTIN0")
            .AddFile("/sys/class/hwmon/hwmon2/temp1_input", "16000")
            .AddFile("/sys/class/hwmon/hwmon2/temp2_label", "SYSTIN")
            .AddFile("/sys/class/hwmon/hwmon2/temp2_input", "32000");

        var resolver = new HwmonSensorResolver(sysFs);

        var resolved = resolver.Resolve([new SensorSpec("board", SensorCategory.BoardAmbient, "nct6798", Label: "SYSTIN")]);

        var sensor = Assert.Single(resolved);
        Assert.Equal("board", sensor.Id);
        Assert.Equal("/sys/class/hwmon/hwmon2/temp2_input", sensor.TempInputPath);
    }

    [Fact]
    public void ReturnsNothingWhenChipIsAbsent()
    {
        var sysFs = new FakeSysFs().AddFile("/sys/class/hwmon/hwmon0/name", "k10temp");

        var resolver = new HwmonSensorResolver(sysFs);

        var resolved = resolver.Resolve([new SensorSpec("board", SensorCategory.BoardAmbient, "nct6798", Label: "SYSTIN")]);

        Assert.Empty(resolved);
    }
}
