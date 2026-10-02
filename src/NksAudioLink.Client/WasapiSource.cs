using NAudio.CoreAudioApi;
using NAudio.Wave;
using NksAudioLink.Core;

namespace NksAudioLink.Client;

public readonly record struct AudioDevice(string Id, string Name, bool IsRender);

public sealed class WasapiSource : IAudioSource
{
    private const int StandardBufferMs = 10;
    private readonly WasapiRecorder _recorder;
    private readonly MMDevice _device;
    private volatile bool _stopping;
    public AudioFrameQueue Queue { get; } = new();
    public WaveFormat Format => _recorder.WaveFormat;
    /// <summary>Actual WASAPI buffer length after Start; zero before initialization.</summary>
    public int CaptureLatencyMs => _recorder.LatencyMilliseconds;
    public bool LowLatencyActive => _recorder.LowLatencyActive;
    public string? LowLatencyUnavailableReason => _recorder.LowLatencyUnavailableReason;
    /// <summary>Raised if capture ends without Stop, including device invalidation.</summary>
    public event Action<Exception>? CaptureError;

    public WasapiSource(string? deviceId, bool loopback)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var enumerator = new MMDeviceEnumerator();
        DataFlow flow = loopback ? DataFlow.Render : DataFlow.Capture;
        _device = deviceId is null
            ? enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia)
            : enumerator.GetDevice(deviceId);
        if (_device.DataFlow != flow)
        {
            _device.Dispose();
            throw new ArgumentException("The selected device cannot be used in this mode. Choose another device.", nameof(deviceId));
        }
        var builder = new WasapiRecorderBuilder()
            .WithDevice(_device)
            .WithFormat(new WaveFormat(Protocol.SampleRate, 16, Protocol.Channels))
            .WithBufferLength(StandardBufferMs);
        if (loopback) builder.WithLoopbackCapture();
        else builder.WithLowLatency(false); // Falls back to the 10 ms standard buffer when unsupported.
        try { _recorder = builder.Build(); }
        catch { _device.Dispose(); throw; }
        if (_recorder.WaveFormat.SampleRate != Protocol.SampleRate || _recorder.WaveFormat.BitsPerSample != 16 ||
            _recorder.WaveFormat.Channels != Protocol.Channels)
        {
            string format = _recorder.WaveFormat.ToString();
            _recorder.Dispose();
            _device.Dispose();
            throw new NotSupportedException($"The audio device does not provide 48 kHz, 16-bit stereo ({format}). Choose another device.");
        }
        _recorder.DataAvailable += (buffer, _, _, _) => Queue.Write(buffer);
        _recorder.RecordingStopped += (_, stopped) =>
        {
            if (_stopping) return;
            Queue.Clear();
            CaptureError?.Invoke(stopped.Exception ?? new IOException("Audio capture stopped. Check the audio device."));
        };
    }

    public void Start()
    {
        _stopping = false;
        _recorder.StartRecording();
    }
    public void Stop()
    {
        _stopping = true;
        _recorder.StopRecording();
    }
    public void Dispose()
    {
        _stopping = true;
        try { _recorder.Dispose(); }
        finally { _device.Dispose(); }
    }

    public static IReadOnlyList<AudioDevice> ListDevices()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var enumerator = new MMDeviceEnumerator();
        var result = new List<AudioDevice>();
        foreach (DataFlow flow in new[] { DataFlow.Render, DataFlow.Capture })
        {
            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (device) result.Add(new AudioDevice(device.ID, device.FriendlyName, flow == DataFlow.Render));
            }
        }
        return result;
    }
}
