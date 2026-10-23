using System.Net;
using System.Net.Sockets;
using NksAudioLink.Client;
using NksAudioLink.Core;

namespace NksAudioLink.Client.Tests;

public sealed class AdaptiveBufferClientTests
{
    [Fact]
    public async Task AdaptiveHandshakeWaitsForAcknowledgmentAndPreservesOptionsOnKeepalive()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var source = new SilentSource();
        var client = new AudioStreamClient(Config(server) with { TargetLatencyMs = 155 }, (_, _) => source);
        var observed = new TaskCompletionSource<StatsMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StatsReceived += (stats, _) => observed.TrySetResult(stats);
        Task running = client.RunAsync(null, true, stop.Token);
        try
        {
            var first = await server.ReceiveAsync(stop.Token);
            Assert.True(Protocol.TryRead(first.Buffer, [], out var header, out var body));
            Assert.Equal(PacketType.Hello, header.Type);
            Assert.True(HelloMessage.TryRead(body, out var hello));
            Assert.Equal(100, hello.TargetLatencyMs);
            Assert.Equal(new AdaptiveBufferOptions(20, 100), hello.AdaptiveBuffer);
            await Task.Delay(150, stop.Token);
            Assert.Equal(0, server.Available); // No PCM before the adaptive acknowledgment.

            var adaptive = new AdaptiveBufferStatus(100, 20, 100, AdaptiveBufferReason.Initial, 0);
            await Reply(server, first.RemoteEndPoint, header.Session, adaptive);
            Assert.Equal(adaptive, (await observed.Task.WaitAsync(stop.Token)).AdaptiveBuffer);
            int audio = 0;
            bool keepalive = false;
            while (!keepalive)
            {
                var packet = await server.ReceiveAsync(stop.Token);
                Assert.True(Protocol.TryRead(packet.Buffer, [], out var next, out var payload));
                if (next.Type == PacketType.Audio) audio++;
                if (next.Type != PacketType.Hello) continue;
                Assert.True(HelloMessage.TryRead(payload, out var repeated));
                Assert.Equal(hello, repeated);
                Assert.Equal(header.Session, next.Session);
                keepalive = true;
            }
            Assert.True(audio > 10);
        }
        finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(3)); }
        Assert.True(source.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrDifferentAdaptiveAcknowledgmentStopsBeforeSendingAudio(bool differentBounds)
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var source = new SilentSource();
        var client = new AudioStreamClient(Config(server), (_, _) => source);
        Task running = client.RunAsync(null, true, stop.Token);
        var first = await server.ReceiveAsync(stop.Token);
        Assert.True(Protocol.TryRead(first.Buffer, [], out var header, out _));
        await Reply(server, first.RemoteEndPoint, header.Session,
            differentBounds ? new AdaptiveBufferStatus(40, 40, 80, AdaptiveBufferReason.Initial, 0) : null);
        var error = await Assert.ThrowsAsync<ServerRejectedException>(() => running.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Contains("turn off Adaptive buffer", error.Message);
        Assert.True(source.Disposed);
        while (server.Available > 0)
        {
            var packet = await server.ReceiveAsync(stop.Token);
            Assert.True(Protocol.TryRead(packet.Buffer, [], out var response, out _));
            Assert.NotEqual(PacketType.Audio, response.Type);
        }
    }

    [Fact]
    public async Task OldServerRejectionExplainsManualFallbackAndDisposesCapture()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var source = new SilentSource();
        var client = new AudioStreamClient(Config(server), (_, _) => source);
        Task running = client.RunAsync(null, true, stop.Token);
        var first = await server.ReceiveAsync(stop.Token);
        Assert.True(Protocol.TryRead(first.Buffer, [], out var header, out _));
        // The legacy parser sees a valid HELLO body and rejects its new format.
        Assert.Equal(0x81, first.Buffer[Protocol.HeaderSize + 5]);
        byte[] packet = new byte[Protocol.HeaderSize + 1];
        int length = Protocol.Write(packet, new(PacketType.Reject, PacketFlags.None, header.Session, 1),
            [(byte)RejectReason.UnsupportedFormat]);
        await server.SendAsync(packet.AsMemory(0, length), first.RemoteEndPoint, stop.Token);
        var error = await Assert.ThrowsAsync<ServerRejectedException>(() => running.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Contains("Update the server", error.Message);
        Assert.Contains("turn off Adaptive buffer", error.Message);
        Assert.True(source.Disposed);
    }

    private static ClientConfig Config(UdpClient server) => new()
    {
        Server = "127.0.0.1", Port = ((IPEndPoint)server.Client.LocalEndPoint!).Port,
        AdaptiveBuffer = true
    };

    private static async Task Reply(UdpClient server, IPEndPoint endpoint, uint session, AdaptiveBufferStatus? adaptive)
    {
        byte[] body = new byte[StatsMessage.AdaptiveSize];
        int bodyLength = new StatsMessage(true, 30, 20, 0, 0, 0, 0, 0, 0, adaptive).Write(body);
        byte[] packet = new byte[Protocol.HeaderSize + bodyLength];
        int length = Protocol.Write(packet, new(PacketType.Stats, PacketFlags.None, session, 1), body.AsSpan(0, bodyLength));
        await server.SendAsync(packet.AsMemory(0, length), endpoint);
    }

    private sealed class SilentSource : IAudioSource
    {
        public AudioFrameQueue Queue { get; } = new();
        public int CaptureLatencyMs => 10;
        public bool Disposed { get; private set; }
        public event Action<Exception>? CaptureError { add { } remove { } }
        public void Start() { }
        public void Stop() { }
        public void Dispose() => Disposed = true;
    }
}
