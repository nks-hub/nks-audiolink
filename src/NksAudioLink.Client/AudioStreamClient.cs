using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using NksAudioLink.Core;

namespace NksAudioLink.Client;

public sealed class AudioStreamClient(ClientConfig config, Func<string?, bool, IAudioSource>? sourceFactory = null)
{
    private readonly Func<IClientDatagramSocket> _socketFactory = () => new ClientDatagramSocket();
    private readonly Func<CancellationToken, Task<IAudioSource>>? _asyncSourceFactory;
    public AudioStreamClient(ClientConfig config, CaptureProcess process) : this(config) =>
        _asyncSourceFactory = token => WasapiSource.CreateProcessAsync(process, token);
    internal AudioStreamClient(ClientConfig config, Func<string?, bool, IAudioSource> sourceFactory,
        Func<IClientDatagramSocket> socketFactory) : this(config, sourceFactory) => _socketFactory = socketFactory;
    private double _gain = config.Gain;
    private int _muted = config.Muted ? 1 : 0;
    private int _limiterEnabled = config.LimiterEnabled ? 1 : 0;
    private int _processingLatencyMs;
    public double Gain
    {
        get => Volatile.Read(ref _gain);
        set
        {
            if (!double.IsFinite(value) || value is < 0 or > 4) throw new ArgumentOutOfRangeException(nameof(value));
            Volatile.Write(ref _gain, value);
        }
    }
    /// <summary>Live mute with a 5 ms ramp; the selected Gain is preserved.</summary>
    public bool Muted
    {
        get => Volatile.Read(ref _muted) != 0;
        set => Volatile.Write(ref _muted, value ? 1 : 0);
    }
    public bool LimiterEnabled
    {
        get => Volatile.Read(ref _limiterEnabled) != 0;
        set => Volatile.Write(ref _limiterEnabled, value ? 1 : 0);
    }
    /// <summary>Maximum DSP delay, including a limiter transition back to bypass.</summary>
    public int ProcessingLatencyMs => LimiterEnabled ? 1 : Volatile.Read(ref _processingLatencyMs);
    public event Action<StatsMessage, double>? StatsReceived;
    public event Action<string>? StateChanged;
    private double _captureLatencyMs = 10;
    public double CaptureLatencyMs => Volatile.Read(ref _captureLatencyMs);
    private readonly AudioLevelMeter _levelMeter = new();
    /// <summary>Levels of the post-gain/mute/limiter PCM submitted to UDP, not receiver output.</summary>
    public AudioLevelSnapshot Levels => _levelMeter.GetSnapshot();

    private sealed class CaptureDeviceException(Exception inner) : Exception("The audio device stopped providing data. Check its connection.", inner);
    private sealed class TransportRestartException(SocketException inner) : IOException("The network socket was invalidated. Reconnecting.", inner);

    public async Task RunAsync(string? deviceId, bool loopback, CancellationToken cancellationToken, bool takeover = false)
    {
        _levelMeter.Reset();
        try { await RunCoreAsync(deviceId, loopback, cancellationToken, takeover); }
        finally { _levelMeter.Reset(); Volatile.Write(ref _processingLatencyMs, 0); }
    }

