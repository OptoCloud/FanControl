using System.Text.Json;

namespace FanControl.Core.Drives;

/// <summary>
/// Pure parsing of smartctl's `-j` JSON output. Kept free of Process/IO so it's
/// unit-testable against literal sample JSON, the same way the HBA ioctl reply parsing
/// is separated from the native call that produces it.
///
/// Schema fields (smart_status.passed, ata_smart_attributes.table[].id/raw.value,
/// power_on_time.hours) verified against smartmontools' own JSON output structure, not
/// guessed. smart_status.passed is smartctl's normalized health flag — populated the same
/// way for both ATA/SATA and SCSI/SAS drives — everything else here is ATA-attribute
/// specific and will simply be absent (not an error) for a SAS drive.
///
/// Absence of "smart_status" entirely (e.g. smartctl run with -n standby against a
/// sleeping drive, which deliberately skips the real query rather than waking it) is
/// treated as "unavailable this poll", not a failure — never crashes on unexpected shape.
/// </summary>
public static class SmartctlJsonParser
{
    private const int AtaAttributeIdReallocatedSectorCount = 5;
    private const int AtaAttributeIdCurrentPendingSectorCount = 197;

    public static DriveHealthStatus Parse(string deviceName, string json, string sourcePath)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            bool? passed = null;
            if (root.TryGetProperty("smart_status", out var smartStatus) &&
                smartStatus.TryGetProperty("passed", out var passedElement) &&
                passedElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                passed = passedElement.GetBoolean();
            }

            ulong? reallocatedSectorCount = null;
            ulong? pendingSectorCount = null;
            if (root.TryGetProperty("ata_smart_attributes", out var ataAttributes) &&
                ataAttributes.TryGetProperty("table", out var table) &&
                table.ValueKind == JsonValueKind.Array)
            {
                foreach (var attribute in table.EnumerateArray())
                {
                    if (!attribute.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.Number)
                    {
                        continue;
                    }

                    if (!attribute.TryGetProperty("raw", out var raw) ||
                        !raw.TryGetProperty("value", out var rawValueElement) ||
                        rawValueElement.ValueKind != JsonValueKind.Number)
                    {
                        continue;
                    }

                    var rawValue = (ulong)rawValueElement.GetInt64();
                    switch (idElement.GetInt32())
                    {
                        case AtaAttributeIdReallocatedSectorCount:
                            reallocatedSectorCount = rawValue;
                            break;
                        case AtaAttributeIdCurrentPendingSectorCount:
                            pendingSectorCount = rawValue;
                            break;
                    }
                }
            }

            ulong? powerOnHours = null;
            if (root.TryGetProperty("power_on_time", out var powerOnTime) &&
                powerOnTime.TryGetProperty("hours", out var hoursElement) &&
                hoursElement.ValueKind == JsonValueKind.Number)
            {
                powerOnHours = (ulong)hoursElement.GetInt64();
            }

            return new DriveHealthStatus(deviceName, passed, reallocatedSectorCount, pendingSectorCount, powerOnHours, sourcePath);
        }
        catch (JsonException)
        {
            return Unavailable(deviceName, sourcePath);
        }
    }

    public static DriveHealthStatus Unavailable(string deviceName, string sourcePath) =>
        new(deviceName, null, null, null, null, sourcePath);
}
