using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using NksAudioLink.Client;
using NksAudioLink.Core;
using NksAudioLink.Server;

// Isolated validation only: no NIC, power-state, default-device or existing-process changes.
if (args.FirstOrDefault() == "quick-reject")
    return await QuickReject.RunAsync(args.Skip(1).ToArray());

if (args.Contains("--help"))
{
    Console.WriteLine("dotnet run --project tests/recovery/RecoveryHarness.csproj -- [options]");
    Console.WriteLine("Default: real WASAPI loopback + owned local null-output receiver.");
    Console.WriteLine("--synthetic       Use silent synthetic capture instead of an audio device.");
    Console.WriteLine("--capture         Capture the input endpoint instead of loopback.");
    Console.WriteLine("--device ID       Select an explicit WASAPI endpoint.");
    Console.WriteLine("--server HOST --port PORT   Use an explicitly selected external receiver (never restarted).");
    Console.WriteLine("--config FILE    Client config, including the external receiver and optional shared key.");
    Console.WriteLine("--report FILE    JSON evidence path (default: TestResults/recovery-UTC.json).");
    Console.WriteLine("Runs a ten-second proxy outage and 200ms sender stall; restarts only an owned local receiver.");
    Console.WriteLine("quick-reject [--iterations N] [--report FILE]  Repeated real WASAPI startup and immediate Busy rejection.");
    return 0;
}

