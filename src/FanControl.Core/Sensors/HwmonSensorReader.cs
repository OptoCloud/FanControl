using FanControl.Core.IO;

namespace FanControl.Core.Sensors;

/// <summary>Reads current values for a set of already-resolved hwmon sensors.</summary>
public sealed class HwmonSensorReader(ISysFs sysFs)
{
    public SensorReading Read(ResolvedSensor sensor)
    {
        var raw = sysFs.TryReadAllText(sensor.TempInputPath);
        double? celsius = raw is not null && long.TryParse(raw, out var milliCelsius)
            ? milliCelsius / 1000.0
            : null;

        return new SensorReading(sensor.Id, sensor.Category, sensor.Label, celsius, sensor.TempInputPath);
    }

    public IReadOnlyList<SensorReading> ReadAll(IEnumerable<ResolvedSensor> sensors) =>
        sensors.Select(Read).ToList();
}
