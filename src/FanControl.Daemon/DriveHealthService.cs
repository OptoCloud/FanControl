using FanControl.Core.Configuration;
using FanControl.Core.Drives;
using FanControl.Core.Sensors;
using FanControl.Core.Status;
using Microsoft.Extensions.Options;

namespace FanControl.Daemon;

/// <summary>
/// Polls SMART health for every drive on its own slow interval (default 15 minutes),
/// completely decoupled from ControlLoopService's 2-second poll — SMART health barely
/// changes and running smartctl against every drive every 2s would be pure waste. Drive
/// device names are resolved the same way ControlLoopService resolves "drive:*" sensors
/// (by drivetemp chip name, not a cached path), so both stay in sync with whatever drives
/// are actually present.
/// </summary>
public sealed class DriveHealthService(
    IOptions<FanControlOptions> options,
    HwmonSensorResolver sensorResolver,
    IDriveHealthProvider healthProvider,
    DriveHealthStore store,
    ILogger<DriveHealthService> logger) : BackgroundService
{
    private static readonly SensorSpec DriveSpec = new("drive", SensorCategory.Drive, "drivetemp", AllInstancesOfChip: true);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var driveHealthOptions = options.Value.DriveHealth;
        if (!driveHealthOptions.Enabled)
        {
            logger.LogInformation("Drive health polling disabled via config.");
            return;
        }

        logger.LogInformation("Drive health polling every {PollInterval}.", driveHealthOptions.PollInterval);

        using var timer = new PeriodicTimer(driveHealthOptions.PollInterval);
        do
        {
            try
            {
                await PollOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Drive health poll failed; will retry next interval.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        var deviceNames = sensorResolver.Resolve([DriveSpec]).Select(s => s.Label).Distinct().ToList();

        var statuses = await Task.WhenAll(
            deviceNames.Select(name => healthProvider.ReadAsync(name, cancellationToken)));

        store.Update(statuses);

        foreach (var status in statuses.Where(s => s.Passed == false))
        {
            logger.LogWarning("Drive {DeviceName} FAILED its SMART overall-health self-assessment.", status.DeviceName);
        }
    }
}
