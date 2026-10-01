using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vigil.Core.Protocol;

/// <summary>
/// The one set of JSON options for everything that crosses a process boundary.
/// </summary>
/// <remarks>
/// camelCase comes from the policy rather than per-property attributes, because the Rust side
/// gets its names from <c>rename_all = "camelCase"</c> the same way. ContractTests is what proves
/// the two algorithms agree on every field vigil actually sends, which is the only claim that
/// matters; a disagreement on some hypothetical name would be caught the moment it is used.
/// </remarks>
public static class VigilJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,

        // A null is meaningful here: "celsiusOrNull": null is how an unreadable sensor is
        // reported, and omitting it would make the field absent rather than null.
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,

        // Every byte of this goes over a loopback socket to one consumer, so there is nothing
        // to gain from indentation and a measurable cost at 13 drives every two seconds.
        WriteIndented = false,
    };
}
