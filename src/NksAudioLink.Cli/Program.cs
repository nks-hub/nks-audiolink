using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using NksAudioLink.Client;
using NksAudioLink.Core;

try
{
    if (args.Length == 0 || args[0] is "help" or "--help")
    {
        Console.WriteLine("NKS AudioLink commands:");
        Console.WriteLine("NksAudioLink.Cli.exe devices");
        Console.WriteLine("NksAudioLink.Cli.exe discover [--port PORT] [--config FILE]");
        Console.WriteLine("NksAudioLink.Cli.exe send --server HOST [--mode loopback|capture] [--device ID] [--seconds N] [--gain 0..4] [--takeover true|false] [--config FILE]");
        Console.WriteLine("NksAudioLink.Cli.exe test-tone --server HOST [--port PORT] [--seconds N] [--config FILE]");
        Console.WriteLine("devices: list audio devices; discover: find servers on the local network.");
        Console.WriteLine("send: stream audio; test-tone: send a 440 Hz test tone.");
        Console.WriteLine("--gain 1 keeps the original level; higher values may clip.");
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
        var discoverConfig = new ClientConfig();
        for (int i = 1; i < args.Length; i++)
        {
            if (i + 1 >= args.Length) throw new ArgumentException($"Missing value after {args[i]}.");
            switch (args[i++])
            {
                case "--port": discoverConfig = discoverConfig with { Port = int.Parse(args[i]) }; break;
                case "--config": discoverConfig = ClientConfig.Load(args[i]); break;
                default: throw new ArgumentException($"Unknown option: {args[i - 1]}");
            }
        }
        discoverConfig.Validate();
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
        var sendConfig = new ClientConfig();
        bool loopback = true;
        bool takeover = false;
        int sendSeconds = 0;
        string? deviceId = null;
        for (int i = 1; i < args.Length; i++)
        {
            if (i + 1 >= args.Length) throw new ArgumentException($"Missing value after {args[i]}.");
            switch (args[i++])
            {
                case "--server": sendConfig = sendConfig with { Server = args[i] }; break;
                case "--port": sendConfig = sendConfig with { Port = int.Parse(args[i]) }; break;
                case "--latency": sendConfig = sendConfig with { TargetLatencyMs = int.Parse(args[i]) }; break;
                case "--gain": sendConfig = sendConfig with { Gain = double.Parse(args[i], System.Globalization.CultureInfo.InvariantCulture) }; break;
                case "--seconds": sendSeconds = int.Parse(args[i]); break;
                case "--takeover": takeover = bool.Parse(args[i]); break;
                case "--device": deviceId = args[i]; break;
                case "--mode": loopback = args[i] switch { "loopback" => true, "capture" => false, _ => throw new ArgumentException("Use loopback or capture with --mode.") }; break;
                case "--config": sendConfig = ClientConfig.Load(args[i]); break;
                default: throw new ArgumentException($"Unknown option: {args[i - 1]}");
            }
        }
        using var stop = new CancellationTokenSource();
        if (sendSeconds is < 0 or > 3600) throw new ArgumentOutOfRangeException(nameof(sendSeconds));
        if (sendSeconds > 0) stop.CancelAfter(TimeSpan.FromSeconds(sendSeconds));
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        var client = new AudioStreamClient(sendConfig);
        client.StateChanged += Console.WriteLine;
        client.StatsReceived += (stats, rttMs) => Console.WriteLine($"buffer={stats.BufferMs}ms sink={stats.SinkDelayMs}ms rtt={rttMs:F1}ms underruns={stats.Underruns} lost={stats.Lost} late={stats.Late}");
        await client.RunAsync(deviceId, loopback, stop.Token, takeover);
        return;
    }
    if (args[0] != "test-tone") throw new ArgumentException("Unknown command. Run NksAudioLink.Cli.exe --help to list commands.");
    var config = new ClientConfig();
    int seconds = 5;
    for (int i = 1; i < args.Length; i++)
    {
        if (i + 1 >= args.Length) throw new ArgumentException($"Missing value after {args[i]}.");
        switch (args[i++])
        {
            case "--server": config = config with { Server = args[i] }; break;
            case "--port": config = config with { Port = int.Parse(args[i]) }; break;
            case "--seconds": seconds = int.Parse(args[i]); break;
            case "--gain": config = config with { Gain = double.Parse(args[i], System.Globalization.CultureInfo.InvariantCulture) }; break;
            case "--config": config = ClientConfig.Load(args[i]); break;
            default: throw new ArgumentException($"Unknown option: {args[i - 1]}");
        }
    }
    config.Validate();
    if (seconds is < 1 or > 3600) throw new ArgumentOutOfRangeException(nameof(seconds));
    using var timerResolution = new WindowsTimerResolution();
    IPAddress address = (await Dns.GetHostAddressesAsync(config.Server)).First(a => a.AddressFamily == AddressFamily.InterNetwork);
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
        var response = await udp.ReceiveAsync(handshake.Token);
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
                        Console.WriteLine($"stats buffer={stats.BufferMs}ms sink={stats.SinkDelayMs}ms underruns={stats.Underruns} overruns={stats.Overruns} lost={stats.Lost} late={stats.Late} ratio={stats.RatioPpm}ppm sendGapMax={largestSendGapMs:F1}ms gaps>10={sendGapsOver10Ms}");
                }
                else if (header.Type == PacketType.Reject)
                    Console.Error.WriteLine("The server ended the active session. Check whether another client connected.");
            }
        }
        catch (OperationCanceledException) { }
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
            for (int frame = 0; frame < frames + trailingSilentFrames; frame++)
            {
                long sendTick = Stopwatch.GetTimestamp();
                if (previousSendTick != 0)
                {
                    double gapMs = (sendTick - previousSendTick) * 1000.0 / Stopwatch.Frequency;
                    largestSendGapMs = Math.Max(largestSendGapMs, gapMs);
                    if (gapMs > 10) sendGapsOver10Ms++;
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
                while (true)
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
    sender.Start();
    await senderDone.Task;
    statusStop.Cancel();
    await statusTask;
    await Send(PacketType.Bye, PacketFlags.None, ReadOnlyMemory<byte>.Empty);
    Console.WriteLine($"final stats: received={statsReceived} buffer={latestStats.BufferMs}ms sink={latestStats.SinkDelayMs}ms underruns={latestStats.Underruns} overruns={latestStats.Overruns} lost={latestStats.Lost} late={latestStats.Late} ratio={latestStats.RatioPpm}ppm");
    Console.WriteLine($"send timing: largest gap={largestSendGapMs:F1}ms gaps>10ms={sendGapsOver10Ms}");
    Console.WriteLine("Done.");
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    Environment.ExitCode = 1;
}
