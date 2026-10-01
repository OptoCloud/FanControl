using System.Collections.Concurrent;
using System.Reflection;

namespace Vigil.Core.Data;

/// <summary>
/// The SQL in <c>core/sql/</c>, embedded in this assembly and loaded by file name.
/// </summary>
/// <remarks>
/// SQL lives in .sql files so it can be read, highlighted and run by hand against a real
/// database (docs/STYLE.md §3.2). Embedding rather than reading from disk means a deployment is
/// still one self-contained thing, and a query that goes missing fails at startup rather than
/// the first time it is used.
/// </remarks>
public static class SqlText
{
    private static readonly Assembly Assembly = typeof(SqlText).Assembly;
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.Ordinal);

    /// <param name="name">The file's base name, e.g. "insert_sensor_samples".</param>
    public static string Load(string name) => Cache.GetOrAdd(name, Read);

    private static string Read(string name)
    {
        var resource = $"Vigil.Core.Sql.{name}.sql";
        using var stream = Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException(
                $"{resource} is not embedded. Expected core/sql/{name}.sql; the csproj globs that directory.");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Every embedded query, so a test can prove they all load.</summary>
    public static IEnumerable<string> Names =>
        Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("Vigil.Core.Sql.", StringComparison.Ordinal))
            .Select(name => name["Vigil.Core.Sql.".Length..^".sql".Length]);
}
