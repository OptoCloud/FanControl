namespace FanControl.Core.Sensors;

public sealed class UnavailableHbaTemperatureProvider : IHbaTemperatureProvider
{
    public Task<SensorReading> ReadAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new SensorReading("hba", SensorCategory.Hba, "LSI HBA", null, "unavailable"));
}
