using Vigil.Core.Configuration;
using Xunit;

namespace Vigil.Core.Tests;

public sealed class ListenAddressTests
{
    private static string? Address(string? host, bool allowNonLoopback = false) =>
        VigilOptionsValidator.ListenAddress(new VigilOptions
        {
            CoreHost = host ?? "127.0.0.1",
            CorePort = 3001,
            CoreAllowNonLoopback = allowNonLoopback,
        });

    [Fact]
    public void BindsLoopbackByDefault()
    {
        Assert.Equal("http://127.0.0.1:3001", Address(null));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.53")]
    [InlineData("::1")]
    [InlineData("[::1]")]
    [InlineData("localhost")]
    [InlineData(" 127.0.0.1 ")]
    public void AcceptsEveryLoopbackSpelling(string host)
    {
        Assert.NotNull(Address(host));
    }

    // Every one of these would put vigil-core on the network.
    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("[::]")]
    [InlineData("")]
    [InlineData("10.0.0.130")]
    [InlineData("192.168.1.5")]
    [InlineData("vigil.lan")]
    public void RefusesARoutableAddress(string host)
    {
        Assert.Null(Address(host));
    }

    [Fact]
    public void ARoutableAddressNeedsAnExplicitOptIn()
    {
        Assert.Equal("http://0.0.0.0:3001", Address("0.0.0.0", allowNonLoopback: true));
    }
}
