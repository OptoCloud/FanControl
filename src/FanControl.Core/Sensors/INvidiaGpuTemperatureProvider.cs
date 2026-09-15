namespace FanControl.Core.Sensors;

public interface INvidiaGpuTemperatureProvider
{
    Task<SensorReading> ReadAsync(CancellationToken cancellationToken);
}
