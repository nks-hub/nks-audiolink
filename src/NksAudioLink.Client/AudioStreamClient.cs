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
    private double _gain = config.Gain;
    public double Gain
    {
        get => Volatile.Read(ref _gain);
        set
        {
            if (!double.IsFinite(value) || value is < 0 or > 4) throw new ArgumentOutOfRangeException(nameof(value));
            Volatile.Write(ref _gain, value);
        }
    }
    public event Action<StatsMessage, double>? StatsReceived;
    public event Action<string>? StateChanged;
    private double _captureLatencyMs = 10;
    public double CaptureLatencyMs => Volatile.Read(ref _captureLatencyMs);

    private sealed class CaptureDeviceException(Exception inner) : Exception("The audio device stopped providing data. Check its connection.", inner);

    public async Task RunAsync(string? deviceId, bool loopback, CancellationToken cancellationToken, bool takeover = false)
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
            catch (CaptureDeviceException) { recoveringCapture = true; }
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
        using var runStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken runToken = runStop.Token;
        IPAddress address = (await Dns.GetHostAddressesAsync(config.Server, cancellationToken))
            .First(ip => ip.AddressFamily == AddressFamily.InterNetwork);
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Connect(new IPEndPoint(address, config.Port));
        using var timerResolution = new WindowsTimerResolution();
        using IAudioSource source = sourceFactory is null
            ? new WasapiSource(deviceId, loopback)
            : sourceFactory(deviceId, loopback);
        Exception? captureFailure = null;
        ServerRejectedException? rejectionFailure = null;
        void CaptureFailed(Exception error)
        {
            Interlocked.CompareExchange(ref captureFailure, error, null);
            runStop.Cancel();
        }
        source.CaptureError += CaptureFailed;
        byte[] key = string.IsNullOrEmpty(config.PskBase64) ? [] : Convert.FromBase64String(config.PskBase64);
        uint session = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4));
        uint sequence = 0;
        ulong sampleIndex = 0;
        byte[] packet = new byte[Protocol.MaxDatagramSize];
        byte[] body = new byte[AudioMessage.PrefixSize + Protocol.PcmBytesPerFrame];
        int accepted = 0;
        int takeoverPending = takeover ? 1 : 0;
        long lastStatsTick = 0;
        double lastRttMs = 0;

        async Task SendAsync(PacketType type, PacketFlags flags, int size)
        {
            int length = Protocol.Write(packet, new PacketHeader(type, flags, session, ++sequence), body.AsSpan(0, size), key);
            try { await udp.SendAsync(packet.AsMemory(0, length)); }
            catch (SocketException error) when (IsTransient(error)) { ConnectionInterrupted(); }
        }

        async Task SendHelloAsync()
        {
            int size = new HelloMessage(48000, 2, 1, 5, (ushort)config.TargetLatencyMs, Environment.MachineName).Write(body);
            await SendAsync(PacketType.Hello, Volatile.Read(ref takeoverPending) == 1 ? PacketFlags.Takeover : PacketFlags.None, size);
        }

        void SendSync(PacketType type, PacketFlags flags, int size)
        {
            int length = Protocol.Write(packet, new PacketHeader(type, flags, session, ++sequence), body.AsSpan(0, size), key);
            try { udp.Send(packet, length); }
            catch (SocketException error) when (IsTransient(error)) { ConnectionInterrupted(); }
        }

        void ConnectionInterrupted()
        {
            if (Interlocked.Exchange(ref accepted, 0) == 1) StateChanged?.Invoke("Waiting for server");
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
                    catch (SocketException error) when (IsTransient(error))
                    {
                        ConnectionInterrupted();
                        await Task.Delay(100, receiveStop.Token);
                        continue;
                    }
                    if (!Protocol.TryRead(response.Buffer, key, out var header, out var responseBody) || header.Session != session) continue;
                    if (header.Type == PacketType.Stats && StatsMessage.TryRead(responseBody, out var stats))
                    {
                        int next = stats.Accepted ? 1 : 0;
                        int previous = Interlocked.Exchange(ref accepted, next);
                        if (next == 1 && previous == 0) source.Queue.Clear();
                        if (next == 1) Interlocked.Exchange(ref takeoverPending, 0);
                        Volatile.Write(ref lastStatsTick, Stopwatch.GetTimestamp());
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
                        Interlocked.Exchange(ref accepted, 0);
                        Interlocked.CompareExchange(ref rejectionFailure,
                            new ServerRejectedException((RejectReason)responseBody[0]), null);
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
            await SendHelloAsync();
            source.Start();
            Volatile.Write(ref _captureLatencyMs, source.CaptureLatencyMs);
            StateChanged?.Invoke("Connecting");
            var senderDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var sender = new Thread(() =>
            {
                try
                {
                    long startTick = Stopwatch.GetTimestamp();
                    int ticks = 0;
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
                            int helloSize = new HelloMessage(48000, 2, 1, 5, (ushort)config.TargetLatencyMs, Environment.MachineName).Write(body);
                            SendSync(PacketType.Hello, Volatile.Read(ref takeoverPending) == 1 ? PacketFlags.Takeover : PacketFlags.None, helloSize);
                        }
                        if (ticks % 200 == 0)
                        {
                            BinaryPrimitives.WriteUInt64LittleEndian(body, (ulong)Stopwatch.GetTimestamp());
                            SendSync(PacketType.Ping, PacketFlags.None, 8);
                        }
                        long last = Volatile.Read(ref lastStatsTick);
                        if (last == 0 || (Stopwatch.GetTimestamp() - last) * 1000.0 / Stopwatch.Frequency > 3000)
                        {
                            if (Interlocked.Exchange(ref accepted, 0) == 1) StateChanged?.Invoke("Waiting for server");
                        }
                        if (Volatile.Read(ref accepted) == 1)
                        {
                            bool hasAudio = source.Queue.TryReadFrame(body.AsSpan(AudioMessage.PrefixSize, Protocol.PcmBytesPerFrame));
                            double gain = Gain;
                            if (hasAudio && gain != 1)
                            {
                                for (int i = AudioMessage.PrefixSize; i < body.Length; i += 2)
                                {
                                    int scaled = (int)Math.Round(BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(i)) * gain);
                                    BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(i), (short)Math.Clamp(scaled, short.MinValue, short.MaxValue));
                                }
                            }
                            int size = AudioMessage.Write(body, sampleIndex, Protocol.SamplesPerFrame,
                                hasAudio ? body.AsSpan(AudioMessage.PrefixSize, Protocol.PcmBytesPerFrame) : [], !hasAudio);
                            SendSync(PacketType.Audio, hasAudio ? PacketFlags.None : PacketFlags.Silent, size);
                            sampleIndex += Protocol.SamplesPerFrame;
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
                throw new CaptureDeviceException(failure);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            source.CaptureError -= CaptureFailed;
            try { source.Stop(); }
            finally
            {
                receiveStop.Cancel();
                try { await receiver; }
                finally
                {
                    try { await SendAsync(PacketType.Bye, PacketFlags.None, 0); } catch (SocketException) { }
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