bool synthetic = false, loopback = true, external = false;
string? deviceId = null;
string reportPath = Path.Combine("TestResults", $"recovery-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
var config = new ClientConfig();
var stages = new List<object>();
var states = new ConcurrentQueue<object>();
var sources = new ConcurrentQueue<PausedSource>();
bool success = false;
string? failure = null;
CancellationTokenSource? receiverStop = null;
Task? receiver = null;
Task? clientTask = null;
using var runStop = new CancellationTokenSource(TimeSpan.FromSeconds(90));
UdpOutageProxy? proxy = null;
try
{
    int configIndex = Array.LastIndexOf(args, "--config");
    if (configIndex >= 0)
    {
        if (configIndex + 1 >= args.Length) throw new ArgumentException("Missing value after --config.");
        config = ClientConfig.Load(args[configIndex + 1]);
        external = true;
    }
    for (int i = 0; i < args.Length; i++)
    {
        string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value after {args[i]}.");
        switch (args[i])
        {
            case "--synthetic": synthetic = true; break;
            case "--capture": loopback = false; break;
            case "--device": deviceId = Value(); break;
            case "--server": config = config with { Server = Value() }; external = true; break;
            case "--port": config = config with { Port = int.Parse(Value()) }; break;
            case "--config": _ = Value(); break;
            case "--report": reportPath = Value(); break;
            default: throw new ArgumentException($"Unknown option: {args[i]}");
        }
    }
    if (!external && args.Contains("--port")) throw new ArgumentException("--port requires --server or --config; the owned local receiver uses a free port.");
    if (!external) config = config with { Server = "127.0.0.1", Port = FreePort() };
    config.Validate();
    if (!external) StartReceiver();
    byte[] key = string.IsNullOrEmpty(config.PskBase64) ? [] : Convert.FromBase64String(config.PskBase64);
    IPAddress address = (await Dns.GetHostAddressesAsync(config.Server, runStop.Token))
        .FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork)
        ?? throw new NotSupportedException("The test receiver must have an IPv4 address.");
    proxy = new UdpOutageProxy(new IPEndPoint(address, config.Port), key);
    var client = new AudioStreamClient(config with { Server = "127.0.0.1", Port = proxy.Port }, (id, captureLoopback) =>
    {
        var source = new PausedSource(synthetic ? new SilentSource() : new WasapiSource(id, captureLoopback));
        sources.Enqueue(source);
        return source;
    });
    client.StateChanged += state =>
    {
        states.Enqueue(new { utc = DateTime.UtcNow, state });
        Console.WriteLine($"{DateTime.UtcNow:O} {state}");
    };
    clientTask = client.RunAsync(deviceId, loopback, runStop.Token);
    await WaitFor(() => proxy.AcceptedStats > 0 && proxy.AudioForwarded >= 20);
    uint session = proxy.LastAcceptedSession;
    stages.Add(new { name = "initial_stream", session, captureInstances = sources.Count });

    proxy.DropTraffic = true;
    var outage = Stopwatch.StartNew();
    Console.WriteLine("Dropping only this harness's UDP traffic for ten seconds.");
    // Allow an already-issued UDP send to settle before checking the closed gate.
    await Task.Delay(100, runStop.Token);
    int beforeStats = proxy.AcceptedStats;
    // Delay can truncate fractional milliseconds; keep the gate closed for at
    // least the full ten seconds measured by the monotonic clock.
    while (outage.Elapsed < TimeSpan.FromSeconds(10))
        await Task.Delay(10, runStop.Token);
    if (proxy.AcceptedStats != beforeStats) throw new InvalidOperationException("A server response crossed the simulated outage.");
    long audioBeforeRecovery = proxy.AudioForwarded;
    outage.Stop();
    proxy.DropTraffic = false;
    var recovery = Stopwatch.StartNew();
    await WaitFor(() => proxy.AcceptedStats > beforeStats && proxy.AudioForwarded >= audioBeforeRecovery + 20);
    RequireSameCaptureAndSession(session);
    stages.Add(new { name = "simulated_udp_outage", outageMilliseconds = outage.ElapsedMilliseconds,
        recoveryMilliseconds = recovery.ElapsedMilliseconds, session, droppedPackets = proxy.DroppedPackets, captureInstances = sources.Count });

    if (!external)
    {
        await StopReceiver();
        Console.WriteLine("Restarting only the receiver created by this harness.");
        await Task.Delay(TimeSpan.FromSeconds(1), runStop.Token);
        beforeStats = proxy.AcceptedStats;
        audioBeforeRecovery = proxy.AudioForwarded;
        StartReceiver();
        recovery.Restart();
        await WaitFor(() => proxy.AcceptedStats > beforeStats && proxy.AudioForwarded >= audioBeforeRecovery + 20);
        RequireSameCaptureAndSession(session);
        stages.Add(new { name = "owned_receiver_restart", recoveryMilliseconds = recovery.ElapsedMilliseconds, session, captureInstances = sources.Count });
    }

    var currentSource = sources.Last();
    currentSource.PauseNextFrame();
    await WaitFor(() => currentSource.PauseMilliseconds > 50);
    audioBeforeRecovery = proxy.AudioForwarded;
    await WaitFor(() => proxy.LastHelloForwardedTick >= currentSource.PauseCompletedTick &&
        proxy.LastAcceptedStatsTick >= currentSource.PauseCompletedTick &&
        proxy.AudioForwarded >= audioBeforeRecovery + 20);
    RequireSameCaptureAndSession(session);
    stages.Add(new { name = "simulated_sender_scheduler_pause", pauseMilliseconds = currentSource.PauseMilliseconds,
        recoveryMilliseconds = Stopwatch.GetElapsedTime(currentSource.PauseCompletedTick).TotalMilliseconds,
        session, captureInstances = sources.Count });
    success = true;

    void RequireSameCaptureAndSession(uint expected)
    {
        if (sources.Count != 1 || proxy.LastAcceptedSession != expected)
            throw new InvalidOperationException("Transport recovery unexpectedly recreated capture or changed the session.");
    }

    async Task WaitFor(Func<bool> ready)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(runStop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!ready())
        {
            if (clientTask?.IsCompleted == true) { await clientTask; throw new InvalidOperationException("The client stopped during recovery validation."); }
            if (receiver?.IsCompleted == true) { await receiver; throw new InvalidOperationException("The test receiver stopped unexpectedly."); }
            await Task.Delay(10, timeout.Token);
        }
    }
}
catch (Exception error)
{
    failure = error.ToString();
    Console.Error.WriteLine(error.Message);
}
finally
{
    runStop.Cancel();
    if (clientTask is not null)
    {
        try { await clientTask.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception error) { success = false; failure ??= error.ToString(); }
    }
    if (proxy is not null)
    {
        try { await proxy.DisposeAsync(); }
        catch (Exception error) { success = false; failure ??= error.ToString(); }
    }
    try { await StopReceiver(); }
    catch (Exception error) { success = false; failure ??= error.ToString(); }
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
    await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
    {
        utc = DateTime.UtcNow, success, failure,
        capture = synthetic ? "synthetic silent source" : loopback ? "real WASAPI loopback" : "real WASAPI capture",
        receiver = external ? "explicit external receiver; restart not attempted" : "owned local production receiver, null sink",
        recoveryCriterion = "accepted STATS and at least twenty forwarded audio frames within five seconds",
        physicalNetworkDisconnectTested = false, pcSleepResumeTested = false, physicalPlaybackVerified = false,
        stages, states, captureInstances = sources.Count,
        forwardedAudioFrames = proxy?.AudioForwarded ?? 0,
        nonSilentAudioFrames = proxy?.NonSilentAudioForwarded ?? 0,
        nonZeroPcmFrames = proxy?.NonZeroPcmFrames ?? 0
    }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Evidence: {Path.GetFullPath(reportPath)}");
}
return success ? 0 : 1;

