using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Vigil.Core.Configuration;

namespace Vigil.Core.Data;

/// <summary>
/// vigil-core's two connection pools: one that writes, one that only reads.
/// </summary>
/// <remarks>
/// The split is the remains of a process boundary. While vigil-core and vigil-web were separate,
/// only vigil-core held a write credential and vigil-web connected as a SELECT-only role; merged
/// into one process that is no longer structural, but routing the read path through the
/// restricted role still means a bug in a query cannot write, drop or truncate anything.
/// docs/SECURITY.md §3.4 has the grant.
/// </remarks>
public sealed class VigilDataSources : IAsyncDisposable
{
    public VigilDataSources(IOptions<VigilOptions> options, ILogger<VigilDataSources> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        var settings = options.Value;
        Writer = new NpgsqlDataSourceBuilder(PostgresUri.ToConnectionString(settings.DatabaseUrl, "vigil-core")).Build();

        if (settings.DatabaseUrlReadonly is { Length: > 0 } readonlyUrl)
        {
            Reader = new NpgsqlDataSourceBuilder(PostgresUri.ToConnectionString(readonlyUrl, "vigil-core (read-only)")).Build();
            SeparateReadRole = true;
        }
        else
        {
            // Said once, at startup, rather than per query: in development one URL is the whole
            // point, but in production this means the dashboard's queries run as the owner.
            logger.LogWarning(
                "DATABASE_URL_READONLY is not set, so the read path shares the schema owner's role. "
                + "See docs/SECURITY.md §3.4 for the grant that separates them.");
            Reader = Writer;
            SeparateReadRole = false;
        }
    }

    /// <summary>The schema owner: samples, drives, events.</summary>
    public NpgsqlDataSource Writer { get; }

    /// <summary>History and the event log. The same pool as <see cref="Writer"/> if unconfigured.</summary>
    public NpgsqlDataSource Reader { get; }

    /// <summary>Whether the read path really is on its own role, for the health endpoint to say.</summary>
    public bool SeparateReadRole { get; }

    public async ValueTask DisposeAsync()
    {
        await Writer.DisposeAsync().ConfigureAwait(false);
        if (SeparateReadRole)
        {
            await Reader.DisposeAsync().ConfigureAwait(false);
        }
    }
}
