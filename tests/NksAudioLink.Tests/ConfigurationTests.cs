using System.Net;
using NksAudioLink.Core;

namespace NksAudioLink.Tests;

public class ConfigurationTests
{
    [Fact]
    public void DefaultsAreValidAndLocalOnly()
    {
        var config = new ServerConfig();
        config.Validate();
        Assert.True(CidrRange.Parse(config.AllowCidrs[0]).Contains(IPAddress.Loopback));
        Assert.False(CidrRange.Parse(config.AllowCidrs[0]).Contains(IPAddress.Parse("10.0.0.1")));
        new ClientConfig().Validate();
    }

    [Fact]
    public void RejectsWeakPskAndBadCidr()
    {
        Assert.Throws<InvalidDataException>(() => new ServerConfig { PskBase64 = Convert.ToBase64String([1, 2, 3]) }.Validate());
        Assert.Throws<InvalidDataException>(() => new ServerConfig { AllowCidrs = ["10.0.0.0/33"] }.Validate());
        Assert.Throws<InvalidDataException>(() => new ServerConfig { AllowCidrs = [null!] }.Validate());
    }

    [Theory]
    [InlineData(-1, "127.0.0.1")]
    [InlineData(65536, "127.0.0.1")]
    [InlineData(7356, "localhost")]
    [InlineData(7356, "0.0.0.0")]
    [InlineData(7356, "255.255.255.255")]
    [InlineData(7356, "::1")]
    [InlineData(7356, null)]
    public void HttpDashboardRejectsInvalidListenSettings(int port, string? address)
    {
        Assert.Throws<InvalidDataException>(() => new ServerConfig { HttpPort = port, HttpBindAddress = address! }.Validate());
    }

    [Fact]
    public void HttpDashboardSupportsExplicitLocalAndLanAddresses()
    {
        Assert.Equal(0, new ServerConfig().HttpPort);
        Assert.Equal("127.0.0.1", new ServerConfig().HttpBindAddress);
        new ServerConfig { HttpPort = 7356 }.Validate();
        new ServerConfig { HttpPort = 7356, HttpBindAddress = "192.168.1.50" }.Validate();
    }

    [Fact]
    public void ServerDiscoveryFieldsUseUtf8ByteLimits()
    {
        Assert.Throws<InvalidDataException>(() => new ServerConfig { Name = new string('é', 33) }.Validate());
        Assert.Throws<InvalidDataException>(() => new ServerConfig { Sink = new string('ž', 65) }.Validate());
    }

    [Fact]
    public void CidrMatchesOnlyItsSubnet()
    {
        var network = CidrRange.Parse("10.20.30.0/24");
        Assert.True(network.Contains(IPAddress.Parse("10.20.30.55")));
        Assert.False(network.Contains(IPAddress.Parse("10.20.31.55")));
        Assert.False(network.Contains(IPAddress.IPv6Loopback));
    }
}
