using System.Buffers.Binary;
using NksAudioLink.Core;

namespace NksAudioLink.Tests;

public class PlayoutEngineTests
{
    [Fact]
    public void ReadsIndexedPcmInOrder()
    {
        var engine = new PlayoutEngine(10);
        byte[] first = new byte[Protocol.PcmBytesPerFrame];
        byte[] second = new byte[Protocol.PcmBytesPerFrame];
        for (int i = 0; i < first.Length; i += 2)
        {
            BinaryPrimitives.WriteInt16LittleEndian(first.AsSpan(i), 1000);
            BinaryPrimitives.WriteInt16LittleEndian(second.AsSpan(i), 2000);
        }
        Assert.True(engine.Receive(0, first, false));
        Assert.True(engine.Receive(240, second, false));
        short[] output = new short[Protocol.SamplesPerFrame * Protocol.Channels];
        engine.Read(output);
        Assert.InRange(output[0], (short)990, (short)1010);
        Assert.InRange(output[^1], (short)990, (short)2010);
    }

    [Fact]
    public void NoInputYieldsSilence()
    {
        var engine = new PlayoutEngine();
        short[] output = Enumerable.Repeat((short)9, Protocol.SamplesPerFrame * Protocol.Channels).ToArray();
        engine.Read(output);
        Assert.All(output, value => Assert.Equal((short)0, value));
    }

    [Fact]
    public void PlaybackResumesAfterLossLongerThanReceiveWindow()
    {
        var engine = new PlayoutEngine(10);
        byte[] pcm = new byte[Protocol.PcmBytesPerFrame];
        for (int i = 0; i < pcm.Length; i += 2)
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i), 1200);
        engine.Receive(0, pcm, false);
        engine.Receive(240, pcm, false);
        short[] output = new short[Protocol.SamplesPerFrame * Protocol.Channels];
        for (int i = 0; i < 6; i++) engine.Read(output);
        Assert.All(output, sample => Assert.Equal((short)0, sample));
        for (ulong i = 200; i < 210; i++)
            Assert.True(engine.Receive(i * Protocol.SamplesPerFrame, pcm, false));
        engine.Read(output);
        engine.Read(output);
        Assert.All(output, sample => Assert.Equal((short)1200, sample));
        Assert.Equal(198, engine.GetStats().Lost);
    }
}
