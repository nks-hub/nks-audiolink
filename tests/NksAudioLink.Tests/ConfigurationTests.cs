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
