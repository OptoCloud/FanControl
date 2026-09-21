namespace FanControl.Core.Sensors;

/// <summary>
/// Declarative description of one sensor (or sensor family) to pull out of hwmon.
/// This is the whitelist: only sensors described here are ever read, so unconnected
/// or meaningless inputs (AUXTIN, floating CPUTIN, etc.) never make it into a reading.
/// </summary>
/// <param name="Id">
/// Stable logical id. For <see cref="AllInstancesOfChip"/> specs this is a prefix:
/// the resolved id becomes "{Id}:{stableName}", where stableName is the drive's WWN for
/// drives, or the hwmon directory name where nothing more stable exists (jc42 DIMMs).
/// </param>
/// <param name="ChipName">Exact match against the hwmon chip's "name" file, e.g. "k10temp", "nct6798", "drivetemp".</param>
/// <param name="Label">
/// When set, matches a specific tempN_label within the chip (e.g. "SYSTIN"). Required unless
/// <see cref="AllInstancesOfChip"/> is set, in which case every hwmon instance of the chip
/// contributes one sensor from its temp1_input.
/// </param>
public sealed record SensorSpec(
    string Id,
    SensorCategory Category,
    string ChipName,
    string? Label = null,
    bool AllInstancesOfChip = false)
{
    public static readonly IReadOnlyList<SensorSpec> DefaultWhitelist =
    [
        new SensorSpec("cpu", SensorCategory.Cpu, "k10temp", Label: "Tctl"),
        new SensorSpec("board", SensorCategory.BoardAmbient, "nct6798", Label: "SYSTIN"),
        new SensorSpec("drive", SensorCategory.Drive, "drivetemp", AllInstancesOfChip: true),
        new SensorSpec("dimm", SensorCategory.Memory, "jc42", AllInstancesOfChip: true),
    ];
}
