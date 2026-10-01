using Vigil.Core.Alerting;
using Vigil.Core.Protocol;
using Xunit;

namespace Vigil.Core.Tests;

/// <summary>Ported from the Rust core's alerts.rs tests: the tests are the specification.</summary>
public sealed class ConditionDebouncerTests
{
    private static Conditions With(string kind, Severity severity, string message, string cleared) =>
        new() { [kind] = new Condition(severity, message, cleared) };

    [Fact]
    public void RaisesAfterTheThresholdAndClearsAfterAsManyAbsences()
    {
        var debouncer = new ConditionDebouncer(3);
        var present = With("fan-stalled:x", Severity.Critical, "stalled", "spinning");
        var absent = new Conditions();

        Assert.Empty(debouncer.Update(present));
        Assert.Empty(debouncer.Update(present));
        Assert.Equal("stalled", Assert.Single(debouncer.Update(present)).Message);
        Assert.Empty(debouncer.Update(present));

        Assert.Empty(debouncer.Update(absent));
        Assert.Empty(debouncer.Update(absent));
        var cleared = Assert.Single(debouncer.Update(absent));
        Assert.Equal((Severity.Info, "spinning"), (cleared.Severity, cleared.Message));
    }

    [Fact]
    public void ABlipShorterThanTheThresholdNeverRaises()
    {
        var debouncer = new ConditionDebouncer(3);
        var present = With("gpu", Severity.Warning, "w", "c");

        Assert.Empty(debouncer.Update(present));
        Assert.Empty(debouncer.Update(new Conditions()));
        Assert.Empty(debouncer.Update(present));
        Assert.Empty(debouncer.Update(present));
    }

    [Fact]
    public void AReturnWhileClearingResetsTheAbsenceCount()
    {
        var debouncer = new ConditionDebouncer(2);
        var present = With("gpu", Severity.Warning, "w", "c");

        Assert.Empty(debouncer.Update(present));
        Assert.Single(debouncer.Update(present));

        // Absent once, back, absent once: never two absences in a row, so it never clears.
        Assert.Empty(debouncer.Update(new Conditions()));
        Assert.Empty(debouncer.Update(present));
        Assert.Empty(debouncer.Update(new Conditions()));
        Assert.Single(debouncer.Update(new Conditions()));
    }

    [Fact]
    public void AThresholdOfZeroBehavesAsOne()
    {
        Assert.Single(new ConditionDebouncer(0).Update(With("gpu", Severity.Warning, "w", "c")));
    }

    [Fact]
    public void WithAThresholdOfOnePowerEventsRaiseAndClearOnTheFirstPoll()
    {
        var debouncer = new ConditionDebouncer(1);

        var raised = Assert.Single(debouncer.Update(UpsConditions.Of(UpsConditionsTests.Ups(["OB"], null, null), UpsConditionsTests.Fresh)));
        Assert.Equal(Severity.Warning, raised.Severity);

        var cleared = Assert.Single(debouncer.Update(UpsConditions.Of(UpsConditionsTests.Ups(["OL"], null, null), UpsConditionsTests.Fresh)));
        Assert.Equal("Mains power is back: the UPS is online again.", cleared.Message);
    }

    [Fact]
    public void AnIncompleteUpdateNeitherClearsNorResetsWhatItCouldNotSee()
    {
        var debouncer = new ConditionDebouncer(2);
        var gpu = With("gpu", Severity.Warning, "w", "c");
        var fan = With("fan", Severity.Warning, "f", "f-c");

        Assert.Empty(debouncer.Update(gpu));
        Assert.Single(debouncer.Update(gpu));
        Assert.Empty(debouncer.Update(fan));

        // Neither raised "gpu" nor pending "fan" was seen, and neither is gone: unknown is not absent.
        Assert.Empty(debouncer.Update(new Conditions(), isComplete: false));
        Assert.Empty(debouncer.Update(new Conditions(), isComplete: false));

        // Both kept their counts across them: one more sighting raises "fan", and one more real
        // absence is the second "gpu" needs to clear.
        Assert.Equal(["f", "c"], debouncer.Update(fan).Select(newEvent => newEvent.Message));
    }
}
