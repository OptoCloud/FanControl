using NpgsqlTypes;
using Vigil.Core.Protocol;

namespace Vigil.Core.Data;

/// <summary>The event log: one row per condition raised or cleared.</summary>
public sealed class EventStore(VigilDataSources databases)
{
    private static readonly string InsertEvent = SqlText.Load("insert_event");

    /// <summary>
    /// Records an event and returns it with the id and timestamp the database assigned, so the
    /// new event can go straight out on the live stream without a second query.
    /// </summary>
    public async Task<EventRecord> InsertAsync(Severity severity, string kind, string message, CancellationToken cancellationToken)
    {
        await using var command = databases.Writer.CreateCommand(InsertEvent);
        command.Parameters.Add(new() { Value = SeverityName(severity), NpgsqlDbType = NpgsqlDbType.Text });
        command.Parameters.Add(new() { Value = kind, NpgsqlDbType = NpgsqlDbType.Text });
        command.Parameters.Add(new() { Value = message, NpgsqlDbType = NpgsqlDbType.Text });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("inserting an event returned no row");
        }

        return new EventRecord
        {
            Id = reader.GetInt64(0),
            Ts = Rfc3339.From(DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1))),
            Severity = severity,
            Kind = kind,
            Message = message,
        };
    }

    /// <summary>
    /// Spelled out rather than reusing the JSON name, for the same reason as a sensor's
    /// category: this value is stored and queried, so a rename on the wire must not split the
    /// event log in two.
    /// </summary>
    private static string SeverityName(Severity severity) => severity switch
    {
        Severity.Info => "info",
        Severity.Warning => "warning",
        Severity.Critical => "critical",
        _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, "unknown severity"),
    };
}
