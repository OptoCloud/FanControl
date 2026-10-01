using System.Text.Json;
using System.Threading.Channels;
using Vigil.Core.Protocol;

namespace Vigil.Core.Runtime;

/// <summary>
/// The runtime's live subscribers. Owned by the runtime's loop alone, so it takes no locks.
/// </summary>
public sealed class LiveBroadcaster
{
    private readonly List<ChannelWriter<string>> _subscribers = [];

    public int Count => _subscribers.Count;

    /// <summary>
    /// Sends <paramref name="currentState"/> first, so a new page is never briefly blank, then
    /// keeps the subscriber for every change. One that cannot even take the current state is
    /// closed instead.
    /// </summary>
    public void Subscribe(ChannelWriter<string> subscriber, IEnumerable<LiveMessage> currentState)
    {
        foreach (var message in currentState)
        {
            if (!subscriber.TryWrite(Serialize(message)))
            {
                subscriber.TryComplete();
                return;
            }
        }

        _subscribers.Add(subscriber);
    }

    /// <summary>
    /// Serialized once, shared by every subscriber. One that cannot keep up is dropped and its
    /// stream completed, so the browser's EventSource reconnects and starts again from the
    /// current state, rather than this process buffering for it without limit.
    /// </summary>
    public void Broadcast(LiveMessage message)
    {
        if (_subscribers.Count == 0)
        {
            return;
        }

        var json = Serialize(message);
        _subscribers.RemoveAll(subscriber =>
        {
            if (subscriber.TryWrite(json))
            {
                return false;
            }

            subscriber.TryComplete();
            return true;
        });
    }

    /// <summary>Ends every stream, so shutdown does not wait out open connections.</summary>
    public void CloseAll()
    {
        foreach (var subscriber in _subscribers)
        {
            subscriber.TryComplete();
        }

        _subscribers.Clear();
    }

    /// <summary>As the base type, so the "type" discriminator is written.</summary>
    public static string Serialize(LiveMessage message) => JsonSerializer.Serialize(message, VigilJson.Options);
}
