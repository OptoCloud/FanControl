using FanControl.Core.Configuration;
using FanControl.Core.Curves;
using FanControl.Core.Fans;
using FanControl.Core.Sensors;
using FanControl.Core.Status;
using Microsoft.Extensions.Options;

namespace FanControl.Daemon;

/// <summary>
/// Owns the whole control loop lifetime: resolves sensors/fans once at startup (by name,
/// never by cached hwmonN path — see HwmonSensorResolver), then repeatedly reads, evaluates
/// curves, writes duty, and kicks the safety deadman. On stop (including a crash that
/// unwinds through StopAsync), FanSafetyGuard releases every channel back to BIOS control.
/// </summary>
public sealed class ControlLoopService(
    IOptions<FanControlOptions> options,
    HwmonSensorResolver sensorResolver,
    HwmonSensorReader sensorReader,
    ISysfsFanController fanController,
    INvidiaGpuTemperatureProvider gpuProvider,
    IHbaTemperatureProvider hbaProvider,
    StatusSnapshotStore statusStore,
    ILogger<ControlLoopService> logger) : BackgroundService
{
    private readonly FanControlOptions _options = options.Value;
    private readonly CurveEngine _curveEngine = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var resolvedSensors = sensorResolver.Resolve(SensorSpec.DefaultWhitelist);
        LogUnresolvedSensors(resolvedSensors);

        var channels = _options.Channels
            .Select(c => new FanChannel(c.Id, sensorResolver.ResolveChipDirectory(c.ChipName), c.Index, c.MinimumDutyPercent))
            .ToList();

        var curves = _options.Curves
            .Select(c => new FanCurve(
                c.FanChannelId,
                c.SensorIds,
                c.Points.Select(p => new CurvePoint(p.TemperatureCelsius, p.DutyPercent)).ToList(),
                c.HysteresisCelsius))
            .ToList();

        using var guard = new FanSafetyGuard(fanController, channels, _options.DeadmanTimeout);

        logger.LogInformation(
            "Control loop starting: {ChannelCount} fan channel(s), {CurveCount} curve(s), poll every {PollInterval}.",
            channels.Count, curves.Count, _options.PollInterval);

        using var timer = new PeriodicTimer(_options.PollInterval);
        do
        {
            try
            {
                await PollOnceAsync(resolvedSensors, channels, curves, guard, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A bad poll must not kill the loop — that would silently stop the deadman
                // kick and hand every fan back to BIOS control, which is safe but surprising.
                // Logging and continuing keeps control active; the deadman is still the
                // backstop if polls keep failing.
                logger.LogError(ex, "Control loop poll failed; will retry next interval.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PollOnceAsync(
        IReadOnlyList<ResolvedSensor> resolvedSensors,
        IReadOnlyList<FanChannel> channels,
        IReadOnlyList<FanCurve> curves,
        FanSafetyGuard guard,
        CancellationToken cancellationToken)
    {
        var readings = new List<SensorReading>(sensorReader.ReadAll(resolvedSensors));
        readings.Add(await gpuProvider.ReadAsync(cancellationToken));
        readings.Add(await hbaProvider.ReadAsync(cancellationToken));

        foreach (var curve in curves)
        {
            var channel = channels.FirstOrDefault(c => c.Id == curve.FanChannelId);
            if (channel is null)
            {
                logger.LogWarning("Curve references unknown fan channel '{FanChannelId}'; skipping.", curve.FanChannelId);
                continue;
            }

            var dutyPercent = Math.Max(_curveEngine.Evaluate(curve, readings), channel.MinimumDutyPercent);
            fanController.SetDutyPercent(channel, dutyPercent);
        }

        guard.Kick(_options.DeadmanTimeout);

        var fanStatuses = channels.Select(fanController.ReadStatus).ToList();
        statusStore.Update(new StatusSnapshot(DateTimeOffset.UtcNow, readings, fanStatuses, ControlLoopHealthy: true));
    }

    private void LogUnresolvedSensors(IReadOnlyList<ResolvedSensor> resolved)
    {
        var resolvedIds = resolved.Select(r => r.Id).ToHashSet();
        foreach (var spec in SensorSpec.DefaultWhitelist.Where(s => !s.AllInstancesOfChip && !resolvedIds.Contains(s.Id)))
        {
            logger.LogWarning("Sensor '{SensorId}' (chip {ChipName}) could not be resolved on this host.", spec.Id, spec.ChipName);
        }
    }
}
