using System.Globalization;

namespace Vigil.Core.Protocol;

/// <summary>
/// Timestamps as they go on the wire: RFC 3339 UTC with exactly three subsecond digits, e.g.
/// "2026-09-21T20:39:27.482Z".
/// </summary>
/// <remarks>
/// Pinned to three digits and a literal Z because that is the format already on the wire and in
/// protocol/contract.json. The Rust side needs a custom format description for the same reason:
/// a general RFC 3339 formatter emits as many subsecond digits as the value happens to need.
/// </remarks>
public static class Rfc3339
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public static string Now() => Format_(DateTimeOffset.UtcNow);

    public static string From(DateTimeOffset at) => Format_(at);

    private static string Format_(DateTimeOffset at) =>
        at.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture);
}
