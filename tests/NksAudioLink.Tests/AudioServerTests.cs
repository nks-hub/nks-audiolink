using System.Net;
using System.Net.Sockets;
using NksAudioLink.Core;
using NksAudioLink.Server;

namespace NksAudioLink.Tests;

public class AudioServerTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task HelloNegotiatesOnlyTheRequestedBufferModeAndNativeErrorsCannotAdaptIt(bool adaptive, bool predictive)
    {
        var sink = new DiagnosticSink();
        var server = new AudioServer(Config(FreePort()), _ => sink);
        using var stop = new CancellationTokenSource();
        Task running = server.RunAsync(stop.Token);
        using var udp = Client(server.GetStatus().UdpPort);
        try
        {
            byte[] body = new byte[74];
            int length = new HelloMessage(48000, 2, 1, 5, 40, "test", adaptive ? new(20, 100, predictive) : null).Write(body);
            await Send(udp, PacketType.Hello, 51, body[..length]);
            var reply = await Receive(udp, PacketType.Stats);
            Assert.Equal(adaptive ? StatsMessage.AdaptiveSize : StatsMessage.Size, reply.Body.Length);
            Assert.True(StatsMessage.TryRead(reply.Body, out var stats));
            Assert.Equal(adaptive, stats.AdaptiveBuffer.HasValue);
            var status = server.GetStatus().Session!;
            Assert.Equal(40, status.TargetLatencyMs);
            Assert.Equal(adaptive, status.AdaptiveBuffer.HasValue);
            Assert.Equal(predictive, status.Predictor.HasValue);
            sink.Log.Record(-32, 0); // Device recovery is not network jitter evidence.
            await Send(udp, PacketType.Hello, 51, body[..length]);
            reply = await Receive(udp, PacketType.Stats);
            Assert.True(StatsMessage.TryRead(reply.Body, out stats));
            if (adaptive) Assert.Equal(new AdaptiveBufferStatus(40, 20, 100, AdaptiveBufferReason.Initial, 0, predictive), stats.AdaptiveBuffer);
            Assert.Equal(40, server.GetStatus().Session!.TargetLatencyMs);
        }
        finally
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task MalformedAdaptiveBoundsAreExplicitlyRejectedWithoutOpeningSink()
    {
        int opens = 0;
        var server = new AudioServer(Config(FreePort()), _ => { opens++; return new CountingSink(); });
        using var stop = new CancellationTokenSource();
        Task running = server.RunAsync(stop.Token);
        using var udp = Client(server.GetStatus().UdpPort);
        try
        {
            byte[] body = HelloBody();
            body[5] = 0x81;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(7), (ushort)(6 | 4 << 6 | 31 << 11));
            await Send(udp, PacketType.Hello, 52, body);
            var rejected = await Receive(udp, PacketType.Reject);
            Assert.Equal(new byte[] { (byte)RejectReason.UnsupportedFormat }, rejected.Body);
            Assert.Equal(0, opens);
            Assert.Null(server.GetStatus().Session);
        }
        finally
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task NativeOutputTotalsSurviveClosedSessionsWhileNewSessionStartsAtZero()
    {
        var sinks = new List<DiagnosticSink>();
        var server = new AudioServer(Config(FreePort()), _ => { var sink = new DiagnosticSink(); sinks.Add(sink); return sink; });
        using var stop = new CancellationTokenSource();
        Task running = server.RunAsync(stop.Token);
        using var udp = Client(server.GetStatus().UdpPort);
        try
        {
            await Send(udp, PacketType.Hello, 1, HelloBody());
            await Receive(udp, PacketType.Stats);
            Assert.Equal(default(NativeOutputStatus), server.GetStatus().Session!.NativeOutput);
            sinks[0].Log.Record(-32, 0);
            sinks[0].Log.Record(-86, -19);
            var expected = new NativeOutputStatus(2, 1, 1, 1, 1);
            Assert.Equal(expected, server.GetStatus().NativeOutput);
            Assert.Equal(expected, server.GetStatus().Session!.NativeOutput);
            await Send(udp, PacketType.Bye, 1, []);
            await Eventually(() => server.GetStatus().Session is null);
            Assert.Equal(expected, server.GetStatus().NativeOutput);
            await Send(udp, PacketType.Hello, 2, HelloBody());
            await Receive(udp, PacketType.Stats, 2);
            Assert.Equal(default(NativeOutputStatus), server.GetStatus().Session!.NativeOutput);
            Assert.Equal(expected, server.GetStatus().NativeOutput);
            sinks[1].Log.Record(-32, 0);
            Assert.Equal(new NativeOutputStatus(3, 2, 1, 2, 1), server.GetStatus().NativeOutput);
            sinks[1].FailNextWrite();
            await Eventually(() => server.GetStatus().Session is null);
            Assert.Equal(new NativeOutputStatus(4, 2, 2, 2, 1), server.GetStatus().NativeOutput);
        }
        finally
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MisalignedAudioFrameDoesNotStopServer(bool silent)
    {
        int port = FreePort();
        using var stop = new CancellationTokenSource();
        var server = new AudioServer(Config(port));
        Task running = server.RunAsync(stop.Token);
        using var client = Client(port);
        try
        {
            await Send(client, PacketType.Hello, 11, HelloBody());
            Assert.Equal(PacketType.Stats, (await Receive(client)).Type);

            byte[] body = new byte[AudioMessage.PrefixSize + (silent ? 0 : Protocol.PcmBytesPerFrame)];
            AudioMessage.Write(body, 0, Protocol.SamplesPerFrame,
                silent ? [] : new byte[Protocol.PcmBytesPerFrame], silent);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(body, 1);
            await Send(client, PacketType.Audio, 11, body, silent ? PacketFlags.Silent : PacketFlags.None);
            await Send(client, PacketType.Ping, 11, new byte[8]);
            Assert.Equal(PacketType.Pong, (await Receive(client, PacketType.Pong)).Type);
            Assert.False(running.IsCompleted);
        }
        finally
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        }
    }

    [Fact]
    public async Task MisalignedTargetBufferIsRejectedWithoutOpeningSink()
    {
        int port = FreePort();
        int opens = 0;
        using var stop = new CancellationTokenSource();
        var server = new AudioServer(Config(port), _ =>
        {
            opens++;
            return new NullSink();
        });
        Task running = server.RunAsync(stop.Token);
        using var client = Client(port);
        try
        {
            byte[] hello = HelloBody();
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(hello.AsSpan(7), 11);
            await Send(client, PacketType.Hello, 11, hello);
            var rejected = await Receive(client);
            Assert.Equal(PacketType.Reject, rejected.Type);
            Assert.Equal((byte)RejectReason.UnsupportedFormat, rejected.Body[0]);
            Assert.Equal(0, opens);

            await Send(client, PacketType.Hello, 11, HelloBody());
            Assert.Equal(PacketType.Stats, (await Receive(client)).Type);
            Assert.Equal(1, opens);
        }
        finally
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        }
    }

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

    private sealed class DiagnosticSink : IAudioSink, INativeOutputDiagnostics
    {
        private int _failNextWrite;
        public AlsaRecoveryLog Log { get; } = new(_ => { });
        public DiagnosticSink() { Log.Record(-32, 0); } // Existing sink count must not leak into the new session baseline.
        public NativeOutputStatus GetNativeOutputStatus() => Log.GetSnapshot();
        public int DelayMs => 0;
        public void FailNextWrite() => Interlocked.Exchange(ref _failNextWrite, 1);
        public void Write(ReadOnlySpan<short> samples)
        {
            if (Interlocked.Exchange(ref _failNextWrite, 0) == 0) return;
            Log.Record(-19, -19);
            throw new IOException("Native output recovery failed.");
        }
        public void Dispose() { }
    }

    private sealed class FailingSink : IAudioSink
    {
        public int DelayMs => 0;
        public void Write(ReadOnlySpan<short> samples) => throw new IOException("output failed");
        public void Dispose() { }
    }
}
