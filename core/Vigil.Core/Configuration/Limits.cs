namespace Vigil.Core.Configuration;

/// <summary>
/// vigil-core's bounds whose values something else depends on (docs/STYLE.md §4.1).
/// </summary>
/// <remarks>
/// Only <see cref="StreamSilenceTimeout"/> still crosses a language boundary: it must outlast
/// vigild's keepalive, which daemon/contract.json publishes and DaemonContractTests checks.
/// Everything else here is vigil-core's own since the Rust core and vigil-web's server merged
/// into this process.
/// </remarks>
public static class Limits
{
    /// <summary>
    /// How long the browser's live stream may stay silent before a keepalive is sent, which is
    /// also what notices a browser that has gone away and frees its slot.
    /// </summary>
    public static readonly TimeSpan StreamKeepalive = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long vigild's stream may go without a byte before it is treated as dead. More than
    /// twice vigild's keepalive, so one keepalive lost to a slow moment is not read as a
    /// disconnect.
    /// </summary>
    public static readonly TimeSpan StreamSilenceTimeout = TimeSpan.FromSeconds(50);

    /// <summary>The dashboard's port, and the one the web dev server proxies /api to.</summary>
    public const int CoreDefaultPort = 3001;

    /// <summary>Only ever used in development; production sets DATABASE_URL.</summary>
    public const string DatabaseUrlDefault = "postgres://vigil@localhost/vigil";

    /// <summary>No query should outlive a page load (docs/SECURITY.md §6).</summary>
    public static readonly TimeSpan DatabaseStatementTimeout = TimeSpan.FromSeconds(15);
}
