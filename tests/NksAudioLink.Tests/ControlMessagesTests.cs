using NksAudioLink.Core;

namespace NksAudioLink.Tests;

public class ControlMessagesTests
{
    [Fact]
    public void StatsRoundTrip()
    {
        var sent = new StatsMessage(true, 30, 20, 1, 2, 3, 4, -200, 123456789);
        Span<byte> body = stackalloc byte[StatsMessage.Size];
        Assert.Equal(StatsMessage.Size, sent.Write(body));
        Assert.True(StatsMessage.TryRead(body, out var received));
        Assert.Equal(sent, received);
        Assert.False(StatsMessage.TryRead(body[..^1], out _));
    }

    [Fact]
    public void DiscoveryRoundTrip()
    {
        var sent = new DiscoverReplyMessage("Obývák", 48000, 2, true, "ALSA USB");
        Span<byte> body = stackalloc byte[256];
        int length = sent.Write(body);
        Assert.True(DiscoverReplyMessage.TryRead(body[..length], out var received));
        Assert.Equal(sent, received);
        Assert.False(DiscoverReplyMessage.TryRead(body[..(length - 1)], out _));
    }
}
