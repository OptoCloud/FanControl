using System.Threading.Channels;

namespace Vigil.Core.Runtime;

/// <summary>
/// Hands out live subscriptions: caps how many are open, and attaches each one to the runtime.
/// </summary>
/// <remarks>
/// The cap is decided here rather than in the runtime because a refusal has to be an HTTP 503
/// before the response starts, and the runtime's loop cannot answer synchronously.
/// </remarks>
public sealed class LiveHub(ChannelWriter<RuntimeInput> inputs)
{
    /// <summary>
    /// How many browser streams may be open at once (docs/SECURITY.md §4.2). Generous for a
    /// dashboard, where one tab is one stream and a closed tab frees its slot at once; what it
    /// stops is a page left reloading in a loop growing the set without bound. vigil-web held the
    /// same cap before the two processes merged.
    /// </summary>
    public const int MaxSubscribers = 64;

    /// <summary>
    /// Messages a subscriber may fall behind by before it is dropped. The stream carries events,
    /// which the next message does not supersede, so the backlog is deep.
    /// </summary>
    private const int SubscriberBacklog = 256;

    private int _open;

    /// <summary>
    /// A new subscription, or null when <see cref="MaxSubscribers"/> are already open or the
    /// runtime has stopped. The caller disposes it when its stream ends.
    /// </summary>
    public LiveSubscription? TrySubscribe()
    {
        if (Interlocked.Increment(ref _open) > MaxSubscribers)
        {
            Interlocked.Decrement(ref _open);
            return null;
        }

        var subscription = new LiveSubscription(SubscriberBacklog, () => Interlocked.Decrement(ref _open));
        if (!inputs.TryWrite(new RuntimeInput.Subscribe(subscription.Writer)))
        {
            subscription.Dispose();
            return null;
        }

        return subscription;
    }
}

/// <summary>One live stream's queue. Disposing it frees its slot and tells the runtime to drop it.</summary>
public sealed class LiveSubscription : IDisposable
{
    private readonly Channel<string> _messages;
    private readonly Action _release;
    private int _disposed;

    internal LiveSubscription(int backlog, Action release)
    {
        // Wait, not DropOldest: a full queue makes the runtime's TryWrite fail, which is how it
        // knows to drop a subscriber that cannot keep up. Silently losing an event would be worse.
        _messages = Channel.CreateBounded<string>(
            new BoundedChannelOptions(backlog) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        _release = release;
    }

    public ChannelReader<string> Reader => _messages.Reader;

    internal ChannelWriter<string> Writer => _messages.Writer;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            // Completing the writer makes the runtime's next write to it fail, so it is dropped.
            _messages.Writer.TryComplete();
            _release();
        }
    }
}
