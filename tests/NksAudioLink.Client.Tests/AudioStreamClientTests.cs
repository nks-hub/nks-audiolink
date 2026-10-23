using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NksAudioLink.Client;
using NksAudioLink.Core;

namespace NksAudioLink.Client.Tests;

public sealed class AudioStreamClientTests
{
    [Fact]
    public async Task StatsTimeoutClearsSentLevelsBeforeQuietSameSessionReacceptance()
    {
        using var server = new PacketServer();
        byte[] pcm = new byte[Protocol.PcmBytesPerFrame * 3];
        for (int index = 0; index < pcm.Length; index += 2)
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(index), short.MaxValue);
        int emit = 1, observingOutage = 0, reaccepting = 0;
        FakeSource? source = null;
        source = new FakeSource
        {
            OnQueueAccess = () =>
            {
                if (Thread.CurrentThread.Name == "NKS AudioLink sender" && Volatile.Read(ref emit) == 1)
                    source!.Write(pcm);
            }
        };
        var client = new AudioStreamClient(Config(server.Port), (_, _) => source);
        var timeoutLevel = new TaskCompletionSource<AudioLevelSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reacceptedLevel = new TaskCompletionSource<AudioLevelSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StateChanged += state =>
        {
            if (state == "Waiting for server" && Volatile.Read(ref observingOutage) == 1)
                timeoutLevel.TrySetResult(client.Levels);
        };
        client.StatsReceived += (stats, _) =>
        {
            if (stats.Accepted && Volatile.Read(ref reaccepting) == 1)
                reacceptedLevel.TrySetResult(client.Levels);
        };
        using var stop = new CancellationTokenSource();
        Task running = client.RunAsync(null, true, stop.Token);
        try
        {
            await Eventually(() => client.Levels.LeftRmsDbfs > -0.01 && client.Levels.Clipping, TimeSpan.FromSeconds(3));
            uint originalSession = server.Audio.First().Session;
            server.BeginOutage();
            Volatile.Write(ref observingOutage, 1);
            // Keep capture active until the transport times out: stopping it sooner
            // would let silent packets decay the meter and mask the stale-state bug.
            var atTimeout = await timeoutLevel.Task.WaitAsync(TimeSpan.FromSeconds(6));
            Assert.Equal(new AudioLevelSnapshot(-90, -90, -90, -90, false), atTimeout);

            Volatile.Write(ref emit, 0);
            source.Queue.Clear();
            int beforeReacceptance = server.Audio.Count;
            Volatile.Write(ref reaccepting, 1);
            server.EndOutage();
            var atReacceptance = await reacceptedLevel.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new AudioLevelSnapshot(-90, -90, -90, -90, false), atReacceptance);
            await Eventually(() => server.Audio.Count >= beforeReacceptance + 5, TimeSpan.FromSeconds(3));
            var recovered = server.Audio.Skip(beforeReacceptance).ToArray();
            Assert.All(recovered, packet => { Assert.True(packet.Silent); Assert.Equal(originalSession, packet.Session); });
            Assert.Equal(new AudioLevelSnapshot(-90, -90, -90, -90, false), client.Levels);
        }
        finally { await Stop(stop, running); }
    }

    [Fact]
    public async Task SentLevelsFollowPostGainWirePcmClipThenDecayOnSilentFramesAndResetOnStop()
    {
        using var server = new PacketServer();
        byte[] pcm = new byte[Protocol.PcmBytesPerFrame * 3];
        for (int index = 0; index < pcm.Length; index += 4)
        {
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(index), 8192);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(index + 2), 4096);
        }
        int emit = 1;
        FakeSource? source = null;
        source = new FakeSource
        {
            OnQueueAccess = () =>
            {
                if (Thread.CurrentThread.Name == "NKS AudioLink sender" && Volatile.Read(ref emit) == 1)
                    source!.Write(pcm);
            }
        };
        var client = new AudioStreamClient(Config(server.Port) with { Gain = 2 }, (_, _) => source);
        using var stop = new CancellationTokenSource();
        Task running = client.RunAsync(null, true, stop.Token);
        try
        {
            await Eventually(() => client.Levels.LeftRmsDbfs > -6.03 && client.Levels.RightRmsDbfs > -12.05, TimeSpan.FromSeconds(3));
            var level = client.Levels;
            Assert.InRange(level.LeftRmsDbfs, -6.1, -6.0);
            Assert.InRange(level.RightRmsDbfs, -12.1, -12.0);
            Assert.InRange(level.LeftPeakDbfs, -6.1, -6.0);
            Assert.InRange(level.RightPeakDbfs, -12.1, -12.0);
            Assert.False(level.Clipping);
            Assert.Contains(server.Audio, packet => packet.LeftSample == 16384 && packet.RightSample == 8192);

            client.Gain = 4;
            await Eventually(() => client.Levels.Clipping && server.Audio.Any(packet => packet.LeftSample == short.MaxValue), TimeSpan.FromSeconds(3));
            Assert.InRange(client.Levels.LeftPeakDbfs, -0.01, 0);

            Volatile.Write(ref emit, 0);
            source.Queue.Clear();
            await Eventually(() => client.Levels.LeftRmsDbfs <= -90 && !client.Levels.Clipping && client.Levels.LeftPeakDbfs < -0.1, TimeSpan.FromSeconds(3));
            Assert.True(server.Audio.Last().Silent);
            Assert.True(client.Levels.LeftPeakDbfs < -0.1, "The held peak must decay when silent frames are sent.");
        }
        finally { await Stop(stop, running); }
        Assert.Equal(new AudioLevelSnapshot(-90, -90, -90, -90, false), client.Levels);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidatedUdpSocketRecreatesTransportAndCapture(bool receiveFailure)
    {
        using var server = new PacketServer();
        var sources = new ConcurrentQueue<FakeSource>();
        var sockets = new ConcurrentQueue<InvalidatableSocket>();
        var client = new AudioStreamClient(Config(server.Port), (_, _) =>
        {
            var source = new FakeSource();
            sources.Enqueue(source);
            return source;
        }, () =>
        {
            var socket = new InvalidatableSocket(receiveFailure);
            sockets.Enqueue(socket);
            return socket;
        });
        using var stop = new CancellationTokenSource();
        Task running = client.RunAsync(null, true, stop.Token);
        try
        {
            await Eventually(() => server.Audio.Count >= 3, TimeSpan.FromSeconds(3));
            uint firstSession = server.Audio.First().Session;
            sockets.Single().Invalidate();
            await Eventually(() => server.Audio.Any(packet => packet.Session != firstSession), TimeSpan.FromSeconds(6));
            Assert.Equal(2, sockets.Count);
            Assert.True(sockets.First().Disposed);
            Assert.Equal(2, sources.Count);
            Assert.True(sources.First().Disposed);
            Assert.False(sources.Last().Disposed);
        }
        finally { await Stop(stop, running); }
        Assert.All(sources, source => Assert.True(source.Disposed));
        Assert.All(sockets, socket => Assert.True(socket.Disposed));
    }

    [Fact]
    public async Task CaptureArgumentErrorIsNotRetriedAsAnInvalidatedSocket()
    {
        using var server = new PacketServer();
        int created = 0;
        var client = new AudioStreamClient(Config(server.Port), (_, _) =>
        {
            Interlocked.Increment(ref created);
            return new FakeSource { OnStart = () => throw new ArgumentException("Invalid capture setting.") };
        });
        await Assert.ThrowsAsync<ArgumentException>(() => client.RunAsync(null, true, CancellationToken.None));
        Assert.Equal(1, created);
    }

    [Fact]
    public async Task SenderSchedulerPauseRehandshakesBeforeSendingMoreAudio()
    {
        using var server = new PacketServer();
        using var paused = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        int pausePending = 1;
        var source = new FakeSource
        {
            OnQueueAccess = () =>
            {
                if (Thread.CurrentThread.Name != "NKS AudioLink sender" || Interlocked.Exchange(ref pausePending, 0) != 1) return;
                paused.Set();
                if (!resume.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test sender was not resumed.");
            }
        };
        var client = new AudioStreamClient(Config(server.Port), (_, _) => source);
        using var stop = new CancellationTokenSource();
        Task running = client.RunAsync(null, true, stop.Token);
        try
        {
            await Eventually(() => paused.IsSet, TimeSpan.FromSeconds(3));
            // Pause just this test sender, not the PC or the receiver. This exceeds
            // the 50 ms discontinuity threshold but precedes the normal 2 s HELLO.
            await Task.Delay(200);
            int before = server.Traffic.Count;
            uint session = server.Traffic.First(packet => packet.Type == PacketType.Hello).Session;
            resume.Set();
            await Eventually(() => server.HelloCount >= 2 && server.Audio.Count >= 3, TimeSpan.FromSeconds(1));
            var after = server.Traffic.ToArray().Skip(before).ToArray();
            int hello = Array.FindIndex(after, packet => packet.Type == PacketType.Hello);
            Assert.InRange(hello, 0, 1);
            // Only the frame already entered before the pause may precede HELLO.
            Assert.True(after.Take(hello).Count(packet => packet.Type == PacketType.Audio) <= 1);
            Assert.All(server.Audio, packet => Assert.Equal(session, packet.Session));
            Assert.False(source.Disposed);
        }
        finally
        {
            resume.Set();
            await Stop(stop, running);
        }
    }

    [Fact]
    public async Task Ipv6OnlyAddressReportsActionableFailureWithoutOpeningCapture()
    {
        var client = new AudioStreamClient(new ClientConfig { Server = "::1" },
            (_, _) => throw new InvalidOperationException("Capture should not be opened."));
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => client.RunAsync(null, true, CancellationToken.None));
        Assert.Contains("IPv4", error.Message);
    }

    [Fact]
    public async Task CaptureCallbackAlreadyInFlightIsSafeAfterShutdown()
    {
        using var server = new PacketServer();
        var source = new FakeSource();
        var client = new AudioStreamClient(Config(server.Port), (_, _) => source);
        using var stop = new CancellationTokenSource();
        Task running = client.RunAsync(null, true, stop.Token);
        Action<Exception>? callback = null;
        try
        {
            await Eventually(() => source.Started, TimeSpan.FromSeconds(3));
            callback = source.CaptureErrorSnapshot();
        }
        finally { await Stop(stop, running); }
        Assert.NotNull(callback);
        Assert.True(source.Disposed);
        callback(new IOException("A callback queued before capture stopped."));
    }

    [Fact]
    public async Task SlowCaptureStartupDoesNotOverwriteConnectedState()
    {
        using var server = new PacketServer();
        using var connected = new ManualResetEventSlim();
        var source = new FakeSource { OnStart = () => Assert.True(connected.Wait(TimeSpan.FromSeconds(3))) };
        var client = new AudioStreamClient(Config(server.Port), (_, _) => source);
        var states = new ConcurrentQueue<string>();
        client.StateChanged += state =>
        {
            states.Enqueue(state);
            if (state == "Connected") connected.Set();
        };
        using var stop = new CancellationTokenSource();
        Task running = Task.Run(() => client.RunAsync(null, true, stop.Token));
        try
        {
            await Eventually(() => server.Audio.Count >= 3, TimeSpan.FromSeconds(5));
            Assert.Equal("Connected", states.Last());
            Assert.Equal("Connecting", states.First());
        }
        finally { await Stop(stop, running); }
    }

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
    public async Task TenSecondNetworkOutageReacceptsSameSessionAndKeepsCapture()
    {
        using var server = new PacketServer();
        var sources = new ConcurrentQueue<FakeSource>();
        var client = new AudioStreamClient(Config(server.Port), (_, _) =>
        {
            var source = new FakeSource();
            sources.Enqueue(source);
            return source;
        });
        int waitingStates = 0;
        client.StateChanged += state =>
        {
            if (state == "Waiting for server") Interlocked.Increment(ref waitingStates);
        };
        using var stop = new CancellationTokenSource();
        Task running = client.RunAsync(null, true, stop.Token);
        try
        {
            await Eventually(() => server.Audio.Count >= 3 && server.AcceptedStatsSent >= 1,
                TimeSpan.FromSeconds(5));
            int beforeOutage = server.Audio.Count;
            int statsBeforeOutage = server.AcceptedStatsSent;
            uint session = server.Audio.Last().Session;
            server.BeginOutage();
            try
            {
                // Longer than the client's three-second STATS timeout and the
                // production server's three-second session timeout.
                await Task.Delay(TimeSpan.FromSeconds(10));
                Assert.True(Volatile.Read(ref waitingStates) >= 1);
                Assert.Equal(statsBeforeOutage, server.AcceptedStatsSent);
                Assert.False(running.IsCompleted);
                Assert.Single(sources);
                Assert.False(sources.Single().Disposed);
            }
            finally { server.EndOutage(); }

            await Eventually(() => server.AcceptedStatsSent >= 2 &&
                server.Audio.Count >= beforeOutage + 3, TimeSpan.FromSeconds(5));
            Assert.Equal(session, server.Audio.Last().Session);
            Assert.Single(sources);
            Assert.False(sources.Single().Disposed);
        }
        finally { await Stop(stop, running); }
        Assert.True(sources.Single().Disposed);
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
        client.StateChanged += state => { if (state == "Connected") Interlocked.Increment(ref connected); };
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

    [Theory]
    [InlineData(RejectReason.Busy)]
    [InlineData(RejectReason.UnsupportedFormat)]
    public async Task TerminalRejectDisposesCaptureAndDoesNotSendHelloAfterServerBecomesFree(RejectReason reason)
    {
        using var server = new RejectThenAcceptServer(reason);
        var sources = new ConcurrentQueue<FakeSource>();
        var client = new AudioStreamClient(Config(server.Port), (_, _) =>
        {
            var source = new FakeSource();
            sources.Enqueue(source);
            return source;
        });

        var rejected = await Assert.ThrowsAsync<ServerRejectedException>(
            () => client.RunAsync(null, true, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(reason, rejected.Reason);
        Assert.Equal(1, server.HelloCount);
        Assert.Single(sources);
        Assert.True(sources.Single().Disposed);

        server.Release();
        // A live sender would emit another HELLO after 400 x 5 ms; a retry
        // would recreate capture after one second. Neither may happen.
        await Task.Delay(2300);
        Assert.Equal(1, server.HelloCount);
        Assert.Equal(0, server.AcceptedAfterRelease);
        Assert.Single(sources);
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
        private readonly AudioFrameQueue _queue = new();
        public AudioFrameQueue Queue { get { OnQueueAccess?.Invoke(); return _queue; } }
        public Action? OnQueueAccess { get; init; }
        public int CaptureLatencyMs => 10;
        public event Action<Exception>? CaptureError;
        public bool Started { get; private set; }
        public bool Disposed { get; private set; }
        public Action? OnStart { get; init; }
        public void Write(ReadOnlySpan<byte> pcm) => _queue.Write(pcm);
        public void Start() { Started = true; OnStart?.Invoke(); }
        public void Stop() => Started = false;
        public void Dispose() => Disposed = true;
        public void Fail() => CaptureError?.Invoke(new IOException("Simulated device invalidation."));
        public Action<Exception>? CaptureErrorSnapshot() => CaptureError;
    }

    private readonly record struct AudioPacket(uint Session, ulong SampleIndex, bool Silent, int PcmLength, short LeftSample, short RightSample);

    private sealed class InvalidatableSocket(bool receiveFailure) : IClientDatagramSocket
    {
        private readonly ClientDatagramSocket _inner = new();
        private readonly CancellationTokenSource _invalidated = new();
        public bool Disposed { get; private set; }
        public void Connect(IPEndPoint endpoint) => _inner.Connect(endpoint);
        public void Invalidate() => _invalidated.Cancel();
        public void Send(byte[] packet, int length)
        {
            if (!receiveFailure && _invalidated.IsCancellationRequested)
                throw new SocketException((int)SocketError.InvalidArgument);
            _inner.Send(packet, length);
        }
        public ValueTask<int> SendAsync(ReadOnlyMemory<byte> packet) => _inner.SendAsync(packet);
        public async ValueTask<UdpReceiveResult> ReceiveAsync(CancellationToken cancellationToken)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
                receiveFailure ? _invalidated.Token : CancellationToken.None);
            try { return await _inner.ReceiveAsync(stop.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && _invalidated.IsCancellationRequested)
            { throw new SocketException((int)SocketError.InvalidArgument); }
        }
        public void Dispose()
        {
            _inner.Dispose();
            _invalidated.Dispose();
            Disposed = true;
        }
    }

    private sealed class PacketServer : IDisposable
    {
        private readonly UdpClient _udp;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _receiver;
        private readonly ConcurrentDictionary<uint, byte> _acceptedSessions = new();
        private int _helloCount;
        private int _acceptedStatsSent;
        private int _dropTraffic;
        private long _outageStartTick;
        private int _disposed;
        public ConcurrentQueue<AudioPacket> Audio { get; } = new();
        public ConcurrentQueue<PacketHeader> Traffic { get; } = new();
        public int Port { get; }
        public int HelloCount => Volatile.Read(ref _helloCount);
        public int AcceptedStatsSent => Volatile.Read(ref _acceptedStatsSent);

        public void BeginOutage()
        {
            Volatile.Write(ref _outageStartTick, Stopwatch.GetTimestamp());
            Volatile.Write(ref _dropTraffic, 1);
        }

        public void EndOutage()
        {
            // Model the server forgetting the session once its idle TTL elapsed.
            if (Stopwatch.GetElapsedTime(Volatile.Read(ref _outageStartTick)) >= TimeSpan.FromSeconds(3))
                _acceptedSessions.Clear();
            Volatile.Write(ref _dropTraffic, 0);
        }

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
                    if (Volatile.Read(ref _dropTraffic) == 1) continue;
                    if (!Protocol.TryRead(packet.Buffer, [], out var header, out var body)) continue;
                    Traffic.Enqueue(header);
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
                        Audio.Enqueue(new AudioPacket(header.Session, index, header.Flags.HasFlag(PacketFlags.Silent), pcm.Length,
                            pcm.Length == 0 ? (short)0 : BinaryPrimitives.ReadInt16LittleEndian(pcm),
                            pcm.Length == 0 ? (short)0 : BinaryPrimitives.ReadInt16LittleEndian(pcm[2..])));
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

    private sealed class RejectThenAcceptServer : IDisposable
    {
        private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _receiver;
        private readonly RejectReason _reason;
        private int _helloCount;
        private int _released;
        private int _acceptedAfterRelease;
        public int Port => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
        public int HelloCount => Volatile.Read(ref _helloCount);
        public int AcceptedAfterRelease => Volatile.Read(ref _acceptedAfterRelease);

        public RejectThenAcceptServer(RejectReason reason)
        {
            _reason = reason;
            _receiver = ReceiveAsync();
        }

        public void Release() => Volatile.Write(ref _released, 1);

        private async Task ReceiveAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var packet = await _udp.ReceiveAsync(_stop.Token);
                    if (!Protocol.TryRead(packet.Buffer, [], out var header, out var body) ||
                        header.Type != PacketType.Hello || !HelloMessage.TryRead(body, out _)) continue;
                    Interlocked.Increment(ref _helloCount);
                    byte[] payload;
                    PacketType type;
                    if (Volatile.Read(ref _released) == 0)
                    {
                        type = PacketType.Reject;
                        payload = [(byte)_reason];
                    }
                    else
                    {
                        Interlocked.Increment(ref _acceptedAfterRelease);
                        type = PacketType.Stats;
                        payload = new byte[StatsMessage.Size];
                        new StatsMessage(true, 30, 20, 0, 0, 0, 0, 0, 0).Write(payload);
                    }
                    byte[] response = new byte[Protocol.HeaderSize + payload.Length];
                    int length = Protocol.Write(response,
                        new PacketHeader(type, PacketFlags.None, header.Session, 1), payload);
                    await _udp.SendAsync(response.AsMemory(0, length), packet.RemoteEndPoint, _stop.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _udp.Dispose();
            _stop.Dispose();
            _ = _receiver;
        }
    }
}
