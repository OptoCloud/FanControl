using Microsoft.Extensions.FileProviders;

namespace Vigil.Core.Api;

/// <summary>
/// Serving the SvelteKit build, and the security headers every response carries. Ported from
/// web/src/hooks.server.ts; docs/SECURITY.md §8.
/// </summary>
public static class Dashboard
{
    /// <summary>
    /// The headers that are not script policy. No Strict-Transport-Security: this deployment
    /// serves plain HTTP on the LAN by decision (ADR-008), where HSTS does nothing.
    /// </summary>
    private static readonly (string Name, string Value)[] SecurityHeaders =
    [
        // JSON, SSE and HTML are served; none of it should ever be content-sniffed.
        ("X-Content-Type-Options", "nosniff"),

        // The dashboard links nowhere outward, so no referrer ever needs to leave.
        ("Referrer-Policy", "no-referrer"),

        // Belt and braces with frame-ancestors, for anything that predates CSP.
        ("X-Frame-Options", "DENY"),
        ("Cross-Origin-Opener-Policy", "same-origin"),

        // A read-only dashboard needs none of these, and a browser told so cannot be talked into them.
        ("Permissions-Policy", "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()"),

        // Only the directives a <meta> CSP cannot carry, or that hold for every response. Script
        // and style policy come from the static build's own <meta> tag, which SvelteKit writes in
        // hash mode with the hash of each inline script it emits: a header policy is enforced
        // alongside it, so a script-src here would block those scripts whatever the meta allows.
        ("Content-Security-Policy", "frame-ancestors 'none'; object-src 'none'; base-uri 'self'; form-action 'none'"),
    ];

    /// <summary>SvelteKit content-hashes everything under here, so it never changes at a given URL.</summary>
    private const string ImmutablePrefix = "/_app/immutable/";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            foreach (var (name, value) in SecurityHeaders)
            {
                context.Response.Headers[name] = value;
            }

            await next(context).ConfigureAwait(false);
        });

    /// <summary>
    /// Serves the build in <paramref name="webRoot"/>: <c>index.html</c> for <c>/</c>, files
    /// as they are. No fallback to index.html for unknown paths: the dashboard is one page, so an
    /// unknown path is a 404, not a route.
    /// </summary>
    public static IApplicationBuilder UseDashboard(this IApplicationBuilder app, string webRoot)
    {
        var files = new PhysicalFileProvider(Path.GetFullPath(webRoot));
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        return app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            OnPrepareResponse = context =>
            {
                context.Context.Response.Headers.CacheControl =
                    context.Context.Request.Path.StartsWithSegments(ImmutablePrefix.TrimEnd('/'), StringComparison.Ordinal)
                        ? "public, max-age=31536000, immutable"
                        // index.html names the current hashed assets, so it must be revalidated
                        // or a browser keeps loading the previous build after an upgrade.
                        : "no-cache";
            },
        });
    }
}
