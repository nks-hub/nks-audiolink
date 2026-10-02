using NksAudioLink.Core;

namespace NksAudioLink.Tests;

public class JitterBufferTests
{
    private static byte[] Frame(byte value) => Enumerable.Repeat(value, Protocol.PcmBytesPerFrame).ToArray();

    [Fact]
    public void ReorderedFramesPlayInSampleOrder()
    {
        var buffer = new JitterBuffer(15);
        byte[] output = new byte[Protocol.PcmBytesPerFrame];
        Assert.True(buffer.Enqueue(0, Frame(1), false));
        Assert.True(buffer.Enqueue(480, Frame(3), false));
        Assert.False(buffer.ReadNext(output));
        Assert.True(buffer.Enqueue(240, Frame(2), false));
        Assert.True(buffer.ReadNext(output)); Assert.All(output, b => Assert.Equal((byte)1, b));
        Assert.True(buffer.ReadNext(output)); Assert.All(output, b => Assert.Equal((byte)2, b));
        Assert.True(buffer.ReadNext(output)); Assert.All(output, b => Assert.Equal((byte)3, b));
    }

    [Fact]
    public void FirstFrameMayArriveAfterEarlierFrame()
    {
        var buffer = new JitterBuffer(10);
        Assert.True(buffer.Enqueue(240, Frame(2), false));
        Assert.True(buffer.Enqueue(0, Frame(1), false));
        byte[] output = new byte[Protocol.PcmBytesPerFrame];
        Assert.True(buffer.ReadNext(output));
        Assert.Equal((byte)1, output[0]);
    }

    [Fact]
    public void DuplicateLateAndFarFutureFramesAreRejected()
    {
        var buffer = new JitterBuffer(10, 8);
        Assert.True(buffer.Enqueue(0, Frame(1), false));
        Assert.False(buffer.Enqueue(0, Frame(2), false));
        Assert.Equal(1, buffer.Duplicates);
        Assert.False(buffer.Enqueue(8ul * Protocol.SamplesPerFrame, Frame(2), false));
        Assert.Equal(1, buffer.Overruns);
        Assert.True(buffer.Enqueue(240, Frame(2), false));
        buffer.ReadNext(new byte[Protocol.PcmBytesPerFrame]);
        Assert.False(buffer.Enqueue(0, Frame(1), false));
        Assert.Equal(1, buffer.Late);
    }

    [Fact]
    public void MissingFrameBecomesSilenceThenBufferReprimes()
    {
        var buffer = new JitterBuffer(10);
        byte[] output = new byte[Protocol.PcmBytesPerFrame];
        buffer.Enqueue(0, Frame(1), false);
        buffer.Enqueue(240, Frame(2), false);
        Assert.True(buffer.ReadNext(output));
        buffer.Enqueue(720, Frame(4), false);
        Assert.True(buffer.ReadNext(output));
        Assert.True(buffer.ReadNext(output));
        Assert.All(output, b => Assert.Equal((byte)0, b));
        Assert.Equal(1, buffer.Lost);
        Assert.Equal(1, buffer.Underruns);
        Assert.False(buffer.ReadNext(output));
        buffer.Enqueue(960, Frame(5), false);
        Assert.True(buffer.ReadNext(output)); Assert.All(output, b => Assert.Equal((byte)4, b));
    }

    [Fact]
    public void SilentFrameExpandsToZeroPcm()
    {
        var buffer = new JitterBuffer(5);
        buffer.Enqueue(0, [], true);
        byte[] output = Frame(255);
        Assert.True(buffer.ReadNext(output));
        Assert.All(output, b => Assert.Equal((byte)0, b));
    }
}
