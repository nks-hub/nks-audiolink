using NksAudioLink.Client;
using NksAudioLink.Core;
using System.Buffers.Binary;

namespace NksAudioLink.Tests;

public sealed class AudioFrameQueueTests
{
    [Theory]
    [InlineData(-200)]
    [InlineData(200)]
    public void DriftCorrectionPreservesStereoToneWithoutDiscontinuities(int ppm)
    {
        var queue = new AudioFrameQueue();
        int sourceIndex = 0;
        void Capture(int frames)
        {
            byte[] pcm = new byte[frames * 4];
            for (int i = 0; i < frames; i++)
            {
                short value = (short)Math.Round(12000 * Math.Sin(2 * Math.PI * 1000 * sourceIndex++ / Protocol.SampleRate));
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4), value);
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4 + 2), (short)-value);
            }
            queue.Write(pcm);
        }
        Capture(720);
        byte[] output = new byte[Protocol.PcmBytesPerFrame];
        int? previous = null;
        double energy = 0;
        double fractional = 0;
        const int ticks = 400;
        for (int tick = 0; tick < ticks; tick++)
        {
            if (tick % 2 == 0)
            {
                fractional += 480 * (1 + ppm / 1_000_000.0);
                int frames = (int)fractional;
                fractional -= frames;
                Capture(frames);
            }
            Assert.True(queue.TryReadFrame(output));
            for (int i = 0; i < Protocol.SamplesPerFrame; i++)
            {
                int left = BinaryPrimitives.ReadInt16LittleEndian(output.AsSpan(i * 4));
                int right = BinaryPrimitives.ReadInt16LittleEndian(output.AsSpan(i * 4 + 2));
                Assert.InRange(Math.Abs(left + right), 0, 1);
                if (previous is not null) Assert.InRange(Math.Abs(left - previous.Value), 0, 1700);
                previous = left;
                energy += (double)left * left;
            }
        }
        Assert.InRange(Math.Sqrt(energy / (ticks * Protocol.SamplesPerFrame)), 8200, 8700);
    }

    [Theory]
    [InlineData(-200)]
    [InlineData(200)]
    public void TenMinutesOfCaptureClockDriftDoesNotCreateSilenceOrGrowingLatency(int ppm)
    {
        var queue = new AudioFrameQueue();
        byte[] capture = Enumerable.Repeat((byte)0x11, 500 * 4).ToArray();
        byte[] output = new byte[Protocol.PcmBytesPerFrame];
        queue.Write(Enumerable.Repeat((byte)0x11, 720 * 4).ToArray());
        double fractionalFrames = 0;
        const int tenMinutesAtFiveMs = 120_000;

        for (int tick = 0; tick < tenMinutesAtFiveMs; tick++)
        {
            // WASAPI commonly delivers a 10 ms callback, while the network sends every 5 ms.
            if (tick % 2 == 0)
            {
                fractionalFrames += 2 * Protocol.SamplesPerFrame * (1 + ppm / 1_000_000.0);
                int frames = (int)fractionalFrames;
                fractionalFrames -= frames;
                queue.Write(capture.AsSpan(0, frames * 4));
            }
            Assert.True(queue.TryReadFrame(output), $"Unexpected SILENT at tick {tick}, ppm {ppm}.");
            Assert.Equal((byte)0x11, output[0]);
            Assert.Equal((byte)0x11, output[^1]);
            Assert.InRange(queue.BufferedBytes, 0, 40 * Protocol.SampleRate / 1000 * 4);
        }

        Assert.Equal(0, queue.DroppedBytes);
        Assert.InRange(queue.BufferedBytes, 5 * 48 * 4, 25 * 48 * 4);
    }

    [Fact]
    public void SilenceAndResumeUseFreshAudioAfterSmallPrefill()
    {
        var queue = new AudioFrameQueue();
        byte[] output = new byte[Protocol.PcmBytesPerFrame];
        queue.Write(Enumerable.Repeat((byte)0x11, 720 * 4).ToArray());
        for (int i = 0; i < 3; i++) Assert.True(queue.TryReadFrame(output));
        Assert.False(queue.TryReadFrame(output));

        queue.Write(Enumerable.Repeat((byte)0x22, 240 * 4).ToArray());
        Assert.False(queue.TryReadFrame(output));
        queue.Write(Enumerable.Repeat((byte)0x22, 480 * 4).ToArray());
        Assert.True(queue.TryReadFrame(output));
        Assert.All(output, value => Assert.Equal((byte)0x22, value));
    }

    [Fact]
    public void DelayedSenderDropsOldAudioInsteadOfBuildingLatency()
    {
        var queue = new AudioFrameQueue();
        byte[] output = new byte[Protocol.PcmBytesPerFrame];
        queue.Write(Enumerable.Repeat((byte)0x11, 50 * 48 * 4).ToArray());
        queue.Write(Enumerable.Repeat((byte)0x22, 50 * 48 * 4).ToArray());

        Assert.InRange(queue.BufferedBytes, 0, 20 * 48 * 4);
        Assert.True(queue.TryReadFrame(output));
        Assert.All(output, value => Assert.Equal((byte)0x22, value));
        Assert.True(queue.DroppedBytes >= 80 * 48 * 4);
    }
}
