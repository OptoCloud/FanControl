using Microsoft.Extensions.Configuration;
using Vigil.Core.Configuration;
using Xunit;

namespace Vigil.Core.Tests;

public sealed class VigilEnvironmentTests
{
    private static VigilOptions Bind(params (string Variable, string Value)[] environment)
    {
        var values = environment.ToDictionary(e => e.Variable, e => e.Value, StringComparer.OrdinalIgnoreCase);
        var configuration = new ConfigurationBuilder()
            .AddVigilEnvironment(name => values.TryGetValue(name, out var value) ? value : null)
            .Build();

        var options = new VigilOptions();
        configuration.Bind(options);
        return options;
    }

    [Fact]
    public void ReadsTheDocumentedVariableNames()
    {
        var options = Bind(
            ("VIGILD_SOCKET", "/mnt/vigil/vigild.sock"),
            ("DATABASE_URL", "postgres://vigil:secret@10.0.0.130:5432/vigil"),
            ("NUT_HOST", "10.0.0.4"),
            ("NUT_PORT", "3493"),
            ("CORE_PORT", "3097"),
            ("PERSIST_INTERVAL_SECONDS", "10"));

        Assert.Equal("/mnt/vigil/vigild.sock", options.VigildSocket);
        Assert.Equal("postgres://vigil:secret@10.0.0.130:5432/vigil", options.DatabaseUrl);
        Assert.Equal("10.0.0.4", options.NutHost);
        Assert.Equal(3493, options.NutPort);
        // The bug this test exists for: .NET binds "CorePort", so CORE_PORT read as nothing and
        // vigil-core silently listened on the default port instead of the configured one.
        Assert.Equal(3097, options.CorePort);
        Assert.Equal(10, options.PersistIntervalSeconds);
    }

    [Fact]
    public void LeavesDefaultsAloneWhenNothingIsSet()
    {
        var options = Bind();

        Assert.Equal("127.0.0.1", options.CoreHost);
        Assert.Equal(Limits.CoreDefaultPort, options.CorePort);
        Assert.Equal(Limits.DatabaseUrlDefault, options.DatabaseUrl);
        Assert.Null(options.NutHost);
        Assert.False(options.CoreAllowNonLoopback);
    }

    [Fact]
    public void AnEmptyVariableMeansUnset()
    {
        // An EnvironmentFile line left as `NTFY_URL=` must not configure an empty URL.
        var options = Bind(("NTFY_URL", ""), ("NUT_HOST", ""));

        Assert.Null(options.NtfyUrl);
        Assert.Null(options.NutHost);
    }

    [Fact]
    public void CoversEveryOptionThereIs()
    {
        // An option that no environment variable maps to is unreachable in production, where
        // the environment is the only configuration source there is.
        var mapped = VigilEnvironment.Variables.Count();
        var declared = typeof(VigilOptions).GetProperties().Length;

        Assert.Equal(declared, mapped);
    }
}
