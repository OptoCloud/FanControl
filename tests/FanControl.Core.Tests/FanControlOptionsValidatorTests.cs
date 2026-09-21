using FanControl.Core.Configuration;
using Xunit;

namespace FanControl.Core.Tests;

public class FanControlOptionsValidatorTests
{
    private static FanChannelOptions Channel(string id = "cpu", int index = 1, int minimumDuty = 30) =>
        new() { Id = id, ChipName = "nct6798", Index = index, MinimumDutyPercent = minimumDuty };

    private static FanCurveOptions Curve(
        string channelId = "cpu",
        int failSafe = 100,
        double hysteresis = 3,
        params (double Temperature, int Duty)[] points) =>
        new()
        {
            FanChannelId = channelId,
            SensorIds = ["cpu"],
            Points = (points.Length > 0 ? points : [(30, 30), (70, 100)])
                .Select(p => new CurvePointOptions { TemperatureCelsius = p.Temperature, DutyPercent = p.Duty })
                .ToList(),
            FailSafeDutyPercent = failSafe,
            HysteresisCelsius = hysteresis,
        };

    [Fact]
    public void AcceptsAValidConfig()
    {
        var options = new FanControlOptions { Channels = [Channel()], Curves = [Curve()] };

        Assert.Empty(FanControlOptionsValidator.Validate(options));
    }

    [Fact]
    public void AcceptsAnEmptyConfig()
    {
        Assert.Empty(FanControlOptionsValidator.Validate(new FanControlOptions()));
    }

    [Fact]
    public void RejectsCurveForUnknownChannel()
    {
        var options = new FanControlOptions { Channels = [Channel()], Curves = [Curve(), Curve("typo")] };

        Assert.Contains(FanControlOptionsValidator.Validate(options), e => e.Contains("'typo'"));
    }

    [Fact]
    public void RejectsChannelWithNoCurve()
    {
        var options = new FanControlOptions { Channels = [Channel(), Channel("case", index: 2)], Curves = [Curve()] };

        Assert.Contains(FanControlOptionsValidator.Validate(options), e => e.Contains("'case'") && e.Contains("no curve"));
    }

    [Fact]
    public void RejectsTwoCurvesOnOneChannel()
    {
        var options = new FanControlOptions { Channels = [Channel()], Curves = [Curve(), Curve()] };

        Assert.Contains(FanControlOptionsValidator.Validate(options), e => e.Contains("2 curves"));
    }

    [Fact]
    public void RejectsDuplicateChannelIdsAndDuplicateHeaders()
    {
        var options = new FanControlOptions
        {
            Channels = [Channel("a", index: 1), Channel("a", index: 2), Channel("b", index: 2)],
            Curves = [Curve("a"), Curve("b")],
        };

        var errors = FanControlOptionsValidator.Validate(options);

        Assert.Contains(errors, e => e.Contains("'a' is defined 2 times"));
        Assert.Contains(errors, e => e.Contains("pwm2"));
    }

    [Theory]
    [InlineData(101)]
    [InlineData(-1)]
    public void RejectsOutOfRangeDuties(int duty)
    {
        var options = new FanControlOptions
        {
            Channels = [Channel(minimumDuty: duty)],
            Curves = [Curve(failSafe: duty, points: [(30, 30), (70, duty)])],
        };

        var errors = FanControlOptionsValidator.Validate(options);

        Assert.Contains(errors, e => e.Contains("MinimumDutyPercent"));
        Assert.Contains(errors, e => e.Contains("FailSafeDutyPercent"));
        Assert.Contains(errors, e => e.Contains("every point's DutyPercent"));
    }

    [Fact]
    public void RejectsUnsortedPointsButAllowsAVerticalStep()
    {
        var unsorted = new FanControlOptions { Channels = [Channel()], Curves = [Curve(points: [(60, 70), (30, 30)])] };
        var step = new FanControlOptions { Channels = [Channel()], Curves = [Curve(points: [(30, 30), (50, 40), (50, 80)])] };

        Assert.Contains(FanControlOptionsValidator.Validate(unsorted), e => e.Contains("sorted"));
        Assert.Empty(FanControlOptionsValidator.Validate(step));
    }

    [Fact]
    public void RejectsEmptyPointsAndSensorIds()
    {
        var curve = new FanCurveOptions { FanChannelId = "cpu", SensorIds = [], Points = [] };
        var options = new FanControlOptions { Channels = [Channel()], Curves = [curve] };

        var errors = FanControlOptionsValidator.Validate(options);

        Assert.Contains(errors, e => e.Contains("Points is empty"));
        Assert.Contains(errors, e => e.Contains("SensorIds is empty"));
    }

    [Fact]
    public void RejectsDeadmanTimeoutTooCloseToPollInterval()
    {
        var options = new FanControlOptions { PollInterval = TimeSpan.FromSeconds(10), DeadmanTimeout = TimeSpan.FromSeconds(15) };

        Assert.Contains(FanControlOptionsValidator.Validate(options), e => e.Contains("DeadmanTimeout"));
    }

    [Fact]
    public void RejectsNonPositiveIntervalsAndNegativeHysteresis()
    {
        var options = new FanControlOptions
        {
            PollInterval = TimeSpan.Zero,
            SensorRescanInterval = TimeSpan.Zero,
            Channels = [Channel()],
            Curves = [Curve(hysteresis: -1)],
        };

        var errors = FanControlOptionsValidator.Validate(options);

        Assert.Contains(errors, e => e.Contains("PollInterval"));
        Assert.Contains(errors, e => e.Contains("SensorRescanInterval"));
        Assert.Contains(errors, e => e.Contains("HysteresisCelsius"));
    }

    [Fact]
    public void ReportsEveryProblemAtOnce()
    {
        var options = new FanControlOptions { Channels = [Channel(), Channel("case", index: 2)], Curves = [Curve("typo", failSafe: 500)] };

        Assert.True(FanControlOptionsValidator.Validate(options).Count >= 4);
    }
}
