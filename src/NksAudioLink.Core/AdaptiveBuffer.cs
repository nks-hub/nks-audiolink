using System.Text.Json.Serialization;

namespace NksAudioLink.Core;

public readonly record struct AdaptiveBufferOptions(int MinimumMs, int MaximumMs, bool Predictive = false)
{
    [JsonIgnore]
    public bool IsValid => MinimumMs is >= 20 and <= 100 && MaximumMs is >= 20 and <= 100 &&
        MinimumMs <= MaximumMs && MinimumMs % Protocol.FrameMs == 0 && MaximumMs % Protocol.FrameMs == 0;
}

[JsonConverter(typeof(JsonStringEnumConverter<AdaptiveBufferReason>))]
public enum AdaptiveBufferReason : byte { Initial, Underrun, PacketLoss, LatePacket, Stable, MaximumReached, PredictiveRisk }

public readonly record struct AdaptiveBufferStatus(int TargetMs, int MinimumMs, int MaximumMs,
    AdaptiveBufferReason Reason, uint Changes, bool Predictive = false)
{
    [JsonIgnore]
    public bool IsValid => new AdaptiveBufferOptions(MinimumMs, MaximumMs).IsValid &&
        TargetMs >= MinimumMs && TargetMs <= MaximumMs && TargetMs % Protocol.FrameMs == 0 && Enum.IsDefined(Reason) &&
        (Predictive || Reason != AdaptiveBufferReason.PredictiveRisk);
}

/// <summary>Frame-clock policy with asymmetric predictive hysteresis and a learned failure hold.</summary>
internal sealed class AdaptiveBufferController
{
    private readonly int _initialTarget;
    private readonly AdaptiveBufferOptions _options;
    private readonly PredictiveJitterEstimator? _predictor;
    private long _frames, _lastIncrease = -400, _stableFrames;
    private long _underruns, _lost, _late;
    private long _predictionGeneration = -1;
    private int _riskStreak;
    private bool _playing;
    private bool _hasPlayed;
    private long _uniqueLatePackets;
    private int _failureHoldFrames;
    public AdaptiveBufferStatus Status { get; private set; }

    public AdaptiveBufferController(int targetMs, AdaptiveBufferOptions options)
    {
        if (!options.IsValid || targetMs < options.MinimumMs || targetMs > options.MaximumMs || targetMs % Protocol.FrameMs != 0)
            throw new ArgumentOutOfRangeException(nameof(options));
        _initialTarget = targetMs;
        _options = options;
        _predictor = options.Predictive ? new PredictiveJitterEstimator() : null;
        Status = new(targetMs, options.MinimumMs, options.MaximumMs, AdaptiveBufferReason.Initial, 0, options.Predictive);
    }

    public void ObserveArrival(ulong index, long tick, bool late)
    {
        if (_predictor?.Observe(index, tick) == true && late) _uniqueLatePackets++;
    }

    public PredictiveBufferStatus? GetPrediction()
    {
        if (_predictor is not { } p) return null;
        string state = !p.WarmedUp ? "WarmingUp" : !_playing ? "WaitingForAudio" :
            Status.TargetMs == _options.MaximumMs && p.EstimatedNeedMs >= Status.TargetMs ? "AtMaximum" :
            _frames - _lastIncrease < 400 ? "Cooldown" : _riskStreak > 0 ? "RiskPending" :
            _failureHoldFrames > 0 ? "FailureHold" :
            p.EstimatedNeedMs <= Status.TargetMs - 10 ? "Settling" : "WithinDeadband";
        return new(Math.Round(p.EstimatedNeedMs, 2), Math.Round(p.JitterSpreadMs, 2), Math.Round(p.TrendMarginMs, 2),
            p.Samples, p.WarmedUp, state, _riskStreak, (int)(_stableFrames * Protocol.FrameMs),
            p.DuplicatesIgnored, p.ReorderedPackets, p.OutdatedPacketsIgnored, p.Discontinuities);
    }

    public bool Observe(long underruns, long lost, long late, bool playedFullFrame)
    {
        _frames++;
        _playing = playedFullFrame;
        if (_predictor is not null)
        {
            late = _uniqueLatePackets;
            if (!_hasPlayed)
            {
                (_underruns, _lost, _late) = (underruns, lost, late);
                _hasPlayed = playedFullFrame;
                return false;
            }
        }
        AdaptiveBufferReason? reason = underruns > _underruns ? AdaptiveBufferReason.Underrun :
            lost > _lost ? AdaptiveBufferReason.PacketLoss : late > _late ? AdaptiveBufferReason.LatePacket : null;
        (_underruns, _lost, _late) = (underruns, lost, late);
        if (reason is not null)
        {
            _stableFrames = 0;
            if (_predictor is not null) _failureHoldFrames = 24000;
            if (Status.TargetMs == _options.MaximumMs)
                Status = Status with { Reason = AdaptiveBufferReason.MaximumReached };
            else if (_frames - _lastIncrease >= 400)
            {
                _lastIncrease = _frames;
                Status = Status with { TargetMs = Math.Min(_options.MaximumMs, Status.TargetMs + 10), Reason = reason.Value, Changes = Status.Changes + 1 };
                return true;
            }
        }
        else if (_predictor is { } predictor)
        {
            if (!predictor.WarmedUp) { _stableFrames = 0; _riskStreak = 0; return false; }
            // Rare delayed packets are deliberately absent from P98. After a real failure,
            // remember their tail for two clean minutes rather than repeatedly relearning it.
            if (_failureHoldFrames > 0)
            {
                if (predictor.TailNeedMs > Status.TargetMs - 10) _failureHoldFrames = 24000;
                else if (playedFullFrame) _failureHoldFrames--;
            }
            if (_predictionGeneration != predictor.Generation)
            {
                _predictionGeneration = predictor.Generation;
                _riskStreak = predictor.EstimatedNeedMs >= Status.TargetMs + 5 ? Math.Min(3, _riskStreak + 1) : 0;
            }
            if (_riskStreak >= 3 && _frames - _lastIncrease >= 400 && Status.TargetMs < _options.MaximumMs)
            {
                _lastIncrease = _frames;
                _stableFrames = 0;
                _riskStreak = 0;
                Status = Status with { TargetMs = Math.Min(_options.MaximumMs, Status.TargetMs + 10), Reason = AdaptiveBufferReason.PredictiveRisk, Changes = Status.Changes + 1 };
                return true;
            }
            if (playedFullFrame && predictor.EstimatedNeedMs <= Status.TargetMs - 10)
            {
                _stableFrames = Math.Min(6000, _stableFrames + 1);
                if (_stableFrames >= 6000 && _failureHoldFrames == 0 && Status.TargetMs > _options.MinimumMs)
                {
                    _stableFrames = 0;
                    Status = Status with { TargetMs = Status.TargetMs - 5, Reason = AdaptiveBufferReason.Stable, Changes = Status.Changes + 1 };
                    return true;
                }
            }
            else _stableFrames = 0;
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
        _predictionGeneration = -1;
        _riskStreak = 0;
        _playing = false;
        _hasPlayed = false;
        _uniqueLatePackets = 0;
        _failureHoldFrames = 0;
        _predictor?.Reset();
        (_underruns, _lost, _late) = (underruns, lost, late);
        Status = new(_initialTarget, _options.MinimumMs, _options.MaximumMs, AdaptiveBufferReason.Initial, 0, _options.Predictive);
    }
}
