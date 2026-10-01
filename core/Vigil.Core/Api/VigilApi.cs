using System.Data.Common;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Vigil.Core.Configuration;
using Vigil.Core.Data;
using Vigil.Core.Protocol;

namespace Vigil.Core.Api;

/// <summary>
/// vigil-core's HTTP API. Every route is a GET: vigil has no mutating route, which is what
/// ADR-008's unauthenticated LAN dashboard depends on. Adding one voids that decision.
/// </summary>
public static class VigilApi
{
    /// <summary>The event log a page shows on first paint, as vigil-web's server load did.</summary>
    private const int RecentEventCount = 50;

    /// <summary>Range names from the enum's own wire spelling, so there is one list of them.</summary>
    private static readonly Dictionary<string, RangeKey> RangeNames = Enum.GetValues<RangeKey>()
        .ToDictionary(range => JsonSerializer.Serialize(range, VigilJson.Options).Trim('"'), StringComparer.Ordinal);

    private static readonly string RangeProblem = $"range must be one of {string.Join(", ", RangeNames.Keys)}\n";

    public static IEndpointRouteBuilder MapVigilApi(this IEndpointRouteBuilder app)
    {
        // 200 once running. Deliberately says nothing about the database: the live view has to
        // work without one (ARCHITECTURE.md invariant 5), so a database outage is not unhealthy.
        app.MapGet("/health", () => TypedResults.Text("ok\n"));

        app.MapGet("/api/live", LiveEndpoint.Handle);
        app.MapGet("/api/history", HistoryAsync);
        app.MapGet("/api/events", EventsAsync);

        // An unknown API path is a 404 with a hint, not the dashboard's index page.
        app.MapGet("/api/{**rest}", () => TypedResults.Text("try /api/live, /api/history?range=24h or /api/events\n", statusCode: 404));
        return app;
    }

    private static async Task<IResult> HistoryAsync(
        string? range,
        HistoryQueries history,
        IOptions<VigilOptions> options,
        ILoggerFactory logging,
        CancellationToken cancellationToken)
    {
        if (range is null || !RangeNames.TryGetValue(range, out var key))
        {
            return TypedResults.Text(RangeProblem, statusCode: StatusCodes.Status400BadRequest);
        }

        var ups = options.Value.NutHost is { Length: > 0 } ? options.Value.NutUps : null;
        try
        {
            var response = await history.QueryAsync(key, ups, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            return TypedResults.Json(response, VigilJson.Options);
        }
        catch (Exception error) when (error is DbException or TimeoutException)
        {
            logging.CreateLogger(typeof(VigilApi)).LogError("history query failed: {Error}", error.Message);
            return TypedResults.Text("History is unavailable: the database could not be reached.\n", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>
    /// The newest events. The page is a static build now, so what vigil-web's server load used to
    /// hand it on first paint it fetches from here; an empty list when the database is down,
    /// as before, since the live stream still works.
    /// </summary>
    private static async Task<IResult> EventsAsync(EventStore events, ILoggerFactory logging, CancellationToken cancellationToken)
    {
        try
        {
            return TypedResults.Json(await events.RecentAsync(RecentEventCount, cancellationToken).ConfigureAwait(false), VigilJson.Options);
        }
        catch (Exception error) when (error is DbException or TimeoutException)
        {
            logging.CreateLogger(typeof(VigilApi)).LogError("event log query failed: {Error}", error.Message);
            return TypedResults.Json(Array.Empty<EventRecord>(), VigilJson.Options);
        }
    }
}
