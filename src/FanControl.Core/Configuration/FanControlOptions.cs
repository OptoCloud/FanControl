namespace FanControl.Core.Configuration;

public sealed class FanControlOptions
{
    public const string SectionName = "FanControl";

    public IReadOnlyList<FanChannelOptions> Channels { get; init; } = [];
    public IReadOnlyList<FanCurveOptions> Curves { get; init; } = [];

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// If the control loop doesn't complete a poll within this window, every channel is
    /// released back to BIOS Smart Fan IV. Must be comfortably larger than PollInterval.
    /// </summary>
    public TimeSpan DeadmanTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public StatusApiOptions StatusApi { get; init; } = new();
}

public sealed class StatusApiOptions
{
    public bool Enabled { get; init; } = true;

    /// <summary>Loopback by default — see FanSafetyGuard docs for why this daemon should never bind a public interface.</summary>
    public string ListenAddress { get; init; } = "127.0.0.1";

    public int Port { get; init; } = 5178;

    /// <summary>Optional shared-secret bearer token, for defense-in-depth if this ever moves off-loopback.</summary>
    public string? BearerToken { get; init; }
}
