using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NksAudioLink.Core;

namespace NksAudioLink.Server;

public sealed class AudioServer
{
    private readonly ServerConfig config;
    private readonly Func<string, IAudioSink> _sinkFactory;

    public AudioServer(ServerConfig config, Func<string, IAudioSink>? sinkFactory = null)
    {
        this.config = config;
        _sinkFactory = sinkFactory ?? CreateSink;
        _key = config.GetPsk();
        _allow = config.AllowCidrs.Select(CidrRange.Parse).ToArray();
    }
    private sealed class Session(IPEndPoint endpoint, uint id, PlayoutEngine engine, IAudioSink sink)
    {
        public IPEndPoint Endpoint { get; } = endpoint;
        public uint Id { get; } = id;
        public PlayoutEngine Engine { get; } = engine;
        public IAudioSink Sink { get; } = sink;
        public long LastPacketTick { get; set; } = Stopwatch.GetTimestamp();
        public ulong EchoTicks { get; set; }
        public uint Sequence { get; set; }
        public object SinkGate { get; } = new();
        public bool Closed { get; set; }
        public long LastAudioTick { get; set; }
        public double MaxAudioGapMs { get; set; }
        public double MaxSinkWriteMs { get; set; }
        public int SinkDelayMs;
    }

    private readonly object _gate = new();
    private Session? _session;
    private IAudioSink? _idleSink;
    private long _idleReleaseTick;
    private readonly byte[]? _key;
    private readonly CidrRange[] _allow;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        config.Validate();
        using var timerResolution = new TimerResolution();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, config.Port));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Console.WriteLine($"Listening UDP {config.Port}; sink {config.Sink}");
        Task playout = Task.Factory.StartNew(() => PlayoutLoop(stop.Token), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task receive = ReceiveLoopAsync(udp, stop.Token);
        Task stats = StatsLoopAsync(udp, stop.Token);
        try
        {
            await Task.WhenAny(playout, receive, stats);
        }
        finally
        {
            stop.Cancel();
            try { await Task.WhenAll(playout, receive, stats); }
            finally
            {
                lock (_gate)
                {
                    CloseSession(releaseNow: true);
                    ReleaseIdleSink();
                }
            }
        }
    }

    private async Task ReceiveLoopAsync(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult received = await udp.ReceiveAsync(ct);
            await HandleAsync(udp, received, ct);
        }
    }

    private async Task HandleAsync(UdpClient udp, UdpReceiveResult received, CancellationToken ct)
    {
        if (!_allow.Any(range => range.Contains(received.RemoteEndPoint.Address))) return;
        if (!Protocol.TryRead(received.Buffer, _key ?? [], out var header, out var bodySpan)) return;
        if ((header.Flags.HasFlag(PacketFlags.Silent) && header.Type != PacketType.Audio) ||
            (header.Flags.HasFlag(PacketFlags.Takeover) && header.Type != PacketType.Hello)) return;
        ReadOnlyMemory<byte> body = received.Buffer.AsMemory(Protocol.HeaderSize, bodySpan.Length);
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
            if (!HelloMessage.TryRead(body.Span, out var hello)) return;
            if (!hello.IsSupported)
            {
                await SendAsync(udp, remote, new(PacketType.Reject, PacketFlags.None, header.Session, 0), [(byte)RejectReason.UnsupportedFormat], ct);
                return;
            }
            bool busy;
            bool opened = true;
            lock (_gate)
            {
                busy = _session is not null && (_session.Id != header.Session || !_session.Endpoint.Equals(remote)) &&
                    !header.Flags.HasFlag(PacketFlags.Takeover);
                if (!busy)
                {
                    if (_session is null || _session.Id != header.Session || !_session.Endpoint.Equals(remote))
                    {
                        // ALSA devices can be exclusive: close the old sink before opening
                        // the replacement, including on takeover.
                        try
                        {
                            CloseSession(releaseNow: true);
                            ReleaseIdleSink();
                            var sink = _sinkFactory(config.Sink);
                            _session = new Session(remote, header.Session, new PlayoutEngine(hello.TargetLatencyMs), sink);
                            Console.WriteLine($"Session {header.Session} from {remote}");
                        }
                        catch (Exception ex)
                        {
                            opened = false;
                            Console.Error.WriteLine($"Cannot open audio sink: {ex.Message}");
                        }
                    }
                    if (opened) _session!.LastPacketTick = Stopwatch.GetTimestamp();
                }
            }
            if (busy)
                await SendAsync(udp, remote, new(PacketType.Reject, PacketFlags.None, header.Session, 0), [(byte)RejectReason.Busy], ct);
            else if (opened) await SendStatsAsync(udp, ct);
            return;
        }
        lock (_gate)
        {
            if (_session is null || _session.Id != header.Session || !_session.Endpoint.Equals(remote)) return;
            if (header.Type == PacketType.Bye && body.Length == 0)
            {
                _session.LastPacketTick = Stopwatch.GetTimestamp();
                CloseSession();
                return;
            }
            if (header.Type == PacketType.Audio)
            {
                bool silent = header.Flags.HasFlag(PacketFlags.Silent);
                if (AudioMessage.TryRead(body.Span, silent, out ulong index, out var pcm))
                {
                    long tick = Stopwatch.GetTimestamp();
                    if (_session.LastAudioTick != 0)
                        _session.MaxAudioGapMs = Math.Max(_session.MaxAudioGapMs,
                            (tick - _session.LastAudioTick) * 1000.0 / Stopwatch.Frequency);
                    _session.LastAudioTick = tick;
                    _session.Engine.Receive(index, pcm, silent);
                    _session.LastPacketTick = tick;
                }
            }
            if (header.Type == PacketType.Ping && body.Length == 8)
            {
                _session.EchoTicks = BinaryPrimitives.ReadUInt64LittleEndian(body.Span);
                _session.LastPacketTick = Stopwatch.GetTimestamp();
            }
        }
        if (header.Type == PacketType.Ping && body.Length == 8)
            await SendAsync(udp, remote, new(PacketType.Pong, PacketFlags.None, header.Session, 0), body.ToArray(), ct);
    }

    private void PlayoutLoop(CancellationToken ct)
    {
        long periodTicks = Stopwatch.Frequency * Protocol.FrameMs / 1000;
        long deadline = Stopwatch.GetTimestamp() + periodTicks;
        short[] samples = new short[Protocol.SamplesPerFrame * Protocol.Channels];
        int ticks = 0;
        while (!ct.IsCancellationRequested)
        {
            long remaining = deadline - Stopwatch.GetTimestamp();
            while (remaining > 0 && !ct.IsCancellationRequested)
            {
                int sleepMs = Math.Max(1, (int)Math.Ceiling(remaining * 1000.0 / Stopwatch.Frequency));
                Thread.Sleep(sleepMs);
                remaining = deadline - Stopwatch.GetTimestamp();
            }
            if (ct.IsCancellationRequested) break;
            Session? active;
            lock (_gate)
            {
                long now = Stopwatch.GetTimestamp();
                if (_session is not null && now - _session.LastPacketTick > 3 * Stopwatch.Frequency)
                    CloseSession();
                if (_idleSink is not null && now >= _idleReleaseTick)
                    ReleaseIdleSink();
                active = _session;
            }
            if (active is not null)
            {
                try
                {
                    lock (active.SinkGate)
                    {
                        if (!active.Closed)
                        {
                            active.Engine.Read(samples);
                            long beforeWrite = Stopwatch.GetTimestamp();
                            active.Sink.Write(samples);
                            active.MaxSinkWriteMs = Math.Max(active.MaxSinkWriteMs,
                                (Stopwatch.GetTimestamp() - beforeWrite) * 1000.0 / Stopwatch.Frequency);
                            if ((ticks + 1) % 200 == 0)
                                Volatile.Write(ref active.SinkDelayMs, active.Sink.DelayMs);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Audio sink failed: {ex.Message}");
                    lock (_gate) if (ReferenceEquals(_session, active)) CloseSession(releaseNow: true);
                }
            }
            ticks++;
            deadline += periodTicks;
            long afterFrame = Stopwatch.GetTimestamp();
            if (deadline <= afterFrame)
                deadline += ((afterFrame - deadline) / periodTicks + 1) * periodTicks;
        }
    }

    private async Task StatsLoopAsync(UdpClient udp, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(ct))
            await SendStatsAsync(udp, ct);
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
            new StatsMessage(true, (ushort)stats.BufferMs, (ushort)Volatile.Read(ref _session.SinkDelayMs),
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
        "alsa" => new AlsaSink("default"),
        _ when description.StartsWith("alsa:", StringComparison.OrdinalIgnoreCase) => new AlsaSink(description[5..]),
        _ when description.StartsWith("wav:", StringComparison.OrdinalIgnoreCase) => new WavSink(description[4..]),
        _ => throw new ArgumentException($"Unsupported sink: {description}")
    };

    private void CloseSession(bool releaseNow = false)
    {
        if (_session is null) return;
        Session closing = _session;
        lock (_session.SinkGate)
        {
            if (!_session.Closed)
            {
                _session.Closed = true;
                ReleaseIdleSink();
                if (releaseNow) DisposeSink(_session.Sink);
                else
                {
                    _idleSink = _session.Sink;
                    _idleReleaseTick = _session.LastPacketTick + config.IdleReleaseSec * Stopwatch.Frequency;
                }
            }
        }
        Console.WriteLine($"Session {closing.Id} closed: {closing.Engine.GetStats()}, maxAudioGap={closing.MaxAudioGapMs:F1}ms maxSinkWrite={closing.MaxSinkWriteMs:F1}ms");
        _session = null;
    }

    private void ReleaseIdleSink()
    {
        var sink = _idleSink;
        _idleSink = null;
        if (sink is not null) DisposeSink(sink);
    }

    private static void DisposeSink(IAudioSink sink)
    {
        try { sink.Dispose(); }
        catch (Exception ex) { Console.Error.WriteLine($"Cannot release audio sink: {ex.Message}"); }
    }
}
