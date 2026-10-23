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

    [Theory]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(64)]
    public void BurstLossRecoversWithBoundedStorage(int missingFrames)
    {
        var buffer = new JitterBuffer(10, 8);
        byte[] output = new byte[Protocol.PcmBytesPerFrame];
        buffer.Enqueue(0, Frame(1), false);
        buffer.Enqueue(240, Frame(2), false);
        Assert.True(buffer.ReadNext(output));
        Assert.True(buffer.ReadNext(output));
        Assert.True(buffer.ReadNext(output)); // One concealment frame starts rebuffering.
        ulong resumedIndex = (ulong)(2 + missingFrames) * Protocol.SamplesPerFrame;
        Assert.True(buffer.Enqueue(resumedIndex, Frame(3), false));
        Assert.False(buffer.ReadNext(output));
        Assert.True(buffer.Enqueue(resumedIndex + Protocol.SamplesPerFrame, Frame(4), false));
        Assert.True(buffer.ReadNext(output));
        Assert.Equal((byte)3, output[0]);
        Assert.True(buffer.ReadNext(output));
        Assert.Equal((byte)4, output[0]);
        Assert.Equal(missingFrames, buffer.Lost);
        Assert.Equal(1, buffer.Underruns);
        Assert.Equal(0, buffer.BufferedFrames);
        Assert.False(buffer.Enqueue(resumedIndex - Protocol.SamplesPerFrame, Frame(9), false));
        Assert.Equal(1, buffer.Late);
    }

    [Fact]
    public void BurstRecoveryRetainsReorderedFramesAndRejectsDuplicates()
    {
        var buffer = new JitterBuffer(15, 8);
        byte[] output = new byte[Protocol.PcmBytesPerFrame];
        for (ulong i = 0; i < 3; i++) buffer.Enqueue(i * 240, Frame(1), false);
        for (int i = 0; i < 4; i++) Assert.True(buffer.ReadNext(output));
        Assert.True(buffer.Enqueue(7 * 240, Frame(7), false));
        Assert.True(buffer.Enqueue(5 * 240, Frame(5), false));
        Assert.False(buffer.Enqueue(7 * 240, Frame(99), false));
        Assert.False(buffer.ReadNext(output));
        Assert.True(buffer.Enqueue(6 * 240, Frame(6), false));
        for (byte i = 5; i <= 7; i++)
        {
            Assert.True(buffer.ReadNext(output));
            Assert.Equal(i, output[0]);
        }
        Assert.Equal(2, buffer.Lost);
        Assert.Equal(1, buffer.Duplicates);
    }

    [Fact]
    public void HealthyPlaybackRejectsFarFutureFramesWithoutDiscardingQueuedAudio()
    {
        var buffer = new JitterBuffer(10, 8);
        buffer.Enqueue(0, Frame(1), false);
        buffer.Enqueue(240, Frame(2), false);
        byte[] output = new byte[Protocol.PcmBytesPerFrame];
        Assert.True(buffer.ReadNext(output));
        Assert.False(buffer.Enqueue(100 * 240, Frame(9), false));
        Assert.True(buffer.ReadNext(output));
        Assert.Equal((byte)2, output[0]);
        Assert.Equal(1, buffer.Overruns);
    }

    [Fact]
    public void InterruptedStartupRecoversAfterWaitingForInitialFill()
    {
        var buffer = new JitterBuffer(10, 8);
        byte[] output = new byte[Protocol.PcmBytesPerFrame];
        buffer.Enqueue(0, Frame(1), false);
        Assert.False(buffer.ReadNext(output));
        Assert.True(buffer.Enqueue(100 * 240, Frame(2), false));
        Assert.False(buffer.Enqueue(0, Frame(9), false));
        Assert.True(buffer.Enqueue(101 * 240, Frame(3), false));
        Assert.True(buffer.ReadNext(output));
        Assert.Equal((byte)2, output[0]);
        Assert.True(buffer.ReadNext(output));
        Assert.Equal((byte)3, output[0]);
        Assert.Equal(99, buffer.Lost);
        Assert.Equal(1, buffer.Overruns);
        Assert.Equal(0, buffer.BufferedFrames);
    }

    [Fact]
    public void StartupReorderingCannotStrandFramesOutsideTheRingWindow()
    {
        var buffer = new JitterBuffer(15, 8);
        Assert.True(buffer.Enqueue(20 * 240, Frame(20), false));
        Assert.True(buffer.Enqueue(13 * 240, Frame(13), false));
        Assert.False(buffer.Enqueue(6 * 240, Frame(6), false));
        Assert.Equal(1, buffer.Late);
        Assert.True(buffer.Enqueue(14 * 240, Frame(14), false));
        Assert.True(buffer.Enqueue(15 * 240, Frame(15), false));
        byte[] output = new byte[Protocol.PcmBytesPerFrame];
        for (byte i = 13; i <= 15; i++)
        {
            Assert.True(buffer.ReadNext(output));
            Assert.Equal(i, output[0]);
        }
    }

    [Fact]
    public void RecoveryDiscardsIsolatedOldFramesWithoutLeakingSlots()
    {
        var buffer = new JitterBuffer(10, 8);
        byte[] output = new byte[Protocol.PcmBytesPerFrame];
        buffer.Enqueue(0, Frame(1), false);
        buffer.Enqueue(240, Frame(2), false);
        Assert.True(buffer.ReadNext(output));
        Assert.True(buffer.ReadNext(output));
        buffer.Enqueue(4 * 240, Frame(4), false);
        Assert.True(buffer.ReadNext(output));
        buffer.Enqueue(6 * 240, Frame(6), false);
        buffer.Enqueue(7 * 240, Frame(7), false);
        for (byte i = 6; i < 40; i++)
        {
            Assert.True(buffer.ReadNext(output));
            Assert.Equal(i, output[0]);
            Assert.True(buffer.Enqueue((ulong)(i + 2) * 240, Frame((byte)(i + 2)), false));
        }
        Assert.Equal(3, buffer.Lost);
        Assert.Equal(1, buffer.Overruns);
        Assert.Equal(2, buffer.BufferedFrames);
    }
}
