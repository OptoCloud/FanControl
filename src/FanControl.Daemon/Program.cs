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

// Bound once, up front: Kestrel's listen address must be known before the host builds,
// and it must stay loopback-only by default — see FanSafetyGuard docs for why this
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
builder.Services.AddSingleton<INvidiaGpuTemperatureProvider, NvidiaSmiGpuTemperatureProvider>();
builder.Services.AddSingleton<IHbaTemperatureProvider>(services => fanControlOptions.Hba.Enabled
    ? new Mpt3ctlHbaTemperatureProvider(
        fanControlOptions.Hba.DevicePath,
        fanControlOptions.Hba.IocNumber,
        services.GetRequiredService<ILogger<Mpt3ctlHbaTemperatureProvider>>())
    : new UnavailableHbaTemperatureProvider());
builder.Services.AddSingleton<IDriveHealthProvider>(_ => new SmartctlDriveHealthProvider(fanControlOptions.DriveHealth.SmartctlPath));
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

        return store.Latest is { } snapshot
            ? Results.Ok(snapshot)
            : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    });
}

app.Run();

static bool IsAuthorized(HttpContext http, string? configuredToken)
{
    if (string.IsNullOrEmpty(configuredToken))
    {
        return true;
    }

    var header = http.Request.Headers.Authorization.ToString();
    return header == $"Bearer {configuredToken}";
}
