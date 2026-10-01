using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;
using Vigil.Core.Clients;
using Vigil.Core.Configuration;
using Xunit;

namespace Vigil.Core.Tests;

public sealed class NutClientTests
{
    /// <summary>
    /// A upsd stand-in that answers every <c>LIST VAR</c> with <paramref name="answer"/>.
    /// </summary>
    private static int FakeUpsd(string answer, bool closeImmediately = false)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        _ = Task.Run(async () =>
        {
            using (listener)
            {
                using var client = await listener.AcceptTcpClientAsync();
                if (closeImmediately)
                {
                    return;
                }

                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);

                while (await reader.ReadLineAsync() is { } request)
                {
                    if (!request.StartsWith("LIST VAR", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    await stream.WriteAsync(Encoding.UTF8.GetBytes(answer));
                    await stream.FlushAsync();
                }
            }
        });

        return port;
    }

    private static NutClient ClientFor(int port) =>
        new(Options.Create(new VigilOptions { NutHost = "127.0.0.1", NutPort = port, NutUps = "apc", NutPollSeconds = 0.05 }));

    private static async Task<List<UpsPoll>> Collect(int port, int count)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var polls = new List<UpsPoll>();

        await foreach (var poll in ClientFor(port).PollAsync(cancellation.Token))
        {
            polls.Add(poll);
            if (polls.Count == count)
            {
                break;
            }
        }

        return polls;
    }

    [Fact]
    public async Task PollsUpsdAndKeepsPollingOnTheSameConnection()
    {
        var answer = "BEGIN LIST VAR apc\n"
            + "VAR apc battery.charge \"100\"\n"
            + "VAR apc ups.status \"OL\"\n"
            + "END LIST VAR apc\n";

        var polls = await Collect(FakeUpsd(answer), 2);

        var readings = polls.OfType<UpsPoll.Read>().ToList();
        Assert.Equal(2, readings.Count);
        Assert.Equal(100, readings[0].Reading.BatteryCharge);
        Assert.Equal(["OL"], readings[0].Reading.Status);
        Assert.Equal("apc", readings[0].Reading.Name);
    }

    [Fact]
    public async Task UpsdsOwnErrorIsReportedWithoutDroppingTheConnection()
    {
        // DATA-STALE means upsd is reachable but has lost the UPS itself, which is worth saying
        // rather than reporting as "cannot reach upsd".
        var polls = await Collect(FakeUpsd("ERR DATA-STALE\n"), 1);

        var failed = Assert.IsType<UpsPoll.Failed>(polls[0]);
        Assert.Equal("upsd: DATA-STALE", failed.Reason);
    }

    [Fact]
    public async Task AClosedConnectionIsReportedAndRetried()
    {
        var polls = await Collect(FakeUpsd(string.Empty, closeImmediately: true), 1);

        var failed = Assert.IsType<UpsPoll.Failed>(polls[0]);
        Assert.Contains("closed", failed.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnUnreachableUpsdIsReportedRatherThanThrowing()
    {
        // Nothing listening: the loop must report it and carry on, not fall over.
        var free = new TcpListener(IPAddress.Loopback, 0);
        free.Start();
        var port = ((IPEndPoint)free.LocalEndpoint).Port;
        free.Stop();

        var polls = await Collect(port, 1);

        Assert.IsType<UpsPoll.Failed>(polls[0]);
    }
}
