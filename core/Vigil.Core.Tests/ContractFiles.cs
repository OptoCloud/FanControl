using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Xunit;

namespace Vigil.Core.Tests;

/// <summary>
/// Where the two halves of the wire contract live, and the comparison both checks use.
/// </summary>
/// <remarks>
/// Each process boundary is described by the side that produces it: daemon/contract.json by
/// vigild's Rust tests, core/contract.json by these. The paths are found from this source file
/// rather than the build output, because core/contract.json is written back into the source tree.
/// </remarks>
internal static class ContractFiles
{
    public static string Daemon => Path.Combine(RepositoryRoot(), "daemon", "contract.json");

    public static string Core => Path.Combine(RepositoryRoot(), "core", "contract.json");

    public static JsonNode Load(string path) =>
        JsonNode.Parse(File.ReadAllText(path)) ?? throw new InvalidOperationException($"{path} is not JSON");

    private static string RepositoryRoot([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    /// <summary>
    /// Structural comparison, because the two serialisers format numbers differently: serde
    /// writes an f64 of 100 as <c>100.0</c> and System.Text.Json writes it as <c>100</c>. That is
    /// the same number, and a test that called it a contract break would be noise. Everything
    /// else (names, nesting, strings, nulls, array order) must match exactly.
    /// </summary>
    public static void AssertEquivalent(JsonNode expected, JsonNode actual, string path)
    {
        switch (expected)
        {
            case JsonObject expectedObject:
                var actualObject = Assert.IsType<JsonObject>(actual);
                Assert.Equal(
                    expectedObject.Select(e => e.Key).OrderBy(k => k, StringComparer.Ordinal),
                    actualObject.Select(e => e.Key).OrderBy(k => k, StringComparer.Ordinal));
                foreach (var (key, value) in expectedObject)
                {
                    AssertEquivalent(value!, actualObject[key]!, $"{path}.{key}");
                }

                break;

            case JsonArray expectedArray:
                var actualArray = Assert.IsType<JsonArray>(actual);
                Assert.Equal(expectedArray.Count, actualArray.Count);
                for (var i = 0; i < expectedArray.Count; i++)
                {
                    AssertEquivalent(expectedArray[i]!, actualArray[i]!, $"{path}[{i}]");
                }

                break;

            default:
                var expectedText = expected.ToJsonString();
                var actualText = actual.ToJsonString();
                if (expectedText != actualText
                    && !(decimal.TryParse(expectedText, out var left) && decimal.TryParse(actualText, out var right) && left == right))
                {
                    Assert.Fail($"{path}: contract has {expectedText}, C# produced {actualText}");
                }

                break;
        }
    }
}
