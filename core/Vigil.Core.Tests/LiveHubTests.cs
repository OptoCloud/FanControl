using System.Net.ServerSentEvents;
using System.Threading.Channels;
using Vigil.Core.Api;
using Vigil.Core.Runtime;
using Xunit;

namespace Vigil.Core.Tests;

public sealed class LiveHubTests
{
    private readonly Channel<RuntimeInput> _inputs = Channel.CreateUnbounded<RuntimeInput>();

    private ChannelWriter<string> SubscriberAskedFor()
    {
        Assert.True(_inputs.Reader.TryRead(out var input));
        return Assert.IsType<RuntimeInput.Subscribe>(input).Subscriber;
    }

    private static async Task<List<SseItem<string>>> Collect(IAsyncEnumerable<SseItem<string>> stream, int atMost)
    {
        var items = new List<SseItem<string>>();
        await foreach (var item in stream)
        {
            items.Add(item);
            if (items.Count == atMost)
            {
                break;
            }
        }

        return items;
    }

    [Fact]
    public void RefusesPastTheCapAndFreesASlotWhenAStreamEnds()
    {
        var hub = new LiveHub(_inputs.Writer);
        var open = Enumerable.Range(0, LiveHub.MaxSubscribers).Select(_ => hub.TrySubscribe()).ToList();
        Assert.All(open, Assert.NotNull);

        Assert.Null(hub.TrySubscribe());

        open[0]!.Dispose();
        open[0]!.Dispose();
        Assert.NotNull(hub.TrySubscribe());
        Assert.Null(hub.TrySubscribe());
    }

    [Fact]
    public void RefusesOnceTheRuntimeHasStoppedWithoutLeakingTheSlot()
    {
        var hub = new LiveHub(_inputs.Writer);
        _inputs.Writer.Complete();

        for (var attempt = 0; attempt <= LiveHub.MaxSubscribers; attempt++)
        {
            Assert.Null(hub.TrySubscribe());
        }
    }

    [Fact]
    public async Task RelaysWhatTheRuntimeQueuesInOrderAndEndsWhenTheRuntimeDropsTheStream()
    {
        using var subscription = new LiveHub(_inputs.Writer).TrySubscribe()!;
        var runtimeSide = SubscriberAskedFor();
        runtimeSide.TryWrite("""{"type":"daemon","connected":true}""");
        runtimeSide.TryWrite("""{"type":"daemon","connected":false}""");
        runtimeSide.TryComplete();

        var items = await Collect(LiveEndpoint.Relay(subscription, TimeSpan.FromMinutes(1), CancellationToken.None), 10);

        Assert.Equal(["""{"type":"daemon","connected":true}""", """{"type":"daemon","connected":false}"""], items.Select(item => item.Data));

        // Unnamed, so EventSource's onmessage gets them: the default type, which the formatter
        // leaves off the wire (ApiTests checks the bytes).
        Assert.All(items, item => Assert.Equal(SseParser.EventTypeDefault, item.EventType));
    }

    [Fact]
    public async Task SendsAKeepaliveWhenTheStreamIsQuiet()
    {
        using var subscription = new LiveHub(_inputs.Writer).TrySubscribe()!;

        var items = await Collect(LiveEndpoint.Relay(subscription, TimeSpan.FromMilliseconds(20), CancellationToken.None), 2);

        Assert.All(items, item => Assert.Equal("keepalive", item.EventType));
    }

    [Fact]
    public async Task ABrowserThatLeavesFreesItsSlotAndTheRuntimeStopsWritingToIt()
    {
        var hub = new LiveHub(_inputs.Writer);
        var others = Enumerable.Range(0, LiveHub.MaxSubscribers - 1).Select(_ => hub.TrySubscribe()).ToList();
        var leaving = hub.TrySubscribe()!;
        Assert.Null(hub.TrySubscribe());

        using var browser = new CancellationTokenSource();
        var relay = Collect(LiveEndpoint.Relay(leaving, TimeSpan.FromMinutes(1), browser.Token), 10);
        await browser.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => relay);

        Assert.NotNull(hub.TrySubscribe());
        Assert.True(leaving.Reader.Completion.IsCompleted);
    }
}
