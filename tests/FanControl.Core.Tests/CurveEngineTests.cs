using FanControl.Core.Curves;
using FanControl.Core.Sensors;
using Xunit;

namespace FanControl.Core.Tests;

public class CurveEngineTests
{
    private static readonly FanCurve Curve = new(
        FanChannelId: "cpu",
        SensorIds: ["cpu"],
        Points: [new CurvePoint(30, 20), new CurvePoint(50, 50), new CurvePoint(70, 100)]);

    [Fact]
    public void InterpolatesLinearlyBetweenPoints()
    {
        var engine = new CurveEngine();

        var duty = engine.Evaluate(Curve, [Reading("cpu", 40)]);

        // Halfway between (30,20) and (50,50) -> 35
        Assert.Equal(35, duty);
    }

    [Fact]
    public void ClampsBelowLowestPointToItsDuty()
    {
        var engine = new CurveEngine();

        var duty = engine.Evaluate(Curve, [Reading("cpu", 10)]);

        Assert.Equal(20, duty);
    }

    [Fact]
    public void ClampsAboveHighestPointToItsDuty()
    {
        var engine = new CurveEngine();

        var duty = engine.Evaluate(Curve, [Reading("cpu", 90)]);

        Assert.Equal(100, duty);
    }

    [Fact]
    public void FailsSafeToFullDutyWhenNoDrivingSensorIsAvailable()
    {
        var engine = new CurveEngine();

        var duty = engine.Evaluate(Curve, [new SensorReading("cpu", SensorCategory.Cpu, "Tctl", null, "path")]);

        Assert.Equal(100, duty);
    }

    [Fact]
    public void RisesImmediatelyButHoldsUntilTemperatureDropsPastHysteresis()
    {
        var curve = Curve with { HysteresisCelsius = 5 };
        var engine = new CurveEngine();

        Assert.Equal(35, engine.Evaluate(curve, [Reading("cpu", 40)])); // rise, applied immediately
        Assert.Equal(35, engine.Evaluate(curve, [Reading("cpu", 38)])); // small drop, within hysteresis -> held
        Assert.Equal(26, engine.Evaluate(curve, [Reading("cpu", 34)])); // dropped >= 5C from 40 -> follows curve
    }

    [Fact]
    public void AggregatesMultipleSensorsByMax()
    {
        var curve = Curve with { SensorIds = ["drive:*"] };
        var engine = new CurveEngine();

        var duty = engine.Evaluate(curve,
        [
            Reading("drive:sda", 30),
            Reading("drive:sdb", 50),
        ]);

        Assert.Equal(50, duty);
    }

    private static SensorReading Reading(string id, double celsius) =>
        new(id, SensorCategory.Cpu, id, celsius, "path");
}
