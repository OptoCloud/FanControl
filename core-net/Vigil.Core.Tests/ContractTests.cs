using System.Text.Json;
using System.Text.Json.Nodes;
using Vigil.Core.Configuration;
using Vigil.Core.Protocol;
using Xunit;

namespace Vigil.Core.Tests;

/// <summary>
/// The C# half of the wire contract.
/// </summary>
/// <remarks>
/// <para>
/// protocol/src/lib.rs defines the JSON; <c>cargo test -p vigil-protocol</c> generates
/// protocol/contract.json from it; web/src/lib/types.contract.test.ts checks the TypeScript
/// mirror against that file, and this checks the C# one. Three languages, one declaration,
/// and nothing held together by a comment asking people to remember (docs/STYLE.md §1.3).
/// </para>
/// <para>
/// The check is a round trip: every sample in the contract is deserialised into the C# type and
/// serialised straight back, then compared to the original. That one assertion catches a renamed
/// field (it fails to bind a required member), a field C# has and Rust does not (it appears in
/// the output), a field Rust has and C# does not (it vanishes from the output), and a type that
/// does not match (the value comes back differently).
/// </para>
/// </remarks>
public sealed class ContractTests
{
    private static readonly JsonNode Contract = Load();

    /// <summary>Every wire type this assembly mirrors, so one added in Rust and missed here fails.</summary>
    private static readonly string[] MirroredTypes =
    [
        nameof(DriveHealth), nameof(DriveState), nameof(EventRecord), nameof(FanStatus),
        nameof(SensorReading), nameof(Snapshot), nameof(UpsReading), nameof(UpsState),
    ];

    /// <summary>Every LiveMessage discriminator this assembly handles.</summary>
    private static readonly string[] HandledLiveMessages = ["daemon", "drives", "event", "snapshot", "ups"];

    private static JsonNode Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "contract.json");
        return JsonNode.Parse(File.ReadAllText(path))
            ?? throw new InvalidOperationException($"{path} is not JSON");
    }

    private static JsonNode Sample(string section, string name) =>
        Contract[section]?[name] ?? throw new InvalidOperationException($"contract.json has no {section}.{name}");

    /// <summary>Round-trips one sample through <typeparamref name="T"/> and compares both ways.</summary>
    private static void RoundTrips<T>(string name)
    {
        var expected = Sample("types", name);

        var parsed = JsonSerializer.Deserialize<T>(expected.ToJsonString(), VigilJson.Options);
        Assert.NotNull(parsed);

        var actual = JsonNode.Parse(JsonSerializer.Serialize(parsed, VigilJson.Options))!;
        AssertEquivalent(expected, actual, name);
    }

    [Fact]
    public void ItIsTheFileTheRustTestsGenerate()
    {
        Assert.Contains("cargo test -p vigil-protocol", Contract["$comment"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryTypeInTheContractIsMirroredHere()
    {
        // A type added on the Rust side that nothing here binds would otherwise go unnoticed
        // until something tried to read it.
        var inContract = Contract["types"]!.AsObject().Select(entry => entry.Key).OrderBy(name => name, StringComparer.Ordinal);
        var mirrored = MirroredTypes.OrderBy(name => name, StringComparer.Ordinal);

        Assert.Equal(mirrored, inContract);
    }

    [Fact] public void SensorReadingRoundTrips() => RoundTrips<SensorReading>(nameof(SensorReading));
    [Fact] public void FanStatusRoundTrips() => RoundTrips<FanStatus>(nameof(FanStatus));
    [Fact] public void DriveHealthRoundTrips() => RoundTrips<DriveHealth>(nameof(DriveHealth));
    [Fact] public void SnapshotRoundTrips() => RoundTrips<Snapshot>(nameof(Snapshot));
    [Fact] public void UpsReadingRoundTrips() => RoundTrips<UpsReading>(nameof(UpsReading));
    [Fact] public void UpsStateRoundTrips() => RoundTrips<UpsState>(nameof(UpsState));
    [Fact] public void DriveStateRoundTrips() => RoundTrips<DriveState>(nameof(DriveState));
    [Fact] public void EventRecordRoundTrips() => RoundTrips<EventRecord>(nameof(EventRecord));

    [Theory]
    [InlineData("snapshot")]
    [InlineData("daemon")]
    [InlineData("event")]
    [InlineData("drives")]
    [InlineData("ups")]
    public void EveryLiveMessageRoundTripsThroughItsDiscriminator(string tag)
    {
        var expected = Sample("liveMessages", tag);

        // Deserialised as the base type, so the "type" property is what selects the shape —
        // the same way vigil-web reads the stream.
        var parsed = JsonSerializer.Deserialize<LiveMessage>(expected.ToJsonString(), VigilJson.Options);
        Assert.NotNull(parsed);

        var actual = JsonNode.Parse(JsonSerializer.Serialize(parsed, VigilJson.Options))!;
        AssertEquivalent(expected, actual, tag);
    }

    [Fact]
    public void EveryLiveMessageVariantIsHandled()
    {
        var inContract = Contract["liveMessages"]!.AsObject().Select(entry => entry.Key).OrderBy(tag => tag, StringComparer.Ordinal);
        var handled = HandledLiveMessages.OrderBy(tag => tag, StringComparer.Ordinal);

        Assert.Equal(handled, inContract);
    }

    [Theory]
    [InlineData("SensorCategory", typeof(SensorCategory))]
    [InlineData("PwmMode", typeof(PwmMode))]
    [InlineData("Severity", typeof(Severity))]
    public void EveryEnumVariantMatches(string name, Type type)
    {
        var variants = Contract["enums"]![name]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();

        // Same count, so a variant that exists only in C# is caught as well.
        Assert.Equal(Enum.GetValues(type).Length, variants.Length);

        foreach (var variant in variants)
        {
            var json = $"\"{variant}\"";
            var parsed = JsonSerializer.Deserialize(json, type, VigilJson.Options);
            Assert.NotNull(parsed);
            // And back, so the spelling is pinned in both directions.
            Assert.Equal(json, JsonSerializer.Serialize(parsed, type, VigilJson.Options));
        }
    }

    [Fact]
    public void TheSharedLimitsMatchProtocolLimitsRs()
    {
        var limits = Contract["limits"]!;

        Assert.Equal(limits["streamKeepaliveSeconds"]!.GetValue<int>(), (int)Limits.StreamKeepalive.TotalSeconds);
        Assert.Equal(limits["streamSilenceTimeoutSeconds"]!.GetValue<int>(), (int)Limits.StreamSilenceTimeout.TotalSeconds);
        Assert.Equal(Limits.CoreDefaultPort, limits["coreDefaultPort"]!.GetValue<int>());
        Assert.Equal(Limits.DatabaseUrlDefault, limits["databaseUrlDefault"]!.GetValue<string>());
        Assert.Equal(
            limits["databaseStatementTimeoutMillis"]!.GetValue<int>(),
            (int)Limits.DatabaseStatementTimeout.TotalMilliseconds);
    }

    /// <summary>
    /// Structural comparison, because the two serialisers format numbers differently: serde
    /// writes an f64 of 100 as <c>100.0</c> and System.Text.Json writes it as <c>100</c>. That is
    /// the same number, and a test that called it a contract break would be noise. Everything
    /// else — names, nesting, strings, nulls, array order — must match exactly.
    /// </summary>
    private static void AssertEquivalent(JsonNode expected, JsonNode actual, string path)
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
