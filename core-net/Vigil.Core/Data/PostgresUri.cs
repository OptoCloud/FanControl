using Npgsql;
using Vigil.Core.Configuration;

namespace Vigil.Core.Data;

/// <summary>
/// Turns a <c>postgres://</c> URL into an Npgsql connection string.
/// </summary>
/// <remarks>
/// <para>
/// Npgsql takes keyword connection strings and does not parse URLs, but <c>DATABASE_URL</c> is a
/// URL: it is what the Rust implementation read, what <c>core/deploy/vigil-core.env</c> contains,
/// and the form every other tool in this stack uses. Converting is this type's whole job.
/// </para>
/// <para>
/// Percent-decoding the user info matters and is the easy thing to get wrong: a password
/// containing <c>@</c>, <c>:</c> or <c>/</c> has to be encoded in a URL, and failing to decode it
/// produces a confusing authentication failure rather than an obvious error.
/// </para>
/// </remarks>
public static class PostgresUri
{
    private const int DefaultPort = 5432;

    /// <summary>
    /// The connection string, with vigil's own settings applied: a statement timeout so no query
    /// can tie up a connection indefinitely (docs/SECURITY.md §6), and an application name so
    /// <c>pg_stat_activity</c> says which process a connection belongs to.
    /// </summary>
    /// <param name="url">A <c>postgres://</c> or <c>postgresql://</c> URL.</param>
    /// <param name="applicationName">What to report to the server, e.g. "vigil-core".</param>
    public static string ToConnectionString(string url, string applicationName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        // Already a keyword string? Accept it, so an operator can set Npgsql options directly.
        if (!url.Contains("://", StringComparison.Ordinal))
        {
            return Decorate(new NpgsqlConnectionStringBuilder(url), applicationName);
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != "postgres" && parsed.Scheme != "postgresql"))
        {
            throw new ArgumentException($"DATABASE_URL must be a postgres:// URL or an Npgsql connection string, got '{Redact(url)}'", nameof(url));
        }

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = parsed.Host,
            Port = parsed.IsDefaultPort || parsed.Port < 0 ? DefaultPort : parsed.Port,
        };

        // "/vigil" to "vigil". An empty path means the database is named after the user, as
        // libpq does.
        var database = parsed.AbsolutePath.TrimStart('/');
        if (database.Length > 0)
        {
            builder.Database = Uri.UnescapeDataString(database);
        }

        if (parsed.UserInfo.Length > 0)
        {
            var separator = parsed.UserInfo.IndexOf(':', StringComparison.Ordinal);
            if (separator < 0)
            {
                builder.Username = Uri.UnescapeDataString(parsed.UserInfo);
            }
            else
            {
                builder.Username = Uri.UnescapeDataString(parsed.UserInfo[..separator]);
                builder.Password = Uri.UnescapeDataString(parsed.UserInfo[(separator + 1)..]);
            }
        }

        // Anything after "?" is passed through as Npgsql keywords, which is how sslmode and
        // friends are set.
        foreach (var pair in parsed.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0)
            {
                builder[Uri.UnescapeDataString(pair[..separator])] = Uri.UnescapeDataString(pair[(separator + 1)..]);
            }
        }

        return Decorate(builder, applicationName);
    }

    private static string Decorate(NpgsqlConnectionStringBuilder builder, string applicationName)
    {
        builder.CommandTimeout = (int)Limits.DatabaseStatementTimeout.TotalSeconds;
        builder.ApplicationName = applicationName;
        return builder.ConnectionString;
    }

    /// <summary>
    /// A URL with its password replaced, for an error message. docs/SECURITY.md §7: a
    /// credential never reaches a log line or an exception.
    /// </summary>
    public static string Redact(string url)
    {
        ArgumentNullException.ThrowIfNull(url);

        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
        {
            return url;
        }

        var authorityStart = schemeEnd + 3;
        var authorityEnd = url.IndexOf('/', authorityStart);
        var authority = authorityEnd < 0 ? url[authorityStart..] : url[authorityStart..authorityEnd];

        var at = authority.LastIndexOf('@');
        if (at < 0)
        {
            return url;
        }

        var userInfo = authority[..at];
        var separator = userInfo.IndexOf(':', StringComparison.Ordinal);
        if (separator < 0)
        {
            return url;
        }

        return string.Concat(url.AsSpan(0, authorityStart), userInfo.AsSpan(0, separator + 1), "***", url.AsSpan(authorityStart + at));
    }
}
