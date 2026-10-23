namespace NksAudioLink.Server;

// Deliberately excludes server configuration, names, endpoints, keys and PCM.
public sealed record StatusHistorySample(long Sequence, long TimestampMs,
    SessionStatus? Session, NativeOutputStatus NativeOutput);
public sealed record StatusHistoryResponse(string Epoch, long NextSequence, bool Reset,
    long ServerTimeMs, StatusHistorySample[] Samples);

/// <summary>One hour of 1 Hz monitoring in memory; allocated only when HTTP is enabled.</summary>
internal sealed class StatusHistory
{
    internal const int Capacity = 3600;
    private readonly object _gate = new();
    private readonly StatusHistorySample?[] _samples = new StatusHistorySample[Capacity];
    private readonly string _epoch = Guid.NewGuid().ToString("N");
    private long _sequence;
    private int _count;

    internal void Add(ServerStatus status, long timestampMs)
    {
        lock (_gate)
        {
            long sequence = ++_sequence;
            _samples[(int)((sequence - 1) % Capacity)] = new(sequence, timestampMs, status.Session, status.NativeOutput);
            _count = Math.Min(Capacity, _count + 1);
        }
    }

    internal StatusHistoryResponse Read(int minutes, long after, string? epoch, long nowMs)
    {
        if (minutes is not (1 or 5 or 15 or 60)) throw new ArgumentOutOfRangeException(nameof(minutes));
        if (after < 0) throw new ArgumentOutOfRangeException(nameof(after));
        lock (_gate)
        {
            // A stale browser cursor, restart or overrun requires a complete window.
            bool reset = epoch != _epoch || after > _sequence || (after > 0 && after < _sequence - _count);
            long cursor = reset ? 0 : after;
            long cutoff = nowMs - minutes * 60_000L;
            var result = new List<StatusHistorySample>(Math.Min(_count, minutes * 60));
            for (long sequence = Math.Max(_sequence - _count + 1, cursor + 1); sequence <= _sequence; sequence++)
            {
                var sample = _samples[(int)((sequence - 1) % Capacity)]!;
                if (sample.TimestampMs > cutoff) result.Add(sample);
            }
            return new(_epoch, _sequence, reset, nowMs, result.ToArray());
        }
    }
}
