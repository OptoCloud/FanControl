using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using Vigil.Core.Configuration;
using Vigil.Core.Runtime;

namespace Vigil.Core.Api;

/// <summary>
/// <c>GET /api/live</c>: Server-Sent Events to the browser. The current state on connect, then
/// every change (snapshots, vigild reachability, drive health, the UPS, new events).
/// </summary>
/// <remarks>
/// ASP.NET's <c>TypedResults.ServerSentEvents</c> does the framing (docs/STYLE.md §3.1); what is
/// ours is attaching a subscription and the keepalive.
/// </remarks>
public static class LiveEndpoint
{
    /// <summary>
    /// Sent after <see cref="Limits.StreamKeepalive"/> of silence. It is what notices a browser
    /// that has gone away without closing (a sleeping laptop): the write fails and the slot is
    /// freed. A named event rather than an SSE comment, because the framework's formatter writes
    /// events only; EventSource dispatches it to no handler the page has, so it is invisible there.
    /// </summary>
    internal static readonly SseItem<string> Keepalive = new(string.Empty, "keepalive");

    public static IResult Handle(LiveHub hub, HttpContext context)
    {
        if (hub.TrySubscribe() is not { } subscription)
        {
            return TypedResults.Text("Too many live streams are open. Close a tab and reload.\n", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return TypedResults.ServerSentEvents(Relay(subscription, Limits.StreamKeepalive, context.RequestAborted));
    }

    /// <summary>
    /// Every message the runtime queues for this subscription, unnamed (the payload's own "type"
    /// says which message it is), with a keepalive whenever the stream has been quiet for
    /// <paramref name="keepalive"/>. Ends when the runtime drops the subscription, and frees its
    /// slot however it ends.
    /// </summary>
    internal static async IAsyncEnumerable<SseItem<string>> Relay(
        LiveSubscription subscription,
        TimeSpan keepalive,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using (subscription)
        {
            while (true)
            {
                var (open, idle) = await WaitAsync(subscription, keepalive, cancellationToken).ConfigureAwait(false);
                if (idle)
                {
                    yield return Keepalive;
                    continue;
                }

                if (!open)
                {
                    yield break;
                }

                while (subscription.Reader.TryRead(out var json))
                {
                    yield return new SseItem<string>(json);
                }
            }
        }
    }

    /// <summary>Separate from <see cref="Relay"/> because C# allows no <c>yield</c> inside a catch.</summary>
    private static async Task<(bool Open, bool Idle)> WaitAsync(LiveSubscription subscription, TimeSpan keepalive, CancellationToken cancellationToken)
    {
        using var quiet = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        quiet.CancelAfter(keepalive);
        try
        {
            return (await subscription.Reader.WaitToReadAsync(quiet.Token).ConfigureAwait(false), false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (true, true);
        }
    }
}
