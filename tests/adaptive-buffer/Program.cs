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

string scenario = "all";
string? output = null;
for (int i = 0; i < args.Length; i += 2)
{
    if (i + 1 >= args.Length) throw new ArgumentException("Each option needs a value.");
    switch (args[i])
    {
        case "--output": output = args[i + 1]; break;
        case "--scenario": scenario = args[i + 1]; break;
        default: throw new ArgumentException("Use --scenario all|manual|loss|jitter and/or --output FILE.json.");
    }
}
Check(scenario is "all" or "manual" or "loss" or "jitter", "Unknown scenario.");
var results = new List<object>();
foreach (string selected in scenario == "all" ? new[] { "manual", "loss", "jitter" } : new[] { scenario })
    results.Add(await Run(selected));
string json = JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
if (output is not null)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
    File.WriteAllText(output, json + Environment.NewLine);
}
Console.WriteLine(json);

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task<object> Run(string scenario)
{
    bool adaptive = scenario != "manual";
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
        PskBase64 = Convert.ToBase64String(key), AdaptiveBuffer = adaptive,
        // Predictive mode must start independently at 40 ms, ignoring this
        // deliberately different remembered manual slider value.
        TargetLatencyMs = adaptive ? 155 : 30
    }, (_, _) => source);
    var clock = Stopwatch.StartNew();
    var received = new ConcurrentQueue<Observation>();
    client.StatsReceived += (stats, _) => received.Enqueue(new(clock.Elapsed.TotalSeconds, stats));
    var sessions = new ConcurrentDictionary<uint, byte>();
    int audioCount = 0, dropped = 0, statsSize = 0, statsMarker = 0, helloFormat = 0;
    var changes = new ConcurrentQueue<PolicyChange>();
    var delayQueue = new TimedForwarder(proxy, stop.Token);
    Task delayed = delayQueue.RunAsync();
    Task observing = Task.Run(async () =>
    {
        uint previous = uint.MaxValue;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                if (server.GetStatus().Session is { AdaptiveBuffer: { } policy } session && policy.Changes != previous)
                {
                    changes.Enqueue(new(clock.Elapsed.TotalSeconds, policy, session.BufferMs,
                        session.Underruns, session.Lost, session.Late, session.Predictor));
                    previous = policy.Changes;
                }
                await Task.Delay(20, stop.Token);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    });
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
                    if (header.Type == PacketType.Stats)
                    {
                        statsSize = body.Length;
                        statsMarker = body.Length > StatsMessage.Size ? body[33] : 0;
                    }
                    if (clientEndpoint is not null)
                        await proxy.SendAsync(datagram.Buffer, clientEndpoint, stop.Token);
                }
                else
                {
                    clientEndpoint = datagram.RemoteEndPoint;
                    if (header.Type == PacketType.Hello)
                    {
                        sessions.TryAdd(header.Session, 0);
                        helloFormat = body[5];
                        Check(HelloMessage.TryRead(body, out var hello) && hello.TargetLatencyMs == (adaptive ? 40 : 30),
                            "The initial target depended on the manual slider.");
                    }
                    if (header.Type == PacketType.Audio)
                    {
                        audioCount++;
                        // Exactly 50 ms of network loss, without changing either endpoint.
                        if (scenario == "loss" && audioCount is >= 120 and < 130) { dropped++; continue; }
                        if (scenario == "jitter")
                        {
                            // Smoothly growing 0..30 ms delay, without dropping packets.
                            // Its variation stays inside the initial 40 ms buffer while
                            // repeated risk assessments can act before an underrun.
                            double amplitude = Math.Min(30, Math.Max(0, audioCount - 200) / 100.0);
                            double delayMs = amplitude * (1 + Math.Sin(audioCount / 25.0)) / 2;
                            delayQueue.Enqueue(datagram.Buffer, serverEndpoint, delayMs);
                            continue;
                        }
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
        await Task.Delay(scenario switch { "loss" => 38_000, "jitter" => 28_000, _ => 3500 }, stop.Token);
        Check(!clientTask.IsCompleted && !forwarding.IsCompleted && !delayed.IsCompleted && !serverTask.IsCompleted,
            "An endpoint/proxy stopped before observation completed.");
        var status = server.GetStatus().Session ?? throw new InvalidOperationException("No live session.");
        var observations = received.Where(s => s.Stats.Accepted).ToArray();
        Check(observations.Length >= 3 && sink.NonzeroFrames > 100 && audioCount > 100,
            "No actual client/server PCM flow.");
        Check(statsSize == (adaptive ? StatsMessage.AdaptiveSize : StatsMessage.Size), "Incorrect STATS format.");
        Check(helloFormat == (adaptive ? 0x82 : 1) && statsMarker == (adaptive ? 0xA2 : 0), "Incorrect predictive capability handshake.");
        Check(sessions.Count == 1 && sessions.ContainsKey(status.Id), "Session restarted during the test.");
        if (adaptive)
        {
            Check(observations.All(s => s.Stats.AdaptiveBuffer is { MinimumMs: 20, MaximumMs: 100, Predictive: true }),
                "Requested adaptive bounds were not acknowledged.");
            if (scenario == "loss")
            {
                Check(dropped == 10 && changes.Any(s => s.Policy is { TargetMs: 50, Reason: AdaptiveBufferReason.Underrun }),
                    "The deliberate loss did not increase the target from 40 to 50 ms.");
                Check(status.AdaptiveBuffer is { TargetMs: 50, Changes: 1 } && status.Predictor?.State == "FailureHold",
                    "A recent real error must retain its learned target beyond the old 30-second settling period.");
                Check(status.Underruns == 1 && status.Lost == 10 && status.Late == 0 && status.Overruns == 0,
                    "Unexpected additional loss, underrun, late packet or overrun; inspect host scheduling/load.");
            }
            else
            {
                Check(changes.Any(s => s.Policy.Reason == AdaptiveBufferReason.PredictiveRisk && s.Underruns == 0 && s.Lost == 0 && s.Late == 0),
                    "No predictive increase was observed before a stream error.");
                Check(dropped == 0 && status.Underruns == 0 && status.Lost == 0 && status.Late == 0 && status.Overruns == 0,
                    "Timed jitter caused an actual stream error; inspect delay and host scheduling evidence.");
            }
        }
        else
            Check(observations.All(s => s.Stats.AdaptiveBuffer is null) && status.AdaptiveBuffer is null && status.TargetLatencyMs == 30,
                "Manual mode changed its target or emitted adaptive STATS.");
        return new
        {
            scenario, adaptive, authenticated = true, elapsedSeconds = clock.Elapsed.TotalSeconds,
            helloFormat, statsMarker, statsSize, statsCount = observations.Length, audioPackets = audioCount, dropped,
            maximumQueuedPackets = delayQueue.MaximumQueued, maximumForwardingLatenessMs = delayQueue.MaximumLatenessMs,
            nonzeroOutputFrames = sink.NonzeroFrames, sessionCount = sessions.Count,
            status.TargetLatencyMs, status.AdaptiveBuffer, status.Underruns, status.Lost,
            status.Late, status.Overruns, predictor = status.Predictor, changes = changes.ToArray(),
            observations = observations.Select(s => new
            {
                seconds = Math.Round(s.Seconds, 3), s.Stats.BufferMs, s.Stats.RatioPpm,
                s.Stats.AdaptiveBuffer, s.Stats.Underruns, s.Stats.Lost
            }).ToArray()
        };
    }
    catch
    {
        // Keep timing/policy evidence when a strict real-time assertion fails.
        Console.Error.WriteLine(JsonSerializer.Serialize(new
        {
            scenario, status = server.GetStatus(), changes = changes.ToArray(),
            observations = received.ToArray(), audioCount, dropped,
            maximumForwardingLatenessMs = delayQueue.MaximumLatenessMs
        }, new JsonSerializerOptions { WriteIndented = true }));
        throw;
    }
    finally
    {
        stop.Cancel();
        foreach (var task in new[] { clientTask, forwarding, delayed, observing, serverTask })
        {
            try { await task.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
        Check(source.Disposed && sink.Disposed, "Capture/output resources were not disposed.");
    }
}

sealed record Observation(double Seconds, StatsMessage Stats);
sealed record PolicyChange(double Seconds, AdaptiveBufferStatus Policy, int BufferMs,
    long Underruns, long Lost, long Late, PredictiveBufferStatus? Predictor);

sealed class TimedForwarder(UdpClient socket, CancellationToken stop)
{
    private readonly object _gate = new();
    private readonly PriorityQueue<(byte[] Packet, IPEndPoint Endpoint), long> _pending = new();
    private int _maximumQueued;
    private double _maximumLatenessMs;
    public int MaximumQueued { get { lock (_gate) return _maximumQueued; } }
    public double MaximumLatenessMs { get { lock (_gate) return _maximumLatenessMs; } }
    public void Enqueue(byte[] packet, IPEndPoint endpoint, double delayMs)
    {
        long due = Stopwatch.GetTimestamp() + (long)(delayMs * Stopwatch.Frequency / 1000);
        lock (_gate)
        {
            if (_pending.Count >= 128) throw new InvalidOperationException("Timed proxy queue exceeded its bound.");
            _pending.Enqueue((packet, endpoint), due);
            _maximumQueued = Math.Max(_maximumQueued, _pending.Count);
        }
    }
    public async Task RunAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                (byte[] Packet, IPEndPoint Endpoint)? send = null;
                lock (_gate)
                {
                    long now = Stopwatch.GetTimestamp();
                    if (_pending.TryPeek(out var next, out long due) && due <= now)
                    {
                        send = _pending.Dequeue();
                        _maximumLatenessMs = Math.Max(_maximumLatenessMs, (now - due) * 1000.0 / Stopwatch.Frequency);
                    }
                }
                if (send is { } item) await socket.SendAsync(item.Packet, item.Endpoint, stop);
                else await Task.Delay(1, stop);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
}

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
