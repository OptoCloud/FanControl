using Microsoft.Extensions.Options;
using Vigil.Core.Configuration;

// vigil-core: vigil's orchestrator. Reads vigild and the UPS, records history in Postgres,
// raises alerts, streams the live state, and serves the dashboard. Configured entirely from the
// environment; runs until killed.
//
// See docs/ARCHITECTURE.md. vigild, which drives the fans as root, stays Rust.

var builder = WebApplication.CreateBuilder(args);

// sd_notify readiness and the journald console format, so `journalctl -p warning` works. Under
// systemd this replaces both hand-rolled pieces the Rust version needed; run by hand it is a
// no-op and logging falls back to the plain console.
builder.Host.UseSystemd();

// Environment only, as the Rust version was, under the names already in
// core/deploy/vigil-core.env. See VigilEnvironment for why the mapping is explicit.
builder.Configuration.AddVigilEnvironment();

builder.Services
    .AddOptions<VigilOptions>()
    .Bind(builder.Configuration)
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<VigilOptions>, VigilOptionsValidator>();

var startup = new VigilOptions();
builder.Configuration.Bind(startup);
var listen = VigilOptionsValidator.ListenAddress(startup);
if (listen is not null)
{
    builder.WebHost.UseUrls(listen);
}

var app = builder.Build();

// 200 once running. Deliberately says nothing about the database: the live view has to work
// without one (ARCHITECTURE.md invariant 8), so a database outage is not unhealthy.
app.MapGet("/health", () => Results.Text("ok\n"));

app.Run();
