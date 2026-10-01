namespace Vigil.Core.Configuration;

/// <summary>
/// Values that both sides of a process boundary have to agree on, mirroring
/// protocol/src/limits.rs. A test checks these against protocol/contract.json, so the three
/// languages cannot drift apart (docs/STYLE.md §1.2).
/// </summary>
public static class Limits
{
    /// <summary>How long an event stream may stay silent before it sends a keepalive comment.</summary>
    public static readonly TimeSpan StreamKeepalive = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long a reader waits without a byte before treating a stream as dead. More than twice
    /// the keepalive interval, so one keepalive lost to a slow hop is not read as a disconnect.
    /// </summary>
    public static readonly TimeSpan StreamSilenceTimeout = TimeSpan.FromSeconds(50);

    public const int CoreDefaultPort = 3001;

    /// <summary>Only ever used in development; production sets DATABASE_URL.</summary>
    public const string DatabaseUrlDefault = "postgres://vigil@localhost/vigil";

    /// <summary>No query should outlive a page load (docs/SECURITY.md §6).</summary>
    public static readonly TimeSpan DatabaseStatementTimeout = TimeSpan.FromSeconds(15);
}
