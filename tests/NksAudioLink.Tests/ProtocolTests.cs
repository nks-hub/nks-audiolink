using System.Text;
using NksAudioLink.Core;

namespace NksAudioLink.Tests;

public class ProtocolTests
{
    [Theory]
    [InlineData(PacketType.Hello)]
    [InlineData(PacketType.Audio)]
    [InlineData(PacketType.Bye)]
    [InlineData(PacketType.Stats)]
    [InlineData(PacketType.Discover)]
    [InlineData(PacketType.DiscoverReply)]
    [InlineData(PacketType.Ping)]
    [InlineData(PacketType.Pong)]
    [InlineData(PacketType.Reject)]
    public void PacketRoundTrip(PacketType type)
    {
        byte[] packet = new byte[Protocol.MaxDatagramSize];
        byte[] payload = [1, 2, 3, 4];
        var sent = new PacketHeader(type, PacketFlags.None, 123, 456);
        int length = Protocol.Write(packet, sent, payload);
        Assert.True(Protocol.TryRead(packet.AsSpan(0, length), default, out var received, out var body));
        Assert.Equal(sent, received);
        Assert.True(body.SequenceEqual(payload));
    }

    [Fact]
    public void AuthRejectsTamperingAndMissingTag()
    {
        byte[] packet = new byte[100];
        byte[] key = Encoding.UTF8.GetBytes("test secret");
        int length = Protocol.Write(packet, new(PacketType.Ping, PacketFlags.None, 1, 2), [7, 8], key);
        Assert.True(Protocol.TryRead(packet.AsSpan(0, length), key, out _, out _));
        Assert.False(Protocol.TryRead(packet.AsSpan(0, length - 16), key, out _, out _));
        packet[17] ^= 1;
        Assert.False(Protocol.TryRead(packet.AsSpan(0, length), key, out _, out _));
        Assert.False(Protocol.TryRead(packet.AsSpan(0, length), default, out _, out _));
    }

    [Fact]
    public void RejectsBrokenHeaders()
    {
        byte[] packet = new byte[100];
        int length = Protocol.Write(packet, new(PacketType.Bye, PacketFlags.None, 1, 2), []);
        packet[0] = 0;
        Assert.False(Protocol.TryRead(packet.AsSpan(0, length), default, out _, out _));
        packet[0] = (byte)'N'; packet[4] = 2;
        Assert.False(Protocol.TryRead(packet.AsSpan(0, length), default, out _, out _));
        Assert.False(Protocol.TryRead(packet.AsSpan(0, 15), default, out _, out _));
    }

    [Fact]
    public void HelloAndSilentAudioRoundTrip()
    {
        Span<byte> body = stackalloc byte[128];
        var hello = new HelloMessage(48000, 2, 1, 5, 30, "Počítač");
        int count = hello.Write(body);
        Assert.True(HelloMessage.TryRead(body[..count], out var parsed));
        Assert.Equal(hello, parsed);
        Assert.True(parsed.IsSupported);
        count = AudioMessage.Write(body, 480, Protocol.SamplesPerFrame, [], true);
        Assert.True(AudioMessage.TryRead(body[..count], true, out var index, out var pcm));
        Assert.Equal(480ul, index);
        Assert.True(pcm.IsEmpty);
        Assert.False(AudioMessage.TryRead(body[..count], false, out _, out _));
    }
}
