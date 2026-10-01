using System.Text.Json;
using System.Text.Json.Nodes;
using Vigil.Core.Configuration;
using Vigil.Core.Protocol;
using Xunit;

namespace Vigil.Core.Tests;

/// <summary>
/// vigil-core as vigild's client: its mirror of vigild's snapshot types, checked against
/// daemon/contract.json, which <c>cargo test -p vigild</c> generates from the Rust types.
/// </summary>
/// <remarks>
/// The check is a round trip: every sample is deserialised into the C# type and serialised
/// straight back. That one assertion catches a renamed field (it fails to bind a required
/// member), a field C# has and Rust does not (it appears in the output), a field Rust has and C#
/// does not (it vanishes from the output), and a type that does not match.
/// </remarks>
public sealed class DaemonContractTests
{
    private static readonly JsonNode Contract = ContractFiles.Load(ContractFiles.Daemon);

    /// <summary>Every vigild type mirrored here, so one added in Rust and missed here fails.</summary>
    private static readonly string[] MirroredTypes = [nameof(DriveHealth), nameof(FanStatus), nameof(SensorReading), nameof(Snapshot)];

    private static void RoundTrips<T>(string name)
    {
        var expected = Contract["types"]?[name] ?? throw new InvalidOperationException($"daemon/contract.json has no types.{name}");

        var parsed = JsonSerializer.Deserialize<T>(expected.ToJsonString(), VigilJson.Options);
        Assert.NotNull(parsed);

        ContractFiles.AssertEquivalent(expected, JsonNode.Parse(JsonSerializer.Serialize(parsed, VigilJson.Options))!, name);
    }

    [Fact]
    public void EveryTypeVigildSendsIsMirroredHere()
    {
        var inContract = Contract["types"]!.AsObject().Select(entry => entry.Key).Order(StringComparer.Ordinal);

        Assert.Equal(MirroredTypes.Order(StringComparer.Ordinal), inContract);
    }

    [Fact] public void SensorReadingRoundTrips() => RoundTrips<SensorReading>(nameof(SensorReading));
    [Fact] public void FanStatusRoundTrips() => RoundTrips<FanStatus>(nameof(FanStatus));
    [Fact] public void DriveHealthRoundTrips() => RoundTrips<DriveHealth>(nameof(DriveHealth));
    [Fact] public void SnapshotRoundTrips() => RoundTrips<Snapshot>(nameof(Snapshot));

    [Theory]
    [InlineData("SensorCategory", typeof(SensorCategory))]
    [InlineData("PwmMode", typeof(PwmMode))]
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
            Assert.Equal(json, JsonSerializer.Serialize(parsed, type, VigilJson.Options));
        }
    }

    [Fact]
    public void TheSilenceTimeoutOutlastsTwoOfVigildsKeepalives()
    {
        var keepalive = TimeSpan.FromSeconds(Contract["limits"]!["streamKeepaliveSeconds"]!.GetValue<int>());

        Assert.True(
            Limits.StreamSilenceTimeout > 2 * keepalive,
            $"vigild keepalives every {keepalive.TotalSeconds}s, so a silence timeout of {Limits.StreamSilenceTimeout.TotalSeconds}s "
            + "would read one lost keepalive as a dead daemon");
    }
}
