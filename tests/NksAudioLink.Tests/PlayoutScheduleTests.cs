using System.Net;
using System.Net.Sockets;
using NksAudioLink.Core;
using NksAudioLink.Server;

namespace NksAudioLink.Tests;

public sealed class PlayoutScheduleTests
{
    [Fact]
    public void RepeatedShortSchedulerStallsPreserveOutputSampleCountAndHeadroom()
    {
        // Milliseconds stand in for monotonic ticks. The receiver starts with
        // 20 ms in its output queue; each write adds 240 stereo sample frames.
        var schedule = new PlayoutSchedule(0, 5);
        long now = 0, previousWrite = 0, discarded = 0;
        long[] stalls = [100, 200, 300, 400];
        int writes = 0;
        long queuedMs = 20;
        while (schedule.Deadline <= 1000)
        {
            now = Math.Max(now, schedule.Deadline);
            if (stalls.Contains(schedule.Deadline)) now += 7;
            discarded += schedule.LimitCatchUp(now);
            queuedMs -= now - previousWrite;
            Assert.True(queuedMs > 0, "Short stalls must not cumulatively empty the output queue.");
            queuedMs += 5;
            previousWrite = now;
            writes++;
            schedule.Advance();
        }
        Assert.Equal(200, writes);
        Assert.Equal(48000, writes * Protocol.SamplesPerFrame);
        Assert.Equal(20, queuedMs);
        Assert.Equal(0, discarded);
    }

    [Theory]
    [InlineData(20L)]
    [InlineData(100L)]
    [InlineData(1000000000L)]
    public void LongPauseHasAtMostFourImmediatelyDueWrites(long now)
    {
        var schedule = new PlayoutSchedule(0, 5);
        long discarded = 0;
        int burst = 0;
        while (schedule.Deadline <= now)
        {
            discarded += schedule.LimitCatchUp(now);
            burst++;
            Assert.True(burst <= PlayoutSchedule.MaximumCatchUpFrames);
            schedule.Advance();
        }
        Assert.Equal(PlayoutSchedule.MaximumCatchUpFrames, burst);
        Assert.Equal(now / 5 - burst, discarded);
        Assert.Equal(now + 5, schedule.Deadline);
    }

    [Fact]
    public void AFrameExactlyOnItsDeadlineIsNotDiscarded()
    {
        var schedule = new PlayoutSchedule(0, 5);
        Assert.Equal(0, schedule.LimitCatchUp(5));
        schedule.Advance();
        Assert.Equal(0, schedule.LimitCatchUp(10));
        Assert.Equal(10, schedule.Deadline);
    }

    [Fact]
    public void BlockingSinkCanPaceWritesWithoutForcingAdditionalDroppedFrames()
    {
        var schedule = new PlayoutSchedule(0, 5);
        long now = 20;
        for (int i = 0; i < 200; i++)
        {
            Assert.Equal(0, schedule.LimitCatchUp(now));
            // A full native output buffer may block a write for one period.
            now += 5;
            schedule.Advance();
        }
        Assert.Equal(1005, schedule.Deadline);
    }

    [Fact]
    public async Task CancellationDuringLateWriteStopsBeforeAnyCatchUpWrite()
    {
        using var reservation = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
        reservation.Dispose();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var sink = new CancellingSink(stop);
        var server = new AudioServer(new ServerConfig { Port = port }, _ => sink);
        Task running = server.RunAsync(stop.Token);
        using var sender = new UdpClient();
        try
        {
            byte[] body = new byte[74];
            int length = new HelloMessage(48000, 2, 1, 5, 30, "cancel-schedule").Write(body);
            byte[] packet = new byte[Protocol.MaxDatagramSize];
            length = Protocol.Write(packet, new PacketHeader(PacketType.Hello, PacketFlags.None, 42, 1), body[..length]);
            await sender.SendAsync(packet.AsMemory(0, length), new IPEndPoint(IPAddress.Loopback, port));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, sink.Writes);
        }
        finally
        {
            stop.Cancel();
            try { await running.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
        }
    }

    private sealed class CancellingSink(CancellationTokenSource stop) : IAudioSink
    {
        public int Writes;
        public int DelayMs => 0;
        public void Write(ReadOnlySpan<short> samples)
        {
            Interlocked.Increment(ref Writes);
            Thread.Sleep(30);
            stop.Cancel();
        }
        public void Dispose() { }
    }
}
