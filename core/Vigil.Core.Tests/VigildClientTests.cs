using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Vigil.Core.Clients;
using Vigil.Core.Configuration;
using Xunit;

namespace Vigil.Core.Tests;

public sealed class VigildClientTests
{
    private const string SnapshotJson =
        """{"timestampUtc":"t","sensors":[],"fans":[],"driveHealth":[],"controlLoopHealthy":true}""";

    /// <summary>
    /// A vigild stand-in that answers one connection with <paramref name="response"/> and then
    /// closes, which is how a real stream ending looks. Returns the port it listened on.
    /// </summary>
    private static int FakeVigild(string response)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        _ = Task.Run(async () =>
        {
            using (listener)
            {
                using var client = await listener.AcceptTcpClientAsync();
                await using var stream = client.GetStream();

                // Read the request line and headers, so the client's write completes.
                var request = new byte[1024];
                _ = await stream.ReadAsync(request);

                var bytes = Encoding.UTF8.GetBytes(response);
                await stream.WriteAsync(bytes);
                await stream.FlushAsync();
            }
        });

        return port;
    }

    private static VigildClient ClientFor(int port) =>
        new(
            Options.Create(new VigilOptions { VigildUrl = $"http://127.0.0.1:{port}" }),
            NullLogger<VigildClient>.Instance);

    /// <summary>Describes each event as a short string, so assertions read like the Rust ones.</summary>
    private static async Task<List<string>> Collect(int port, int count)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var described = new List<string>();

        await foreach (var item in ClientFor(port).StreamAsync(cancellation.Token))
        {
            described.Add(item switch
            {
                DaemonEvent.Connected => "connected",
                DaemonEvent.Received received => $"snapshot {received.Snapshot.ControlLoopHealthy}",
                DaemonEvent.Disconnected disconnected => $"disconnected: {disconnected.Reason}",
                _ => throw new InvalidOperationException($"unexpected {item}"),
            });

            if (described.Count == count)
            {
                break;
            }
        }

        return described;
    }

    [Fact]
    public async Task StreamsSnapshotsAndReportsTheStreamEnding()
    {
        var body = "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\n"
            + ": keepalive\n\n"
            + $"event: status\ndata: {SnapshotJson}\n\n"
            + $"event: status\ndata: {SnapshotJson}\n\n";

        var events = await Collect(FakeVigild(body), 4);

        // The keepalive comment is skipped, not reported as an event.
        Assert.Equal(["connected", "snapshot True", "snapshot True"], events[..3]);
        Assert.Equal("disconnected: vigild closed the stream", events[3]);
    }

    [Fact]
    public async Task ARefusalIsADisconnectWithTheStatus()
    {
        // 503 is what vigild answers once api.max_clients is reached.
        var events = await Collect(FakeVigild("HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), 1);

        Assert.Equal("disconnected: vigild answered HTTP 503", events[0]);
    }

    [Fact]
    public async Task AnEventThatIsNotAStatusIsIgnoredRatherThanFatal()
    {
        // Forward compatibility: a newer vigild with something else to say must not break this.
        var body = "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\n"
            + "event: somethingNew\ndata: {\"whatever\":1}\n\n"
            + $"event: status\ndata: {SnapshotJson}\n\n";

        var events = await Collect(FakeVigild(body), 2);

        Assert.Equal(["connected", "snapshot True"], events);
    }

    [Fact]
    public async Task ASnapshotThatCannotBeParsedIsDiscardedNotFatal()
    {
        var body = "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\n"
            + "event: status\ndata: {\"timestampUtc\":\"t\"}\n\n"
            + $"event: status\ndata: {SnapshotJson}\n\n";

        var events = await Collect(FakeVigild(body), 2);

        // The incomplete snapshot is dropped; the good one that follows still arrives.
        Assert.Equal(["connected", "snapshot True"], events);
    }

    [Fact]
    public async Task StreamsOverAUnixSocket()
    {
        // The production path: vigild's API is a unix socket and never TCP (ADR-002), so the
        // every-other-test-uses-TCP shortcut would leave the real one unexercised.
        var path = Path.Combine(Path.GetTempPath(), $"vigil-test-{Guid.NewGuid():N}.sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(1);

        var body = "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\n"
            + $"event: status\ndata: {SnapshotJson}\n\n";

        // Captured rather than read inside the task, so the analyser can see it and the token
        // is the test's own however the task is scheduled.
        var token = TestContext.Current.CancellationToken;
        var serving = Task.Run(
            async () =>
            {
                using var accepted = await listener.AcceptAsync(token);
                await using var stream = new NetworkStream(accepted, ownsSocket: false);
                var request = new byte[1024];
                _ = await stream.ReadAsync(request, token);
                await stream.WriteAsync(Encoding.UTF8.GetBytes(body), token);
                await stream.FlushAsync(token);
            },
            token);

        try
        {
            var client = new VigildClient(
                Options.Create(new VigilOptions { VigildSocket = path }),
                NullLogger<VigildClient>.Instance);

            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var described = new List<string>();
            await foreach (var item in client.StreamAsync(cancellation.Token))
            {
                described.Add(item is DaemonEvent.Received received ? $"snapshot {received.Snapshot.ControlLoopHealthy}" : item.GetType().Name);
                if (described.Count == 2)
                {
                    break;
                }
            }

            Assert.Equal(["Connected", "snapshot True"], described);
            await serving;
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AMissingSocketIsADisconnectRatherThanACrash()
    {
        // The socket file is recreated on every vigild start, so "not there yet" is routine.
        var client = new VigildClient(
            Options.Create(new VigilOptions { VigildSocket = Path.Combine(Path.GetTempPath(), "vigil-does-not-exist.sock") }),
            NullLogger<VigildClient>.Instance);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await foreach (var item in client.StreamAsync(cancellation.Token))
        {
            Assert.IsType<DaemonEvent.Disconnected>(item);
            break;
        }
    }

    [Theory]
    [InlineData(null, "127.0.0.1", 5178)]
    [InlineData("", "127.0.0.1", 5178)]
    [InlineData("http://127.0.0.1:5178", "127.0.0.1", 5178)]
    [InlineData("http://127.0.0.1:5178/", "127.0.0.1", 5178)]
    [InlineData("http://10.0.0.4:9999", "10.0.0.4", 9999)]
    public void ParsesTheDevelopmentUrl(string? url, string host, int port)
    {
        Assert.Equal((host, port), VigildClient.TcpTarget(url));
    }

    [Fact]
    public void RefusesAUrlItCannotUnderstand()
    {
        Assert.Throws<InvalidOperationException>(() => VigildClient.TcpTarget("unix:/x"));
    }
}
