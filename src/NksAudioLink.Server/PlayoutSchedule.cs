namespace NksAudioLink.Server;

/// <summary>
/// Keeps the output sample clock across short scheduler delays. Long pauses retain
/// at most four due frames (20 ms), so recovery cannot replay an unbounded backlog.
/// </summary>
internal sealed class PlayoutSchedule
{
    public const int MaximumCatchUpFrames = 4;
    private readonly long _periodTicks;

    public PlayoutSchedule(long startTick, long periodTicks)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(periodTicks);
        _periodTicks = periodTicks;
        Deadline = startTick + periodTicks;
    }

    public long Deadline { get; private set; }

    public long LimitCatchUp(long now)
    {
        if (now < Deadline) return 0;
        long dueFrames = (now - Deadline) / _periodTicks + 1;
        long discarded = Math.Max(0, dueFrames - MaximumCatchUpFrames);
        Deadline += discarded * _periodTicks;
        return discarded;
    }

    public void Advance() => Deadline += _periodTicks;
}
