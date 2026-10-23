using System.Buffers.Binary;
using NksAudioLink.Core;

namespace NksAudioLink.Tests;

public sealed class AudioLevelMeterTests
{
    [Fact]
    public void SilenceAndResetHaveFiniteFloorOnBothChannels()
    {
        var meter = new AudioLevelMeter();
        var silent = new AudioLevelSnapshot(-90, -90, -90, -90, false);
        Assert.Equal(silent, meter.GetSnapshot());
        meter.Process([short.MinValue, short.MaxValue]);
        Assert.True(meter.GetSnapshot().Clipping);
        meter.Reset();
        meter.ProcessSilence(48000);
        Assert.Equal(silent, meter.GetSnapshot());
    }

    [Fact]
    public void StereoChannelsMeasureIndependentRmsAndPeaks()
    {
        var meter = new AudioLevelMeter();
        short[] pcm = StereoConstant(14400, 16384, -8192);
        meter.Process(pcm);
        var levels = meter.GetSnapshot();
        Assert.Equal(-6.0206, levels.LeftRmsDbfs, 4);
        Assert.Equal(-12.0412, levels.RightRmsDbfs, 4);
        Assert.Equal(levels.LeftRmsDbfs, levels.LeftPeakDbfs, 8);
        Assert.Equal(levels.RightRmsDbfs, levels.RightPeakDbfs, 8);
        Assert.False(levels.Clipping);
    }

    [Fact]
    public void SineRmsIsThreeDbBelowItsPeak()
    {
        var meter = new AudioLevelMeter();
        short[] pcm = new short[14400 * 2];
        for (int frame = 0; frame < 14400; frame++)
            pcm[frame * 2] = (short)Math.Round(16384 * Math.Sin(2 * Math.PI * frame / 48));
        meter.Process(pcm);
        var levels = meter.GetSnapshot();
        Assert.InRange(levels.LeftRmsDbfs, -9.032, -9.030);
        Assert.InRange(levels.LeftPeakDbfs - levels.LeftRmsDbfs, 3.009, 3.012);
        Assert.Equal(-90, levels.RightRmsDbfs);
        Assert.Equal(-90, levels.RightPeakDbfs);
    }

    [Fact]
    public void SilenceClearsRmsWindowWhilePeakHoldsThenDecays()
    {
        var meter = new AudioLevelMeter();
        meter.Process(StereoConstant(14400, -32768, 16384));
        meter.ProcessSilence(14400);
        var held = meter.GetSnapshot();
        Assert.Equal(-90, held.LeftRmsDbfs);
        Assert.Equal(-90, held.RightRmsDbfs);
        Assert.Equal(0, held.LeftPeakDbfs);
        Assert.Equal(-6.0206, held.RightPeakDbfs, 4);
        Assert.True(held.Clipping);
        meter.ProcessSilence(33600); // One full second since the last peak.
        Assert.False(meter.GetSnapshot().Clipping);
        Assert.Equal(0, meter.GetSnapshot().LeftPeakDbfs);
        meter.ProcessSilence(48000);
        Assert.Equal(-20, meter.GetSnapshot().LeftPeakDbfs, 5);
        meter.ProcessSilence(48000 * 4);
        Assert.Equal(-90, meter.GetSnapshot().LeftPeakDbfs);
        Assert.Equal(-90, meter.GetSnapshot().RightPeakDbfs);
    }

    [Fact]
    public void BytesShortsAndChunkBoundariesProduceTheSameLevels()
    {
        var whole = new AudioLevelMeter();
        var chunks = new AudioLevelMeter();
        var bytes = new AudioLevelMeter();
        short[] pcm = new short[Protocol.SampleRate * 2];
        var random = new Random(724);
        byte[] encoded = new byte[pcm.Length * 2];
        for (int i = 0; i < pcm.Length; i++)
        {
            pcm[i] = (short)random.Next(short.MinValue, short.MaxValue + 1);
            BinaryPrimitives.WriteInt16LittleEndian(encoded.AsSpan(i * 2), pcm[i]);
        }
        whole.Process(pcm);
        for (int offset = 0; offset < pcm.Length; offset += 480) chunks.Process(pcm.AsSpan(offset, 480));
        bytes.ProcessPcm16(encoded);
        Assert.Equal(whole.GetSnapshot(), chunks.GetSnapshot());
        Assert.Equal(whole.GetSnapshot(), bytes.GetSnapshot());
    }

    [Theory]
    [InlineData(short.MaxValue)]
    [InlineData(short.MinValue)]
    public void EitherFullScalePolarityOnEitherChannelTriggersClipping(short fullScale)
    {
        var meter = new AudioLevelMeter();
        meter.Process([0, fullScale]);
        Assert.True(meter.GetSnapshot().Clipping);
        Assert.InRange(meter.GetSnapshot().RightPeakDbfs, -0.001, 0);
        meter.ProcessSilence(48000);
        Assert.False(meter.GetSnapshot().Clipping);
        meter.Process([fullScale, 0]);
        Assert.True(meter.GetSnapshot().Clipping);
    }

    [Fact]
    public void PartialStereoFramesAreRejectedBeforeChangingTheMeter()
    {
        var meter = new AudioLevelMeter();
        var initial = meter.GetSnapshot();
        Assert.Throws<ArgumentException>(() => meter.Process(new short[3]));
        Assert.Throws<ArgumentException>(() => meter.ProcessPcm16(new byte[6]));
        Assert.Throws<ArgumentOutOfRangeException>(() => meter.ProcessSilence(-1));
        Assert.Equal(initial, meter.GetSnapshot());
    }

    [Fact]
    public void AudioHotPathAndSnapshotDoNotAllocate()
    {
        var meter = new AudioLevelMeter();
        byte[] pcm = new byte[Protocol.PcmBytesPerFrame];
        short[] samples = StereoConstant(240, 12000, -8000);
        for (int i = 0; i < 100; i++)
        {
            meter.ProcessPcm16(pcm);
            meter.Process(samples);
            meter.ProcessSilence(240);
            _ = meter.GetSnapshot();
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            meter.ProcessPcm16(pcm);
            meter.Process(samples);
            meter.ProcessSilence(240);
            _ = meter.GetSnapshot();
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static short[] StereoConstant(int frames, short left, short right)
    {
        short[] pcm = new short[frames * 2];
        for (int i = 0; i < frames; i++) { pcm[i * 2] = left; pcm[i * 2 + 1] = right; }
        return pcm;
    }
}
