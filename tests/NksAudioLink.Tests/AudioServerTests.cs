using System.Net;
using System.Net.Sockets;
using NksAudioLink.Core;
using NksAudioLink.Server;

namespace NksAudioLink.Tests;

public class AudioServerTests
{
    [Fact]
    public async Task HelloBusyTakeoverAndByeManageSinkLifecycle()
    {
        int port = FreePort();
        var sinks = new List<CountingSink>();
        using var stop = new CancellationTokenSource();
        var server = new AudioServer(Config(port), _ =>
        {
            var sink = new CountingSink();
            sinks.Add(sink);
            return sink;
        });
        Task running = server.RunAsync(stop.Token);
        using var first = Client(port);
        using var second = Client(port);
        try
        {
            await Send(first, PacketType.Hello, 11, HelloBody());
            Assert.Equal(PacketType.Stats, (await Receive(first)).Type);
            Assert.Single(sinks);

            await Send(second, PacketType.Hello, 22, HelloBody());
            var rejected = await Receive(second);
            Assert.Equal(PacketType.Reject, rejected.Type);
            Assert.Equal((byte)RejectReason.Busy, rejected.Body[0]);
            Assert.Single(sinks);
            Assert.False(sinks[0].Disposed);

            await Send(second, PacketType.Hello, 22, HelloBody(), PacketFlags.Takeover);
            Assert.Equal(PacketType.Stats, (await Receive(second)).Type);
            Assert.Equal(2, sinks.Count);
            Assert.True(sinks[0].Disposed);

            // Periodic STATS can precede the reply to the next command.
            await Task.Delay(1100);
            await Send(first, PacketType.Bye, 11, []);
            await Send(second, PacketType.Ping, 22, new byte[8]);
            Assert.Equal(PacketType.Pong, (await Receive(second, PacketType.Pong)).Type);
            await Send(second, PacketType.Bye, 22, []);
            Assert.False(sinks[1].Disposed);
            await Eventually(() => sinks[1].Disposed, 150);
        }
        finally
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        }
    }

    [Fact]
    public async Task BadHelloIsRejectedAndFailedSinkOpenLeavesServerReusable()
    {
        int port = FreePort();
        int opens = 0;
        using var stop = new CancellationTokenSource();
        var server = new AudioServer(Config(port), _ =>
        {
            if (++opens == 2) throw new IOException("device unavailable");
            return new NullSink();
        });
        Task running = server.RunAsync(stop.Token);
        using var first = Client(port);
        using var second = Client(port);
        try
        {
            await Send(first, PacketType.Hello, 11, [1, 2, 3]);
            await ExpectNoPacket(first);
            await Send(first, PacketType.Hello, 11, HelloBody(), PacketFlags.Silent);
            await ExpectNoPacket(first);
            Assert.Equal(0, opens);

            byte[] unsupported = HelloBody();
            unsupported[4] = 1;
            await Send(first, PacketType.Hello, 11, unsupported);
            var rejected = await Receive(first);
            Assert.Equal(PacketType.Reject, rejected.Type);
            Assert.Equal((byte)RejectReason.UnsupportedFormat, rejected.Body[0]);
            Assert.Equal(0, opens);

            await Send(first, PacketType.Hello, 11, HelloBody());
            Assert.Equal(PacketType.Stats, (await Receive(first)).Type);
            await Send(second, PacketType.Hello, 22, HelloBody(), PacketFlags.Takeover);
            await ExpectNoPacket(second);
            await Send(first, PacketType.Ping, 11, new byte[8]);
            await ExpectNoPacket(first);
            await Send(first, PacketType.Hello, 11, HelloBody());
            Assert.Equal(PacketType.Stats, (await Receive(first)).Type);
            Assert.Equal(3, opens);
        }
        finally
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        }
    }

    [Fact]
    public async Task SinkWriteFailureClosesSessionAndAllowsNewHello()
    {
        int port = FreePort();
        int opens = 0;
        using var stop = new CancellationTokenSource();
        var server = new AudioServer(Config(port), _ => ++opens == 1 ? new FailingSink() : new NullSink());
        Task running = server.RunAsync(stop.Token);
        using var client = Client(port);
        try
        {
            await Send(client, PacketType.Hello, 11, HelloBody());
            Assert.Equal(PacketType.Stats, (await Receive(client)).Type);
            await Eventually(() => opens == 1);
            await Task.Delay(30);
            await Send(client, PacketType.Hello, 11, HelloBody());
            Assert.Equal(PacketType.Stats, (await Receive(client)).Type);
            Assert.Equal(2, opens);
            Assert.False(running.IsCompleted);
        }
        finally
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        }
    }

    [Fact]
    public async Task NewHelloReleasesIdleSinkBeforeOpeningExclusiveReplacement()
    {
        int port = FreePort();
        var firstSink = new CountingSink();
        int opens = 0;
        using var stop = new CancellationTokenSource();
        var server = new AudioServer(Config(port), _ =>
        {
            if (++opens == 1) return firstSink;
            Assert.True(firstSink.Disposed);
            return new CountingSink();
        });
        Task running = server.RunAsync(stop.Token);
        using var client = Client(port);
        try
        {
            await Send(client, PacketType.Hello, 11, HelloBody());
            Assert.Equal(PacketType.Stats, (await Receive(client)).Type);
            await Send(client, PacketType.Bye, 11, []);
            await Send(client, PacketType.Hello, 12, HelloBody());
            Assert.Equal(PacketType.Stats, (await Receive(client, PacketType.Stats, 12)).Type);
            Assert.Equal(2, opens);
        }
        finally
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        }
    }

    [Fact]
    public void WavSinkWritesValidStereoHeader()
    {
        string path = Path.Combine(Path.GetTempPath(), $"nkal-{Guid.NewGuid():N}.wav");
        try
        {
            using (var sink = new WavSink(path))
                sink.Write(new short[Protocol.SamplesPerFrame * Protocol.Channels]);
            byte[] wav = File.ReadAllBytes(path);
            Assert.Equal(44 + Protocol.PcmBytesPerFrame, wav.Length);
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
            Assert.Equal((ushort)2, System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)));
            Assert.Equal((uint)Protocol.PcmBytesPerFrame,
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(40)));
        }
        finally { File.Delete(path); }
    }

    private static ServerConfig Config(int port) => new() { Port = port, AllowCidrs = ["127.0.0.0/8"], IdleReleaseSec = 1 };

    private static int FreePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static UdpClient Client(int port)
    {
        var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Connect(IPAddress.Loopback, port);
        return udp;
    }

    private static byte[] HelloBody()
    {
        byte[] body = new byte[74];
        return body[..new HelloMessage(48000, 2, 1, 5, 30, "test").Write(body)];
    }

    private static async Task Send(UdpClient udp, PacketType type, uint session, byte[] body, PacketFlags flags = PacketFlags.None)
    {
        byte[] packet = new byte[Protocol.MaxDatagramSize];
        int length = Protocol.Write(packet, new PacketHeader(type, flags, session, 1), body);
        await udp.SendAsync(packet.AsMemory(0, length));
    }

    private static async Task<(PacketType Type, byte[] Body)> Receive(UdpClient udp, PacketType? expected = null, uint? session = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (true)
        {
            var packet = await udp.ReceiveAsync(timeout.Token);
            Assert.True(Protocol.TryRead(packet.Buffer, [], out var header, out var body));
            if (header.Type == PacketType.Stats &&
                ((expected is not null && expected != PacketType.Stats) || (session is not null && header.Session != session)))
            {
                Assert.True(StatsMessage.TryRead(body, out var stats) && stats.Accepted);
                continue;
            }
            return (header.Type, body.ToArray());
        }
    }

    private static async Task ExpectNoPacket(UdpClient udp)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await udp.ReceiveAsync(timeout.Token));
    }

    private static async Task Eventually(Func<bool> predicate, int attempts = 20)
    {
        for (int i = 0; i < attempts && !predicate(); i++) await Task.Delay(10);
        Assert.True(predicate());
    }

    private sealed class CountingSink : IAudioSink
    {
        public bool Disposed { get; private set; }
        public int DelayMs => 0;
        public void Write(ReadOnlySpan<short> samples) { }
        public void Dispose() => Disposed = true;
    }

    private sealed class FailingSink : IAudioSink
    {
        public int DelayMs => 0;
        public void Write(ReadOnlySpan<short> samples) => throw new IOException("output failed");
        public void Dispose() { }
    }
}
