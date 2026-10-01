using System.ComponentModel.DataAnnotations;

namespace Vigil.Core.Configuration;

/// <summary>
/// Everything vigil-core is configured with, all from the environment, as the Rust version was
/// (/etc/vigil-core.env in production).
/// </summary>
public sealed class VigilOptions
{
    /// <summary>vigild's unix socket, bind-mounted into the container.</summary>
    public string? VigildSocket { get; init; }

    /// <summary>host:port of a TCP stand-in. Development only, against dev/mock-vigild.mjs.</summary>
    public string? VigildUrl { get; init; }

    /// <summary>The role that owns the schema and writes every row.</summary>
    [Required]
    public string DatabaseUrl { get; init; } = Limits.DatabaseUrlDefault;

    /// <summary>
    /// A SELECT-only role for the read path (history, the event log). Optional: unset, the read
    /// path shares <see cref="DatabaseUrl"/> and startup says so.
    /// </summary>
    /// <remarks>
    /// This exists because vigil-core and vigil-web are one process now. The separation phase 1
    /// built relied on them being two, with only the writer holding a write credential; one
    /// address space cannot give that back, but a SELECT-only role still means a bug in a query
    /// path cannot write. docs/SECURITY.md §3.4.
    /// </remarks>
    public string? DatabaseUrlReadonly { get; init; }

    /// <summary>vigild publishes every 2s. That resolution matters live, not in history.</summary>
    public double PersistIntervalSeconds { get; init; } = 10;

    public double RawRetentionDays { get; init; } = 30;

    /// <summary>Unset leaves the UPS out entirely rather than showing it as broken.</summary>
    public string? NutHost { get; init; }

    public int NutPort { get; init; } = 3493;

    /// <summary>The UPS's name on upsd ([apc] in ups.conf), which its history is stored under.</summary>
    public string NutUps { get; init; } = "apc";

    public double NutPollSeconds { get; init; } = 5;

    /// <summary>Optional. HTTPS is free in .NET, so this no longer goes through curl.</summary>
    public string? NtfyUrl { get; init; }

    /// <summary>
    /// The one address vigil-core listens on, which serves the dashboard to the LAN: every
    /// interface by default. There is deliberately no second, private listener (ADR-008).
    /// </summary>
    public string CoreHost { get; init; } = "0.0.0.0";

    public int CorePort { get; init; } = Limits.CoreDefaultPort;

    /// <summary>
    /// The dashboard's static build (SvelteKit's output directory). Unset serves the API only,
    /// which is what development wants: there, Vite serves the page and proxies the API.
    /// </summary>
    public string? WebRoot { get; init; }

    /// <summary>How long vigild has to be unreachable before that counts as an incident.</summary>
    public double DaemonLostAfterSeconds { get; init; } = 20;
}
