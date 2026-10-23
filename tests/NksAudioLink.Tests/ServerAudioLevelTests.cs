using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using NksAudioLink.Core;
using NksAudioLink.Server;

namespace NksAudioLink.Tests;

public sealed class ServerAudioLevelTests
{
    [Fact]
    public async Task LevelsMeasureActualPlayoutSamplesAndResetOnTakeover()
    {
        using var portReservation = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)portReservation.Client.LocalEndPoint!).Port;
        portReservation.Dispose();
        using var output = new InspectingSink();
        int opened = 0;
        var server = new AudioServer(new ServerConfig { Port = port },
            _ => Interlocked.Increment(ref opened) == 1 ? output : new NullSink());
        using var stop = new CancellationTokenSource();
        Task running = server.RunAsync(stop.Token);
        using var sender = new UdpClient();
        sender.Connect(IPAddress.Loopback, port);
        try
        {
            await Hello(sender, 101);
            Assert.Equal(-90, server.GetStatus().Session!.Levels!.Value.LeftRmsDbfs);

            // A left-only source would miss a channel swap. Use unequal stereo
            // amplitudes and compare the meter with exactly what the sink sees.
            byte[] pcm = new byte[Protocol.PcmBytesPerFrame];
            for (int i = 0; i < Protocol.SamplesPerFrame; i++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4), 16384);
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4 + 2), 8192);
            }
            for (int frame = 0; frame < 12; frame++)
            {
                byte[] body = new byte[AudioMessage.PrefixSize + pcm.Length];
                AudioMessage.Write(body, (ulong)(frame * Protocol.SamplesPerFrame), Protocol.SamplesPerFrame, pcm, false);
                await Send(sender, PacketType.Audio, 101, body);
            }

            AudioLevelSnapshot actualOutput = await output.FirstAudio.Task.WaitAsync(TimeSpan.FromSeconds(3));
            AudioLevelSnapshot measured = server.GetStatus().Session!.Levels!.Value;
            Assert.Equal(actualOutput, measured);
            Assert.InRange(measured.LeftPeakDbfs, -6.1, -6.0);
            Assert.InRange(measured.RightPeakDbfs, -12.1, -12.0);
            Assert.True(measured.LeftRmsDbfs > measured.RightRmsDbfs);
            Assert.False(measured.Clipping);

            output.Release();
            // A new session must not inherit signal, peak hold, or clipping.
            await Hello(sender, 202, PacketFlags.Takeover);
            var fresh = server.GetStatus().Session!;
            Assert.Equal(202u, fresh.Id);
            Assert.Equal(new AudioLevelSnapshot(-90, -90, -90, -90, false), fresh.Levels);
            await Send(sender, PacketType.Bye, 202, []);
            for (int i = 0; i < 100 && server.GetStatus().Session is not null; i++) await Task.Delay(10);
            Assert.Null(server.GetStatus().Session);
        }
        finally
        {
            output.Release();
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    private static async Task Hello(UdpClient sender, uint session, PacketFlags flags = PacketFlags.None)
    {
        byte[] body = new byte[74];
        int length = new HelloMessage(48000, 2, 1, 5, 30, "meter-test").Write(body);
        await Send(sender, PacketType.Hello, session, body[..length], flags);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (true)
        {
            var response = await sender.ReceiveAsync(timeout.Token);
            Assert.True(Protocol.TryRead(response.Buffer, [], out var header, out _));
            if (header.Type == PacketType.Stats && header.Session == session) return;
        }
    }

    private static async Task Send(UdpClient sender, PacketType type, uint session, byte[] body,
        PacketFlags flags = PacketFlags.None)
    {
        byte[] packet = new byte[Protocol.MaxDatagramSize];
        int length = Protocol.Write(packet, new PacketHeader(type, flags, session, 1), body);
        await sender.SendAsync(packet.AsMemory(0, length));
    }

    private sealed class InspectingSink : IAudioSink
    {
        private readonly AudioLevelMeter _reference = new();
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<AudioLevelSnapshot> FirstAudio { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DelayMs => 0;

        public void Write(ReadOnlySpan<short> samples)
        {
            _reference.Process(samples);
            AudioLevelSnapshot levels = _reference.GetSnapshot();
            if (levels.LeftRmsDbfs > -90 && FirstAudio.TrySetResult(levels))
                Assert.True(_release.Task.Wait(TimeSpan.FromSeconds(5)), "The test did not release the output sink.");
        }

        public void Release() => _release.TrySetResult();
        public void Dispose() => Release();
    }
}
