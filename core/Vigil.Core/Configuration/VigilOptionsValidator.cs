using System.Net;
using System.Net.Sockets;
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

        // Checked here rather than left to the first request, which would 404 the dashboard with
        // nothing in the log to say why.
        if (options.WebRoot is { Length: > 0 } webRoot && !File.Exists(Path.Combine(webRoot, "index.html")))
        {
            problems.Add($"WEB_ROOT is '{webRoot}', which has no index.html. Point it at the dashboard's build output.");
        }

        if (options.NtfyUrl is { Length: > 0 } ntfy
            && (!Uri.TryCreate(ntfy, UriKind.Absolute, out var ntfyUri) || ntfyUri.Scheme is not ("https" or "http")))
        {
            // The URL itself is not repeated: a private topic's name is its only secret.
            problems.Add("NTFY_URL must be an absolute http(s) URL.");
        }

        if (ListenAddress(options) is null)
        {
            problems.Add(
                $"CORE_HOST is '{options.CoreHost}', which is not an IP address. Use one (0.0.0.0 for every "
                + "interface, the default); a hostname here would silently bind every interface anyway.");
        }

        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }

    /// <summary>
    /// The URL Kestrel binds, or null if <see cref="VigilOptions.CoreHost"/> is not an address.
    /// </summary>
    /// <remarks>
    /// One listener, on the LAN (ADR-008). Validated before it reaches the bind because Kestrel
    /// treats any name but "localhost" as "every interface", so a typo would not fail, it would
    /// just listen somewhere other than intended.
    /// </remarks>
    public static string? ListenAddress(VigilOptions options)
    {
        var host = options.CoreHost.Trim().Trim('[', ']');
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return $"http://localhost:{options.CorePort}";
        }

        if (!IPAddress.TryParse(host, out var address))
        {
            return null;
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"http://[{address}]:{options.CorePort}"
            : $"http://{address}:{options.CorePort}";
    }
}
