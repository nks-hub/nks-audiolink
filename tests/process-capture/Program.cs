using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NksAudioLink.Client;
using NksAudioLink.Core;

// Opt-in hardware test: no UDP, no routing/default-device writes, no production process control.
// The default test sink is an explicitly selected virtual CABLE Input endpoint.
if (args is ["tone", var endpoint, var frequency])
{
    using var enumerator = new MMDeviceEnumerator();
    using var device = enumerator.GetDevice(endpoint);
    using var player = new WasapiPlayerBuilder().WithDevice(device).Build();
    player.Init(new SignalGenerator(48000, 2) { Frequency = double.Parse(frequency, System.Globalization.CultureInfo.InvariantCulture), Gain = 0.01, Type = SignalGeneratorType.Sin }.ToWaveProvider());
    player.Play(); Console.WriteLine("READY");
    while (Console.ReadLine() is { } command)
    {
        if (command == "pause") { player.Stop(); Console.WriteLine("PAUSED"); }
        else break;
    }
    player.Stop(); return;
}
if (args is ["parent", var childEndpoint])
{
    using var tone = StartChild("tone", childEndpoint, "440");
    try
    {
        await Ready(tone); Console.WriteLine("READY");
        while (Console.ReadLine() is { } command)
        {
            if (command != "pause") break;
            tone.StandardInput.WriteLine("pause"); tone.StandardInput.Flush();
            if (await tone.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) != "PAUSED") throw new Exception("Tone did not pause.");
            Console.WriteLine("PAUSED");
        }
    }
    finally { await Finish(tone); }
    return;
}
if (args is not ["--virtual-cable"])
{
    Console.WriteLine("Opt-in real process capture test: dotnet run --project tests/process-capture -- --virtual-cable");
    Console.WriteLine("Uses two controlled quiet tones on the installed CABLE Input endpoint. Never changes default outputs or contacts a server.");
    return;
}
var cable = WasapiSource.ListDevices().FirstOrDefault(d => d.IsRender && d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase));
if (string.IsNullOrEmpty(cable.Id)) throw new InvalidOperationException("Install/enable CABLE Input to run this isolated test. No physical/default output fallback is allowed.");
using var resolution = new WindowsTimerResolution();
using var selected = StartChild("parent", cable.Id);
using var unrelated = StartChild("tone", cable.Id, "990");
try
{
    await Task.WhenAll(Ready(selected), Ready(unrelated));
    var target = CaptureProcess.FromId(selected.Id);
    using var source = await WasapiSource.CreateProcessAsync(target);
    var exited = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
    source.CaptureError += error => exited.TrySetResult(error);
    source.Start();
    await ReadSamples(source, TimeSpan.FromMilliseconds(300)); // warm-up
    double[] samples = await ReadSamples(source, TimeSpan.FromSeconds(2));
    double rms = Math.Sqrt(samples.Select(s => s * s).DefaultIfEmpty().Average());
    double wanted = Amplitude(samples, 440), excluded = Amplitude(samples, 990);
    Require(samples.Length >= 48000, "Captured at least one second of actual PCM");
    Require(rms > 0.001 && wanted > 0.001, "Selected parent's child 440 Hz tone is captured");
    Require(excluded < wanted / 15, "Unrelated 990 Hz process is excluded by at least 23 dB");
    selected.StandardInput.WriteLine("pause"); selected.StandardInput.Flush();
    Require(await selected.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) == "PAUSED", "Controlled child paused");
    await ReadSamples(source, TimeSpan.FromMilliseconds(500));
    double[] quiet = await ReadSamples(source, TimeSpan.FromMilliseconds(500));
    Require(quiet.Length == 0 || quiet.Max(Math.Abs) < 0.0001, "Paused target stays silent while unrelated tone continues");
    await Finish(selected);
    Require(await exited.Task.WaitAsync(TimeSpan.FromSeconds(5)) is CaptureProcessUnavailableException, "Selected process exit reports terminal identity failure");
    source.Stop();
    Console.WriteLine(JsonSerializer.Serialize(new { os = Environment.OSVersion.Version.ToString(), capturedSamples = samples.Length, rms, selected440Amplitude = wanted, excluded990Amplitude = excluded, processTree = true, identityExit = true, noDefaultRoutingChanges = true }));
}
finally { await Finish(selected); await Finish(unrelated); }

static Process StartChild(params string[] arguments)
{
    var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
    start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    foreach (string argument in arguments) start.ArgumentList.Add(argument);
    return Process.Start(start) ?? throw new IOException("Could not start controlled audio process.");
}
static async Task Ready(Process process)
{
    string? result = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
    if (result != "READY") throw new IOException("Controlled audio process did not initialize: " + await process.StandardError.ReadToEndAsync());
}
static async Task Finish(Process process)
{
    if (process.HasExited) return;
    try { process.StandardInput.WriteLine("quit"); process.StandardInput.Flush(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
    catch { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
}
static async Task<double[]> ReadSamples(IAudioSource source, TimeSpan duration)
{
    var samples = new List<double>(); byte[] frame = new byte[Protocol.PcmBytesPerFrame];
    var watch = Stopwatch.StartNew();
    while (watch.Elapsed < duration)
    {
        while (source.Queue.TryReadFrame(frame))
            for (int offset = 0; offset < frame.Length; offset += 4) samples.Add(BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(offset)) / 32768.0);
        await Task.Delay(3);
    }
    return samples.ToArray();
}
static double Amplitude(double[] samples, double frequency)
{
    double real = 0, imaginary = 0;
    for (int i = 0; i < samples.Length; i++) { double angle = 2 * Math.PI * frequency * i / 48000; real += samples[i] * Math.Cos(angle); imaginary += samples[i] * Math.Sin(angle); }
    return samples.Length == 0 ? 0 : 2 * Math.Sqrt(real * real + imaginary * imaginary) / samples.Length;
}
static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); Console.WriteLine("PASS: " + message); }
