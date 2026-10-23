using System.Buffers.Binary;
using NksAudioLink.Core;

namespace NksAudioLink.Tests;

public sealed class PcmOutputProcessorTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(0.125)]
    [InlineData(0.5)]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    [InlineData(4)]
    public void BypassMatchesLegacyRoundingForEveryPcm16Value(double gain)
    {
        var processor = new PcmOutputProcessor(gain);
        var pcm = new short[65536 * 2];
        for (int i = 0; i < 65536; i++) pcm[i * 2] = pcm[i * 2 + 1] = (short)(i + short.MinValue);
        processor.Process(pcm, gain, false, false);
        for (int i = 0; i < 65536; i++)
        {
            short expected = (short)Math.Clamp((int)Math.Round((i + short.MinValue) * gain), short.MinValue, short.MaxValue);
            Assert.Equal(expected, pcm[i * 2]);
            Assert.Equal(expected, pcm[i * 2 + 1]);
        }
        Assert.Equal(0, processor.LatencyFrames);
    }

    [Fact]
    public void LookaheadIsExactlyOneMillisecondAndSilenceDrainsTheTail()
    {
        var processor = new PcmOutputProcessor(limiterEnabled: true);
        short[] pulse = [12000, -6000];
        Assert.False(processor.Process(pulse, 1, false, true));
        for (int frame = 1; frame < PcmOutputProcessor.LookaheadFrames; frame++)
        {
            short[] silence = [0, 0];
            Assert.False(processor.Process(silence, 1, false, true));
        }
        short[] tail = [0, 0];
        Assert.True(processor.Process(tail, 1, false, true));
        Assert.Equal(new short[] { 12000, -6000 }, tail);
        tail.AsSpan().Clear();
        Assert.False(processor.Process(tail, 1, false, true));
        Assert.Equal(48, processor.LatencyFrames);
    }

    [Fact]
    public void HotStereoIsLinkedAndNeverExceedsTheCeiling()
    {
        var processor = new PcmOutputProcessor(4, limiterEnabled: true);
        var pcm = new short[4000];
        for (int i = 0; i < pcm.Length; i += 2) { pcm[i] = 30000; pcm[i + 1] = 15000; }
        processor.Process(pcm, 4, false, true);
        for (int frame = 48; frame < pcm.Length / 2; frame++)
        {
            Assert.InRange(Math.Abs((int)pcm[frame * 2]), 1, PcmOutputProcessor.Ceiling);
            Assert.InRange(Math.Abs(pcm[frame * 2] - 2 * pcm[frame * 2 + 1]), 0, 1);
        }
        Assert.Equal(PcmOutputProcessor.Ceiling, pcm[96]);

        for (int i = 0; i < pcm.Length; i += 2) { pcm[i] = short.MinValue; pcm[i + 1] = short.MaxValue; }
        processor.Process(pcm, 4, false, true);
        Assert.All(pcm, sample => Assert.InRange((int)sample, -PcmOutputProcessor.Ceiling, PcmOutputProcessor.Ceiling));
    }

    [Fact]
    public void PeakHasBoundedLookaheadAttackAndFiftyMillisecondRelease()
    {
        const int peakFrame = 100;
        const int peakOutput = peakFrame + PcmOutputProcessor.LookaheadFrames;
        const int releaseFrames = 2400;
        var processor = new PcmOutputProcessor(4, limiterEnabled: true);
        var pcm = Enumerable.Repeat((short)1000, (peakOutput + releaseFrames + 2) * 2).ToArray();
        pcm[peakFrame * 2] = pcm[peakFrame * 2 + 1] = short.MaxValue;
        processor.Process(pcm, 4, false, true);
        Assert.Equal(4000, pcm[peakFrame * 2]);
        for (int frame = peakFrame + 1; frame < peakOutput; frame++)
        {
            int previous = pcm[(frame - 1) * 2], current = pcm[frame * 2];
            Assert.InRange(previous - current, 0, 64);
        }
        Assert.Equal(PcmOutputProcessor.Ceiling, pcm[peakOutput * 2]);
        double required = PcmOutputProcessor.Ceiling / (short.MaxValue * 4.0);
        double expected = 4000 * (1 - (1 - required) * Math.Exp(-1));
        Assert.InRange(pcm[(peakOutput + releaseFrames) * 2], expected - 1, expected + 1);
        for (int frame = peakOutput + 2; frame < pcm.Length / 2; frame++)
            Assert.InRange(pcm[frame * 2] - pcm[(frame - 1) * 2], 0, 2);
    }

    [Fact]
    public void LiveGainAndMuteRampsAreContinuousAndMutePreservesTheSelectedGain()
    {
        var processor = new PcmOutputProcessor();
        int previous = 10000;
        for (int frame = 0; frame < 240; frame++)
        {
            short[] pcm = [10000, -10000];
            processor.Process(pcm, 2, false, false);
            Assert.InRange(pcm[0] - previous, 41, 42);
            previous = pcm[0];
        }
        Assert.Equal(20000, previous);
        for (int frame = 0; frame < 240; frame++)
        {
            short[] pcm = [10000, -10000];
            processor.Process(pcm, 2, true, false);
            Assert.InRange(previous - pcm[0], 83, 84);
            previous = pcm[0];
        }
        Assert.Equal(0, previous);
        var unmute = Enumerable.Repeat((short)10000, 480).ToArray();
        processor.Process(unmute, 2, false, false);
        Assert.Equal(20000, unmute[^1]);
    }

    [Fact]
    public void LiveLimiterToggleCrossfadesWithoutChangingSteadyQuietPcm()
    {
        var processor = new PcmOutputProcessor();
        short[] history = Enumerable.Repeat((short)10000, 200).ToArray();
        processor.Process(history, 1, false, false);
        for (int frame = 0; frame < 240; frame++)
        {
            short[] pcm = [10000, 10000];
            processor.Process(pcm, 1, false, true);
            Assert.Equal(new short[] { 10000, 10000 }, pcm);
            Assert.Equal(48, processor.LatencyFrames);
        }
        for (int frame = 0; frame < 240; frame++)
        {
            short[] pcm = [10000, 10000];
            processor.Process(pcm, 1, false, false);
            Assert.Equal(new short[] { 10000, 10000 }, pcm);
            Assert.Equal(frame == 239 ? 0 : 48, processor.LatencyFrames);
        }
    }

    [Fact]
    public void HotEnableIsImmediatelyProtectedAndDisableOpensTheGuardGradually()
    {
        var processor = new PcmOutputProcessor(4);
        var pcm = Enumerable.Repeat(short.MaxValue, 480).ToArray();
        processor.Process(pcm, 4, false, false);
        pcm.AsSpan().Fill(short.MaxValue);
        processor.Process(pcm, 4, false, true);
        Assert.All(pcm, sample => Assert.InRange((int)sample, 0, PcmOutputProcessor.Ceiling));
        pcm.AsSpan().Fill(short.MaxValue);
        processor.Process(pcm, 4, false, false);
        for (int frame = 1; frame < 240; frame++) Assert.InRange(pcm[frame * 2] - pcm[(frame - 1) * 2], 0, 3);
        Assert.Equal(short.MaxValue, pcm[^1]);
        Assert.Equal(0, processor.LatencyFrames);
    }

    [Fact]
    public void ByteAndSampleApisAgreeAcrossGainMuteAndLimiterChanges()
    {
        var samples = new PcmOutputProcessor();
        var bytes = new PcmOutputProcessor();
        var random = new Random(53);
        foreach (var settings in new[] { (1.0, false, false), (4.0, false, true), (4.0, true, true), (0.5, false, false) })
        {
            short[] pcm = new short[480];
            byte[] raw = new byte[960];
            for (int i = 0; i < pcm.Length; i++)
            {
                pcm[i] = (short)random.Next(short.MinValue, short.MaxValue + 1);
                BinaryPrimitives.WriteInt16LittleEndian(raw.AsSpan(i * 2), pcm[i]);
            }
            Assert.Equal(samples.Process(pcm, settings.Item1, settings.Item2, settings.Item3),
                bytes.ProcessPcm16(raw, settings.Item1, settings.Item2, settings.Item3));
            for (int i = 0; i < pcm.Length; i++) Assert.Equal(pcm[i], BinaryPrimitives.ReadInt16LittleEndian(raw.AsSpan(i * 2)));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SteadyProcessingAllocatesNothingAndSilenceStaysSilent(bool limiter)
    {
        var processor = new PcmOutputProcessor(limiterEnabled: limiter);
        var pcm = new byte[Protocol.PcmBytesPerFrame];
        for (int i = 0; i < 1000; i++) processor.ProcessPcm16(pcm, 1, false, limiter);
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool nonzero = false;
        for (int i = 0; i < 1000; i++) nonzero |= processor.ProcessPcm16(pcm, 1, false, limiter);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.False(nonzero);
    }

    [Fact]
    public void StreamResetDiscardsDelayedPcmAndRestoresCurrentControls()
    {
        var processor = new PcmOutputProcessor(4, limiterEnabled: true);
        short[] hot = [30000, 15000];
        processor.Process(hot, 4, false, true);
        processor.Reset(2, false, true);
        var silence = new short[480];
        Assert.False(processor.Process(silence, 2, false, true));
        Assert.All(silence, value => Assert.Equal(0, value));
        processor.Reset(4, true, false);
        silence.AsSpan().Fill(30000);
        Assert.False(processor.Process(silence, 4, true, false));
        Assert.Equal(0, processor.LatencyFrames);
    }

    [Fact]
    public void InvalidGainAndPartialStereoFramesAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PcmOutputProcessor(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PcmOutputProcessor(4.01));
        var processor = new PcmOutputProcessor();
        Assert.Throws<ArgumentOutOfRangeException>(() => processor.Process(new short[2], double.PositiveInfinity, false, false));
        Assert.Throws<ArgumentException>(() => processor.Process(new short[1], 1, false, false));
        Assert.Throws<ArgumentException>(() => processor.ProcessPcm16(new byte[2], 1, false, false));
    }
}
