using System.Globalization;
using Vigil.Core.Protocol;

namespace Vigil.Core.Clients;

/// <summary>
/// The pure half of the NUT client: parsing a <c>LIST VAR</c> answer and turning it into a
/// reading. No I/O, so all of it is unit-tested, as the Rust version is.
/// </summary>
public static class NutProtocol
{
    /// <summary>
    /// The variables in a <c>LIST VAR</c> answer: lines of <c>VAR &lt;ups&gt; &lt;name&gt;
    /// "&lt;value&gt;"</c>, where the value escapes <c>"</c> and <c>\</c> with a backslash.
    /// BEGIN/END framing and anything unexpected are skipped.
    /// </summary>
    /// <remarks>
    /// Ordinal-sorted to match the Rust side's <c>BTreeMap</c>: the whole map goes out on the
    /// wire as <c>variables</c>, so its order is part of the contract.
    /// </remarks>
    public static SortedDictionary<string, string> ParseVariables(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var variables = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (!line.StartsWith("VAR ", StringComparison.Ordinal))
            {
                continue;
            }

            // VAR <ups> <name> "<value>" — the value may contain spaces, so only split three ways.
            var parts = line["VAR ".Length..].Split(' ', 3);
            if (parts.Length < 3)
            {
                continue;
            }

            var quoted = parts[2];
            if (quoted.Length < 2 || quoted[0] != '"' || quoted[^1] != '"')
            {
                continue;
            }

            variables[parts[1]] = Unescape(quoted[1..^1]);
        }

        return variables;
    }

    /// <summary>A backslash escapes the character after it; a trailing one is itself.</summary>
    private static string Unescape(string inner)
    {
        if (!inner.Contains('\\', StringComparison.Ordinal))
        {
            return inner;
        }

        var value = new System.Text.StringBuilder(inner.Length);
        for (var i = 0; i < inner.Length; i++)
        {
            if (inner[i] == '\\' && i + 1 < inner.Length)
            {
                value.Append(inner[++i]);
            }
            else
            {
                value.Append(inner[i]);
            }
        }

        return value.ToString();
    }

    public static UpsReading ToReading(string name, SortedDictionary<string, string> variables, string at)
    {
        ArgumentNullException.ThrowIfNull(variables);

        var load = Number(variables, "ups.load");

        // Not every UPS reports watts. When it does not, derive them the way NUT's own tools do:
        // load is a percentage of the nominal real power.
        var realPower = Number(variables, "ups.realpower");
        if (realPower is null && load is not null && Number(variables, "ups.realpower.nominal") is { } nominal)
        {
            // AwayFromZero, not .NET's default banker's rounding, so this matches Rust's
            // f64::round: 166.5 is 167 here, and would be 166 under the default.
            realPower = Math.Round(load.Value / 100 * nominal, MidpointRounding.AwayFromZero);
        }

        return new UpsReading
        {
            TimestampUtc = at,
            Name = name,
            Model = Model(variables),
            Status = variables.TryGetValue("ups.status", out var status)
                ? status.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [],
            BatteryCharge = Number(variables, "battery.charge"),
            BatteryRuntimeSeconds = Number(variables, "battery.runtime"),
            Load = load,
            RealPower = realPower,
            InputVoltage = Number(variables, "input.voltage"),
            OutputVoltage = Number(variables, "output.voltage"),
            BatteryVoltage = Number(variables, "battery.voltage"),
            Variables = variables,
        };
    }

    /// <summary>
    /// The model, prefixed with the manufacturer unless it already starts with it: APC reports
    /// "Smart-UPS 1000" with mfr "American Power Conversion", while others repeat themselves.
    /// </summary>
    private static string? Model(SortedDictionary<string, string> variables)
    {
        var model = Lookup(variables, "device.model") ?? Lookup(variables, "ups.model");
        if (model is null)
        {
            return null;
        }

        var manufacturer = Lookup(variables, "device.mfr") ?? Lookup(variables, "ups.mfr");
        return manufacturer is not null && !model.StartsWith(manufacturer, StringComparison.Ordinal)
            ? $"{manufacturer} {model}".Trim()
            : model.Trim();
    }

    private static string? Lookup(SortedDictionary<string, string> variables, string key) =>
        variables.TryGetValue(key, out var value) ? value : null;

    /// <summary>
    /// A numeric variable, or null when the UPS does not report it or reports something that is
    /// not a finite number — "n/a" is a value NUT drivers really do send.
    /// </summary>
    private static double? Number(SortedDictionary<string, string> variables, string key)
    {
        if (!variables.TryGetValue(key, out var raw))
        {
            return null;
        }

        return double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? value
            : null;
    }
}
