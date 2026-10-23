using System.Buffers.Binary;
using NksAudioLink.Core;

namespace NksAudioLink.Tests;

public sealed class AdaptiveBufferTests
{
    private static byte[] Pcm()
    {
        byte[] bytes = new byte[Protocol.PcmBytesPerFrame];
        for (int i = 0; i < bytes.Length; i += 2) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i), 1200);
        return bytes;
    }

    [Fact]
    public void ManualModeKeepsTheRequestedTargetAfterLossAndHasNoAdaptiveState()
    {
        var engine = new PlayoutEngine(30);
        var pcm = Pcm();
        for (ulong i = 0; i < 6; i++) engine.Receive(i * 240, pcm, false);
        var output = new short[480];
        for (int i = 0; i < 100; i++) engine.Read(output);
        Assert.True(engine.GetStats().Underruns > 0);
        Assert.Equal(30, engine.TargetLatencyMs);
        Assert.Null(engine.GetAdaptiveStatus());
    }

    [Fact]
    public void UnderrunRaisesTheTargetAndSilenceWithoutInputDoesNotLowerIt()
    {
        var engine = new PlayoutEngine(30, new(20, 50));
        var pcm = Pcm();
        for (ulong i = 0; i < 6; i++) engine.Receive(i * 240, pcm, false);
        var output = new short[480];
        for (int i = 0; i < 30; i++) engine.Read(output);
        Assert.Equal(40, engine.TargetLatencyMs);
        Assert.Equal(AdaptiveBufferReason.Underrun, engine.GetAdaptiveStatus()!.Value.Reason);
        for (int i = 0; i < 12_000; i++) engine.Read(output);
        Assert.Equal(40, engine.TargetLatencyMs);
        Assert.Equal(1u, engine.GetAdaptiveStatus()!.Value.Changes);
        engine.Reset();
        Assert.Equal(new AdaptiveBufferStatus(30, 20, 50, AdaptiveBufferReason.Initial, 0), engine.GetAdaptiveStatus());
    }

    [Fact]
    public void LatePacketBurstsIncreaseOnlyOncePerCooldownAndNeverExceedMaximum()
    {
        var engine = new PlayoutEngine(40, new(20, 60));
        var pcm = Pcm();
        ulong next = 0;
        for (int i = 0; i < 10; i++) { engine.Receive(next, pcm, false); next += 240; }
        var output = new short[480];
        for (int tick = 0; tick < 1000; tick++)
        {
            engine.Receive(next, pcm, false); next += 240;
            engine.Read(output);
            if (tick >= 10) engine.Receive(0, pcm, false);
            Assert.InRange(engine.TargetLatencyMs, 40, 60);
            if (tick < 410) Assert.InRange(engine.GetAdaptiveStatus()!.Value.Changes, 0u, 1u);
            Assert.All(output, value => Assert.Equal((short)1200, value));
        }
        Assert.Equal(60, engine.TargetLatencyMs);
        Assert.Equal(2u, engine.GetAdaptiveStatus()!.Value.Changes);
        Assert.Equal(AdaptiveBufferReason.MaximumReached, engine.GetAdaptiveStatus()!.Value.Reason);
        Assert.Equal(0, engine.GetStats().Underruns);
        Assert.Equal(0, engine.GetStats().Overruns);
    }

    [Fact]
    public void SustainedPlaybackLowersTargetSlowlyWithoutDroppingOrResettingAudioTimeline()
    {
        var engine = new PlayoutEngine(40, new(30, 80));
        var pcm = Pcm();
        ulong next = 0;
        for (int i = 0; i < 8; i++) { engine.Receive(next, pcm, false); next += 240; }
        var output = new short[480];
        int previousPpm = 0;
        for (int tick = 0; tick < 18_000; tick++)
        {
            engine.Receive(next, pcm, false); next += 240;
            engine.Read(output);
            Assert.All(output, value => Assert.Equal((short)1200, value));
            var stats = engine.GetStats();
            Assert.InRange(stats.RatioPpm, -5000, 5000);
            Assert.InRange(Math.Abs(stats.RatioPpm - previousPpm), 0, 20);
            previousPpm = stats.RatioPpm;
            if (tick < 5999) Assert.Equal(40, engine.TargetLatencyMs);
        }
        Assert.Equal(30, engine.TargetLatencyMs);
        Assert.Equal(2u, engine.GetAdaptiveStatus()!.Value.Changes);
        Assert.Equal(AdaptiveBufferReason.Stable, engine.GetAdaptiveStatus()!.Value.Reason);
        Assert.Equal(0, engine.GetStats().Lost);
        Assert.Equal(0, engine.GetStats().Underruns);
        Assert.Equal(0, engine.GetStats().Overruns);
    }

    [Theory]
    [InlineData(10, 100, 30)]
    [InlineData(20, 105, 30)]
    [InlineData(25, 20, 25)]
    [InlineData(20, 100, 15)]
    [InlineData(22, 100, 30)]
    public void InvalidBoundsAndInitialTargetAreRejected(int min, int max, int initial)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlayoutEngine(initial, new(min, max)));
    }

    [Fact]
    public void TargetIncreaseAndDecreasePreserveAContinuousVaryingSignalWithoutFrameJumps()
    {
        var engine = new PlayoutEngine(40, new(30, 80));
        var pcm = new byte[Protocol.PcmBytesPerFrame];
        ulong next = 0;
        void EnqueueTriangle()
        {
            for (int i = 0; i < Protocol.SamplesPerFrame; i++)
            {
                int phase = (int)((next + (ulong)i) % 40000);
                short value = (short)(phase < 20000 ? phase - 10000 : 30000 - phase);
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4), value);
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4 + 2), value);
            }
            Assert.True(engine.Receive(next, pcm, false));
            next += Protocol.SamplesPerFrame;
        }
        for (int i = 0; i < 8; i++) EnqueueTriangle();
        var output = new short[480];
        short? previous = null;
        for (int tick = 0; tick < 8000; tick++)
        {
            EnqueueTriangle();
            if (tick == 100) Assert.False(engine.Receive(0, pcm, false));
            engine.Read(output);
            for (int i = 0; i < output.Length; i += 2)
            {
                // The source changes at most one unit/sample; <=0.5% resampling
                // plus integer rounding cannot create a jump over two units.
                if (previous is { } last) Assert.InRange(Math.Abs(output[i] - last), 0, 2);
                Assert.Equal(output[i], output[i + 1]);
                previous = output[i];
            }
        }
        Assert.Equal(45, engine.TargetLatencyMs);
        Assert.Equal(2u, engine.GetAdaptiveStatus()!.Value.Changes);
        Assert.Equal(0, engine.GetStats().Lost);
        Assert.Equal(0, engine.GetStats().Underruns);
        Assert.Equal(0, engine.GetStats().Overruns);
    }
}
