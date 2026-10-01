using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Vigil.Core.Clients;
using Vigil.Core.Configuration;
using Vigil.Core.Data;
using Vigil.Core.Notifications;
using Vigil.Core.Runtime;

namespace Vigil.Core;

/// <summary>How vigil-core is put together. Program.cs only calls this and starts it.</summary>
public static class VigilServices
{
    public static IServiceCollection AddVigilCore(this IServiceCollection services, VigilOptions startup)
    {
        services.AddSingleton(TimeProvider.System);

        services.AddSingleton<VigilDataSources>();
        services.AddSingleton<SchemaSetup>();
        services.AddSingleton<SampleWriter>();
        services.AddSingleton<DriveStore>();
        services.AddSingleton<EventStore>();
        services.AddSingleton<HistoryQueries>();
        services.AddSingleton<IRuntimeStore, DatabaseRuntimeStore>();

        services.AddSingleton<VigildClient>();
        services.AddSingleton<NutClient>();

        if (startup.NtfyUrl is { Length: > 0 } ntfy)
        {
            // One long-lived HttpClient, with pooled connections recycled so a DNS change for the
            // ntfy host is picked up; the alternative, IHttpClientFactory, is the same thing with
            // more parts for a client that sends a handful of requests a month.
            services.AddSingleton(provider => new NtfyClient(
                new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(15) }),
                new Uri(ntfy),
                provider.GetRequiredService<ILogger<NtfyClient>>()));
        }

        // The runtime's one input. Unbounded, as the Rust core's channel was: the producers are two
        // pollers at fixed rates and the page's subscriptions, which LiveHub caps.
        services.AddSingleton(_ => Channel.CreateUnbounded<RuntimeInput>(new UnboundedChannelOptions { SingleReader = true }));
        services.AddSingleton(provider => new LiveHub(provider.GetRequiredService<Channel<RuntimeInput>>().Writer));
        services.AddSingleton(provider => new VigilRuntime(
            provider.GetRequiredService<IOptions<VigilOptions>>().Value,
            provider.GetRequiredService<IRuntimeStore>(),
            provider.GetService<NtfyClient>(),
            provider.GetRequiredService<ILogger<VigilRuntime>>(),
            provider.GetRequiredService<TimeProvider>()));

        services.AddHostedService<RuntimeService>();
        services.AddHostedService<DaemonFeed>();
        if (startup.NutHost is { Length: > 0 })
        {
            services.AddHostedService<UpsFeed>();
        }

        return services;
    }
}
