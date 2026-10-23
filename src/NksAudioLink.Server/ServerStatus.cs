using NksAudioLink.Core;

namespace NksAudioLink.Server;

public sealed record ServerStatus(
    string Name, string Version, long UptimeSeconds, int UdpPort,
    int SampleRate, int Channels, int BitsPerSample, SessionStatus? Session,
    NativeOutputStatus NativeOutput = default);

public sealed record SessionStatus(
    uint Id, long ConnectedSeconds, double LastPacketAgeMs,
    int BufferMs, int SinkDelayMs, int RatioPpm,
    long Lost, long Late, long Underruns, long Overruns, long Duplicates,
    AudioLevelSnapshot? Levels = null, NativeOutputStatus? NativeOutput = null);

/// <summary>ALSA events, independent of packet loss and jitter-buffer underruns.</summary>
public readonly record struct NativeOutputStatus(
    long WriteErrors, long Recoveries, long Failures, long Underruns, long Suspensions)
{
    public static NativeOutputStatus operator +(NativeOutputStatus a, NativeOutputStatus b) =>
        new(a.WriteErrors + b.WriteErrors, a.Recoveries + b.Recoveries, a.Failures + b.Failures,
            a.Underruns + b.Underruns, a.Suspensions + b.Suspensions);
    public static NativeOutputStatus operator -(NativeOutputStatus a, NativeOutputStatus b) =>
        new(a.WriteErrors - b.WriteErrors, a.Recoveries - b.Recoveries, a.Failures - b.Failures,
            a.Underruns - b.Underruns, a.Suspensions - b.Suspensions);
}

public interface INativeOutputDiagnostics
{
    NativeOutputStatus GetNativeOutputStatus();
}
