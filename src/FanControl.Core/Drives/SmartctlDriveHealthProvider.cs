using System.ComponentModel;
using System.Diagnostics;

namespace FanControl.Core.Drives;

/// <summary>
/// Shells out to smartctl per drive, the same pattern as the GPU provider shelling out
/// to nvidia-smi: an existing, well-tested tool does the actual device I/O, we just parse
/// its JSON.
///
/// Always passes `-n standby`: this makes smartctl skip (not perform) the SMART query and
/// return immediately if the drive is already asleep, rather than spinning it up just to
/// answer a health check — the whole point of drivetemp/SMART polling being on a slow,
/// separate cadence from the fan control loop is to never be the thing that defeats a
/// drive spindown policy.
///
/// smartctl's process exit code is a bitmask with several unrelated meanings (parse
/// errors, prefailure attributes, self-test failures, ...) and is deliberately not used
/// as a success/failure signal here — whether we got usable data is determined entirely
/// by whether the JSON contains "smart_status", not by the exit code.
/// </summary>
public sealed class SmartctlDriveHealthProvider(string smartctlPath = "smartctl") : IDriveHealthProvider
{
    public async Task<DriveHealthStatus> ReadAsync(string deviceName, CancellationToken cancellationToken)
    {
        var devicePath = $"/dev/{deviceName}";

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = smartctlPath,
                Arguments = $"-H -A -j -n standby {devicePath}",
                RedirectStandardOutput = true,
                UseShellExecute = false,
            });

            if (process is null)
            {
                return SmartctlJsonParser.Unavailable(deviceName, devicePath);
            }

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            return SmartctlJsonParser.Parse(deviceName, output, devicePath);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            // smartctl missing/not executable — report as absent, not fatal.
            return SmartctlJsonParser.Unavailable(deviceName, devicePath);
        }
    }
}
