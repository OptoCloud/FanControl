using Microsoft.Extensions.Configuration;

namespace Vigil.Core.Configuration;

/// <summary>
/// Reads vigil-core's configuration from the environment variable names it has always used.
/// </summary>
/// <remarks>
/// These names are a published contract: they are in <c>/etc/vigil-core.env</c>, in
/// <c>core/deploy/vigil-core.env</c> and in the README, and the Rust implementation read them
/// directly. .NET's own environment provider would bind <c>CorePort</c>, not <c>CORE_PORT</c>,
/// so the mapping is spelled out here rather than left to a naming convention that would
/// silently read nothing — which is exactly what it did before this existed.
/// </remarks>
public static class VigilEnvironment
{
    /// <summary>Environment variable name to the option property it sets.</summary>
    private static readonly (string Variable, string Property)[] Names =
    [
        ("VIGILD_SOCKET", nameof(VigilOptions.VigildSocket)),
        ("VIGILD_URL", nameof(VigilOptions.VigildUrl)),
        ("DATABASE_URL", nameof(VigilOptions.DatabaseUrl)),
        ("DATABASE_URL_READONLY", nameof(VigilOptions.DatabaseUrlReadonly)),
        ("PERSIST_INTERVAL_SECONDS", nameof(VigilOptions.PersistIntervalSeconds)),
        ("RAW_RETENTION_DAYS", nameof(VigilOptions.RawRetentionDays)),
        ("NUT_HOST", nameof(VigilOptions.NutHost)),
        ("NUT_PORT", nameof(VigilOptions.NutPort)),
        ("NUT_UPS", nameof(VigilOptions.NutUps)),
        ("NUT_POLL_SECONDS", nameof(VigilOptions.NutPollSeconds)),
        ("NTFY_URL", nameof(VigilOptions.NtfyUrl)),
        ("CORE_HOST", nameof(VigilOptions.CoreHost)),
        ("CORE_PORT", nameof(VigilOptions.CorePort)),
        ("DAEMON_LOST_AFTER_SECONDS", nameof(VigilOptions.DaemonLostAfterSeconds)),
        ("WEB_ROOT", nameof(VigilOptions.WebRoot)),
    ];

    /// <summary>Every variable vigil-core reads, for documentation and for tests.</summary>
    public static IEnumerable<string> Variables => Names.Select(name => name.Variable);

    public static IConfigurationBuilder AddVigilEnvironment(this IConfigurationBuilder builder) =>
        builder.AddVigilEnvironment(Environment.GetEnvironmentVariable);

    /// <summary>Overload taking the lookup, so a test needs no ambient environment.</summary>
    public static IConfigurationBuilder AddVigilEnvironment(this IConfigurationBuilder builder, Func<string, string?> lookup)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(lookup);

        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (variable, property) in Names)
        {
            // An empty variable means "unset", as it did in the Rust version: an EnvironmentFile
            // line left as `NTFY_URL=` should not configure an empty URL.
            if (lookup(variable) is { Length: > 0 } value)
            {
                settings[property] = value;
            }
        }

        return builder.AddInMemoryCollection(settings);
    }
}
