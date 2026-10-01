using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Vigil.Core.Configuration;
using Vigil.Core.Data;
using Xunit;

namespace Vigil.Core.Tests;

/// <summary>
/// A real Postgres for the tests that need one, or a skip when there is not one.
/// </summary>
/// <remarks>
/// <para>
/// The queries here are the ones a mock cannot check: <c>unnest</c> array expansion, the casts
/// in <c>$1::text::timestamptz</c>, <c>on conflict</c> behaviour and the epoch-milliseconds
/// round trip. Phase 3 found a real bug in exactly this layer by running it against TimescaleDB
/// rather than reasoning about it.
/// </para>
/// <para>
/// Point them at a database with <c>VIGIL_TEST_DATABASE_URL</c>; without it they skip, so
/// <c>scripts/check.sh</c> needs no Docker:
/// </para>
/// <code>
/// docker compose -f dev/docker-compose.yml up -d
/// VIGIL_TEST_DATABASE_URL=postgres://vigil:vigil@127.0.0.1:5433/vigil dotnet run --project core/Vigil.Core.Tests
/// </code>
/// </remarks>
public static class DatabaseFixture
{
    public static string? Url => Environment.GetEnvironmentVariable("VIGIL_TEST_DATABASE_URL");

    /// <summary>Skips the calling test unless a database has been pointed at it.</summary>
    public static string RequireUrl()
    {
        Assert.SkipWhen(string.IsNullOrWhiteSpace(Url), "set VIGIL_TEST_DATABASE_URL to run the database tests");
        return Url!;
    }

    /// <summary>
    /// Data sources over a database the real <see cref="SchemaSetup"/> has prepared, so the tests
    /// also cover applying the schema, and applying it again over itself.
    /// </summary>
    public static async Task<VigilDataSources> ConnectAsync(CancellationToken cancellationToken)
    {
        var url = RequireUrl();
        var databases = new VigilDataSources(
            Options.Create(new VigilOptions { DatabaseUrl = url }),
            NullLogger<VigilDataSources>.Instance);

        await new SchemaSetup(databases, NullLogger<SchemaSetup>.Instance)
            .ApplyAsync(TimeSpan.FromDays(30), cancellationToken)
            .ConfigureAwait(false);

        return databases;
    }

    /// <summary>Empties the tables a test writes to, so one test cannot see another's rows.</summary>
    public static async Task TruncateAsync(NpgsqlDataSource writer, CancellationToken cancellationToken)
    {
        await using var command = writer.CreateCommand(
            "truncate sensor_samples, fan_samples, ups_samples, sensors, fans, bay_occupants, drives, drive_health_history, events");
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
