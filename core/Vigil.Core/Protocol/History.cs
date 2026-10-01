using System.Text.Json.Serialization;

namespace Vigil.Core.Protocol;

// What the dashboard adds on top of vigild's snapshot. Not yet in core/contract.json, which
// began as the live stream's half of the old Rust-generated file; until it moves there,
// HistoryQueriesTests pins every property name against what web/src/lib/types.ts declares.

/// <summary>How far back a chart goes, and at what resolution.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RangeKey>))]
public enum RangeKey
{
    [JsonStringEnumMemberName("1h")] OneHour,
    [JsonStringEnumMemberName("6h")] SixHours,
    [JsonStringEnumMemberName("24h")] OneDay,
    [JsonStringEnumMemberName("7d")] SevenDays,
    [JsonStringEnumMemberName("30d")] ThirtyDays,
    [JsonStringEnumMemberName("1y")] OneYear,
}

/// <summary>
/// One series of <c>[epochMillis, value]</c> points. An array pair rather than an object per
/// point: at a few hundred points across a dozen series the difference in payload is real, and
/// this is the shape the chart already reads.
/// </summary>
public sealed record HistoryResponse
{
    public required RangeKey Range { get; init; }

    /// <summary>Epoch milliseconds.</summary>
    public required long From { get; init; }

    public required long To { get; init; }
    public required int BucketSeconds { get; init; }

    /// <summary>
    /// Keyed by series id: a sensor id ("drive:&lt;wwn&gt;" follows a disk), a bay
    /// ("port:&lt;by-path&gt;" follows a location, whichever disk is in it), or "drives:max" /
    /// "memory:max" for the hottest of a group.
    /// </summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<double[]>> Temperatures { get; init; }

    /// <summary>Keyed by fan id.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<double[]>> Duties { get; init; }

    public required IReadOnlyDictionary<string, IReadOnlyList<double[]>> Rpms { get; init; }

    /// <summary>
    /// "charge" and "load" (%), "runtime" (minutes), "inputVoltage" (V). Empty without NUT.
    /// </summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<double[]>> Ups { get; init; }
}
