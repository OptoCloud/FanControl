using System.Diagnostics;
using System.Globalization;

namespace FanControl.Core.Sensors;

/// <summary>
/// Shells out to nvidia-smi rather than parsing an hwmon node directly: nvidia's hwmon
/// exposure varies by driver version/packaging, while --query-gpu is a stable, documented
/// interface across driver releases.
/// </summary>
public sealed class NvidiaSmiGpuTemperatureProvider(string nvidiaSmiPath = "nvidia-smi") : INvidiaGpuTemperatureProvider
{
    private const string SensorId = "gpu";

    public async Task<SensorReading> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = nvidiaSmiPath,
                Arguments = "--query-gpu=temperature.gpu --format=csv,noheader",
                RedirectStandardOutput = true,
                UseShellExecute = false,
            });

            if (process is null)
            {
                return Unavailable();
            }

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
            {
                return Unavailable();
            }

            return double.TryParse(output.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var celsius)
                ? new SensorReading(SensorId, SensorCategory.Gpu, "GPU", celsius, nvidiaSmiPath)
                : Unavailable();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            // nvidia-smi missing/not executable (e.g. driver not installed) — report as absent, not fatal.
            return Unavailable();
        }
    }

    private SensorReading Unavailable() => new(SensorId, SensorCategory.Gpu, "GPU", null, nvidiaSmiPath);
}
