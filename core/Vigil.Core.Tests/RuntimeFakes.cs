using Npgsql;
using Vigil.Core.Alerting;
using Vigil.Core.Protocol;
using Vigil.Core.Runtime;

namespace Vigil.Core.Tests;

/// <summary>
/// The database, faked at the runtime's boundary. Every call is recorded by name; while
/// <see cref="Failure"/> is set, every call throws it instead.
/// </summary>
internal sealed class FakeRuntimeStore : IRuntimeStore
{
    private long _nextEventId = 1;

    public List<string> Calls { get; } = [];

    public Exception? Failure { get; set; }

    public List<DriveState> StoredDrives { get; } = [];

    /// <summary>What Npgsql throws when it cannot reach the server.</summary>
    public static Exception ConnectionLost() => new NpgsqlException("Failed to connect to 127.0.0.1:5432");

    /// <summary>What Npgsql throws when the server answered with an error: the connection is fine.</summary>
    public static Exception QueryFailed() => new PostgresException("duplicate key", "ERROR", "ERROR", "23505");

    public Task<IReadOnlyList<DriveState>> PrepareAsync(CancellationToken cancellationToken) =>
        Record<IReadOnlyList<DriveState>>("prepare", StoredDrives);

    public Task SaveDriveAsync(DriveState drive, bool changed, CancellationToken cancellationToken) => Record("save-drive", 0);

    public Task InsertSamplesAsync(Snapshot snapshot, CancellationToken cancellationToken) => Record("insert-samples", 0);

    public Task TouchInventoryAsync(Snapshot snapshot, CancellationToken cancellationToken) => Record("touch-inventory", 0);

    public Task InsertUpsSampleAsync(UpsReading reading, CancellationToken cancellationToken) => Record("insert-ups", 0);

    public Task<EventRecord> InsertEventAsync(NewEvent newEvent, CancellationToken cancellationToken) =>
        Record("insert-event", new EventRecord
        {
            Id = _nextEventId++,
            Ts = "2026-10-01T12:00:00.000Z",
            Severity = newEvent.Severity,
            Kind = newEvent.Kind,
            Message = newEvent.Message,
        });

    public int CountOf(string call) => Calls.Count(made => made == call);

    private Task<T> Record<T>(string call, T result)
    {
        Calls.Add(call);
        return Failure is null ? Task.FromResult(result) : Task.FromException<T>(Failure);
    }
}

/// <summary>A clock that moves only when told to.</summary>
internal sealed class ManualTime : TimeProvider
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private long _ticks = TimeSpan.TicksPerDay;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _ticks;

    public override DateTimeOffset GetUtcNow() => Start.AddTicks(_ticks);

    public void Advance(TimeSpan by) => _ticks += by.Ticks;
}
