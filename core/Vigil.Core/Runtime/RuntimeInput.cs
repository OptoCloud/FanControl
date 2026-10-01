using System.Threading.Channels;
using Vigil.Core.Clients;

namespace Vigil.Core.Runtime;

/// <summary>Everything that reaches the runtime, on its one input channel, in the order it happened.</summary>
public abstract record RuntimeInput
{
    public sealed record Daemon(DaemonEvent Event) : RuntimeInput;

    public sealed record Ups(UpsPoll Poll) : RuntimeInput;

    /// <summary>
    /// A new live subscriber: gets the current state at once, then every change as one JSON
    /// message each. The runtime completes the writer when it drops the subscriber.
    /// </summary>
    public sealed record Subscribe(ChannelWriter<string> Subscriber) : RuntimeInput;
}
