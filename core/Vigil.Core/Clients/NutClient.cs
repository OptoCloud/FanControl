using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Options;
using Vigil.Core.Configuration;
using Vigil.Core.Protocol;

namespace Vigil.Core.Clients;

/// <summary>One poll of the UPS: a reading, or why there is not one.</summary>
public abstract record UpsPoll
{
    public sealed record Read(UpsReading Reading) : UpsPoll;

    /// <summary>
    /// Both "upsd cannot be reached" and "upsd answered ERR". The second matters on its own:
    /// DATA-STALE and DRIVER-NOT-CONNECTED mean upsd is fine but has lost the UPS itself.
    /// </summary>
    public sealed record Failed(string Reason) : UpsPoll;
}

/// <summary>
/// The one connection vigil-core keeps to NUT's upsd: a plain TCP line protocol (RFC 9271).
/// Variable reads are anonymous on upsd, so no login is needed. It polls <c>LIST VAR &lt;ups&gt;</c>
/// on a fixed interval, the way upsmon does, reconnecting forever.
/// </summary>
/// <remarks>
/// Hand-rolled because nothing maintained exists: <c>rups</c>, the one Rust NUT client, had 1,100
/// recent downloads and no release since 2023, and there is no .NET equivalent at all. The
/// protocol is four verbs (ADR-015).
/// </remarks>
public sealed class NutClient(IOptions<VigilOptions> options)
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MinimumRetry = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumRetry = TimeSpan.FromSeconds(30);

    private readonly VigilOptions _options = options.Value;

    /// <summary>
    /// Polls until cancelled. Backoff resets once upsd has answered at all, so a upsd that is
    /// restarting is picked back up immediately.
    /// </summary>
    public async IAsyncEnumerable<UpsPoll> PollAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var retry = MinimumRetry;

        while (!cancellationToken.IsCancellationRequested)
        {
            var answered = false;

            // The connection's polls are pumped by hand: C# allows no `yield` inside a try with
            // a catch, and every failure here has to become a Failed poll rather than an
            // exception, because this loop is the thing that must never stop.
            string? failure = null;
            await using (var polls = PollConnectionAsync(cancellationToken).GetAsyncEnumerator(cancellationToken))
            {
                var stopped = false;
                while (!stopped)
                {
                    UpsPoll poll;
                    try
                    {
                        if (!await polls.MoveNextAsync().ConfigureAwait(false))
                        {
                            break;
                        }

                        poll = polls.Current;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        stopped = true;
                        break;
                    }
                    catch (TimeoutException timeout)
                    {
                        failure = $"no answer from upsd: {timeout.Message}";
                        break;
                    }
                    catch (Exception error) when (error is SocketException or IOException)
                    {
                        failure = Describe(error);
                        break;
                    }

                    answered = true;
                    yield return poll;
                }

                if (stopped)
                {
                    yield break;
                }
            }

            if (failure is not null)
            {
                yield return new UpsPoll.Failed(failure);
            }

            try
            {
                await Task.Delay(retry, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            retry = answered ? MinimumRetry : TimeSpan.FromTicks(Math.Min(retry.Ticks * 2, MaximumRetry.Ticks));
        }
    }

    /// <summary>One connection's worth of polls, ending when it breaks.</summary>
    private async IAsyncEnumerable<UpsPoll> PollConnectionAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var host = _options.NutHost ?? throw new InvalidOperationException("NUT_HOST is not configured");
        var interval = TimeSpan.FromSeconds(_options.NutPollSeconds);

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

        using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connect.CancelAfter(RequestTimeout);
        await socket.ConnectAsync(host, _options.NutPort, connect.Token).ConfigureAwait(false);

        await using var network = new NetworkStream(socket, ownsSocket: false);
        await using var guarded = new IdleTimeoutStream(network, RequestTimeout);
        using var reader = new StreamReader(guarded, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);

        while (!cancellationToken.IsCancellationRequested)
        {
            yield return await ListVarAsync(network, reader, _options.NutUps, cancellationToken).ConfigureAwait(false);

            try
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Say goodbye so upsd does not log a dropped client, then let the caller stop.
                await Goodbye(network).ConfigureAwait(false);
                yield break;
            }
        }
    }

    /// <summary>
    /// What went wrong, for the event log. A reset is upsd closing the connection as much as an
    /// end of stream is: which one the client sees depends on the platform and on timing (a
    /// request written after the close draws a reset), so both say the same thing.
    /// </summary>
    private static string Describe(Exception error) =>
        (error as SocketException ?? error.InnerException as SocketException)?.SocketErrorCode
            is SocketError.ConnectionReset or SocketError.ConnectionAborted
            ? "upsd closed the connection"
            : error.Message;

    /// <summary>One <c>LIST VAR</c> exchange: the variables, or upsd's own error word.</summary>
    private static async Task<UpsPoll> ListVarAsync(Stream stream, StreamReader reader, string ups, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes($"LIST VAR {ups}\n"), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        var lines = new List<string>();
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new IOException("upsd closed the connection");

            if (line.StartsWith("ERR ", StringComparison.Ordinal))
            {
                return new UpsPoll.Failed($"upsd: {line["ERR ".Length..]}");
            }

            lines.Add(line);

            if (line.StartsWith("END LIST VAR", StringComparison.Ordinal))
            {
                var variables = NutProtocol.ParseVariables(lines);
                return new UpsPoll.Read(NutProtocol.ToReading(ups, variables, Rfc3339.Now()));
            }
        }
    }

    private static async Task Goodbye(Stream stream)
    {
        try
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes("LOGOUT\n")).ConfigureAwait(false);
        }
        catch (Exception error) when (error is SocketException or IOException or ObjectDisposedException)
        {
            // Shutting down anyway; upsd will notice the close either way.
        }
    }
}
