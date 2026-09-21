using System.Diagnostics;
using System.Globalization;
using FanControl.Core.Processes;

namespace FanControl.Core.Sensors;

/// <summary>
/// Shells out to nvidia-smi rather than parsing an hwmon node directly: nvidia's hwmon
/// exposure varies by driver version/packaging, while --query-gpu is a stable, documented
/// interface across driver releases. Staying out-of-process (rather than binding NVML) is
/// also deliberate: a wedged driver can only hang the child, which gets killed on
/// <paramref name="timeout"/>, never the control loop's own thread.
///
/// Spawning a process is far heavier than a sysfs read, so the result is reused for
/// <paramref name="minimumReadInterval"/> instead of being re-queried on every control
/// loop poll. Not thread-safe: expects the control loop's one-poll-at-a-time calls.
/// </summary>
public sealed class NvidiaSmiGpuTemperatureProvider(
    string nvidiaSmiPath = "nvidia-smi",
    TimeSpan? minimumReadInterval = null,
    TimeSpan? timeout = null) : INvidiaGpuTemperatureProvider
{
    private const string SensorId = "gpu";

    private readonly TimeSpan _minimumReadInterval = minimumReadInterval ?? TimeSpan.Zero;
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(5);

    private SensorReading? _lastReading;
    private long _lastReadTimestamp;

    public async Task<SensorReading> ReadAsync(CancellationToken cancellationToken)
    {
        if (_lastReading is not null && Stopwatch.GetElapsedTime(_lastReadTimestamp) < _minimumReadInterval)
        {
            return _lastReading;
        }

        _lastReading = await ReadFreshAsync(cancellationToken);
        _lastReadTimestamp = Stopwatch.GetTimestamp();
        return _lastReading;
    }

    private async Task<SensorReading> ReadFreshAsync(CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(
            nvidiaSmiPath,
            ["--query-gpu=temperature.gpu", "--format=csv,noheader"],
            _timeout,
            cancellationToken);

        // Null covers nvidia-smi missing/not executable (e.g. driver not installed) and a
        // hang past the timeout alike: report as absent, not fatal.
        if (result is not { ExitCode: 0 })
        {
            return Unavailable();
        }

        return double.TryParse(result.StandardOutput.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var celsius)
            ? new SensorReading(SensorId, SensorCategory.Gpu, "GPU", celsius, nvidiaSmiPath)
            : Unavailable();
    }

    private SensorReading Unavailable() => new(SensorId, SensorCategory.Gpu, "GPU", null, nvidiaSmiPath);
}
