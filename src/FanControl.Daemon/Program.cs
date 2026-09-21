using System.Security.Cryptography;
using System.Text;
using FanControl.Core.Configuration;
using FanControl.Core.Drives;
using FanControl.Core.Fans;
using FanControl.Core.IO;
using FanControl.Core.Sensors;
using FanControl.Core.Status;
using FanControl.Daemon;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSystemd();

var fanControlOptions = builder.Configuration.GetSection(FanControlOptions.SectionName).Get<FanControlOptions>()
    ?? new FanControlOptions();

// Refuse to start on a bad config, before any fan has been touched. Throwing out of Main
// exits non-zero, so systemd records a failure rather than a clean stop.
var configErrors = FanControlOptionsValidator.Validate(fanControlOptions);
if (configErrors.Count > 0)
{
    throw new InvalidOperationException(
        $"Invalid {FanControlOptions.SectionName} configuration:{Environment.NewLine}  - {string.Join($"{Environment.NewLine}  - ", configErrors)}");
}

// Bound once, up front: Kestrel's listen address must be known before the host builds,
// and it must stay loopback-only by default. See FanSafetyGuard docs for why this
// daemon (root, raw PWM/ioctl access) should never be the thing exposed publicly.
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Listen(System.Net.IPAddress.Parse(fanControlOptions.StatusApi.ListenAddress), fanControlOptions.StatusApi.Port);
});

builder.Services.Configure<FanControlOptions>(builder.Configuration.GetSection(FanControlOptions.SectionName));

builder.Services.AddSingleton<ISysFs, LinuxSysFs>();
builder.Services.AddSingleton<HwmonSensorResolver>();
builder.Services.AddSingleton<HwmonSensorReader>();
builder.Services.AddSingleton<ISysfsFanController, SysfsFanController>();
builder.Services.AddSingleton<INvidiaGpuTemperatureProvider>(_ => fanControlOptions.Gpu.Enabled
    ? new NvidiaSmiGpuTemperatureProvider(
        fanControlOptions.Gpu.NvidiaSmiPath,
        fanControlOptions.Gpu.MinimumReadInterval,
        fanControlOptions.Gpu.Timeout)
    : new UnavailableGpuTemperatureProvider());
builder.Services.AddSingleton<IHbaTemperatureProvider>(services => fanControlOptions.Hba.Enabled
    ? new Mpt3ctlHbaTemperatureProvider(
        fanControlOptions.Hba.DevicePath,
        fanControlOptions.Hba.IocNumber,
        services.GetRequiredService<ILogger<Mpt3ctlHbaTemperatureProvider>>())
    : new UnavailableHbaTemperatureProvider());
builder.Services.AddSingleton<IDriveHealthProvider>(_ => new SmartctlDriveHealthProvider(
    fanControlOptions.DriveHealth.SmartctlPath,
    fanControlOptions.DriveHealth.Timeout));
builder.Services.AddSingleton<StatusSnapshotStore>();
builder.Services.AddSingleton<DriveHealthStore>();
builder.Services.AddHostedService<ControlLoopService>();
builder.Services.AddHostedService<DriveHealthService>();

var app = builder.Build();

if (fanControlOptions.StatusApi.Enabled)
{
    app.MapGet("/status", (StatusSnapshotStore store, HttpContext http) =>
    {
        if (!IsAuthorized(http, fanControlOptions.StatusApi.BearerToken))
        {
            return Results.Unauthorized();
        }

        if (store.Latest is not { } snapshot)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        // A loop that has hung outright (rather than failing polls, which it reports
        // itself) can't mark its own last snapshot unhealthy, so staleness is judged here.
        var stale = DateTimeOffset.UtcNow - snapshot.TimestampUtc > fanControlOptions.DeadmanTimeout;
        return Results.Ok(stale ? snapshot with { ControlLoopHealthy = false } : snapshot);
    });
}

app.Run();

static bool IsAuthorized(HttpContext http, string? configuredToken)
{
    if (string.IsNullOrEmpty(configuredToken))
    {
        return true;
    }

    // Fixed-time so response timing doesn't leak how much of a guessed token matched.
    var presented = Encoding.UTF8.GetBytes(http.Request.Headers.Authorization.ToString());
    var expected = Encoding.UTF8.GetBytes($"Bearer {configuredToken}");
    return CryptographicOperations.FixedTimeEquals(presented, expected);
}
