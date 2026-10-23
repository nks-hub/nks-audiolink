using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using NksAudioLink.Client;
using NksAudioLink.Core;
using NksAudioLink.Server;

if (args.Length != 0 && args is not ["--output", _])
    throw new ArgumentException("Usage: dotnet run --project tests/adaptive-buffer -- [--output FILE.json]");
var results = new List<object>();
foreach (bool adaptive in new[] { false, true }) results.Add(await Run(adaptive));
string json = JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
if (args is ["--output", var output])
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
    File.WriteAllText(output, json + Environment.NewLine);
}
Console.WriteLine(json);

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task<object> Run(bool adaptive)
{
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(50));
    using var allocation = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    int serverPort = ((IPEndPoint)allocation.Client.LocalEndPoint!).Port;
    allocation.Close();
    using var proxy = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    int proxyPort = ((IPEndPoint)proxy.Client.LocalEndPoint!).Port;
    byte[] key = RandomNumberGenerator.GetBytes(32);
    var sink = new ProbeSink();
    var server = new AudioServer(new ServerConfig
    {
        Port = serverPort,
        PskBase64 = Convert.ToBase64String(key)
    }, _ => sink);
    Task serverTask = server.RunAsync(stop.Token);
    var source = new ProbeSource();
    var client = new AudioStreamClient(new ClientConfig
    {
        Server = "127.0.0.1", Port = proxyPort,
        PskBase64 = Convert.ToBase64String(key), AdaptiveBuffer = adaptive
    }, (_, _) => source);
    var clock = Stopwatch.StartNew();
    var received = new ConcurrentQueue<Observation>();
    client.StatsReceived += (stats, _) => received.Enqueue(new(clock.Elapsed.TotalSeconds, stats));
    var sessions = new ConcurrentDictionary<uint, byte>();
    int audioCount = 0, dropped = 0, statsSize = 0;
    IPEndPoint? clientEndpoint = null;
    var serverEndpoint = new IPEndPoint(IPAddress.Loopback, serverPort);
    Task forwarding = Task.Run(async () =>
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var datagram = await proxy.ReceiveAsync(stop.Token);
                Check(Protocol.TryRead(datagram.Buffer, key, out var header, out var body), "Invalid authenticated packet.");
                if (datagram.RemoteEndPoint.Port == serverPort)
                {
                    if (header.Type == PacketType.Stats) statsSize = body.Length;
                    if (clientEndpoint is not null)
                        await proxy.SendAsync(datagram.Buffer, clientEndpoint, stop.Token);
                }
                else
                {
                    clientEndpoint = datagram.RemoteEndPoint;
                    if (header.Type == PacketType.Hello) sessions.TryAdd(header.Session, 0);
                    if (header.Type == PacketType.Audio)
                    {
                        audioCount++;
                        // Exactly 50 ms of network loss, without changing either endpoint.
                        if (adaptive && audioCount is >= 120 and < 130) { dropped++; continue; }
                    }
                    await proxy.SendAsync(datagram.Buffer, serverEndpoint, stop.Token);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    });
    Task clientTask = client.RunAsync(null, true, stop.Token);
    try
    {
        // Leave over 35 seconds after the induced loss for the 30-second
        // clean-playback decrease and gradual occupancy response to be observed.
        await Task.Delay(adaptive ? 38_000 : 3500, stop.Token);
        Check(!clientTask.IsCompleted && !forwarding.IsCompleted && !serverTask.IsCompleted,
            "An endpoint/proxy stopped before observation completed.");
        var status = server.GetStatus().Session ?? throw new InvalidOperationException("No live session.");
        var observations = received.Where(s => s.Stats.Accepted).ToArray();
        Check(observations.Length >= 3 && sink.NonzeroFrames > 100 && audioCount > 100,
            "No actual client/server PCM flow.");
        Check(statsSize == (adaptive ? StatsMessage.AdaptiveSize : StatsMessage.Size), "Incorrect STATS format.");
        Check(sessions.Count == 1 && sessions.ContainsKey(status.Id), "Session restarted during the test.");
        if (adaptive)
        {
            Check(dropped == 10 && observations.All(s => s.Stats.AdaptiveBuffer is { MinimumMs: 20, MaximumMs: 100 }),
                "Requested adaptive bounds were not acknowledged.");
            Check(observations.Any(s => s.Stats.AdaptiveBuffer is { TargetMs: 40, Reason: AdaptiveBufferReason.Underrun }),
                "The deliberate loss did not increase the target from 30 to 40 ms.");
            Check(status.AdaptiveBuffer is { TargetMs: 35, Reason: AdaptiveBufferReason.Stable, Changes: 2 },
                "Clean continuation did not decrease the target to 35 ms; inspect host scheduling/load.");
            Check(status.Underruns == 1 && status.Lost == 10 && status.Late == 0 && status.Overruns == 0,
                "Unexpected additional loss, underrun, late packet or overrun; inspect host scheduling/load.");
        }
        else
            Check(observations.All(s => s.Stats.AdaptiveBuffer is null) && status.AdaptiveBuffer is null && status.TargetLatencyMs == 30,
                "Manual mode changed its target or emitted adaptive STATS.");
        return new
        {
            adaptive, authenticated = true, elapsedSeconds = clock.Elapsed.TotalSeconds,
            statsSize, statsCount = observations.Length, audioPackets = audioCount, dropped,
            nonzeroOutputFrames = sink.NonzeroFrames, sessionCount = sessions.Count,
            status.TargetLatencyMs, status.AdaptiveBuffer, status.Underruns, status.Lost,
            status.Late, status.Overruns,
            observations = observations.Select(s => new
            {
                seconds = Math.Round(s.Seconds, 3), s.Stats.BufferMs, s.Stats.RatioPpm,
                s.Stats.AdaptiveBuffer, s.Stats.Underruns, s.Stats.Lost
            }).ToArray()
        };
    }
    finally
    {
        stop.Cancel();
        foreach (var task in new[] { clientTask, forwarding, serverTask })
        {
            try { await task.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
        Check(source.Disposed && sink.Disposed, "Capture/output resources were not disposed.");
    }
}

sealed record Observation(double Seconds, StatsMessage Stats);

sealed class ProbeSource : IAudioSource
{
    public AudioFrameQueue Queue { get; } = new();
    public int CaptureLatencyMs => 10;
    public bool Disposed { get; private set; }
    private readonly CancellationTokenSource _stop = new();
    private Task? _fill;
    public event Action<Exception>? CaptureError { add { } remove { } }
    public void Start()
    {
        _fill = Task.Run(async () =>
        {
            byte[] pcm = new byte[Protocol.PcmBytesPerFrame];
            for (int i = 0; i < pcm.Length; i += 2)
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i), 2000);
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    Queue.Write(pcm);
                    await Task.Delay(5, _stop.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        });
    }
    public void Stop() { _stop.Cancel(); _fill?.GetAwaiter().GetResult(); }
    public void Dispose() { Stop(); _stop.Dispose(); Disposed = true; }
}

sealed class ProbeSink : IAudioSink
{
    private int _nonzeroFrames;
    public int NonzeroFrames => Volatile.Read(ref _nonzeroFrames);
    public bool Disposed { get; private set; }
    public int DelayMs => 0;
    public void Write(ReadOnlySpan<short> samples)
    {
        foreach (short value in samples)
            if (value != 0) { Interlocked.Increment(ref _nonzeroFrames); break; }
    }
    public void Dispose() => Disposed = true;
}