void StartReceiver()
{
    receiverStop = CancellationTokenSource.CreateLinkedTokenSource(runStop.Token);
    receiver = new AudioServer(new ServerConfig { Port = config.Port, Sink = "null" }).RunAsync(receiverStop.Token);
}

async Task StopReceiver()
{
    if (receiverStop is null || receiver is null) return;
    receiverStop.Cancel();
    try { await receiver.WaitAsync(TimeSpan.FromSeconds(5)); }
    catch (OperationCanceledException) when (receiverStop.IsCancellationRequested) { }
    finally { receiverStop.Dispose(); receiverStop = null; receiver = null; }
}

static int FreePort()
{
    using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
}

sealed class SilentSource : IAudioSource
{
    public AudioFrameQueue Queue { get; } = new();
    public int CaptureLatencyMs => 0;
    public event Action<Exception>? CaptureError { add { } remove { } }
    public void Start() { }
    public void Stop() { }
    public void Dispose() { }
}

sealed class PausedSource(IAudioSource inner) : IAudioSource
{
    private int _pausePending;
    private long _pauseMilliseconds;
    private long _pauseCompletedTick;
    public long PauseMilliseconds => Volatile.Read(ref _pauseMilliseconds);
    public long PauseCompletedTick => Volatile.Read(ref _pauseCompletedTick);
    public void PauseNextFrame() => Volatile.Write(ref _pausePending, 1);
    public AudioFrameQueue Queue
    {
        get
        {
            if (Thread.CurrentThread.Name == "NKS AudioLink sender" && Interlocked.Exchange(ref _pausePending, 0) == 1)
            {
                var watch = Stopwatch.StartNew();
                Thread.Sleep(200);
                Volatile.Write(ref _pauseCompletedTick, Stopwatch.GetTimestamp());
                Volatile.Write(ref _pauseMilliseconds, watch.ElapsedMilliseconds);
            }
            return inner.Queue;
        }
    }
    public int CaptureLatencyMs => inner.CaptureLatencyMs;
    public event Action<Exception>? CaptureError { add => inner.CaptureError += value; remove => inner.CaptureError -= value; }
    public void Start() => inner.Start();
    public void Stop() => inner.Stop();
    public void Dispose() => inner.Dispose();
}

