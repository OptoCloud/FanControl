using Npgsql;
using Vigil.Core.Data;
using Xunit;

namespace Vigil.Core.Tests;

public sealed class PostgresUriTests
{
    private static NpgsqlConnectionStringBuilder Parse(string url) =>
        new(PostgresUri.ToConnectionString(url, "vigil-test"));

    [Fact]
    public void ReadsAFullUrl()
    {
        var parsed = Parse("postgres://vigil:secret@10.0.0.130:5432/vigil");

        Assert.Equal("10.0.0.130", parsed.Host);
        Assert.Equal(5432, parsed.Port);
        Assert.Equal("vigil", parsed.Database);
        Assert.Equal("vigil", parsed.Username);
        Assert.Equal("secret", parsed.Password);
    }

    [Fact]
    public void DefaultsThePortAndAcceptsNoPassword()
    {
        // This is the development default, Limits.DatabaseUrlDefault.
        var parsed = Parse("postgres://vigil@localhost/vigil");

        Assert.Equal("localhost", parsed.Host);
        Assert.Equal(5432, parsed.Port);
        Assert.Equal("vigil", parsed.Username);
        Assert.Null(parsed.Password);
    }

    [Fact]
    public void AcceptsThePostgresqlScheme()
    {
        Assert.Equal("example", Parse("postgresql://u@example/db").Host);
    }

    [Fact]
    public void DecodesAPercentEncodedPassword()
    {
        // A password containing @ : / must be encoded in a URL. Not decoding it produces an
        // authentication failure that looks like a wrong password rather than a parsing bug.
        var parsed = Parse("postgres://vigil:p%40ss%3Aword%2F1@db:5432/vigil");

        Assert.Equal("p@ss:word/1", parsed.Password);
        Assert.Equal("db", parsed.Host);
    }

    [Fact]
    public void PassesQueryParametersThroughAsNpgsqlKeywords()
    {
        var parsed = Parse("postgres://u:p@h:5432/db?sslmode=require&Timeout=4");

        Assert.Equal(SslMode.Require, parsed.SslMode);
        Assert.Equal(4, parsed.Timeout);
    }

    [Fact]
    public void AcceptsAKeywordConnectionStringUnchanged()
    {
        // So an operator can set Npgsql options directly when a URL cannot express them.
        var parsed = Parse("Host=db;Database=vigil;Username=vigil");

        Assert.Equal("db", parsed.Host);
        Assert.Equal("vigil", parsed.Database);
    }

    [Fact]
    public void AppliesVigilsOwnSettings()
    {
        var parsed = Parse("postgres://vigil@localhost/vigil");

        // No query may tie up a connection indefinitely (docs/SECURITY.md §6).
        Assert.Equal(15, parsed.CommandTimeout);
        // So pg_stat_activity says which process a connection belongs to.
        Assert.Equal("vigil-test", parsed.ApplicationName);
    }

    [Theory]
    [InlineData("mysql://u:p@h/db")]
    [InlineData("not a url at all://")]
    public void RefusesSomethingThatIsNotPostgres(string url)
    {
        Assert.Throws<ArgumentException>(() => PostgresUri.ToConnectionString(url, "vigil-test"));
    }

    [Fact]
    public void RefusesAnEmptyUrlRatherThanConnectingSomewhereSurprising()
    {
        Assert.Throws<ArgumentException>(() => PostgresUri.ToConnectionString("  ", "vigil-test"));
    }

    [Theory]
    [InlineData("postgres://vigil:secret@10.0.0.130:5432/vigil", "postgres://vigil:***@10.0.0.130:5432/vigil")]
    [InlineData("postgres://vigil@localhost/vigil", "postgres://vigil@localhost/vigil")]
    [InlineData("postgres://vigil:p%40ss@db/vigil", "postgres://vigil:***@db/vigil")]
    [InlineData("Host=db;Password=secret", "Host=db;Password=secret")]
    public void RedactsThePasswordForAnErrorMessage(string url, string expected)
    {
        // docs/SECURITY.md §7: a credential never reaches a log line or an exception. The
        // keyword form is left alone because it is never echoed - only URLs are.
        Assert.Equal(expected, PostgresUri.Redact(url));
    }

    [Fact]
    public void AMalformedUrlsErrorDoesNotLeakThePassword()
    {
        var error = Assert.Throws<ArgumentException>(
            () => PostgresUri.ToConnectionString("mysql://vigil:verysecret@h/db", "vigil-test"));

        Assert.DoesNotContain("verysecret", error.Message, StringComparison.Ordinal);
    }
}
