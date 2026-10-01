using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Vigil.Core.Clients;

namespace Vigil.Core.Runtime;

// The three long-running loops, as hosted services. Each poller only forwards what it reads into
// the runtime's input channel; everything that decides anything happens on the runtime's loop.

/// <summary>Runs the runtime. When it stops, the input channel is closed so no new stream attaches.</summary>
public sealed class RuntimeService(VigilRuntime runtime, Channel<RuntimeInput> inputs) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await runtime.RunAsync(inputs.Reader, stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            inputs.Writer.TryComplete();
        }
    }
}

/// <summary>vigild's stream, reconnecting forever (the client's own loop).</summary>
public sealed class DaemonFeed(VigildClient client, Channel<RuntimeInput> inputs) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var daemonEvent in client.StreamAsync(stoppingToken).ConfigureAwait(false))
        {
            inputs.Writer.TryWrite(new RuntimeInput.Daemon(daemonEvent));
        }
    }
}

/// <summary>upsd's readings. Registered only when NUT_HOST is set.</summary>
public sealed class UpsFeed(NutClient client, Channel<RuntimeInput> inputs) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var poll in client.PollAsync(stoppingToken).ConfigureAwait(false))
        {
            inputs.Writer.TryWrite(new RuntimeInput.Ups(poll));
        }
    }
}
