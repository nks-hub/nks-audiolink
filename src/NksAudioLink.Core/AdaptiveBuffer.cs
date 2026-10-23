using System.Text.Json.Serialization;

namespace NksAudioLink.Core;

public readonly record struct AdaptiveBufferOptions(int MinimumMs, int MaximumMs)
{
    [JsonIgnore]
    public bool IsValid => MinimumMs is >= 20 and <= 100 && MaximumMs is >= 20 and <= 100 &&
        MinimumMs <= MaximumMs && MinimumMs % Protocol.FrameMs == 0 && MaximumMs % Protocol.FrameMs == 0;
}

[JsonConverter(typeof(JsonStringEnumConverter<AdaptiveBufferReason>))]
public enum AdaptiveBufferReason : byte { Initial, Underrun, PacketLoss, LatePacket, Stable, MaximumReached }

public readonly record struct AdaptiveBufferStatus(int TargetMs, int MinimumMs, int MaximumMs,
    AdaptiveBufferReason Reason, uint Changes)
{
    [JsonIgnore]
    public bool IsValid => new AdaptiveBufferOptions(MinimumMs, MaximumMs).IsValid &&
        TargetMs >= MinimumMs && TargetMs <= MaximumMs && TargetMs % Protocol.FrameMs == 0 && Enum.IsDefined(Reason);
}

/// <summary>Frame-clock policy: quick bounded increases and a 30-second clean-playback decrease.</summary>
internal sealed class AdaptiveBufferController
{
    private readonly int _initialTarget;
    private readonly AdaptiveBufferOptions _options;
    private long _frames, _lastIncrease = -400, _stableFrames;
    private long _underruns, _lost, _late;
    public AdaptiveBufferStatus Status { get; private set; }

    public AdaptiveBufferController(int targetMs, AdaptiveBufferOptions options)
    {
        if (!options.IsValid || targetMs < options.MinimumMs || targetMs > options.MaximumMs || targetMs % Protocol.FrameMs != 0)
            throw new ArgumentOutOfRangeException(nameof(options));
        _initialTarget = targetMs;
        _options = options;
        Status = new(targetMs, options.MinimumMs, options.MaximumMs, AdaptiveBufferReason.Initial, 0);
    }

    public bool Observe(long underruns, long lost, long late, bool playedFullFrame)
    {
        _frames++;
        AdaptiveBufferReason? reason = underruns > _underruns ? AdaptiveBufferReason.Underrun :
            lost > _lost ? AdaptiveBufferReason.PacketLoss : late > _late ? AdaptiveBufferReason.LatePacket : null;
        (_underruns, _lost, _late) = (underruns, lost, late);
        if (reason is not null)
        {
            _stableFrames = 0;
            if (Status.TargetMs == _options.MaximumMs)
                Status = Status with { Reason = AdaptiveBufferReason.MaximumReached };
            else if (_frames - _lastIncrease >= 400)
            {
                _lastIncrease = _frames;
                Status = Status with { TargetMs = Math.Min(_options.MaximumMs, Status.TargetMs + 10), Reason = reason.Value, Changes = Status.Changes + 1 };
                return true;
            }
        }
        else if (playedFullFrame)
        {
            if (++_stableFrames >= 6000 && Status.TargetMs > _options.MinimumMs)
            {
                _stableFrames = 0;
                Status = Status with { TargetMs = Status.TargetMs - 5, Reason = AdaptiveBufferReason.Stable, Changes = Status.Changes + 1 };
                return true;
            }
        }
        else _stableFrames = 0;
        return false;
    }

    public void Reset(long underruns, long lost, long late)
    {
        _frames = _stableFrames = 0;
        _lastIncrease = -400;
        (_underruns, _lost, _late) = (underruns, lost, late);
        Status = new(_initialTarget, _options.MinimumMs, _options.MaximumMs, AdaptiveBufferReason.Initial, 0);
    }
}
