using FanControl.Core.IO;

namespace FanControl.Core.Sensors;

/// <summary>
/// Resolves <see cref="SensorSpec"/> whitelist entries against the live hwmon tree.
/// hwmonN numbering shifts across reboots and depends on module load order, so
/// resolution always goes by chip "name" (and, for drives, the backing block device),
/// never by a cached hwmonN path.
/// </summary>
public sealed class HwmonSensorResolver(ISysFs sysFs, string hwmonRoot = "/sys/class/hwmon")
{
    public IReadOnlyList<ResolvedSensor> Resolve(IEnumerable<SensorSpec> specs)
    {
        var chips = EnumerateChips().ToList();
        var resolved = new List<ResolvedSensor>();

        foreach (var spec in specs)
        {
            var matchingChips = chips.Where(c => c.Name == spec.ChipName).ToList();

            foreach (var chip in matchingChips)
            {
                if (spec.AllInstancesOfChip)
                {
                    resolved.AddRange(ResolveAllInstances(spec, chip));
                }
                else if (spec.Label is not null)
                {
                    if (TryResolveByLabel(spec, chip) is { } sensor)
                    {
                        resolved.Add(sensor);
                    }
                }
            }
        }

        return resolved;
    }

    /// <summary>
    /// Finds the hwmon chip directory for a named chip (e.g. "nct6798"), for callers that
    /// need direct sysfs attribute access (fan control) rather than a temperature reading.
    /// Throws if the chip isn't present — a fan channel with no backing chip is a config
    /// error the daemon should refuse to start with, not silently ignore.
    /// </summary>
    public string ResolveChipDirectory(string chipName) =>
        EnumerateChips().FirstOrDefault(c => c.Name == chipName)?.Path
        ?? throw new InvalidOperationException($"No hwmon chip named '{chipName}' was found.");

    private IEnumerable<ResolvedSensor> ResolveAllInstances(SensorSpec spec, HwmonChip chip)
    {
        // drivetemp/jc42 instances expose a single temp1_input per hwmon directory.
        var inputPath = SysFsPath.Combine(chip.Path, "temp1_input");
        if (!sysFs.FileExists(inputPath))
        {
            yield break;
        }

        var deviceName = ResolveBackingDeviceName(chip) ?? SysFsPath.FileName(chip.Path);
        yield return new ResolvedSensor($"{spec.Id}:{deviceName}", spec.Category, deviceName, inputPath);
    }

    private ResolvedSensor? TryResolveByLabel(SensorSpec spec, HwmonChip chip)
    {
        // hwmon attribute counts are small and not enumerable as files via ISysFs
        // (which only lists directories), so probe temp1.._label..temp32.._label directly.
        for (var index = 1; index <= 32; index++)
        {
            var labelPath = SysFsPath.Combine(chip.Path, $"temp{index}_label");
            var label = sysFs.TryReadAllText(labelPath);
            if (label is null)
            {
                continue;
            }

            if (string.Equals(label, spec.Label, StringComparison.OrdinalIgnoreCase))
            {
                var inputPath = SysFsPath.Combine(chip.Path, $"temp{index}_input");
                return sysFs.FileExists(inputPath)
                    ? new ResolvedSensor(spec.Id, spec.Category, label, inputPath)
                    : null;
            }
        }

        return null;
    }

    private string? ResolveBackingDeviceName(HwmonChip chip)
    {
        var devicePath = SysFsPath.Combine(chip.Path, "device");
        if (!sysFs.DirectoryExists(devicePath))
        {
            return null;
        }

        var blockDir = SysFsPath.Combine(devicePath, "block");
        var blockDevices = sysFs.EnumerateDirectories(blockDir).ToList();
        if (blockDevices.Count > 0)
        {
            return SysFsPath.FileName(blockDevices[0]);
        }

        return null;
    }

    private IEnumerable<HwmonChip> EnumerateChips()
    {
        foreach (var dir in sysFs.EnumerateDirectories(hwmonRoot, "hwmon*"))
        {
            var name = sysFs.TryReadAllText(SysFsPath.Combine(dir, "name"));
            if (name is not null)
            {
                yield return new HwmonChip(dir, name);
            }
        }
    }

    private sealed record HwmonChip(string Path, string Name);
}
