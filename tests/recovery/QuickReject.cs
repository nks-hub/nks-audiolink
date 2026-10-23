using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using NksAudioLink.Client;
using NksAudioLink.Core;

internal static class QuickReject
{
    public static async Task<int> RunAsync(string[] args)
    {
        int iterations = 20;
        string report = Path.Combine("TestResults", "quick-reject.json");
        for (int i = 0; i < args.Length; i++)
        {
            string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException("Missing option value.");
            switch (args[i])
            {
                case "--iterations": iterations = int.Parse(Value()); break;
                case "--report": report = Value(); break;
                default: throw new ArgumentException($"Unknown option {args[i]}");
            }
        }
        if (iterations is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(iterations));
        var results = new List<object>();
        string? failure = null;
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var stop = new CancellationTokenSource();
        int port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
        int helloCount = 0;
        Task rejecting = RejectAsync();
        try
        {
            for (int i = 0; i < iterations; i++)
            {
                var watch = Stopwatch.StartNew();
                var states = new List<string>();
                int sources = 0;
                var client = new AudioStreamClient(new ClientConfig { Server = "127.0.0.1", Port = port }, (id, loopback) =>
                {
                    Interlocked.Increment(ref sources);
                    return new WasapiSource(id, loopback);
                });
                client.StateChanged += state => { lock (states) states.Add(state); };
                try
                {
                    // Construction/start/disposal can complete synchronously up to
                    // the first incomplete await. Bound those paths as well.
                    await Task.Run(() => client.RunAsync(null, true, stop.Token))
                        .WaitAsync(TimeSpan.FromSeconds(5));
                    throw new InvalidOperationException("Busy rejection did not propagate to the caller.");
                }
                catch (ServerRejectedException error) when (error.Reason == RejectReason.Busy) { }
                if (sources != 1) throw new InvalidOperationException("Terminal rejection retried capture.");
                results.Add(new { iteration = i + 1, elapsedMs = watch.Elapsed.TotalMilliseconds, captureInstances = sources, states });
                Console.WriteLine($"Quick Busy rejection {i + 1}/{iterations}: {watch.ElapsedMilliseconds} ms, capture disposed.");
            }
        }
        catch (Exception error) { failure = error.ToString(); }
        finally
        {
            stop.Cancel();
            try { await rejecting; }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
            {
                utc = DateTime.UtcNow, success = failure is null, failure,
                capture = "real WASAPI default render loopback",
                receiver = "owned loopback Busy responder; no audio output",
                requestedIterations = iterations, completedIterations = results.Count, helloCount, results
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Evidence: {Path.GetFullPath(report)}");
        }
        return failure is null ? 0 : 1;

        async Task RejectAsync()
        {
            while (!stop.IsCancellationRequested)
            {
                var packet = await receiver.ReceiveAsync(stop.Token);
                if (!Protocol.TryRead(packet.Buffer, [], out var header, out _) || header.Type != PacketType.Hello) continue;
                Interlocked.Increment(ref helloCount);
                byte[] response = new byte[Protocol.HeaderSize + 1];
                int length = Protocol.Write(response, new PacketHeader(PacketType.Reject, PacketFlags.None, header.Session, 0), [(byte)RejectReason.Busy]);
                await receiver.SendAsync(response.AsMemory(0, length), packet.RemoteEndPoint, stop.Token);
            }
        }
    }
}
