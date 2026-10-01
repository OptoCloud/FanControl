using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Vigil.Core.Alerting;
using Vigil.Core.Clients;
using Vigil.Core.Configuration;
using Vigil.Core.Notifications;
using Vigil.Core.Protocol;

namespace Vigil.Core.Runtime;

/// <summary>
/// vigil-core's heart: one loop that owns all state. vigild's snapshots, the UPS readings and
/// new live subscribers arrive on one channel; history goes to Postgres, events to the log, the
/// database and ntfy, and every change to the live subscribers. One owner means no locks, and
/// the order of events is the order they happened in. Ported from the Rust core's runtime.rs.
/// </summary>
/// <remarks>
/// The schema is applied each time the database becomes reachable (<see cref="Data.SchemaSetup"/>).
/// The first fill of the rollups can take minutes, and the live view waits for it, as it did in
/// the Rust core: it happens once per database, not once per start.
/// </remarks>
public sealed class VigilRuntime
{
    /// <summary>How often the inventory tables are refreshed. They change when hardware does.</summary>
    private static readonly TimeSpan InventoryEvery = TimeSpan.FromSeconds(60);

    /// <summary>How often time-based conditions are judged when nothing arrives.</summary>
    private static readonly TimeSpan TickEvery = TimeSpan.FromSeconds(1);

    /// <summary>Three polls (6s at vigild's 2s cadence), so one odd read never notifies anyone.</summary>
    private const uint SnapshotThreshold = 3;

    /// <summary>
    /// The UPS reports its own state, so one poll is enough: an OB flag is a real power event,
    /// not a flaky read.
    /// </summary>
    private const uint UpsThreshold = 1;

    private readonly IRuntimeStore _store;
    private readonly NtfyClient? _ntfy;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly DatabaseGate _database;
    private readonly LiveBroadcaster _live = new();
    private readonly TimeSpan _persistInterval;
    private readonly TimeSpan _daemonLostAfter;

    private Snapshot? _latest;
    private bool _daemonConnected;
    private (long Since, string Reason)? _daemonLostSince;
    private bool _daemonLostRaised;
    private readonly SortedDictionary<string, DriveState> _drives = new(StringComparer.Ordinal);
    private UpsState _ups;
    private long? _upsUnreadableSince;

    private readonly ConditionDebouncer _snapshotDebouncer = new(SnapshotThreshold);
    private readonly ConditionDebouncer _upsDebouncer = new(UpsThreshold);
    private readonly HashSet<string> _knownSensorIds = new(StringComparer.Ordinal);
    private string _lastDriveHealthAsOf = string.Empty;
    private long? _lastPersisted;
    private long? _lastUpsPersisted;
    private long? _lastInventory;

    public VigilRuntime(VigilOptions options, IRuntimeStore store, NtfyClient? ntfy, ILogger<VigilRuntime> logger, TimeProvider time)
    {
        _store = store;
        _ntfy = ntfy;
        _logger = logger;
        _time = time;
        _database = new DatabaseGate(logger, time);
        _persistInterval = TimeSpan.FromSeconds(options.PersistIntervalSeconds);
        _daemonLostAfter = TimeSpan.FromSeconds(options.DaemonLostAfterSeconds);
        _ups = new UpsState { Enabled = options.NutHost is { Length: > 0 }, Name = options.NutUps, Reading = null, Error = null };
        _daemonLostSince = (time.GetTimestamp(), "not connected yet");
        _upsUnreadableSince = time.GetTimestamp();
    }

