using System.Text.Json.Serialization;

namespace Vigil.Core.Protocol;

// vigil-core's live stream: the current state on connect, then every change. These types are the
// source of core/contract.json, which CoreContractTests writes and the web checks its mirror
// against (docs/STYLE.md §1.3).

/// <summary>
/// One poll of a UPS's variables (<c>LIST VAR &lt;ups&gt;</c> on upsd). The named fields are the
/// handful vigil charts and alerts on; <see cref="Variables"/> is everything upsd reported,
/// verbatim but for credentials, which <c>NutProtocol</c> masks. Any of them is null when this UPS or driver does not provide it.
/// </summary>
public sealed record UpsReading
{
    public required string TimestampUtc { get; init; }

    /// <summary>The UPS's name on upsd, e.g. "apc".</summary>
    public required string Name { get; init; }

    public required string? Model { get; init; }

    /// <summary>
    /// <c>ups.status</c> split into its flags: OL, OB, LB, HB, RB, CHRG, DISCHRG, BYPASS, CAL,
    /// OFF, OVER, TRIM, BOOST, FSD.
    /// </summary>
    public required IReadOnlyList<string> Status { get; init; }

    /// <summary>Percent.</summary>
    public required double? BatteryCharge { get; init; }

    public required double? BatteryRuntimeSeconds { get; init; }

    /// <summary>Percent of the UPS's capacity.</summary>
    public required double? Load { get; init; }

    /// <summary>Watts, derived from load and <c>ups.realpower.nominal</c> when not reported directly.</summary>
    public required double? RealPower { get; init; }

    public required double? InputVoltage { get; init; }
    public required double? OutputVoltage { get; init; }
    public required double? BatteryVoltage { get; init; }

    /// <summary>°C. Heat is what ages a lead-acid battery fastest.</summary>
    public required double? BatteryTemperature { get; init; }

    /// <summary>
    /// <c>battery.date</c>: when the battery was installed, as the UPS was told. Text, because
    /// drivers format it as they please.
    /// </summary>
    public required string? BatteryDate { get; init; }

    /// <summary>
    /// <c>battery.runtime.low</c>: below this much runtime the UPS is LB, and upsmon starts the
    /// shutdown. With the driver's <c>ignorelb</c> flag NUT judges this itself rather than
    /// trusting the UPS, which is how orion's is set up.
    /// </summary>
    public required double? LowBatteryRuntimeSeconds { get; init; }

    /// <summary><c>battery.charge.low</c>, percent: the other way to LB, whichever comes first.</summary>
    public required double? LowBatteryCharge { get; init; }

    /// <summary><c>input.transfer.reason</c>: why the UPS last went to battery, e.g. "LowInputVoltage".</summary>
    public required string? TransferReason { get; init; }

    public required double? OutputFrequency { get; init; }

    /// <summary>Amps.</summary>
    public required double? OutputCurrent { get; init; }

    /// <summary>
    /// How many monitors (upsmon) are logged in to upsd for this UPS, from <c>GET NUMLOGINS</c>.
    /// Zero means nothing will shut the host down on a power cut. Null when upsd would not say.
    /// </summary>
    public required int? Monitors { get; init; }

    /// <summary>Sorted, as the Rust side uses a BTreeMap: the wire order has to match.</summary>
    public required IReadOnlyDictionary<string, string> Variables { get; init; }
}

/// <summary>
/// The operator's numbers the UPS is judged against: configuration, where the reading cannot
/// say. The shutdown thresholds are not here because upsd publishes them.
/// </summary>
public sealed record UpsLimits
{
    /// <summary>How long the host takes to shut down, <c>HOST_SHUTDOWN_SECONDS</c>. Null when unset.</summary>
    public required double? HostShutdownSeconds { get; init; }

    /// <summary>°C, <c>UPS_BATTERY_TEMPERATURE_WARN</c>: at this, the battery is too hot.</summary>
    public required double BatteryTemperatureWarn { get; init; }
}

public sealed record UpsState
{
    /// <summary>False when NUT is not configured: the UPS section is hidden, not shown as broken.</summary>
    public required bool Enabled { get; init; }

    /// <summary>The UPS's name on upsd, which its history is stored under.</summary>
    public required string Name { get; init; }

    /// <summary>The latest reading, or null while upsd cannot be reached or has nothing fresh.</summary>
    public required UpsReading? Reading { get; init; }

    /// <summary>Why there is no reading: a connection error, or upsd's own (DATA-STALE, ...).</summary>
    public required string? Error { get; init; }

    public required UpsLimits Limits { get; init; }
}

/// <summary>
/// A drive's last GOOD health result. vigild reports only what its latest poll saw, and a
/// sleeping drive is not woken, so this is what survives those gaps.
/// </summary>
public sealed record DriveState
{
    public required string Wwn { get; init; }

    /// <summary>The bay it was in when it last answered.</summary>
    public required string? Port { get; init; }

    public required bool? Passed { get; init; }
    public required ulong? ReallocatedSectorCount { get; init; }
    public required ulong? PendingSectorCount { get; init; }
    public required ulong? PowerOnHours { get; init; }
    public required string SourcePath { get; init; }

    /// <summary>When the drive last actually answered.</summary>
    public required string AsOf { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<Severity>))]
public enum Severity
{
    // Lowercase on the wire, matching Rust's `rename_all = "lowercase"`.
    [JsonStringEnumMemberName("info")] Info,
    [JsonStringEnumMemberName("warning")] Warning,
    [JsonStringEnumMemberName("critical")] Critical,
}

public sealed record EventRecord
{
    public required long Id { get; init; }
    public required string Ts { get; init; }
    public required Severity Severity { get; init; }

    /// <summary>Stable key for the condition, e.g. "fan-stalled:drive-cage".</summary>
    public required string Kind { get; init; }

    public required string Message { get; init; }
}

/// <summary>
/// What vigil-core streams on <c>/live</c>: the current state on connect, then every change.
/// </summary>
/// <remarks>
/// Internally tagged, matching Rust's <c>#[serde(tag = "type")]</c>: the discriminator is a
/// "type" property beside the payload, not a wrapper object. System.Text.Json writes it first,
/// which is what the Rust side produces too.
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SnapshotMessage), "snapshot")]
[JsonDerivedType(typeof(DaemonMessage), "daemon")]
[JsonDerivedType(typeof(EventMessage), "event")]
[JsonDerivedType(typeof(DrivesMessage), "drives")]
[JsonDerivedType(typeof(UpsMessage), "ups")]
public abstract record LiveMessage;

public sealed record SnapshotMessage : LiveMessage
{
    public required Snapshot Snapshot { get; init; }
}

/// <summary>Whether vigil-core can reach vigild at all.</summary>
public sealed record DaemonMessage : LiveMessage
{
    public required bool Connected { get; init; }
}

public sealed record EventMessage : LiveMessage
{
    public required EventRecord Event { get; init; }
}

public sealed record DrivesMessage : LiveMessage
{
    public required IReadOnlyList<DriveState> Drives { get; init; }
}

public sealed record UpsMessage : LiveMessage
{
    public required UpsState Ups { get; init; }
}
