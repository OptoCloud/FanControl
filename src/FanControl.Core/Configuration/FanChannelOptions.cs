namespace FanControl.Core.Configuration;

/// <summary>
/// Config-file shape for one fan header. ChipName + Index are resolved against the live
/// hwmon tree at startup (see HwmonSensorResolver.ResolveChipDirectory) rather than storing
/// a hwmonN path, for the same reason sensors are resolved by name: numbering isn't stable.
/// </summary>
public sealed class FanChannelOptions
{
    public required string Id { get; init; }
    public required string ChipName { get; init; }
    public required int Index { get; init; }
    public int MinimumDutyPercent { get; init; } = 20;
}
