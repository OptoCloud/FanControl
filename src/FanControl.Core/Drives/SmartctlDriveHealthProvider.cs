using FanControl.Core.Processes;

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
public sealed class SmartctlDriveHealthProvider(string smartctlPath = "smartctl", TimeSpan? timeout = null) : IDriveHealthProvider
{
    // Generous: a healthy drive answers in well under a second, but one in error recovery
    // can take tens of seconds and still come back with usable data.
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(60);

    public async Task<DriveHealthStatus> ReadAsync(string liveDeviceName, string stableId, CancellationToken cancellationToken)
    {
        var devicePath = $"/dev/{liveDeviceName}";

        var result = await ProcessRunner.RunAsync(
            smartctlPath,
            ["-H", "-A", "-j", "-n", "standby", devicePath],
            _timeout,
            cancellationToken);

        // Null: smartctl missing/not executable, or hung past the timeout. Absent, not fatal.
        return result is null
            ? SmartctlJsonParser.Unavailable(stableId, devicePath)
            : SmartctlJsonParser.Parse(stableId, result.StandardOutput, devicePath);
    }
}
