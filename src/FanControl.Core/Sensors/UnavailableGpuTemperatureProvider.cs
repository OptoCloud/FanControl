namespace FanControl.Core.Sensors;

public sealed class UnavailableGpuTemperatureProvider : INvidiaGpuTemperatureProvider
{
    public Task<SensorReading> ReadAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new SensorReading("gpu", SensorCategory.Gpu, "GPU", null, "unavailable"));
}
