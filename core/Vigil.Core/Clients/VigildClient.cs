using System.Net.ServerSentEvents;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vigil.Core.Configuration;
using Vigil.Core.Protocol;

namespace Vigil.Core.Clients;

/// <summary>
/// The one connection vigil-core keeps to vigild: its <c>/events</c> stream of snapshots, over
/// the daemon's unix socket. However many consumers vigil-core has, vigild only ever sees this
/// one connection.
/// </summary>
/// <remarks>
/// Unlike the Rust implementation this needs no SSE parser of its own:
/// <c>System.Net.ServerSentEvents.SseParser</c> is in the framework, and
/// <c>SocketsHttpHandler.ConnectCallback</c> lets HttpClient speak to a unix socket. The Rust
/// side hand-rolls both because nothing maintained exists there (ADR-015).
/// </remarks>
public sealed class VigildClient(IOptions<VigilOptions> options, ILogger<VigildClient> logger)
{
    private static readonly TimeSpan MinimumRetry = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumRetry = TimeSpan.FromSeconds(30);

    /// <summary>How long to wait for the connect itself, as opposed to for data on it.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Oversized response headers are a malformed peer, not a slow one.</summary>
    private const int MaximumHeaderBytes = 16 * 1024;

    private readonly VigilOptions _options = options.Value;

    /// <summary>
    /// Streams from vigild until cancelled, reconnecting forever. Backoff doubles to
    /// <see cref="MaximumRetry"/> and resets once a connection has actually been established, so
    /// a daemon that is restarting is picked up immediately while one that is absent is not
    /// hammered.
    /// </summary>
    public async IAsyncEnumerable<DaemonEvent> StreamAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var retry = MinimumRetry;

        while (!cancellationToken.IsCancellationRequested)
        {
            var connected = false;
            string reason;

            // The inner enumerator is pumped by hand because C# will not allow a `yield` inside
            // a try that has a catch, and every failure here has to become a Disconnected event
            // rather than an exception: this loop is the thing that must never stop.
            await using (var events = StreamOnceAsync(cancellationToken).GetAsyncEnumerator(cancellationToken))
            {
                while (true)
                {
                    DaemonEvent item;
                    try
                    {
                        if (!await events.MoveNextAsync().ConfigureAwait(false))
                        {
                            reason = "vigild closed the stream";
                            break;
                        }

                        item = events.Current;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        yield break;
                    }
                    catch (TimeoutException timeout)
                    {
                        reason = $"no data from vigild: {timeout.Message}";
                        break;
                    }
                    catch (Exception error) when (error is HttpRequestException or SocketException or IOException)
                    {
                        reason = error.Message;
                        break;
                    }

                    if (item is DaemonEvent.Connected)
                    {
                        connected = true;
                        retry = MinimumRetry;
                    }

                    yield return item;
                }
            }

            yield return new DaemonEvent.Disconnected(reason);

            try
            {
                await Task.Delay(retry, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            if (!connected)
            {
                retry = TimeSpan.FromTicks(Math.Min(retry.Ticks * 2, MaximumRetry.Ticks));
            }
        }
    }

    private async IAsyncEnumerable<DaemonEvent> StreamOnceAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var client = CreateClient();

        // ResponseHeadersRead: the body is an endless stream, so it must not be buffered.
        using var response = await client
            .GetAsync(new Uri("http://vigild/events"), HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // 503 is vigild's answer when its max_clients cap is reached.
            throw new HttpRequestException($"vigild answered HTTP {(int)response.StatusCode}");
        }

        yield return new DaemonEvent.Connected();

        var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var guarded = new IdleTimeoutStream(body, Limits.StreamSilenceTimeout);

        await foreach (var item in SseParser.Create(guarded).EnumerateAsync(cancellationToken).ConfigureAwait(false))
        {
            // vigild names every event "status"; anything else is from a version that has
            // something new to say, and ignoring it is how this stays forward-compatible.
            if (item.EventType != "status")
            {
                continue;
            }

            Snapshot? snapshot = null;
            try
            {
                snapshot = JsonSerializer.Deserialize<Snapshot>(item.Data, VigilJson.Options);
            }
            catch (JsonException error)
            {
                // One bad snapshot must not take the connection down with it.
                logger.LogWarning("discarding a snapshot that could not be parsed: {Reason}", error.Message);
            }

            if (snapshot is not null)
            {
                yield return new DaemonEvent.Received(snapshot);
            }
        }
    }

    private HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = ConnectAsync,
            ConnectTimeout = ConnectTimeout,
            MaxResponseHeadersLength = MaximumHeaderBytes / 1024,

            // The stream is long-lived by design: the pool must not recycle it underneath us.
            PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
        };

        // An SSE response never completes, so a request timeout would end every stream on a
        // clock. Silence is what IdleTimeoutStream watches for instead.
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        if (_options.VigildSocket is { Length: > 0 } path)
        {
            var unix = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await unix.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(unix, ownsSocket: true);
            }
            catch
            {
                unix.Dispose();
                throw;
            }
        }

        // Development only, against dev/mock-vigild.mjs. VIGILD_SOCKET wins when both are set,
        // as it does in the Rust version.
        var (host, port) = TcpTarget(_options.VigildUrl);
        var tcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(tcp, ownsSocket: true);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>"http://127.0.0.1:5178/" to ("127.0.0.1", 5178).</summary>
    internal static (string Host, int Port) TcpTarget(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return ("127.0.0.1", 5178);
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.HostNameType == UriHostNameType.Unknown)
        {
            throw new InvalidOperationException($"VIGILD_URL must look like http://host:port, got '{url}'");
        }

        return (parsed.Host, parsed.Port);
    }
}
