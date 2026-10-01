using Vigil.Core.Protocol;

namespace Vigil.Core.Clients;

/// <summary>What the one connection to vigild reports: its reachability, and its snapshots.</summary>
public abstract record DaemonEvent
{
    public sealed record Connected : DaemonEvent;

    /// <summary>Why the stream ended. Carried through so the event log can say.</summary>
    public sealed record Disconnected(string Reason) : DaemonEvent;

    public sealed record Received(Snapshot Snapshot) : DaemonEvent;
}
