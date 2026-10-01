using System.Net;
using System.Text;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vigil.Core.Api;
using Vigil.Core.Configuration;
using Vigil.Core.Data;
using Vigil.Core.Runtime;
using Xunit;

namespace Vigil.Core.Tests;

/// <summary>
/// The HTTP surface against a real Kestrel on loopback, with the runtime replaced by whatever
/// the test writes to the subscriber it is handed.
/// </summary>
public sealed class ApiTests : IAsyncLifetime
{
    private readonly Channel<RuntimeInput> _inputs = Channel.CreateUnbounded<RuntimeInput>();
    private readonly string _webRoot = Directory.CreateTempSubdirectory("vigil-web-root-").FullName;
    private WebApplication? _app;
    private HttpClient? _http;

    private HttpClient Http => _http ?? throw new InvalidOperationException("not started");

    private LiveHub Hub => _app?.Services.GetRequiredService<LiveHub>() ?? throw new InvalidOperationException("not started");

    public async ValueTask InitializeAsync()
    {
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<!doctype html><title>vigil</title>");
        Directory.CreateDirectory(Path.Combine(_webRoot, "_app", "immutable"));
        File.WriteAllText(Path.Combine(_webRoot, "_app", "immutable", "start.abc123.js"), "export {};");

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        // Nothing listens on port 1: these tests never reach a query.
        builder.Services.AddSingleton(Options.Create(new VigilOptions { DatabaseUrl = "postgres://vigil@127.0.0.1:1/vigil" }));
        builder.Services.AddSingleton<VigilDataSources>();
        builder.Services.AddSingleton<HistoryQueries>();
        builder.Services.AddSingleton<EventStore>();
        builder.Services.AddSingleton(new LiveHub(_inputs.Writer));

        _app = builder.Build();
        _app.UseSecurityHeaders();
        _app.UseDashboard(_webRoot);
        _app.MapVigilApi();
        await _app.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri(_app.Urls.First()), Timeout = TimeSpan.FromSeconds(10) };
    }

    public async ValueTask DisposeAsync()
    {
        _http?.Dispose();
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        Directory.Delete(_webRoot, recursive: true);
    }

    /// <summary>
    /// ADR-008's whole condition: vigil-core answers the LAN with no authentication, which is
    /// acceptable only while nothing it serves can change anything. A route with any other
    /// method fails here, and that is the point to stop and add authentication first.
    /// </summary>
    [Fact]
    public void EveryRouteIsAGet()
    {
        var endpoints = _app!.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        Assert.NotEmpty(endpoints);
        Assert.All(endpoints, endpoint =>
            Assert.Equal(["GET"], endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["(any method)"]));
    }

    [Fact]
    public async Task HealthAnswersWithEverySecurityHeader()
    {
        using var response = await Http.GetAsync(new Uri("/health", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok\n", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownApiPathIsANotFoundRatherThanThePage()
    {
        using var response = await Http.GetAsync(new Uri("/api/nope", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("try /api/live", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/api/history")]
    [InlineData("/api/history?range=2h")]
    [InlineData("/api/history?range=24H")]
    public async Task HistoryRefusesARangeItDoesNotHave(string path)
    {
        using var response = await Http.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("range must be one of 1h, 6h, 24h, 7d, 30d, 1y\n", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LiveRelaysWhatTheRuntimeSendsAsServerSentEvents()
    {
        // The runtime's side: answer the subscription with one message.
        var runtime = Task.Run(async () =>
        {
            var input = await _inputs.Reader.ReadAsync();
            Assert.IsType<RuntimeInput.Subscribe>(input).Subscriber.TryWrite("""{"type":"daemon","connected":true}""");
        }, TestContext.Current.CancellationToken);

        using var response = await Http.GetAsync(new Uri("/api/live", UriKind.Relative), HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        await runtime;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        await using var body = await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        var received = new StringBuilder();
        var buffer = new byte[1024];
        while (!received.ToString().Contains("\n\n", StringComparison.Ordinal))
        {
            var read = await body.ReadAsync(buffer, TestContext.Current.CancellationToken);
            Assert.NotEqual(0, read);
            received.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }

        // Unnamed and unwrapped: the JSON exactly as the runtime serialized it.
        Assert.Equal("data: {\"type\":\"daemon\",\"connected\":true}\n\n", received.ToString());
    }

    [Fact]
    public async Task LiveRefusesPastTheCap()
    {
        var held = Enumerable.Range(0, LiveHub.MaxSubscribers).Select(_ => Hub.TrySubscribe()).ToList();

        using var response = await Http.GetAsync(new Uri("/api/live", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        held.ForEach(subscription => subscription?.Dispose());
    }

    [Fact]
    public async Task ServesTheDashboardAndCachesOnlyHashedAssetsForever()
    {
        using var page = await Http.GetAsync(new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("<title>vigil</title>", await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.True(page.Headers.CacheControl?.NoCache);

        using var asset = await Http.GetAsync(new Uri("/_app/immutable/start.abc123.js", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        Assert.Equal("public, max-age=31536000, immutable", asset.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", asset.Headers.GetValues("X-Content-Type-Options").Single());

        using var missing = await Http.GetAsync(new Uri("/settings", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
