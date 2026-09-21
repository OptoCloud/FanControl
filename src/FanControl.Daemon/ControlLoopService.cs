using System.Diagnostics;
using FanControl.Core.Configuration;
using FanControl.Core.Curves;
using FanControl.Core.Fans;
using FanControl.Core.Sensors;
using FanControl.Core.Status;
using Microsoft.Extensions.Options;

namespace FanControl.Daemon;

/// <summary>
/// Owns the whole control loop lifetime: resolves fans once at startup and sensors on a
/// periodic re-scan (both by name, never by cached hwmonN path, see HwmonSensorResolver),
/// then repeatedly reads, evaluates curves, writes duty, and kicks the safety deadman. On
/// stop (including a crash that unwinds through StopAsync), FanSafetyGuard releases every
/// channel back to automatic control.
/// </summary>
public sealed class ControlLoopService(
    IOptions<FanControlOptions> options,
    HwmonSensorResolver sensorResolver,
    HwmonSensorReader sensorReader,
    ISysfsFanController fanController,
    INvidiaGpuTemperatureProvider gpuProvider,
    IHbaTemperatureProvider hbaProvider,
    StatusSnapshotStore statusStore,
    DriveHealthStore driveHealthStore,
    ILogger<ControlLoopService> logger,
    ILogger<FanSafetyGuard> guardLogger) : BackgroundService
{
    private const string PollFailed = "poll-failed";

    private readonly FanControlOptions _options = options.Value;
    private readonly CurveEngine _curveEngine = new();
    private readonly FanStallDetector _stallDetector = new();
    private readonly ConditionTracker _conditions = new();

    private IReadOnlyList<ResolvedSensor>? _sensors;
    private long _sensorsScannedAt;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The host reacts to this by shutting down cleanly, which on its own exits 0:
            // to systemd that is a success, and Restart=on-failure would never relaunch.
            // The typical cause (nct6775 not loaded yet at boot) is exactly the kind of
            // thing a restart a few seconds later fixes.
            Environment.ExitCode = 1;
            throw;
        }
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        var channels = _options.Channels
            .Select(c => new FanChannel(c.Id, sensorResolver.ResolveChipDirectory(c.ChipName), c.Index, c.MinimumDutyPercent))
            .ToList();

        // Every curve has a channel and every channel exactly one curve: enforced at
        // startup by FanControlOptionsValidator.
        var controlled = _options.Curves
            .Select(c => new ControlledFan(
                channels.Single(channel => channel.Id == c.FanChannelId),
                new FanCurve(
                    c.FanChannelId,
                    c.SensorIds,
                    c.Points.Select(p => new CurvePoint(p.TemperatureCelsius, p.DutyPercent)).ToList(),
                    c.HysteresisCelsius,
                    c.FailSafeDutyPercent)))
            .ToList();

        using var guard = new FanSafetyGuard(fanController, channels, _options.DeadmanTimeout, guardLogger);

        logger.LogInformation(
            "Control loop starting: {ChannelCount} fan channel(s), poll every {PollInterval}.",
            channels.Count, _options.PollInterval);

        using var timer = new PeriodicTimer(_options.PollInterval);
        do
        {
            try
            {
                await PollOnceAsync(channels, controlled, guard, stoppingToken);

                if (_conditions.Changed(PollFailed, false))
                {
                    logger.LogInformation("Control loop poll succeeded again.");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A bad poll must not kill the loop: logging and continuing keeps control
                // active if the cause is transient. The guard wasn't kicked, so if polls
                // keep failing the deadman hands every fan back to automatic control.
                if (_conditions.Changed(PollFailed, true))
                {
                    logger.LogError(ex, "Control loop poll failed; retrying every interval.");
                }

                if (statusStore.Latest is { } last)
                {
                    statusStore.Update(last with { ControlLoopHealthy = false });
                }
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PollOnceAsync(
        IReadOnlyList<FanChannel> channels,
        IReadOnlyList<ControlledFan> controlled,
        FanSafetyGuard guard,
        CancellationToken cancellationToken)
    {
        var readings = new List<SensorReading>(sensorReader.ReadAll(ResolveSensors()));
        readings.Add(await gpuProvider.ReadAsync(cancellationToken));
        readings.Add(await hbaProvider.ReadAsync(cancellationToken));

        var everyChannelApplied = true;
        foreach (var fan in controlled)
        {
            everyChannelApplied &= TryApply(fan, readings);
        }

        // Kicked even if an individual channel failed: that channel was handed back to
        // automatic control on its own, and the loop itself is demonstrably alive. Not
        // kicking would make the deadman release the healthy channels every timeout too.
        guard.Kick(_options.DeadmanTimeout);

        var fanStatuses = channels.Select(fanController.ReadStatus).Select(_stallDetector.Apply).ToList();
        LogFanConditions(fanStatuses);
        statusStore.Update(new StatusSnapshot(DateTimeOffset.UtcNow, readings, fanStatuses, driveHealthStore.Latest, everyChannelApplied));
    }

    /// <summary>
    /// Applies one curve to its channel. Isolated per channel so that one failing (a bad
    /// sysfs write) can't stop the channels after it from being driven this poll.
    /// </summary>
    private bool TryApply(ControlledFan fan, IReadOnlyList<SensorReading> readings)
    {
        var (channel, curve) = fan;
        var condition = $"apply-failed:{channel.Id}";

        try
        {
            // Re-assert manual mode every poll, not just once at startup: observed on real
            // hardware that some Super I/O channels silently revert pwmN_enable back to 0
            // (chip-level fail-safe, not something the Linux driver documents) if it isn't
            // periodically refreshed. Writing the duty value alone wasn't enough.
            fanController.TakeManualControl(channel);

            var dutyPercent = Math.Max(_curveEngine.Evaluate(curve, readings), channel.MinimumDutyPercent);
            fanController.SetDutyPercent(channel, dutyPercent);

            if (_conditions.Changed(condition, false))
            {
                logger.LogInformation("Fan channel '{FanChannelId}' is under control again.", channel.Id);
            }

            return true;
        }
        catch (Exception ex)
        {
            if (_conditions.Changed(condition, true))
            {
                logger.LogError(ex, "Failed to drive fan channel '{FanChannelId}'; handing it back to automatic control and retrying every poll.", channel.Id);
            }

            // It may be sitting on manual at a stale duty. Best effort: if this write
            // fails too there is nothing left to try until the next poll.
            try
            {
                fanController.ReleaseToAuto(channel);
            }
            catch (Exception releaseEx)
            {
                logger.LogDebug(releaseEx, "Releasing fan channel '{FanChannelId}' after a failed poll also failed.", channel.Id);
            }

            return false;
        }
    }

    private IReadOnlyList<ResolvedSensor> ResolveSensors()
    {
        if (_sensors is not null && Stopwatch.GetElapsedTime(_sensorsScannedAt) < _options.SensorRescanInterval)
        {
            return _sensors;
        }

        var previous = _sensors;
        var current = sensorResolver.Resolve(SensorSpec.DefaultWhitelist);
        _sensors = current;
        _sensorsScannedAt = Stopwatch.GetTimestamp();

        if (previous is null)
        {
            LogUnresolvedSensors(current);
            return current;
        }

        foreach (var sensor in current.Where(c => previous.All(p => p.Id != c.Id)))
        {
            logger.LogInformation("Sensor '{SensorId}' appeared at {Path}.", sensor.Id, sensor.TempInputPath);
        }

        foreach (var sensor in previous.Where(p => current.All(c => c.Id != p.Id)))
        {
            logger.LogWarning("Sensor '{SensorId}' disappeared (was at {Path}).", sensor.Id, sensor.TempInputPath);
        }

        foreach (var sensor in current.Where(c => previous.Any(p => p.Id == c.Id && p.TempInputPath != c.TempInputPath)))
        {
            logger.LogInformation("Sensor '{SensorId}' moved to {Path}.", sensor.Id, sensor.TempInputPath);
        }

        return current;
    }

    private void LogFanConditions(IReadOnlyList<FanStatus> fanStatuses)
    {
        foreach (var status in fanStatuses)
        {
            // A channel that failed this poll was deliberately released, so it isn't expected to read Manual.
            var unexpectedMode = status.Mode != PwmEnableMode.Manual && !_conditions.IsActive($"apply-failed:{status.Id}");
            if (_conditions.Changed($"unexpected-mode:{status.Id}", unexpectedMode))
            {
                if (unexpectedMode)
                {
                    logger.LogWarning(
                        "Fan channel '{FanChannelId}' read back pwm_enable={Mode} right after being re-asserted to Manual: a Super I/O watchdog or hardware quirk may be reverting it.",
                        status.Id, status.Mode?.ToString() ?? "unreadable");
                }
                else
                {
                    logger.LogInformation("Fan channel '{FanChannelId}' reads back as Manual again.", status.Id);
                }
            }

            if (_conditions.Changed($"stalled:{status.Id}", status.Stalled))
            {
                if (status.Stalled)
                {
                    logger.LogWarning(
                        "Fan channel '{FanChannelId}' reads 0 RPM at {DutyPercent}% duty: fan dead, jammed or unplugged?",
                        status.Id, status.DutyPercent);
                }
                else
                {
                    logger.LogInformation("Fan channel '{FanChannelId}' is spinning again ({Rpm} RPM).", status.Id, status.Rpm);
                }
            }
        }
    }

    private void LogUnresolvedSensors(IReadOnlyList<ResolvedSensor> resolved)
    {
        var resolvedIds = resolved.Select(r => r.Id).ToHashSet();
        foreach (var spec in SensorSpec.DefaultWhitelist.Where(s => !s.AllInstancesOfChip && !resolvedIds.Contains(s.Id)))
        {
            logger.LogWarning("Sensor '{SensorId}' (chip {ChipName}) could not be resolved on this host.", spec.Id, spec.ChipName);
        }
    }

    private sealed record ControlledFan(FanChannel Channel, FanCurve Curve);
}
