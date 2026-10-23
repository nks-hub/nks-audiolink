using NksAudioLink.Core;

namespace NksAudioLink.Server;

public sealed record ServerStatus(
    string Name, string Version, long UptimeSeconds, int UdpPort,
    int SampleRate, int Channels, int BitsPerSample, SessionStatus? Session);

public sealed record SessionStatus(
    uint Id, long ConnectedSeconds, double LastPacketAgeMs,
    int BufferMs, int SinkDelayMs, int RatioPpm,
    long Lost, long Late, long Underruns, long Overruns, long Duplicates,
    AudioLevelSnapshot? Levels = null);
