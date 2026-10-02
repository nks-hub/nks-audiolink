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
}
