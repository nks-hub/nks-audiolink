using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using NksAudioLink.Core;

try
{
    if (args.Length == 0 || args[0] is "help" or "--help")
    {
        Console.WriteLine("nksaudio test-tone --server HOST [--port PORT] [--seconds N] [--config FILE]");
        return;
    }
    if (args[0] != "test-tone") throw new ArgumentException("Unknown client command.");
    var config = new ClientConfig();
    int seconds = 5;
    for (int i = 1; i < args.Length; i++)
    {
        if (i + 1 >= args.Length) throw new ArgumentException($"Missing value for {args[i]}.");
        switch (args[i++])
        {
            case "--server": config = config with { Server = args[i] }; break;
            case "--port": config = config with { Port = int.Parse(args[i]) }; break;
            case "--seconds": seconds = int.Parse(args[i]); break;
            case "--config": config = ClientConfig.Load(args[i]); break;
            default: throw new ArgumentException($"Unknown option: {args[i - 1]}");
        }
    }
    config.Validate();
    if (seconds is < 1 or > 3600) throw new ArgumentOutOfRangeException(nameof(seconds));
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
        if (header.Type == PacketType.Reject) throw new InvalidOperationException($"Server rejected session: {(responseBody.IsEmpty ? 0 : responseBody[0])}");
        if (header.Type == PacketType.Stats && StatsMessage.TryRead(responseBody, out var stats)) accepted = stats.Accepted;
    }

    Console.WriteLine($"Streaming 440 Hz to {endpoint} for {seconds} s");
    var watch = Stopwatch.StartNew();
    int frames = seconds * 1000 / Protocol.FrameMs;
    for (int frame = 0; frame < frames; frame++)
    {
        if (frame > 0 && frame % 400 == 0)
        {
            helloSize = new HelloMessage(48000, 2, 1, 5, (ushort)config.TargetLatencyMs, Environment.MachineName).Write(body);
            await Send(PacketType.Hello, PacketFlags.None, body.AsMemory(0, helloSize));
        }
        for (int i = 0; i < Protocol.SamplesPerFrame; i++)
        {
            double phase = 2 * Math.PI * 440 * (frame * Protocol.SamplesPerFrame + i) / Protocol.SampleRate;
            short sample = (short)Math.Clamp((int)Math.Round(Math.Sin(phase) * 12000 * config.Gain), short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(AudioMessage.PrefixSize + i * 4), sample);
            BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(AudioMessage.PrefixSize + i * 4 + 2), sample);
        }
        int size = AudioMessage.Write(body, (ulong)frame * Protocol.SamplesPerFrame, Protocol.SamplesPerFrame,
            body.AsSpan(AudioMessage.PrefixSize, Protocol.PcmBytesPerFrame), false);
        await Send(PacketType.Audio, PacketFlags.None, body.AsMemory(0, size));
        TimeSpan delay = TimeSpan.FromMilliseconds((frame + 1) * Protocol.FrameMs) - watch.Elapsed;
        if (delay > TimeSpan.Zero) await Task.Delay(delay);
    }
    await Task.Delay(50);
    await Send(PacketType.Bye, PacketFlags.None, ReadOnlyMemory<byte>.Empty);
    Console.WriteLine("Done");
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    Environment.ExitCode = 1;
}
