using System.Net;
using Microsoft.Extensions.Options;

namespace Vigil.Core.Configuration;

/// <summary>
/// Validates the configuration before anything acts on it, as vigild validates its TOML before
/// touching a fan (docs/SECURITY.md §4.4). A bad value fails startup with every problem listed,
/// rather than surfacing as a poll that fails every few seconds.
/// </summary>
public sealed class VigilOptionsValidator : IValidateOptions<VigilOptions>
{
    public ValidateOptionsResult Validate(string? name, VigilOptions options)
    {
        var problems = new List<string>();

        foreach (var (field, value) in new[]
                 {
                     ("PERSIST_INTERVAL_SECONDS", options.PersistIntervalSeconds),
                     ("RAW_RETENTION_DAYS", options.RawRetentionDays),
                     ("NUT_POLL_SECONDS", options.NutPollSeconds),
                     ("DAEMON_LOST_AFTER_SECONDS", options.DaemonLostAfterSeconds),
                 })
        {
            if (!double.IsFinite(value) || value <= 0)
            {
                problems.Add($"{field} must be a positive number, got '{value}'.");
            }
        }

        if (options.NutPort is < 1 or > 65535)
        {
            problems.Add($"NUT_PORT must be a port number, got '{options.NutPort}'.");
        }

        if (options.CorePort is < 1 or > 65535)
        {
            problems.Add($"CORE_PORT must be a port number, got '{options.CorePort}'.");
        }

        if (string.IsNullOrWhiteSpace(options.DatabaseUrl))
        {
            problems.Add("DATABASE_URL must not be empty.");
        }

        if (ListenAddress(options) is null)
        {
            problems.Add(
                $"CORE_HOST is '{options.CoreHost}', which is not loopback. vigil-core must not be reachable from "
                + "the network: run it beside nothing else and leave CORE_HOST unset (127.0.0.1). If you really mean "
                + "to expose it, set CORE_ALLOW_NON_LOOPBACK=1 and read docs/SECURITY.md §1 first.");
        }

        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }

    /// <summary>
    /// The address to bind, or null if the configuration asks for a routable one without the
    /// explicit opt-in.
    /// </summary>
    /// <remarks>
    /// vigil-core is read-only today, but action endpoints are meant to arrive here, and a single
    /// typo in CORE_HOST is the difference between "the dashboard beside it can reach this" and
    /// "the whole network can". A config value that decides a bind address is validated before it
    /// reaches the syscall: docs/SECURITY.md §1.1.
    /// </remarks>
    public static string? ListenAddress(VigilOptions options)
    {
        if (!IsLoopback(options.CoreHost) && !options.CoreAllowNonLoopback)
        {
            return null;
        }

        return $"http://{options.CoreHost}:{options.CorePort}";
    }

    /// <summary>
    /// True only for an address that cannot be routed to from another host. An empty host and
    /// "0.0.0.0"/"[::]" mean <em>every</em> interface, so they are deliberately not loopback.
    /// </summary>
    private static bool IsLoopback(string host)
    {
        var address = host.Trim().Trim('[', ']');

        if (string.Equals(address, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Not an address we can reason about (a hostname, or nonsense): assume the worst.
        return IPAddress.TryParse(address, out var parsed) && IPAddress.IsLoopback(parsed);
    }
}
