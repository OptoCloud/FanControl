using System.Text.Json.Serialization;

namespace Vigil.Core.Protocol;

// vigild's snapshot: the read-only view of its state, served on its unix socket's /status and
// /events. vigild's Rust types are the contract: DaemonContractTests checks every type here
// against daemon/contract.json, which vigild's tests generate, so neither side can drift
// (docs/STYLE.md §1.3).
//
// Field names are the JSON contract. They come from VigilJson's camelCase policy rather than
// per-property attributes, and the contract test is what proves that policy produces exactly
// the names Rust's `rename_all = "camelCase"` does.

[JsonConverter(typeof(JsonStringEnumConverter<SensorCategory>))]
public enum SensorCategory
{
    // Spelled out rather than left to a naming policy: these strings are the wire format, and
    // "boardAmbient" and "smartFanIII" are not what a general-purpose camelCase rule produces.
    [JsonStringEnumMemberName("cpu")] Cpu,
    [JsonStringEnumMemberName("boardAmbient")] BoardAmbient,
    [JsonStringEnumMemberName("drive")] Drive,
    [JsonStringEnumMemberName("gpu")] Gpu,
    [JsonStringEnumMemberName("memory")] Memory,
    [JsonStringEnumMemberName("hba")] Hba,
}

/// <summary>Values accepted by nct6775/nct6798's <c>pwmN_enable</c> sysfs attribute.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PwmMode>))]
public enum PwmMode
{
    /// <summary>Fans jump to full speed. Never written by vigild.</summary>
    [JsonStringEnumMemberName("disabled")] Disabled = 0,
    [JsonStringEnumMemberName("manual")] Manual = 1,
    [JsonStringEnumMemberName("thermalCruise")] ThermalCruise = 2,
    [JsonStringEnumMemberName("speedCruise")] SpeedCruise = 3,

    /// <summary>NCT6775F only; listed so a read-back of it is not reported as unknown.</summary>
    [JsonStringEnumMemberName("smartFanIII")] SmartFanIII = 4,

    /// <summary>BIOS "Smart Fan IV": the multi-slope curve mode the board ships in.</summary>
    [JsonStringEnumMemberName("smartFanIV")] SmartFanIV = 5,
}

/// <summary>
/// A single point-in-time reading. <see cref="Id"/> is a stable logical name ("cpu",
/// "drive:{wwn}"), never a raw hwmonN path.
/// </summary>
public sealed record SensorReading
{
    public required string Id { get; init; }
    public required SensorCategory Category { get; init; }
    public required string Label { get; init; }
    public required double? CelsiusOrNull { get; init; }
    public required string SourcePath { get; init; }
    public required bool IsAvailable { get; init; }

    /// <summary>
    /// Drives only: the by-path name of the port the drive is plugged into. Null for every other
    /// sensor, and for a drive no by-path link points at. Absent from daemons predating ports,
    /// which is why it is not required.
    /// </summary>
    public string? Port { get; init; }
}

public sealed record FanStatus
{
    public required string Id { get; init; }
    public required byte DutyPercent { get; init; }
    public required uint? Rpm { get; init; }

    /// <summary>Null if <c>pwmN_enable</c> could not be read, or held a value vigild does not know.</summary>
    public required PwmMode? Mode { get; init; }

    /// <summary>Reading 0 RPM for several consecutive polls while being driven.</summary>
    public required bool Stalled { get; init; }
}

/// <summary>
/// <see cref="Passed"/> mirrors smartctl's normalised overall-health flag, which works the same
/// way for ATA and SCSI/SAS drives. Everything else is ATA-attribute-specific and is simply
/// absent for a drive that does not report it (SAS drives, attribute 197 on most SSDs).
/// </summary>
public sealed record DriveHealth
{
    /// <summary>Stable drive identity (WWN), NOT the live sdX letter.</summary>
    public required string DeviceName { get; init; }

    public string? Port { get; init; }
    public required bool? Passed { get; init; }
    public required ulong? ReallocatedSectorCount { get; init; }
    public required ulong? PendingSectorCount { get; init; }
    public required ulong? PowerOnHours { get; init; }

    /// <summary>The live /dev/sdX path smartctl ran against. Not stable, informational only.</summary>
    public required string SourcePath { get; init; }

    /// <summary>
    /// False when the drive was asleep (so deliberately not queried), or smartctl was missing,
    /// timed out, or printed something unusable.
    /// </summary>
    public required bool IsAvailable { get; init; }

    /// <summary>When this poll ran (RFC 3339, UTC). Health is polled far slower than the snapshot.</summary>
    public required string AsOf { get; init; }
}

/// <summary>
/// The full read-only view of vigild's state. This is the contract consumers build on: treat a
/// field rename here as a breaking change.
/// </summary>
public sealed record Snapshot
{
    public required string TimestampUtc { get; init; }
    public required IReadOnlyList<SensorReading> Sensors { get; init; }
    public required IReadOnlyList<FanStatus> Fans { get; init; }
    public required IReadOnlyList<DriveHealth> DriveHealth { get; init; }

    /// <summary>
    /// False when any channel could not be driven this poll, or (judged at read time) when this
    /// snapshot is older than the deadman timeout, i.e. the loop has hung.
    /// </summary>
    public required bool ControlLoopHealthy { get; init; }
}
