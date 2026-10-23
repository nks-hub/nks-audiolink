using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using NksAudioLink.Client;
using NksAudioLink.Cli;
using NksAudioLink.Core;

try
{
    if (args is ["--version", ..])
    {
        Console.WriteLine($"NKS AudioLink CLI {ProductVersion.Informational}");
        return;
    }
    if (args.Length == 0 || args[0] is "help" or "--help")
    {
        Console.WriteLine("NKS AudioLink commands:");
        Console.WriteLine("NksAudioLink.Cli.exe --version");
        Console.WriteLine("NksAudioLink.Cli.exe devices");
        Console.WriteLine("NksAudioLink.Cli.exe discover [--port PORT] [--config FILE]");
        Console.WriteLine("NksAudioLink.Cli.exe send --server HOST [--port PORT] [--latency MS] [--mode loopback|capture] [--device ID] [--seconds N] [--gain 0..4] [--takeover true|false] [--config FILE]");
        Console.WriteLine("NksAudioLink.Cli.exe test-tone --server HOST [--port PORT] [--seconds N] [--gain 0..4] [--config FILE]");
        Console.WriteLine("devices: list audio devices; discover: find servers on the local network.");
        Console.WriteLine("send: stream audio; test-tone: send a 440 Hz test tone.");
        Console.WriteLine("--gain 1 keeps the original level; higher values may clip.");
        Console.WriteLine("Explicit options override --config values regardless of argument order.");
        return;
    }
    if (args[0] == "devices")
    {
        foreach (var device in WasapiSource.ListDevices())
            Console.WriteLine($"{(device.IsRender ? "render " : "capture")} {device.Name} [{device.Id}]");
        return;
    }
    if (args[0] == "discover")
    {
        var discoverConfig = CommandLineOptions.Parse(args).Config;
        byte[] discoverKey = string.IsNullOrEmpty(discoverConfig.PskBase64) ? [] : Convert.FromBase64String(discoverConfig.PskBase64);
        byte[] request = new byte[Protocol.MaxDatagramSize];
        int requestLength = Protocol.Write(request, new(PacketType.Discover, PacketFlags.None, 0, 0), [], discoverKey);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var probes = new List<Task>();
        var foundServers = new ConcurrentDictionary<IPEndPoint, byte>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null) continue;
                byte[] ip = unicast.Address.GetAddressBytes();
                byte[] mask = unicast.IPv4Mask.GetAddressBytes();
                byte[] broadcast = new byte[4];
                for (int j = 0; j < 4; j++) broadcast[j] = (byte)(ip[j] | ~mask[j]);
                probes.Add(ProbeAsync(unicast.Address, new IPAddress(broadcast)));
            }
        }
        await Task.WhenAll(probes);
        if (probes.Count == 0) Console.WriteLine("No active IPv4 network adapter is available. Check the network connection.");
        return;

        async Task ProbeAsync(IPAddress local, IPAddress directedBroadcast)
        {
            try
            {
                using var probe = new UdpClient(new IPEndPoint(local, 0)) { EnableBroadcast = true };
                await probe.SendAsync(request.AsMemory(0, requestLength), new IPEndPoint(directedBroadcast, discoverConfig.Port));
                if (!directedBroadcast.Equals(IPAddress.Broadcast))
                {
                    try { await probe.SendAsync(request.AsMemory(0, requestLength), new IPEndPoint(IPAddress.Broadcast, discoverConfig.Port)); }
                    catch (SocketException) { }
                }
                while (!timeout.IsCancellationRequested)
                {
                    var reply = await probe.ReceiveAsync(timeout.Token);
                    if (Protocol.TryRead(reply.Buffer, discoverKey, out var header, out var body) &&
                        header.Type == PacketType.DiscoverReply && DiscoverReplyMessage.TryRead(body, out var server) &&
                        foundServers.TryAdd(reply.RemoteEndPoint, 0))
                        Console.WriteLine($"{reply.RemoteEndPoint.Address}:{reply.RemoteEndPoint.Port} {server.Name} ({server.Sink}, auth={server.AuthRequired})");
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) { }
        }
    }
    if (args[0] == "send")
    {
        var options = CommandLineOptions.Parse(args);
        var sendConfig = options.Config;
        bool loopback = options.Loopback;
        bool takeover = options.Takeover;
        int sendSeconds = options.Seconds;
        string? deviceId = options.DeviceId;
        using var stop = new CancellationTokenSource();
        if (sendSeconds > 0) stop.CancelAfter(TimeSpan.FromSeconds(sendSeconds));
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        var client = new AudioStreamClient(sendConfig);
        client.StateChanged += Console.WriteLine;
        client.StatsReceived += (stats, rttMs) => Console.WriteLine($"buffer={stats.BufferMs}ms sink={stats.SinkDelayMs}ms rtt={rttMs:F1}ms underruns={stats.Underruns} lost={stats.Lost} late={stats.Late}");
        await client.RunAsync(deviceId, loopback, stop.Token, takeover);
        return;
    }
    if (args[0] != "test-tone") throw new ArgumentException("Unknown command. Run NksAudioLink.Cli.exe --help to list commands.");
    var toneOptions = CommandLineOptions.Parse(args);
    var config = toneOptions.Config;
    int seconds = toneOptions.Seconds;
    using var timerResolution = new WindowsTimerResolution();
    IPAddress address = (await Dns.GetHostAddressesAsync(config.Server)).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
        ?? throw new NotSupportedException($"The server '{config.Server}' has no IPv4 address. Use an IPv4 address or a hostname with an IPv4 record.");
    var endpoint = new IPEndPoint(address, config.Port);
    using var udp = new UdpClient(AddressFamily.InterNetwork);
    udp.Connect(endpoint);
    byte[] key = string.IsNullOrEmpty(config.PskBase64) ? [] : Convert.FromBase64String(config.PskBase64);
    uint session = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4));
    uint sequence = 0;
    byte[] packet = new byte[Protocol.MaxDatagramSize];
    byte[] body = new byte[Protocol.PcmBytesPerFrame + AudioMessage.PrefixSize];

    async Task Send(PacketType type, PacketFlags flags, ReadOnlyMemory<byte> data)
    {
        int length = Protocol.Write(packet, new PacketHeader(type, flags, session, ++sequence), data.Span, key);
        await udp.SendAsync(packet.AsMemory(0, length));
    }

    int helloSize = new HelloMessage(48000, 2, 1, 5, (ushort)config.TargetLatencyMs, Environment.MachineName).Write(body);
    await Send(PacketType.Hello, PacketFlags.None, body.AsMemory(0, helloSize));
    using var handshake = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    bool accepted = false;
    while (!accepted)
    {
        UdpReceiveResult response;
        try { response = await udp.ReceiveAsync(handshake.Token); }
        catch (OperationCanceledException) when (handshake.IsCancellationRequested)
        {
            throw new TimeoutException("The server did not accept the test tone within two seconds. Check the server address, port and shared key.");
        }
        if (!Protocol.TryRead(response.Buffer, key, out var header, out var responseBody) || header.Session != session) continue;
        if (header.Type == PacketType.Reject) throw new InvalidOperationException($"The server rejected the connection (code {(responseBody.IsEmpty ? 0 : responseBody[0])}). Check the shared key and whether another client is connected.");
        if (header.Type == PacketType.Stats && StatsMessage.TryRead(responseBody, out var stats)) accepted = stats.Accepted;
    }

    using var statusStop = new CancellationTokenSource();
    int statsReceived = 0;
    StatsMessage latestStats = default;
    long previousSendTick = 0;
    double largestSendGapMs = 0;
    int sendGapsOver10Ms = 0;
    Task statusTask = ReceiveStatusAsync();

    async Task ReceiveStatusAsync()
    {
        try
        {
            while (!statusStop.IsCancellationRequested)
            {
                var response = await udp.ReceiveAsync(statusStop.Token);
                if (!Protocol.TryRead(response.Buffer, key, out var header, out var responseBody) || header.Session != session) continue;
                if (header.Type == PacketType.Stats && StatsMessage.TryRead(responseBody, out var stats))
                {
                    latestStats = stats;
                    if (++statsReceived % 10 == 0)
                        Console.WriteLine($"stats buffer={stats.BufferMs}ms sink={stats.SinkDelayMs}ms underruns={stats.Underruns} overruns={stats.Overruns} lost={stats.Lost} late={stats.Late} ratio={stats.RatioPpm}ppm sendGapMax={Volatile.Read(ref largestSendGapMs):F1}ms gaps>10={Volatile.Read(ref sendGapsOver10Ms)}");
                }
                else if (header.Type == PacketType.Reject && responseBody.Length == 1 && Enum.IsDefined((RejectReason)responseBody[0]))
                    throw new ServerRejectedException((RejectReason)responseBody[0]);
            }
        }
        catch (OperationCanceledException) when (statusStop.IsCancellationRequested) { }
        catch { statusStop.Cancel(); throw; }
    }

    Console.WriteLine($"Sending a 440 Hz tone to {endpoint} for {seconds} s.");
    int frames = seconds * 1000 / Protocol.FrameMs;
    int trailingSilentFrames = config.TargetLatencyMs / Protocol.FrameMs + 2;
    var senderDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var sender = new Thread(() =>
    {
        try
        {
            long startTick = Stopwatch.GetTimestamp();
            for (int frame = 0; frame < frames + trailingSilentFrames && !statusStop.IsCancellationRequested; frame++)
            {
                long sendTick = Stopwatch.GetTimestamp();
                if (previousSendTick != 0)
                {
                    double gapMs = (sendTick - previousSendTick) * 1000.0 / Stopwatch.Frequency;
                    Volatile.Write(ref largestSendGapMs, Math.Max(largestSendGapMs, gapMs));
                    if (gapMs > 10) Interlocked.Increment(ref sendGapsOver10Ms);
                }
                previousSendTick = sendTick;
                if (frame > 0 && frame % 400 == 0)
                {
                    helloSize = new HelloMessage(48000, 2, 1, 5, (ushort)config.TargetLatencyMs, Environment.MachineName).Write(body);
                    int helloLength = Protocol.Write(packet, new(PacketType.Hello, PacketFlags.None, session, ++sequence), body.AsSpan(0, helloSize), key);
                    udp.Send(packet, helloLength);
                }
                bool silent = frame >= frames;
                for (int i = 0; !silent && i < Protocol.SamplesPerFrame; i++)
                {
                    double phase = 2 * Math.PI * 440 * (frame * Protocol.SamplesPerFrame + i) / Protocol.SampleRate;
                    short sample = (short)Math.Clamp((int)Math.Round(Math.Sin(phase) * 12000 * config.Gain), short.MinValue, short.MaxValue);
                    BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(AudioMessage.PrefixSize + i * 4), sample);
                    BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(AudioMessage.PrefixSize + i * 4 + 2), sample);
                }
                int size = AudioMessage.Write(body, (ulong)frame * Protocol.SamplesPerFrame, Protocol.SamplesPerFrame,
                    silent ? [] : body.AsSpan(AudioMessage.PrefixSize, Protocol.PcmBytesPerFrame), silent);
                int length = Protocol.Write(packet, new(PacketType.Audio, silent ? PacketFlags.Silent : PacketFlags.None, session, ++sequence), body.AsSpan(0, size), key);
                udp.Send(packet, length);
                long deadline = startTick + (long)((frame + 1) * Protocol.FrameMs * (double)Stopwatch.Frequency / 1000);
                while (!statusStop.IsCancellationRequested)
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
    }) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "NKS AudioLink test sender" };
    try
    {
        sender.Start();
        await senderDone.Task;
    }
    finally
    {
        statusStop.Cancel();
        try { await statusTask; }
        finally
        {
            try { await Send(PacketType.Bye, PacketFlags.None, ReadOnlyMemory<byte>.Empty); }
            catch (SocketException) { }
        }
    }
    Console.WriteLine($"final stats: received={statsReceived} buffer={latestStats.BufferMs}ms sink={latestStats.SinkDelayMs}ms underruns={latestStats.Underruns} overruns={latestStats.Overruns} lost={latestStats.Lost} late={latestStats.Late} ratio={latestStats.RatioPpm}ppm");
    Console.WriteLine($"send timing: largest gap={largestSendGapMs:F1}ms gaps>10ms={sendGapsOver10Ms}");
    Console.WriteLine("Done.");
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    Environment.ExitCode = 1;
}
