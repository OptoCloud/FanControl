using Vigil.Core.Configuration;
using Xunit;

namespace Vigil.Core.Tests;

public sealed class ListenAddressTests
{
    private static string? Address(string host) =>
        VigilOptionsValidator.ListenAddress(new VigilOptions { CoreHost = host, CorePort = 3001 });

    [Fact]
    public void ListensOnTheLanByDefault()
    {
        Assert.Equal("http://0.0.0.0:3001", VigilOptionsValidator.ListenAddress(new VigilOptions { CorePort = 3001 }));
    }

    [Theory]
    [InlineData("0.0.0.0", "http://0.0.0.0:3001")]
    [InlineData("10.0.0.130", "http://10.0.0.130:3001")]
    [InlineData(" 127.0.0.1 ", "http://127.0.0.1:3001")]
    [InlineData("localhost", "http://localhost:3001")]
    [InlineData("::", "http://[::]:3001")]
    [InlineData("[::1]", "http://[::1]:3001")]
    public void BindsAnyAddressItIsGiven(string host, string expected)
    {
        Assert.Equal(expected, Address(host));
    }

    // Kestrel would bind every interface for any of these, rather than fail.
    [Theory]
    [InlineData("")]
    [InlineData("vigil.lan")]
    public void RefusesAHostThatIsNotAnAddress(string host)
    {
        Assert.Null(Address(host));
    }
}
