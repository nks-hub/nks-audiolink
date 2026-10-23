using NAudio.CoreAudioApi;
using NAudio.Wave;
using NksAudioLink.Core;

namespace NksAudioLink.Client;

public readonly record struct AudioDevice(string Id, string Name, bool IsRender);

public sealed class WasapiSource : IAudioSource
{
    private const int StandardBufferMs = 10;
    private readonly WasapiRecorder _recorder;
    private readonly MMDevice? _device;
    private readonly System.Diagnostics.Process? _process;
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
        AttachRecorder();
    }

    private WasapiSource(WasapiRecorder recorder, System.Diagnostics.Process process)
    {
        _recorder = recorder;
        _process = process;
        AttachRecorder();
        _process.Exited += ProcessExited;
        _process.EnableRaisingEvents = true;
    }

    public static async Task<IAudioSource> CreateProcessAsync(CaptureProcess target, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CaptureProcess.PlatformMaySupportCapture) throw new PlatformNotSupportedException(CaptureProcess.UnsupportedMessage);
        var process = target.OpenVerified();
        WasapiRecorder? recorder = null;
        try
        {
            // NAudio wraps ActivateAudioInterfaceAsync with INCLUDE_TARGET_PROCESS_TREE.
            // Run activation away from the WPF synchronization context. Updated Windows 10
            // builds below the original SDK's 20348 requirement can support this API too;
            // the activation result is the capability check, not the marketing OS version.
            var pending = Task.Run(() => new WasapiRecorderBuilder()
                .WithProcessLoopback((uint)target.Id, ProcessLoopbackMode.IncludeTargetProcessTree)
                .WithFormat(new WaveFormat(Protocol.SampleRate, 16, Protocol.Channels))
                .WithBufferLength(StandardBufferMs).BuildAsync());
            try { recorder = await pending.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false); }
            catch
            {
                _ = pending.ContinueWith(task =>
                {
                    if (task.IsCompletedSuccessfully) task.Result.Dispose();
                    else _ = task.Exception;
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw;
            }
            if (process.HasExited) throw new CaptureProcessUnavailableException("The selected application exited during capture initialization. Select it again.");
            return new WasapiSource(recorder, process);
        }
        catch (Exception error)
        {
            recorder?.Dispose();
            process.Dispose();
            if (error is System.Runtime.InteropServices.COMException)
                throw new NotSupportedException($"{CaptureProcess.UnsupportedMessage} Windows activation failed (0x{error.HResult:X8}).", error);
            throw;
        }
    }

    private void AttachRecorder()
    {
        _recorder.DataAvailable += (buffer, _, _, _) => Queue.Write(buffer);
        _recorder.RecordingStopped += (_, stopped) =>
        {
            if (_stopping) return;
            Queue.Clear();
            CaptureError?.Invoke(stopped.Exception ?? new IOException("Audio capture stopped. Check the audio device."));
        };
    }

    private void ProcessExited(object? sender, EventArgs e)
    {
        if (_stopping) return;
        Queue.Clear();
        CaptureError?.Invoke(new CaptureProcessUnavailableException("The selected application exited. Refresh and select its new process before reconnecting."));
    }

    public void Start()
    {
        if (_process?.HasExited == true) throw new CaptureProcessUnavailableException("The selected application exited. Refresh and select it again.");
        _stopping = false;
        _recorder.StartRecording();
        // NAudio 3.1's capture thread changes Starting to Capturing after its
        // native Start call. A quick rejection/cancellation must not call Stop
        // before that transition: Capturing would overwrite Stopping and make
        // Dispose wait forever for a capture thread that never received Stop.
        SpinWait.SpinUntil(() => _recorder.CaptureState != CaptureState.Starting);
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
        finally
        {
            if (_process is not null) { _process.Exited -= ProcessExited; _process.Dispose(); }
            _device?.Dispose();
        }
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