    private async Task RunCoreAsync(string? deviceId, bool loopback, CancellationToken cancellationToken, bool takeover)
    {
        bool recoveringCapture = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            string retryState = "Recovering audio device";
            try
            {
                await RunSessionAsync(deviceId, loopback, cancellationToken, takeover);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (CaptureDeviceException) { recoveringCapture = true; }
            catch (TransportRestartException) { retryState = "Waiting for server"; }
            catch (COMException error) when ((uint)error.HResult is 0x88890004 or 0x88890010 or 0x88890026 or 0x80070490 or 0x800706BA)
            {
                recoveringCapture = true;
            }
            catch (Exception error) when (recoveringCapture && error is COMException or InvalidOperationException or ArgumentException)
            {
                // The endpoint can remain unavailable for a short time after wake or unplug.
            }
            catch (SocketException error) when (IsTransient(error) || error.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData)
            {
                retryState = "Waiting for server";
            }
            takeover = false;
            StateChanged?.Invoke(retryState);
            try { await Task.Delay(1000, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        }
    }

    private async Task RunSessionAsync(string? deviceId, bool loopback, CancellationToken cancellationToken, bool takeover)
    {
        config.Validate();
        var outputProcessor = new PcmOutputProcessor(Gain, Muted, LimiterEnabled);
        using var runStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken runToken = runStop.Token;
        IPAddress address = (await Dns.GetHostAddressesAsync(config.Server, cancellationToken))
            .FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork)
            ?? throw new NotSupportedException($"The server '{config.Server}' has no IPv4 address. Use an IPv4 address or a hostname with an IPv4 record.");
        using IClientDatagramSocket udp = _socketFactory();
        udp.Connect(new IPEndPoint(address, config.Port));
        using var timerResolution = new WindowsTimerResolution();
        using IAudioSource source = _asyncSourceFactory is not null
            ? await _asyncSourceFactory(runToken).ConfigureAwait(false)
            : sourceFactory is null
            ? new WasapiSource(deviceId, loopback)
            : sourceFactory(deviceId, loopback);
        Exception? captureFailure = null;
        ServerRejectedException? rejectionFailure = null;
        void CaptureFailed(Exception error)
        {
            Interlocked.CompareExchange(ref captureFailure, error, null);
            // An audio callback may already have copied the delegate when we unsubscribe.
            try { runStop.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        source.CaptureError += CaptureFailed;
        byte[] key = string.IsNullOrEmpty(config.PskBase64) ? [] : Convert.FromBase64String(config.PskBase64);
        uint session = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4));
        uint sequence = 0;
        ulong sampleIndex = 0;
        byte[] packet = new byte[Protocol.MaxDatagramSize];
        byte[] body = new byte[AudioMessage.PrefixSize + Protocol.PcmBytesPerFrame];
        int accepted = 0;
        int resetProcessing = 1;
        object connectionGate = new();
        int takeoverPending = takeover ? 1 : 0;
        long lastStatsTick = 0;
        double lastRttMs = 0;

        async Task SendAsync(PacketType type, PacketFlags flags, int size)
        {
            int length = Protocol.Write(packet, new PacketHeader(type, flags, session, ++sequence), body.AsSpan(0, size), key);
            try { await udp.SendAsync(packet.AsMemory(0, length)); }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.InvalidArgument)
            { throw new TransportRestartException(error); }
            catch (SocketException error) when (IsTransient(error)) { ConnectionInterrupted(); }
        }

        int WriteHello() => new HelloMessage(48000, 2, 1, 5, (ushort)config.EffectiveInitialLatencyMs,
            Environment.MachineName, config.AdaptiveBuffer
                ? new AdaptiveBufferOptions(config.AdaptiveMinLatencyMs, config.AdaptiveMaxLatencyMs, Predictive: true) : null).Write(body);

        async Task SendHelloAsync()
        {
            int size = WriteHello();
            await SendAsync(PacketType.Hello, Volatile.Read(ref takeoverPending) == 1 ? PacketFlags.Takeover : PacketFlags.None, size);
        }

        void SendSync(PacketType type, PacketFlags flags, int size)
        {
            int length = Protocol.Write(packet, new PacketHeader(type, flags, session, ++sequence), body.AsSpan(0, size), key);
            try { udp.Send(packet, length); }
            // Windows can invalidate a connected UDP socket across S3 resume.
            // Recreate it in the outer session loop; retrying this socket fails.
            catch (SocketException error) when (error.SocketErrorCode == SocketError.InvalidArgument)
            { throw new TransportRestartException(error); }
            catch (SocketException error) when (IsTransient(error)) { ConnectionInterrupted(); }
        }

        void ConnectionInterrupted()
        {
            int previous;
            lock (connectionGate)
            {
                previous = Interlocked.Exchange(ref accepted, 0);
                Volatile.Write(ref resetProcessing, 1);
                _levelMeter.Reset();
            }
            if (previous == 1) StateChanged?.Invoke("Waiting for server");
        }

        using var receiveStop = CancellationTokenSource.CreateLinkedTokenSource(runToken);
        Task receiver = ReceiveLoopAsync();
        async Task ReceiveLoopAsync()
        {
            try
            {
                while (!receiveStop.IsCancellationRequested)
                {
                    UdpReceiveResult response;
                    try { response = await udp.ReceiveAsync(receiveStop.Token); }
                    catch (SocketException error) when (error.SocketErrorCode == SocketError.InvalidArgument)
                    { throw new TransportRestartException(error); }
                    catch (SocketException error) when (IsTransient(error))
                    {
                        ConnectionInterrupted();
                        await Task.Delay(100, receiveStop.Token);
                        continue;
                    }
                    if (!Protocol.TryRead(response.Buffer, key, out var header, out var responseBody) || header.Session != session) continue;
                    if (header.Type == PacketType.Stats && StatsMessage.TryRead(responseBody, out var stats))
                    {
                        // Do not send PCM until the receiver confirms the requested
                        // adaptive policy; an ordinary acceptance is not an acknowledgment.
                        if (stats.Accepted && config.AdaptiveBuffer &&
                            (stats.AdaptiveBuffer is not { } adaptive ||
                             !adaptive.Predictive ||
                             adaptive.MinimumMs != config.AdaptiveMinLatencyMs ||
                             adaptive.MaximumMs != config.AdaptiveMaxLatencyMs))
                            throw new ServerRejectedException(RejectReason.UnsupportedFormat, adaptiveBuffer: true);
                        int next = stats.Accepted ? 1 : 0;
                        int previous;
                        lock (connectionGate)
                        {
                            previous = accepted;
                            if (previous != next)
                            {
                                _levelMeter.Reset();
                                Volatile.Write(ref resetProcessing, 1);
                                if (next == 1) source.Queue.Clear();
                            }
                            lastStatsTick = Stopwatch.GetTimestamp();
                            Volatile.Write(ref accepted, next);
                        }
                        if (next == 1) Interlocked.Exchange(ref takeoverPending, 0);
                        StatsReceived?.Invoke(stats, lastRttMs);
                        if (previous != next) StateChanged?.Invoke(stats.Accepted ? "Connected" : "Rejected");
                    }
                    else if (header.Type == PacketType.Pong && responseBody.Length == 8)
                    {
                        ulong echoed = BinaryPrimitives.ReadUInt64LittleEndian(responseBody);
                        long elapsed = Stopwatch.GetTimestamp() - (long)echoed;
                        if (elapsed >= 0) lastRttMs = elapsed * 1000.0 / Stopwatch.Frequency;
                    }
                    else if (header.Type == PacketType.Reject)
                    {
                        if (responseBody.Length != 1 || !Enum.IsDefined((RejectReason)responseBody[0])) continue;
                        lock (connectionGate)
                        {
                            Interlocked.Exchange(ref accepted, 0);
                            _levelMeter.Reset();
                        }
                        Interlocked.CompareExchange(ref rejectionFailure,
                            new ServerRejectedException((RejectReason)responseBody[0], config.AdaptiveBuffer), null);
                        runStop.Cancel();
                        StateChanged?.Invoke("Rejected");
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch { runStop.Cancel(); throw; }
        }

        try
        {
            StateChanged?.Invoke("Connecting");
            await SendHelloAsync();
            source.Start();
            Volatile.Write(ref _captureLatencyMs, source.CaptureLatencyMs);
            var senderDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var sender = new Thread(() =>
            {
                try
                {
                    long startTick = Stopwatch.GetTimestamp();
                    long ticks = 0;
                    while (!runToken.IsCancellationRequested)
                    {
                        // After suspend or a long scheduler pause, resume from now instead
                        // of flooding the server with all missed clock ticks.
                        long now = Stopwatch.GetTimestamp();
                        if (now - (startTick + (long)(ticks * Protocol.FrameMs * (double)Stopwatch.Frequency / 1000)) > Stopwatch.Frequency / 20)
                        {
                            startTick = now;
                            ticks = 399; // Send HELLO immediately after the clock discontinuity.
                            startTick -= (long)(ticks * Protocol.FrameMs * (double)Stopwatch.Frequency / 1000);
                            source.Queue.Clear();
                            ConnectionInterrupted();
                        }
                        ticks++;
                        if (runToken.IsCancellationRequested) break;
                        if (ticks % 400 == 0)
                        {
                            int helloSize = WriteHello();
                            SendSync(PacketType.Hello, Volatile.Read(ref takeoverPending) == 1 ? PacketFlags.Takeover : PacketFlags.None, helloSize);
                        }
                        if (ticks % 200 == 0)
                        {
                            BinaryPrimitives.WriteUInt64LittleEndian(body, (ulong)Stopwatch.GetTimestamp());
                            SendSync(PacketType.Ping, PacketFlags.None, 8);
                        }
                        bool timedOut = false;
                        lock (connectionGate)
                        {
                            if (accepted == 1 && (lastStatsTick == 0 || Stopwatch.GetElapsedTime(lastStatsTick) > TimeSpan.FromSeconds(3)))
                            {
                                timedOut = Interlocked.Exchange(ref accepted, 0) == 1;
                                Volatile.Write(ref resetProcessing, 1);
                                _levelMeter.Reset();
                            }
                        }
                        if (timedOut) StateChanged?.Invoke("Waiting for server");
                        // Capture read, DSP state, metering and submission share the
                        // connection boundary: an old delayed frame cannot cross a
                        // disconnect/reaccept transition or repopulate a reset meter.
                        lock (connectionGate)
                        {
                            if (accepted == 1)
                            {
                                bool hasAudio = source.Queue.TryReadFrame(body.AsSpan(AudioMessage.PrefixSize, Protocol.PcmBytesPerFrame));
                                var pcm = body.AsSpan(AudioMessage.PrefixSize, Protocol.PcmBytesPerFrame);
                                if (!hasAudio) pcm.Clear();
                                double gain = Gain;
                                bool muted = Muted, limiterEnabled = LimiterEnabled;
                                if (Interlocked.Exchange(ref resetProcessing, 0) != 0)
                                    outputProcessor.Reset(gain, muted, limiterEnabled);
                                // Silence must still advance ramps/release and drain delayed
                                // limiter PCM. Never discard that tail using the capture flag.
                                bool processedAudio = outputProcessor.ProcessPcm16(pcm, gain, muted, limiterEnabled);
                                Volatile.Write(ref _processingLatencyMs,
                                    outputProcessor.LatencyFrames * 1000 / Protocol.SampleRate);
                                // Effect-produced silence can omit PCM. Fully bypassed
                                // capture retains the original packet representation.
                                bool effectsActive = muted || limiterEnabled || outputProcessor.LatencyFrames != 0;
                                hasAudio = processedAudio || (hasAudio && !effectsActive);
                                int size = AudioMessage.Write(body, sampleIndex, Protocol.SamplesPerFrame,
                                    hasAudio ? body.AsSpan(AudioMessage.PrefixSize, Protocol.PcmBytesPerFrame) : [], !hasAudio);
                                if (hasAudio) _levelMeter.ProcessPcm16(body.AsSpan(AudioMessage.PrefixSize, Protocol.PcmBytesPerFrame));
                                else _levelMeter.ProcessSilence(Protocol.SamplesPerFrame);
                                SendSync(PacketType.Audio, hasAudio ? PacketFlags.None : PacketFlags.Silent, size);
                                sampleIndex += Protocol.SamplesPerFrame;
                            }
                        }
                        long deadline = startTick + (long)(ticks * Protocol.FrameMs * (double)Stopwatch.Frequency / 1000);
                        while (!runToken.IsCancellationRequested)
                        {
                            long remaining = deadline - Stopwatch.GetTimestamp();
                            if (remaining <= 0) break;
                            if (remaining * 1000.0 / Stopwatch.Frequency > 1) Thread.Sleep(1);
                            else Thread.Yield();
                        }
                    }
                    senderDone.TrySetResult();
                }
                catch (Exception error)
                {
                    senderDone.TrySetException(error);
                }
            }) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "NKS AudioLink sender" };
            sender.Start();
            await senderDone.Task;
            if (Volatile.Read(ref rejectionFailure) is { } rejection && !cancellationToken.IsCancellationRequested)
                throw rejection;
            if (Volatile.Read(ref captureFailure) is { } failure && !cancellationToken.IsCancellationRequested)
            {
                if (failure is CaptureProcessUnavailableException) throw failure;
                throw new CaptureDeviceException(failure);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            _levelMeter.Reset();
            source.CaptureError -= CaptureFailed;
            try { source.Stop(); }
            finally
            {
                receiveStop.Cancel();
                try { await receiver; }
                finally
                {
                    try { await SendAsync(PacketType.Bye, PacketFlags.None, 0); } catch (SocketException) { }
                    catch (TransportRestartException) { }
                    StateChanged?.Invoke("Disconnected");
                }
            }
        }
    }

    private static bool IsTransient(SocketException error) => error.SocketErrorCode is
        SocketError.ConnectionReset or SocketError.ConnectionRefused or SocketError.NetworkDown or
        SocketError.NetworkUnreachable or SocketError.HostDown or SocketError.HostUnreachable or
        SocketError.TimedOut or SocketError.TryAgain or SocketError.NoBufferSpaceAvailable;
}
