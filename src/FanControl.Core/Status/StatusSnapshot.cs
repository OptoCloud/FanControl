using FanControl.Core.Fans;
using FanControl.Core.Sensors;

namespace FanControl.Core.Status;

/// <summary>
/// The full read-only view of daemon state, serialized as-is for the loopback status
/// endpoint. This is the contract the public dashboard's API layer consumes — treat
/// field renames here as a breaking change for that consumer.
/// </summary>
public sealed record StatusSnapshot(
    DateTimeOffset TimestampUtc,
    IReadOnlyList<SensorReading> Sensors,
    IReadOnlyList<FanStatus> Fans,
    bool ControlLoopHealthy);
