using Vigil.Core.Clients;
using Xunit;

namespace Vigil.Core.Tests;

/// <summary>
/// Ported from the Rust core's nut.rs tests. The NUT protocol is hand-rolled because nothing
/// maintained exists for it in either language (ADR-015), so these cases are its specification.
/// </summary>
public sealed class NutProtocolTests
{
    private static SortedDictionary<string, string> Variables(params (string Name, string Value)[] entries)
    {
        var lines = entries.Select(entry => $"""VAR apc {entry.Name} "{entry.Value}" """.TrimEnd()).ToList();
        return NutProtocol.ParseVariables(lines);
    }

    [Fact]
    public void ReadsVarLinesAndSkipsTheFraming()
    {
        var variables = NutProtocol.ParseVariables(
        [
            "BEGIN LIST VAR apc",
            """VAR apc battery.charge "100" """.TrimEnd(),
            """VAR apc ups.status "OL CHRG" """.TrimEnd(),
            "END LIST VAR apc",
        ]);

        Assert.Equal(2, variables.Count);
        Assert.Equal("OL CHRG", variables["ups.status"]);
    }

    [Fact]
    public void SkipsLinesItCannotUnderstandRatherThanFailing()
    {
        var variables = NutProtocol.ParseVariables(
        [
            "BEGIN LIST VAR apc",
            "VAR apc missing.quotes 100",
            "VAR apc",
            "not a var line at all",
            """VAR apc battery.charge "100" """.TrimEnd(),
        ]);

        Assert.Equal(["battery.charge"], variables.Keys);
    }

    [Fact]
    public void UnescapesQuotesAndBackslashes()
    {
        // A value may contain both, each escaped with a backslash.
        var variables = NutProtocol.ParseVariables([@"VAR apc device.model ""Smart \""UPS\"" C:\\1000""", @"VAR apc ups.test.result """""]);

        Assert.Equal(@"Smart ""UPS"" C:\1000", variables["device.model"]);
        Assert.Equal(string.Empty, variables["ups.test.result"]);
    }

    [Fact]
    public void ParsesAFullVariableSet()
    {
        var reading = NutProtocol.ToReading("apc", Variables(
            ("battery.charge", "87"),
            ("battery.runtime", "2310"),
            ("ups.load", "25"),
            ("ups.realpower.nominal", "670"),
            ("input.voltage", "231.4"),
            ("ups.status", "OL"),
            ("device.mfr", "American Power Conversion"),
            ("device.model", "Smart-UPS 1000")), "t");

        // The manufacturer is prefixed, because APC reports them separately.
        Assert.Equal("American Power Conversion Smart-UPS 1000", reading.Model);
        Assert.Equal(87, reading.BatteryCharge);
        Assert.Equal(2310, reading.BatteryRuntimeSeconds);
        Assert.Equal(25, reading.Load);
        // Derived: 25% of 670 W.
        Assert.Equal(168, reading.RealPower);
        Assert.Equal(231.4, reading.InputVoltage);
        Assert.Equal(["OL"], reading.Status);
    }

    [Fact]
    public void PrefersRealPowerWhenReportedAndTreatsUnparseableValuesAsAbsent()
    {
        var reading = NutProtocol.ToReading("apc", Variables(
            ("ups.realpower", "150"),
            ("ups.load", "25"),
            ("ups.realpower.nominal", "670"),
            ("input.voltage", "n/a")), "t");

        Assert.Equal(150, reading.RealPower);
        // "n/a" is a value NUT drivers really do send.
        Assert.Null(reading.InputVoltage);
        Assert.Null(reading.Model);
    }

    [Fact]
    public void DoesNotPrefixAModelThatAlreadyNamesItsManufacturer()
    {
        var reading = NutProtocol.ToReading("apc", Variables(("ups.mfr", "Eaton"), ("ups.model", "Eaton 5P 1550i")), "t");

        Assert.Equal("Eaton 5P 1550i", reading.Model);
    }

    [Fact]
    public void RoundsDerivedPowerAwayFromZeroLikeRustDoes()
    {
        // .NET's default is banker's rounding, which would make this 166 rather than 167 and put
        // the two implementations a watt apart. 24.85% of 670 W is 166.495; 24.86% is 166.562.
        var reading = NutProtocol.ToReading("apc", Variables(("ups.load", "24.85"), ("ups.realpower.nominal", "670")), "t");

        Assert.Equal(166, reading.RealPower);

        var halfway = NutProtocol.ToReading("apc", Variables(("ups.load", "25.0"), ("ups.realpower.nominal", "666")), "t");

        // 166.5 exactly: away from zero gives 167, banker's would give 166.
        Assert.Equal(167, halfway.RealPower);
    }

    [Fact]
    public void KeepsVariablesInTheSameOrderTheRustSideDoes()
    {
        // The whole map goes out on the wire, and Rust's BTreeMap is ordinal-sorted.
        var variables = Variables(("ups.status", "OL"), ("battery.charge", "100"), ("Ups.Load", "25"));

        Assert.Equal(["Ups.Load", "battery.charge", "ups.status"], variables.Keys);
    }

    [Fact]
    public void AnEmptyAnswerIsAReadingWithNothingInIt()
    {
        // upsd can answer BEGIN/END with no variables while a driver is starting up.
        var reading = NutProtocol.ToReading("apc", NutProtocol.ParseVariables(["BEGIN LIST VAR apc", "END LIST VAR apc"]), "t");

        Assert.Null(reading.BatteryCharge);
        Assert.Null(reading.Model);
        Assert.Empty(reading.Status);
        Assert.Empty(reading.Variables);
    }
}
