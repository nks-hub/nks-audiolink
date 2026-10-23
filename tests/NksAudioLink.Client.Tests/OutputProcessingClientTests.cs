using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using NksAudioLink.Client;
using NksAudioLink.Core;

namespace NksAudioLink.Client.Tests;

public sealed class OutputProcessingClientTests
{
    [Fact]
    public async Task LimiterTailIsSentAfterCaptureRunsDryThenSilentPacketsAdvanceTheMeter()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var source = new Source(12000, -6000) { Refills = 1 };
        var client = new AudioStreamClient(Config(server) with { LimiterEnabled = true }, (_, _) => source);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        Task running = client.RunAsync(null, true, stop.Token);
        try
        {
            await Accept(server, stop.Token);
            var audible = new List<short[]>();
            while (true)
            {
                var packet = await Audio(server, stop.Token);
                if (packet.Silent && audible.Count > 0) break;
                if (!packet.Silent) audible.Add(packet.Pcm);
            }
            Assert.True(audible.Count >= 3);
            short[] tail = audible[^1];
            for (int frame = 0; frame < 240; frame++)
            {
                Assert.Equal(frame < 48 ? 12000 : 0, tail[frame * 2]);
                Assert.Equal(frame < 48 ? -6000 : 0, tail[frame * 2 + 1]);
            }
            Assert.Equal(1, client.ProcessingLatencyMs);
            Assert.True(client.Levels.LeftRmsDbfs > -90);
            for (int i = 0; i < 80; i++) Assert.True((await Audio(server, stop.Token)).Silent);
            Assert.Equal(-90, client.Levels.LeftRmsDbfs);
            Assert.Equal(-90, client.Levels.RightRmsDbfs);
        }
        finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(3)); }
        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task LiveMutePreservesGainAndMeterMeasuresLimitedThenMutedThenBypassedPcm()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var source = new Source(30000, 15000);
        var client = new AudioStreamClient(Config(server) with { Gain = 4, LimiterEnabled = true }, (_, _) => source);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        Task running = client.RunAsync(null, true, stop.Token);
        try
        {
            await Accept(server, stop.Token);
            for (int i = 0; i < 80; i++)
            {
                var packet = await Audio(server, stop.Token);
                Assert.All(packet.Pcm, value => Assert.InRange((int)value, -PcmOutputProcessor.Ceiling, PcmOutputProcessor.Ceiling));
            }
            Assert.InRange(client.Levels.LeftRmsDbfs, -0.3, -0.1);
            Assert.InRange(client.Levels.RightRmsDbfs, -6.4, -6.1);
            Assert.False(client.Levels.Clipping);
            client.Muted = true;
            // Allow the bounded 5 ms ramp and already queued datagrams to finish.
            for (int i = 0; i < 10; i++) await Audio(server, stop.Token);
            for (int i = 0; i < 80; i++) Assert.True((await Audio(server, stop.Token)).Silent);
            Assert.Equal(4, client.Gain);
            Assert.Equal(-90, client.Levels.LeftRmsDbfs);
            Assert.False(client.Levels.Clipping);
            client.Muted = false;
            for (int i = 0; i < 10; i++) await Audio(server, stop.Token);
            Assert.Contains((await Audio(server, stop.Token)).Pcm, value => value == PcmOutputProcessor.Ceiling);

            client.LimiterEnabled = false;
            for (int i = 0; i < 10; i++) await Audio(server, stop.Token);
            Assert.Contains((await Audio(server, stop.Token)).Pcm, value => value == short.MaxValue);
            Assert.Equal(0, client.ProcessingLatencyMs);
            Assert.True(client.Levels.Clipping);
            Assert.Equal(4, client.Gain);
        }
        finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(3)); }
        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task ReacceptanceDiscardsPreOutageLookaheadAndKeepsQuietMeterReset()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var source = new Source(30000, 15000);
        var client = new AudioStreamClient(Config(server) with { LimiterEnabled = true }, (_, _) => source);
        var rejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StateChanged += state => { if (state == "Rejected") rejected.TrySetResult(); };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        Task running = client.RunAsync(null, true, stop.Token);
        try
        {
            var connection = await Accept(server, stop.Token);
            for (int i = 0; i < 10; i++) await Audio(server, stop.Token);
            await Stats(server, connection.Endpoint, connection.Session, false, stop.Token);
            await rejected.Task.WaitAsync(stop.Token);
            source.Refills = 0;
            source.Queue.Clear();
            // Drain datagrams sent before the rejection was observed.
            while (server.Available > 0) await server.ReceiveAsync(stop.Token);
            await Stats(server, connection.Endpoint, connection.Session, true, stop.Token);
            for (int i = 0; i < 10; i++) Assert.True((await Audio(server, stop.Token)).Silent);
            Assert.Equal(new AudioLevelSnapshot(-90, -90, -90, -90, false), client.Levels);
        }
        finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    [Fact]
    public void ControlsInitializeFromConfigWithoutChangingGain()
    {
        var client = new AudioStreamClient(new ClientConfig { Gain = 2.5, Muted = true, LimiterEnabled = true });
        Assert.True(client.Muted);
        Assert.True(client.LimiterEnabled);
        Assert.Equal(1, client.ProcessingLatencyMs);
        client.Muted = false;
        Assert.Equal(2.5, client.Gain);
        var defaults = new AudioStreamClient(new ClientConfig());
        Assert.False(defaults.Muted);
        Assert.False(defaults.LimiterEnabled);
        Assert.Equal(0, defaults.ProcessingLatencyMs);
    }

    private static ClientConfig Config(UdpClient server) => new()
    { Server = "127.0.0.1", Port = ((IPEndPoint)server.Client.LocalEndPoint!).Port };

    private static async Task<(IPEndPoint Endpoint, uint Session)> Accept(UdpClient server, CancellationToken stop)
    {
        var packet = await server.ReceiveAsync(stop);
        Assert.True(Protocol.TryRead(packet.Buffer, [], out var header, out _));
        Assert.Equal(PacketType.Hello, header.Type);
        await Stats(server, packet.RemoteEndPoint, header.Session, true, stop);
        return (packet.RemoteEndPoint, header.Session);
    }

    private static async Task Stats(UdpClient server, IPEndPoint endpoint, uint session, bool accepted, CancellationToken stop)
    {
        byte[] body = new byte[StatsMessage.Size];
        new StatsMessage(accepted, 30, 0, 0, 0, 0, 0, 0, 0).Write(body);
        byte[] packet = new byte[Protocol.HeaderSize + body.Length];
        int size = Protocol.Write(packet, new(PacketType.Stats, PacketFlags.None, session, 1), body);
        await server.SendAsync(packet.AsMemory(0, size), endpoint, stop);
    }

    private static async Task<(bool Silent, short[] Pcm)> Audio(UdpClient server, CancellationToken stop)
    {
        while (true)
        {
            var packet = await server.ReceiveAsync(stop);
            Assert.True(Protocol.TryRead(packet.Buffer, [], out var header, out var body));
            if (header.Type != PacketType.Audio) continue;
            bool silent = header.Flags.HasFlag(PacketFlags.Silent);
            Assert.True(AudioMessage.TryRead(body, silent, out _, out var pcm));
            var result = new short[pcm.Length / 2];
            for (int i = 0; i < result.Length; i++) result[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm[(i * 2)..]);
            return (silent, result);
        }
    }

    private sealed class Source(short left, short right) : IAudioSource
    {
        private readonly AudioFrameQueue _queue = new();
        private readonly byte[] _pcm = Pcm(left, right);
        private int _refills = int.MaxValue;
        public int Refills { get => Volatile.Read(ref _refills); set => Volatile.Write(ref _refills, value); }
        public AudioFrameQueue Queue
        {
            get
            {
                if (Thread.CurrentThread.Name == "NKS AudioLink sender" && Refills > 0)
                {
                    Interlocked.Decrement(ref _refills);
                    _queue.Write(_pcm);
                }
                return _queue;
            }
        }
        public int CaptureLatencyMs => 10;
        public bool Disposed { get; private set; }
        public event Action<Exception>? CaptureError { add { } remove { } }
        public void Start() { }
        public void Stop() { }
        public void Dispose() => Disposed = true;
        private static byte[] Pcm(short left, short right)
        {
            byte[] pcm = new byte[Protocol.PcmBytesPerFrame * 3];
            for (int i = 0; i < pcm.Length; i += 4)
            {
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i), left);
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i + 2), right);
            }
            return pcm;
        }
    }
}
