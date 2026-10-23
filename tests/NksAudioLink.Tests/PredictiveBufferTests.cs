using System.Buffers.Binary;
using System.Diagnostics;
using NksAudioLink.Core;

namespace NksAudioLink.Tests;

public sealed class PredictiveBufferTests
{
    private sealed record Change(double TimeMs, AdaptiveBufferStatus Policy, PlayoutStats Stats);
    private sealed record Observation(double TimeMs, AdaptiveBufferStatus Policy, PredictiveBufferStatus Prediction);
    private sealed record Result(PlayoutEngine Engine, List<Change> Changes, List<Observation> Observations, int MaximumSampleJump);
    private static long Tick(double ms) => Stopwatch.Frequency * 1000 + (long)Math.Round(ms * Stopwatch.Frequency / 1000);

    private static Result Run(int initial, int seconds, Func<int, double>? delay = null, int driftPpm = 0,
        Func<int, bool>? drop = null, bool duplicates = false, bool triangle = false)
    {
        var engine = new PlayoutEngine(initial, new(20, 100, Predictive: true));
        var packets = new List<(int Index, double Arrival)>();
        for (int i = 0; i <= seconds * 200; i++)
        {
            if (drop?.Invoke(i) == true) continue;
            double time = i * 5.0 / (1 + driftPpm / 1_000_000.0) + (delay?.Invoke(i) ?? 0);
            packets.Add((i, time));
            if (duplicates && i % 10 == 0) packets.Add((i, time + .5));
        }
        packets.Sort((a, b) => a.Arrival != b.Arrival ? a.Arrival.CompareTo(b.Arrival) : a.Index.CompareTo(b.Index));
        byte[] pcm = new byte[960];
        short[] output = new short[480];
        var changes = new List<Change>();
        var observations = new List<Observation>();
        uint changesSeen = 0;
        int position = 0, maximumJump = 0;
        short? previous = null;
        bool started = false;
        for (int tick = 0; tick <= seconds * 200; tick++)
        {
            double now = tick * 5;
            while (position < packets.Count && packets[position].Arrival <= now)
            {
                var packet = packets[position++];
                for (int i = 0; i < 240; i++)
                {
                    int phase = (packet.Index * 240 + i) % 40000;
                    short value = triangle ? (short)(phase < 20000 ? phase - 10000 : 30000 - phase) : (short)1200;
                    BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4), value);
                    BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4 + 2), value);
                }
                engine.Receive((ulong)packet.Index * 240, pcm, false, Tick(packet.Arrival));
            }
            engine.Read(output);
            var policy = engine.GetAdaptiveStatus()!.Value;
            if (policy.Changes != changesSeen)
            {
                changes.Add(new(now, policy, engine.GetStats()));
                changesSeen = policy.Changes;
            }
            if (tick % 20 == 0) observations.Add(new(now, policy, engine.GetPredictiveStatus()!.Value));
            if (triangle)
            {
                if (!started && output[0] != 0) started = true;
                if (started)
                    for (int i = 0; i < output.Length; i += 2)
                    {
                        if (previous is { } last) maximumJump = Math.Max(maximumJump, Math.Abs(output[i] - last));
                        Assert.Equal(output[i], output[i + 1]);
                        previous = output[i];
                    }
            }
        }
        return new(engine, changes, observations, maximumJump);
    }

    [Fact]
    public void RisingJitterRaisesTargetBeforeAnyLateLossOrUnderrun()
    {
        var result = Run(20, 25, i => Math.Min(18, Math.Max(0, i - 200) / 200.0) * (1 + Math.Sin(i / 18.0)) / 2);
        Assert.NotEmpty(result.Changes);
        var first = result.Changes[0];
        Assert.Equal(AdaptiveBufferReason.PredictiveRisk, first.Policy.Reason);
        Assert.Equal(0, first.Stats.Underruns);
        Assert.Equal(0, first.Stats.Lost);
        Assert.Equal(0, first.Stats.Late);
        Assert.Equal(0, result.Engine.GetStats().Underruns);
        Assert.InRange(first.TimeMs, 500, 20_000);
    }

    [Fact]
    public void IsolatedDelaySpikeIsFilteredInsteadOfRaisingTheTarget()
    {
        var result = Run(40, 12, i => i == 500 ? 20 : 0);
        Assert.Empty(result.Changes);
        Assert.Equal(0, result.Engine.GetStats().Underruns);
        Assert.True(result.Engine.GetPredictiveStatus()!.Value.ReorderedPackets > 0);
        Assert.InRange(result.Engine.GetPredictiveStatus()!.Value.EstimatedNeedMs, 15, 20);
    }

    [Fact]
    public void RepeatedAlternatingJitterUsesHysteresisWithoutOscillating()
    {
        var result = Run(40, 90, i => i % 80 < 40 ? 0 : 12);
        Assert.Equal(0, result.Engine.GetStats().Underruns);
        Assert.InRange(result.Changes.Count, 0, 3);
        Assert.All(result.Changes, change => Assert.Equal(AdaptiveBufferReason.Stable, change.Policy.Reason));
        Assert.All(result.Changes.Zip(result.Changes.Skip(1)), pair => Assert.True(pair.Second.TimeMs - pair.First.TimeMs >= 30_000));
        Assert.InRange(result.Engine.TargetLatencyMs, 30, 40);
        Assert.InRange(result.Engine.GetPredictiveStatus()!.Value.JitterSpreadMs, 10, 14);
    }

    [Theory]
    [InlineData(-200)]
    [InlineData(200)]
    public void NormalSourceClockDriftDoesNotMasqueradeAsNetworkJitter(int driftPpm)
    {
        var result = Run(40, 120, driftPpm: driftPpm);
        Assert.Equal(0, result.Engine.GetStats().Underruns);
        Assert.All(result.Changes, change => Assert.Equal(AdaptiveBufferReason.Stable, change.Policy.Reason));
        Assert.InRange(result.Engine.GetPredictiveStatus()!.Value.JitterSpreadMs, 0, 2);
        Assert.InRange(result.Engine.GetPredictiveStatus()!.Value.EstimatedNeedMs, 15, 19);
    }

    [Fact]
    public void UniqueReorderedPacketsContributeButDuplicatesDoNotRaiseRisk()
    {
        var result = Run(40, 12, i => i % 20 == 0 ? 10 : 0, duplicates: true);
        Assert.Empty(result.Changes);
        var prediction = result.Engine.GetPredictiveStatus()!.Value;
        Assert.True(prediction.DuplicatesIgnored > 100);
        Assert.True(prediction.ReorderedPackets > 100);
        Assert.InRange(prediction.JitterSpreadMs, 9, 11);
        Assert.Equal(0, result.Engine.GetStats().Underruns);
    }

    [Fact]
    public void ActualPacketLossUsesRecoveryFallbackWithoutInventingTransitJitter()
    {
        var result = Run(40, 12, drop: i => i == 600);
        Assert.Contains(result.Changes, change => change.Policy.Reason == AdaptiveBufferReason.Underrun);
        Assert.DoesNotContain(result.Changes, change => change.Policy.Reason == AdaptiveBufferReason.PredictiveRisk);
        Assert.InRange(result.Engine.GetPredictiveStatus()!.Value.EstimatedNeedMs, 15, 20);
        Assert.Equal(1, result.Engine.GetStats().Lost);
    }

    [Fact]
    public void PauseResetsPredictionWarmupAndMissingInputCannotCountAsStability()
    {
        var resumed = Run(40, 12, drop: i => i is >= 600 and < 800);
        Assert.True(resumed.Engine.GetPredictiveStatus()!.Value.Discontinuities >= 1);
        Assert.Contains(resumed.Observations, o => o.TimeMs >= 4000 && o.TimeMs < 4500 && !o.Prediction.WarmedUp);
        Assert.DoesNotContain(resumed.Changes, c => c.Policy.Reason == AdaptiveBufferReason.PredictiveRisk);
        var stopped = Run(40, 40, drop: i => i >= 1000);
        Assert.DoesNotContain(stopped.Changes, c => c.Policy.Reason == AdaptiveBufferReason.Stable);
        Assert.Equal(0, stopped.Engine.GetPredictiveStatus()!.Value.StableForMs);
    }

    [Fact]
    public void StartupDelayIsNotTransitVariationAndRequiresWarmup()
    {
        var result = Run(40, 2, _ => 200);
        Assert.Empty(result.Changes);
        Assert.Contains(result.Observations, o => o.TimeMs == 600 && !o.Prediction.WarmedUp);
        Assert.True(result.Engine.GetPredictiveStatus()!.Value.WarmedUp);
        Assert.Equal(15, result.Engine.GetPredictiveStatus()!.Value.EstimatedNeedMs);
    }

    [Fact]
    public void PredictionAndGradualSettlingPreserveWaveformContinuity()
    {
        var result = Run(40, 100, i => i < 8000 ? Math.Min(34, Math.Max(0, i - 400) / 140.0) * (1 + Math.Sin(i / 25.0)) / 2 : 0, triangle: true);
        Assert.Contains(result.Changes, c => c.Policy.Reason == AdaptiveBufferReason.PredictiveRisk);
        Assert.Contains(result.Changes, c => c.Policy.Reason == AdaptiveBufferReason.Stable);
        Assert.Equal(0, result.Engine.GetStats().Underruns);
        Assert.Equal(0, result.Engine.GetStats().Lost);
        Assert.InRange(result.MaximumSampleJump, 0, 2);
        Assert.All(result.Changes, c => Assert.InRange(c.Policy.TargetMs, 20, 100));
    }

    [Fact]
    public void SteadyPredictionUsesBoundedStorageAndNoAudioPathAllocationsAfterWarmup()
    {
        var engine = new PlayoutEngine(40, new(20, 100, Predictive: true));
        byte[] pcm = new byte[960];
        short[] output = new short[480];
        int frame = 0;
        for (; frame < 2000; frame++)
        {
            engine.Receive((ulong)frame * 240, pcm, false, Tick(frame * 5));
            engine.Read(output);
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (; frame < 12000; frame++)
        {
            engine.Receive((ulong)frame * 240, pcm, false, Tick(frame * 5));
            engine.Read(output);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Equal(512, engine.GetPredictiveStatus()!.Value.Samples);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(90)]
    public void RareRecurringDelayRetainsLearnedSafetyThenEventuallySettles(int intervalSeconds)
    {
        int lastBurst = 5 + 3 * intervalSeconds;
        var result = Run(20, lastBurst + 165, i =>
            i >= 1000 && i <= lastBurst * 200 && (i - 1000) % (intervalSeconds * 200) == 0 ? 25 : 0);
        var failures = result.Changes.Where(c => c.Policy.Reason == AdaptiveBufferReason.Underrun).ToArray();
        // The bounded +10 ms fallback needs two observations to learn 40 ms from 20 ms.
        Assert.Equal(2, failures.Length);
        Assert.InRange(failures[0].TimeMs, 5000, 5100);
        Assert.InRange(failures[1].TimeMs, (5 + intervalSeconds) * 1000, (5 + intervalSeconds) * 1000 + 100);
        Assert.DoesNotContain(result.Changes, c => c.Policy.Reason == AdaptiveBufferReason.Stable && c.TimeMs < lastBurst * 1000 + 120_000);
        Assert.Contains(result.Observations, o => o.TimeMs > 8000 && o.Prediction.State == "FailureHold");
        Assert.Contains(result.Changes, c => c.Policy.Reason == AdaptiveBufferReason.Stable && c.TimeMs >= lastBurst * 1000 + 120_000);
        Assert.Equal(2, result.Engine.GetStats().Lost);
    }
}
