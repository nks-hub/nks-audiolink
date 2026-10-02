using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using NksAudioLink.Client;
using NksAudioLink.Core;

namespace NksAudioLink.Client.Tests;

public sealed class AudioStreamClientTests
{
    [Fact]
    public async Task EmptyCaptureSendsTwentyContinuousSilentFrames()
    {
        using var server = new PacketServer();
        var source = new FakeSource();
        var client = new AudioStreamClient(Config(server.Port), (_, _) => source);
        using var stop = new CancellationTokenSource();
        Task running = client.RunAsync(null, true, stop.Token);
        try
        {
            await Eventually(() => server.Audio.Count >= 20, TimeSpan.FromSeconds(5));
            var firstTwenty = server.Audio.ToArray().Take(20).ToArray();
            Assert.All(firstTwenty, frame => Assert.True(frame.Silent));
            Assert.All(firstTwenty, frame => Assert.Equal(0, frame.PcmLength));
            ulong firstIndex = firstTwenty[0].SampleIndex;
            for (int i = 0; i < firstTwenty.Length; i++)
                Assert.Equal(firstIndex + (ulong)i * Protocol.SamplesPerFrame, firstTwenty[i].SampleIndex);
        }
        finally { await Stop(stop, running); }
        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task LateServerStartAcceptsNextHelloWithoutRecreatingCapture()
    {
        int port = FreePort();
        var source = new FakeSource();
        var client = new AudioStreamClient(Config(port), (_, _) => source);
        using var stop = new CancellationTokenSource();
        Task running = client.RunAsync(null, true, stop.Token);
        try
        {
            await Eventually(() => source.Started, TimeSpan.FromSeconds(2));
            using var server = new PacketServer(port);
            await Eventually(() => server.Audio.Count >= 3, TimeSpan.FromSeconds(5));
            Assert.True(server.HelloCount >= 1);
            Assert.True(server.AcceptedStatsSent >= 1);
            Assert.False(source.Disposed);
        }
        finally { await Stop(stop, running); }
        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task ServerRestartAcceptsExistingClientAtNextHello()
    {
        int port = FreePort();
        var source = new FakeSource();
        var client = new AudioStreamClient(Config(port), (_, _) => source);
        using var stop = new CancellationTokenSource();
        using var firstServer = new PacketServer(port);
        Task running = client.RunAsync(null, true, stop.Token);
        try
        {
            await Eventually(() => firstServer.Audio.Count >= 3, TimeSpan.FromSeconds(5));
            uint session = firstServer.Audio.ToArray()[0].Session;
            firstServer.Dispose();

            using var secondServer = new PacketServer(port);
            await Eventually(() => secondServer.Audio.Count >= 3, TimeSpan.FromSeconds(5));
            Assert.Equal(session, secondServer.Audio.ToArray()[0].Session);
            Assert.True(secondServer.AcceptedStatsSent >= 1);
            Assert.False(source.Disposed);
        }
        finally { await Stop(stop, running); }
        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task CaptureErrorDisposesOldSourceAndStartsAcceptedNewSession()
    {
        using var server = new PacketServer();
        var sources = new ConcurrentQueue<FakeSource>();
        var client = new AudioStreamClient(Config(server.Port), (_, _) =>
        {
            var source = new FakeSource();
            sources.Enqueue(source);
            return source;
        });
        int connected = 0;
        client.StateChanged += state => { if (state == "Připojeno") Interlocked.Increment(ref connected); };
        using var stop = new CancellationTokenSource();
        Task running = client.RunAsync(null, true, stop.Token);
        try
        {
            await Eventually(() => server.Audio.Count >= 3 && sources.Count == 1, TimeSpan.FromSeconds(5));
            Assert.True(sources.TryPeek(out var first));
            uint oldSession = server.Audio.ToArray()[0].Session;
            first!.Fail();
            await Eventually(() => sources.Count >= 2 && server.Audio.Any(frame => frame.Session != oldSession), TimeSpan.FromSeconds(6));

            Assert.True(first.Disposed);
            Assert.True(Volatile.Read(ref connected) >= 2);
            Assert.True(server.AcceptedStatsSent >= 2);
            Assert.Contains(server.Audio, frame => frame.Session != oldSession);
        }
        finally { await Stop(stop, running); }
        Assert.All(sources, source => Assert.True(source.Disposed));
    }

    private static ClientConfig Config(int port) => new() { Server = "127.0.0.1", Port = port };

    private static int FreePort()
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
    }

    private static async Task Eventually(Func<bool> predicate, TimeSpan timeout)
    {
        using var stop = new CancellationTokenSource(timeout);
        while (!predicate())
        {
            stop.Token.ThrowIfCancellationRequested();
            await Task.Delay(5, stop.Token);
        }
    }

    private static async Task Stop(CancellationTokenSource stop, Task running)
    {
        stop.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class FakeSource : IAudioSource
    {
        public AudioFrameQueue Queue { get; } = new();
        public int CaptureLatencyMs => 10;
        public event Action<Exception>? CaptureError;
        public bool Started { get; private set; }
        public bool Disposed { get; private set; }
        public void Start() => Started = true;
        public void Stop() => Started = false;
        public void Dispose() => Disposed = true;
        public void Fail() => CaptureError?.Invoke(new IOException("Simulated device invalidation."));
    }

    private readonly record struct AudioPacket(uint Session, ulong SampleIndex, bool Silent, int PcmLength);

    private sealed class PacketServer : IDisposable
    {
        private readonly UdpClient _udp;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _receiver;
        private readonly ConcurrentDictionary<uint, byte> _acceptedSessions = new();
        private int _helloCount;
        private int _acceptedStatsSent;
        private int _disposed;
        public ConcurrentQueue<AudioPacket> Audio { get; } = new();
        public int Port { get; }
        public int HelloCount => Volatile.Read(ref _helloCount);
        public int AcceptedStatsSent => Volatile.Read(ref _acceptedStatsSent);

        public PacketServer(int port = 0)
        {
            _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
            Port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
            _receiver = ReceiveAsync();
        }

        private async Task ReceiveAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var packet = await _udp.ReceiveAsync(_stop.Token);
                    if (!Protocol.TryRead(packet.Buffer, [], out var header, out var body)) continue;
                    if (header.Type == PacketType.Hello && HelloMessage.TryRead(body, out var hello) && hello.IsSupported)
                    {
                        Interlocked.Increment(ref _helloCount);
                        _acceptedSessions.TryAdd(header.Session, 0);
                        byte[] payload = new byte[StatsMessage.Size];
                        new StatsMessage(true, 30, 20, 0, 0, 0, 0, 0, 0).Write(payload);
                        byte[] response = new byte[Protocol.HeaderSize + StatsMessage.Size];
                        int length = Protocol.Write(response, new PacketHeader(PacketType.Stats, PacketFlags.None, header.Session, 1), payload);
                        await _udp.SendAsync(response.AsMemory(0, length), packet.RemoteEndPoint, _stop.Token);
                        Interlocked.Increment(ref _acceptedStatsSent);
                    }
                    else if (header.Type == PacketType.Audio && _acceptedSessions.ContainsKey(header.Session) &&
                        AudioMessage.TryRead(body, header.Flags.HasFlag(PacketFlags.Silent), out ulong index, out var pcm))
                    {
                        Audio.Enqueue(new AudioPacket(header.Session, index, header.Flags.HasFlag(PacketFlags.Silent), pcm.Length));
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _stop.Cancel();
            _udp.Dispose();
            _stop.Dispose();
            _ = _receiver;
        }
    }
}
