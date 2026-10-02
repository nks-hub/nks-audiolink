namespace NksAudioLink.Client;

/// <summary>Capture boundary consumed by the network sender.</summary>
public interface IAudioSource : IDisposable
{
    AudioFrameQueue Queue { get; }
    int CaptureLatencyMs { get; }
    event Action<Exception>? CaptureError;
    void Start();
    void Stop();
}