sealed class UdpOutageProxy : IAsyncDisposable
{
    private readonly UdpClient _local = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly UdpClient _upstream = new(AddressFamily.InterNetwork);
    private readonly CancellationTokenSource _stop = new();
    private readonly byte[] _key;
    private readonly Task _outgoing, _incoming;
    private IPEndPoint? _client;
    private int _dropTraffic, _acceptedStats;
    private uint _lastAcceptedSession;
    private long _audioForwarded, _nonSilentAudioForwarded, _nonZeroPcmFrames, _helloForwarded, _droppedPackets;
    private long _lastHelloForwardedTick, _lastAcceptedStatsTick;
    public int Port => ((IPEndPoint)_local.Client.LocalEndPoint!).Port;
    public int AcceptedStats => Volatile.Read(ref _acceptedStats);
    public uint LastAcceptedSession => Volatile.Read(ref _lastAcceptedSession);
    public long AudioForwarded => Interlocked.Read(ref _audioForwarded);
    public long NonSilentAudioForwarded => Interlocked.Read(ref _nonSilentAudioForwarded);
    public long NonZeroPcmFrames => Interlocked.Read(ref _nonZeroPcmFrames);
    public long HelloForwarded => Interlocked.Read(ref _helloForwarded);
    public long LastHelloForwardedTick => Interlocked.Read(ref _lastHelloForwardedTick);
    public long LastAcceptedStatsTick => Interlocked.Read(ref _lastAcceptedStatsTick);
    public long DroppedPackets => Interlocked.Read(ref _droppedPackets);
    public bool DropTraffic { set => Volatile.Write(ref _dropTraffic, value ? 1 : 0); }

    public UdpOutageProxy(IPEndPoint target, byte[] key)
    {
        _key = key;
        _upstream.Connect(target);
        _outgoing = RelayAsync(true);
        _incoming = RelayAsync(false);
    }

    private async Task RelayAsync(bool outgoing)
    {
        UdpClient source = outgoing ? _local : _upstream;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    var packet = await source.ReceiveAsync(_stop.Token);
                    if (outgoing)
                    {
                        Interlocked.CompareExchange(ref _client, packet.RemoteEndPoint, null);
                        if (!Volatile.Read(ref _client)!.Equals(packet.RemoteEndPoint)) continue;
                    }
                    if (Volatile.Read(ref _dropTraffic) != 0) { Interlocked.Increment(ref _droppedPackets); continue; }
                    if (outgoing) await _upstream.SendAsync(packet.Buffer, _stop.Token);
                    else if (Volatile.Read(ref _client) is { } client) await _local.SendAsync(packet.Buffer, client, _stop.Token);
                    if (!Protocol.TryRead(packet.Buffer, _key, out var header, out var body)) continue;
                    if (outgoing && header.Type == PacketType.Audio)
                    {
                        Interlocked.Increment(ref _audioForwarded);
                        if (!header.Flags.HasFlag(PacketFlags.Silent)) Interlocked.Increment(ref _nonSilentAudioForwarded);
                        if (AudioMessage.TryRead(body, header.Flags.HasFlag(PacketFlags.Silent), out _, out var pcm) && pcm.IndexOfAnyExcept((byte)0) >= 0)
                            Interlocked.Increment(ref _nonZeroPcmFrames);
                    }
                    if (outgoing && header.Type == PacketType.Hello)
                    {
                        Interlocked.Exchange(ref _lastHelloForwardedTick, Stopwatch.GetTimestamp());
                        Interlocked.Increment(ref _helloForwarded);
                    }
                    if (!outgoing && header.Type == PacketType.Stats && StatsMessage.TryRead(body, out var stats) && stats.Accepted)
                    {
                        Volatile.Write(ref _lastAcceptedSession, header.Session);
                        Interlocked.Exchange(ref _lastAcceptedStatsTick, Stopwatch.GetTimestamp());
                        Interlocked.Increment(ref _acceptedStats);
                    }
                }
                catch (SocketException) { await Task.Delay(10, _stop.Token); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { await Task.WhenAll(_outgoing, _incoming); }
        finally { _local.Dispose(); _upstream.Dispose(); _stop.Dispose(); }
    }
}
