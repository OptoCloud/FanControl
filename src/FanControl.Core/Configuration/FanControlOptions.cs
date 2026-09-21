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

    /// <summary>
    /// How often the hwmon tree is re-scanned for sensors. hwmon nodes aren't permanent: a
    /// drive that resets or is hot-swapped comes back under a new hwmonN, and without a
    /// re-scan it would stay "unavailable" (and silently out of every drive:* curve) until
    /// the daemon restarted.
    /// </summary>
    public TimeSpan SensorRescanInterval { get; init; } = TimeSpan.FromSeconds(30);

    public StatusApiOptions StatusApi { get; init; } = new();
    public GpuOptions Gpu { get; init; } = new();
    public HbaOptions Hba { get; init; } = new();
    public DriveHealthOptions DriveHealth { get; init; } = new();
}

public sealed class GpuOptions
{
    /// <summary>Set false on a host with no NVIDIA GPU to skip spawning nvidia-smi entirely and always report the gpu sensor unavailable.</summary>
    public bool Enabled { get; init; } = true;

    public string NvidiaSmiPath { get; init; } = "nvidia-smi";

    /// <summary>
    /// nvidia-smi is a process spawn, far heavier than the sysfs reads the rest of the poll
    /// consists of, so its result is reused for this long rather than re-queried every
    /// PollInterval. Zero queries on every poll.
    /// </summary>
    public TimeSpan MinimumReadInterval { get; init; } = TimeSpan.FromSeconds(6);

    /// <summary>nvidia-smi is killed and the gpu sensor reported unavailable if it takes longer than this. Counts against DeadmanTimeout.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);
}

public sealed class DriveHealthOptions
{
    public bool Enabled { get; init; } = true;

    public string SmartctlPath { get; init; } = "smartctl";

    /// <summary>
    /// Deliberately independent of and much slower than PollInterval: SMART health rarely
    /// changes poll-to-poll, and running smartctl against every drive every 2s would be
    /// wasteful for no benefit.
    /// </summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Per-drive: smartctl is killed and that drive reported unavailable for the poll if it takes longer than this.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);
}

public sealed class HbaOptions
{
    /// <summary>Set false to skip the mpt3ctl ioctl entirely and always report the hba sensor unavailable.</summary>
    public bool Enabled { get; init; } = true;

    public string DevicePath { get; init; } = "/dev/mpt3ctl";

    /// <summary>Which IOC this is, per mpt3sas' enumeration order. 0 is correct for a single HBA.</summary>
    public uint IocNumber { get; init; }
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