    /// <summary>Runs until cancelled or until the input channel is completed.</summary>
    public async Task RunAsync(ChannelReader<RuntimeInput> inputs, CancellationToken cancellationToken)
    {
        try
        {
            while (await WaitForInputAsync(inputs, cancellationToken).ConfigureAwait(false))
            {
                // Everything already queued goes first, so the time-based checks in the tick judge
                // on the latest news: a "connected to vigild" that queued up while the database
                // was slow must be seen before an outage is declared.
                while (inputs.TryRead(out var input))
                {
                    await HandleAsync(input, cancellationToken).ConfigureAwait(false);
                }

                await TickAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _live.CloseAll();
        }
    }

    /// <summary>True when there is input or a tick is due; false once the channel is completed.</summary>
    private static async Task<bool> WaitForInputAsync(ChannelReader<RuntimeInput> inputs, CancellationToken cancellationToken)
    {
        using var tick = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        tick.CancelAfter(TickEvery);
        try
        {
            return await inputs.WaitToReadAsync(tick.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return true;
        }
    }

    internal int SubscriberCount => _live.Count;

    internal async Task HandleAsync(RuntimeInput input, CancellationToken cancellationToken)
    {
        switch (input)
        {
            case RuntimeInput.Daemon { Event: DaemonEvent.Received received }:
                await HandleSnapshotAsync(received.Snapshot, cancellationToken).ConfigureAwait(false);
                break;
            case RuntimeInput.Daemon { Event: DaemonEvent.Connected }:
                await HandleDaemonConnectionAsync(true, null, cancellationToken).ConfigureAwait(false);
                break;
            case RuntimeInput.Daemon { Event: DaemonEvent.Disconnected disconnected }:
                await HandleDaemonConnectionAsync(false, disconnected.Reason, cancellationToken).ConfigureAwait(false);
                break;
            case RuntimeInput.Ups ups:
                await HandleUpsAsync(ups.Poll, cancellationToken).ConfigureAwait(false);
                break;
            case RuntimeInput.Subscribe subscribe:
                _live.Subscribe(subscribe.Subscriber, CurrentState());
                break;
        }
    }

    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        // A daemon restart drops the stream for a second or two; only a lasting outage is news.
        if (_daemonLostSince is var (since, reason) && !_daemonLostRaised && _time.GetElapsedTime(since) >= _daemonLostAfter)
        {
            _daemonLostRaised = true;
            await RaiseAsync(
                new NewEvent(
                    Severity.Critical,
                    "daemon-lost",
                    $"vigild is unreachable ({reason}). If it is not running, the fans are on BIOS control."),
                cancellationToken).ConfigureAwait(false);
        }

        // The unreadable-UPS condition depends on time, not only on the next poll.
        if (_ups is { Enabled: true, Reading: null })
        {
            await EvaluateUpsAsync(cancellationToken).ConfigureAwait(false);
        }

        // Last, because it may wait out a connect timeout; RunAsync handles what queued meanwhile first.
        if (_database.IsDue)
        {
            var drives = await _database.TryOpenAsync("preparing the database", () => _store.PrepareAsync(cancellationToken))
                .ConfigureAwait(false);
            if (drives is not null)
            {
                foreach (var drive in drives)
                {
                    _drives.TryAdd(drive.Wwn, drive);
                }

                _live.Broadcast(new DrivesMessage { Drives = DriveList() });
            }
        }
    }

    private async Task HandleSnapshotAsync(Snapshot snapshot, CancellationToken cancellationToken)
    {
        var events = _snapshotDebouncer.Update(SnapshotConditions.In(snapshot, _knownSensorIds));
        _knownSensorIds.UnionWith(snapshot.Sensors.Select(sensor => sensor.Id));

        // Drive health only changes when vigild's slow SMART poll runs, which shows up as a new asOf.
        var asOf = snapshot.DriveHealth.Count > 0 ? snapshot.DriveHealth[0].AsOf : string.Empty;
        var drivesToSave = new List<(DriveState State, bool Changed)>();
        if (asOf.Length > 0 && asOf != _lastDriveHealthAsOf)
        {
            _lastDriveHealthAsOf = asOf;
            foreach (var health in snapshot.DriveHealth)
            {
                var update = DriveHealthDiff.Diff(_drives.GetValueOrDefault(health.DeviceName), health);
                if (update.State is not { } state)
                {
                    continue;
                }

                _drives[state.Wwn] = state;
                drivesToSave.Add((state, update.Changed));
                events.AddRange(update.Events);
            }
        }

        var now = _time.GetTimestamp();
        var persist = IsDue(_lastPersisted, _persistInterval);
        var inventory = IsDue(_lastInventory, InventoryEvery);
        if (_database.IsReady)
        {
            _lastPersisted = persist ? now : _lastPersisted;
            _lastInventory = inventory ? now : _lastInventory;
        }

        await _database.TryAsync("writing history", async () =>
        {
            foreach (var (state, changed) in drivesToSave)
            {
                await _store.SaveDriveAsync(state, changed, cancellationToken).ConfigureAwait(false);
            }

            if (persist)
            {
                await _store.InsertSamplesAsync(snapshot, cancellationToken).ConfigureAwait(false);
            }

            if (inventory)
            {
                await _store.TouchInventoryAsync(snapshot, cancellationToken).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);

        _live.Broadcast(new SnapshotMessage { Snapshot = snapshot });
        _latest = snapshot;
        if (drivesToSave.Count > 0)
        {
            _live.Broadcast(new DrivesMessage { Drives = DriveList() });
        }

        foreach (var newEvent in events)
        {
            await RaiseAsync(newEvent, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleDaemonConnectionAsync(bool connected, string? reason, CancellationToken cancellationToken)
    {
        if (connected == _daemonConnected)
        {
            // Still down: keep the first reason, which is the one worth reporting.
            return;
        }

        _daemonConnected = connected;
        _live.Broadcast(new DaemonMessage { Connected = connected });

        if (!connected)
        {
            reason ??= "unknown reason";
            _logger.LogWarning("vigild connection lost: {Reason}", reason);
            _daemonLostSince = (_time.GetTimestamp(), reason);
            return;
        }

        _logger.LogInformation("connected to vigild");
        _daemonLostSince = null;
        if (_daemonLostRaised)
        {
            _daemonLostRaised = false;
            await RaiseAsync(new NewEvent(Severity.Info, "daemon-lost", "vigild is reachable again."), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleUpsAsync(UpsPoll poll, CancellationToken cancellationToken)
    {
        switch (poll)
        {
            case UpsPoll.Read { Reading: var reading }:
                if (_ups.Reading is null && _logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("reading the UPS: {Status}", string.Join(' ', reading.Status));
                }

                _upsUnreadableSince = null;
                _ups = _ups with { Reading = reading, Error = null };

                if (_database.IsReady && IsDue(_lastUpsPersisted, _persistInterval))
                {
                    _lastUpsPersisted = _time.GetTimestamp();
                    await _database.TryAsync("writing UPS history", () => _store.InsertUpsSampleAsync(reading, cancellationToken))
                        .ConfigureAwait(false);
                }

                break;

            case UpsPoll.Failed { Reason: var error }:
                if (_ups.Error != error)
                {
                    _logger.LogWarning("cannot read the UPS: {Error}", error);
                }

                if (_ups.Reading is not null || _upsUnreadableSince is null)
                {
                    _upsUnreadableSince = _time.GetTimestamp();
                }

                _ups = _ups with { Reading = null, Error = error };
                break;
        }

        _live.Broadcast(new UpsMessage { Ups = _ups });
        await EvaluateUpsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EvaluateUpsAsync(CancellationToken cancellationToken)
    {
        var unreadableFor = _upsUnreadableSince is { } since ? _time.GetElapsedTime(since) : TimeSpan.Zero;
        foreach (var newEvent in _upsDebouncer.Update(UpsConditions.Of(_ups, unreadableFor)))
        {
            await RaiseAsync(newEvent, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RaiseAsync(NewEvent newEvent, CancellationToken cancellationToken)
    {
        var level = newEvent.Severity == Severity.Info ? LogLevel.Information : LogLevel.Warning;
        if (_logger.IsEnabled(level))
        {
            _logger.Log(level, "{Severity}: {Message}", newEvent.Severity, newEvent.Message);
        }

        var record = await _database.TryAsync("recording an event", () => _store.InsertEventAsync(newEvent, cancellationToken))
            .ConfigureAwait(false)
            ?? new EventRecord
            {
                // Negative ids never collide with the database's, and are unique enough for a page's list.
                Id = -_time.GetUtcNow().ToUnixTimeMilliseconds(),
                Ts = Rfc3339.From(_time.GetUtcNow()),
                Severity = newEvent.Severity,
                Kind = newEvent.Kind,
                Message = newEvent.Message,
            };

        _live.Broadcast(new EventMessage { Event = record });
        if (newEvent.Severity != Severity.Info)
        {
            _ntfy?.Send(newEvent);
        }
    }

    private bool IsDue(long? last, TimeSpan interval) => last is not { } at || _time.GetElapsedTime(at) >= interval;

    private List<DriveState> DriveList() => [.. _drives.Values];

    private IEnumerable<LiveMessage> CurrentState()
    {
        yield return new DaemonMessage { Connected = _daemonConnected };
        if (_latest is not null)
        {
            yield return new SnapshotMessage { Snapshot = _latest };
        }

        yield return new DrivesMessage { Drives = DriveList() };
        yield return new UpsMessage { Ups = _ups };
    }
}
