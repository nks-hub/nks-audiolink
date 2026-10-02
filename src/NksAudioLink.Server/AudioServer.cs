using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using NksAudioLink.Core;

namespace NksAudioLink.Server;

public sealed class AudioServer(ServerConfig config)
{
    private sealed class Session(IPEndPoint endpoint, uint id, PlayoutEngine engine, IAudioSink sink)
    {
        public IPEndPoint Endpoint { get; } = endpoint;
        public uint Id { get; } = id;
        public PlayoutEngine Engine { get; } = engine;
        public IAudioSink Sink { get; } = sink;
        public DateTime LastPacketUtc { get; set; } = DateTime.UtcNow;
        public ulong EchoTicks { get; set; }
        public uint Sequence { get; set; }
    }

    private readonly object _gate = new();
    private Session? _session;
    private readonly byte[]? _key = config.GetPsk();
    private readonly CidrRange[] _allow = config.AllowCidrs.Select(CidrRange.Parse).ToArray();

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        config.Validate();
        using var timerResolution = new TimerResolution();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, config.Port));
        Console.WriteLine($"Listening UDP {config.Port}; sink {config.Sink}");
        Task playout = PlayoutLoopAsync(udp, cancellationToken);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                UdpReceiveResult received = await udp.ReceiveAsync(cancellationToken);
                await HandleAsync(udp, received, cancellationToken);
            }
        }
        finally
        {
            lock (_gate) CloseSession();
            try { await playout; } catch (OperationCanceledException) { }
        }
    }

    private async Task HandleAsync(UdpClient udp, UdpReceiveResult received, CancellationToken ct)
    {
        if (!_allow.Any(range => range.Contains(received.RemoteEndPoint.Address))) return;
        if (!Protocol.TryRead(received.Buffer, _key ?? [], out var header, out var bodySpan)) return;
        byte[] body = bodySpan.ToArray();
        var remote = received.RemoteEndPoint;
        if (header.Type == PacketType.Discover && body.Length == 0)
        {
            byte[] response = new byte[256];
            int size = new DiscoverReplyMessage(config.Name, Protocol.SampleRate, Protocol.Channels, _key is not null, config.Sink).Write(response);
            await SendAsync(udp, remote, new(PacketType.DiscoverReply, PacketFlags.None, 0, 0), response[..size], ct);
            return;
        }
        if (header.Type == PacketType.Hello)
        {
            if (!HelloMessage.TryRead(body, out var hello)) return;
            if (!hello.IsSupported)
            {
                await SendAsync(udp, remote, new(PacketType.Reject, PacketFlags.None, header.Session, 0), [(byte)RejectReason.UnsupportedFormat], ct);
                return;
            }
            bool busy;
            lock (_gate)
            {
                busy = _session is not null && (_session.Id != header.Session || !_session.Endpoint.Equals(remote)) &&
                    !header.Flags.HasFlag(PacketFlags.Takeover);
                if (!busy)
                {
                    if (_session is null || _session.Id != header.Session || !_session.Endpoint.Equals(remote))
                    {
                        CloseSession();
                        _session = new Session(remote, header.Session, new PlayoutEngine(hello.TargetLatencyMs), CreateSink(config.Sink));
                        Console.WriteLine($"Session {header.Session} from {remote}");
                    }
                    _session.LastPacketUtc = DateTime.UtcNow;
                }
            }
            if (busy)
                await SendAsync(udp, remote, new(PacketType.Reject, PacketFlags.None, header.Session, 0), [(byte)RejectReason.Busy], ct);
            else await SendStatsAsync(udp, ct);
            return;
        }
        lock (_gate)
        {
            if (_session is null || _session.Id != header.Session || !_session.Endpoint.Equals(remote)) return;
            if (header.Type == PacketType.Bye && body.Length == 0) { CloseSession(); return; }
            if (header.Type == PacketType.Audio)
            {
                bool silent = header.Flags.HasFlag(PacketFlags.Silent);
                if (AudioMessage.TryRead(body, silent, out ulong index, out var pcm))
                {
                    _session.Engine.Receive(index, pcm, silent);
                    _session.LastPacketUtc = DateTime.UtcNow;
                }
            }
            if (header.Type == PacketType.Ping && body.Length == 8)
            {
                _session.EchoTicks = BinaryPrimitives.ReadUInt64LittleEndian(body);
                _session.LastPacketUtc = DateTime.UtcNow;
            }
        }
        if (header.Type == PacketType.Ping && body.Length == 8)
            await SendAsync(udp, remote, new(PacketType.Pong, PacketFlags.None, header.Session, 0), body, ct);
    }

    private async Task PlayoutLoopAsync(UdpClient udp, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Protocol.FrameMs));
        short[] samples = new short[Protocol.SamplesPerFrame * Protocol.Channels];
        int ticks = 0;
        while (await timer.WaitForNextTickAsync(ct))
        {
            lock (_gate)
            {
                if (_session is not null)
                {
                    if ((DateTime.UtcNow - _session.LastPacketUtc).TotalSeconds > 3) CloseSession();
                    else
                    {
                        _session.Engine.Read(samples);
                        _session.Sink.Write(samples);
                    }
                }
            }
            if (++ticks % 200 == 0) await SendStatsAsync(udp, ct);
        }
    }

    private async Task SendStatsAsync(UdpClient udp, CancellationToken ct)
    {
        IPEndPoint endpoint;
        PacketHeader header;
        byte[] body = new byte[StatsMessage.Size];
        lock (_gate)
        {
            if (_session is null) return;
            var stats = _session.Engine.GetStats();
            new StatsMessage(true, (ushort)stats.BufferMs, (ushort)_session.Sink.DelayMs,
                (uint)stats.Underruns, (uint)stats.Overruns, (uint)stats.Lost, (uint)stats.Late,
                stats.RatioPpm, _session.EchoTicks).Write(body);
            endpoint = _session.Endpoint;
            header = new PacketHeader(PacketType.Stats, PacketFlags.None, _session.Id, ++_session.Sequence);
        }
        await SendAsync(udp, endpoint, header, body, ct);
    }

    private async Task SendAsync(UdpClient udp, IPEndPoint remote, PacketHeader header, byte[] body, CancellationToken ct)
    {
        byte[] packet = new byte[Protocol.MaxDatagramSize];
        int size = Protocol.Write(packet, header, body, _key ?? []);
        await udp.SendAsync(packet.AsMemory(0, size), remote, ct);
    }

    private static IAudioSink CreateSink(string description) => description switch
    {
        "null" => new NullSink(),
        _ when description.StartsWith("wav:", StringComparison.OrdinalIgnoreCase) => new WavSink(description[4..]),
        _ => throw new ArgumentException($"Unsupported sink: {description}")
    };

    private void CloseSession()
    {
        if (_session is null) return;
        _session.Sink.Dispose();
        Console.WriteLine($"Session {_session.Id} closed: {_session.Engine.GetStats()}");
        _session = null;
    }
}
