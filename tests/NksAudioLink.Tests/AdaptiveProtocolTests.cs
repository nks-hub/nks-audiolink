using System.Buffers.Binary;
using NksAudioLink.Core;

namespace NksAudioLink.Tests;

public sealed class AdaptiveProtocolTests
{
    [Fact]
    public void ManualHelloRemainsByteForByteCompatibleAndAdaptiveUsesTheReservedFormatVariant()
    {
        byte[] body = new byte[74];
        var manual = new HelloMessage(48000, 2, 1, 5, 30, "x");
        int size = manual.Write(body);
        Assert.Equal(new byte[] { 0x80, 0xBB, 0, 0, 2, 1, 5, 30, 0, 1, (byte)'x' }, body[..size]);
        var adaptive = new HelloMessage(48000, 2, 1, 5, 40, "x", new(20, 100));
        Assert.Equal(size, adaptive.Write(body));
        Assert.Equal(0x81, body[5]); // Legacy servers parse the same body and reject unsupported format.
        Assert.Equal(0xA108, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(7)));
        Assert.True(HelloMessage.TryRead(body.AsSpan(0, size), out var parsed));
        Assert.True(parsed.IsSupported);
        Assert.Equal(adaptive, parsed);
    }

    [Theory]
    [InlineData(0, 4, 20)]
    [InlineData(8, 0, 20)]
    [InlineData(8, 4, 31)]
    [InlineData(8, 9, 20)]
    [InlineData(8, 4, 7)]
    [InlineData(63, 4, 20)]
    public void PackedOutOfRangeSettingsParseButAreRejectedBeforeOpeningOutput(int initial, int min, int max)
    {
        byte[] body = new byte[74];
        int size = new HelloMessage(48000, 2, 1, 5, 30, "x").Write(body);
        body[5] = 0x81;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(7), (ushort)(initial | min << 6 | max << 11));
        Assert.True(HelloMessage.TryRead(body.AsSpan(0, size), out var parsed));
        Assert.False(parsed.IsSupported);
    }

    [Fact]
    public void ManualFormatNeverInterpretsPackedSettingsAndMalformedPartialExtensionsAreRejected()
    {
        byte[] body = new byte[80];
        int size = new HelloMessage(48000, 2, 1, 5, 40, "x", new(20, 100)).Write(body);
        body[5] = 1;
        Assert.True(HelloMessage.TryRead(body.AsSpan(0, size), out var parsed));
        Assert.Null(parsed.AdaptiveBuffer);
        Assert.False(parsed.IsSupported);
        Assert.False(HelloMessage.TryRead(body.AsSpan(0, size + 1), out _));
        Assert.Throws<ArgumentException>(() => new HelloMessage(48000, 2, 2, 5, 40, "x", new(20, 100)).Write(body));
    }

    [Fact]
    public void AdaptiveStatsPreserveLegacyPrefixAndRoundTripTheActualTargetAndReason()
    {
        var manual = new StatsMessage(true, 35, 20, 1, 2, 3, 4, -80, 12345);
        var adaptive = manual with { AdaptiveBuffer = new(50, 20, 100, AdaptiveBufferReason.LatePacket, 2) };
        byte[] old = new byte[StatsMessage.Size], extended = new byte[StatsMessage.AdaptiveSize];
        Assert.Equal(33, manual.Write(old));
        Assert.Equal(45, adaptive.Write(extended));
        Assert.Equal(old, extended[..33]);
        Assert.Equal(0xA1, extended[33]);
        Assert.True(StatsMessage.TryRead(old, out var parsedManual));
        Assert.Null(parsedManual.AdaptiveBuffer);
        Assert.True(StatsMessage.TryRead(extended, out var parsed));
        Assert.Equal(adaptive, parsed);
        Assert.False(StatsMessage.TryRead(extended.AsSpan(0, 44), out _));
        extended[33] = 0;
        Assert.False(StatsMessage.TryRead(extended, out _));
        adaptive.Write(extended);
        extended[34] = 255;
        Assert.False(StatsMessage.TryRead(extended, out _));
        adaptive.Write(extended);
        BinaryPrimitives.WriteUInt16LittleEndian(extended.AsSpan(35), 105);
        Assert.False(StatsMessage.TryRead(extended, out _));
        Assert.Throws<ArgumentException>(() => adaptive.Write(old));
    }
}
