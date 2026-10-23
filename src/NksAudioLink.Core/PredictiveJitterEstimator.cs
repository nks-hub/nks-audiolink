using System.Diagnostics;

namespace NksAudioLink.Core;

public readonly record struct PredictiveBufferStatus(double EstimatedNeedMs, double JitterSpreadMs,
    double TrendMarginMs, int Samples, bool WarmedUp, string State, int RiskStreak,
    int StableForMs, long DuplicatesIgnored, long ReorderedPackets, long OutdatedPacketsIgnored, long Discontinuities);

/// <summary>Bounded robust transit-delay estimator; timestamps use Stopwatch ticks.</summary>
internal sealed class PredictiveJitterEstimator
{
    private const int Capacity = 512;
    private readonly double[] _transit = new double[Capacity];
    private readonly double[] _ordered = new double[Capacity];
    private readonly ulong[] _seen = new ulong[Capacity];
    private readonly bool[] _seenPresent = new bool[Capacity];
    private int _count, _position;
    private bool _started, _estimated;
    private ulong _originIndex, _lastIndex;
    private long _originTick, _lastTick, _assessmentTick;
    private double _slowSpread;

    public double EstimatedNeedMs { get; private set; }
    public double JitterSpreadMs { get; private set; }
    public double TrendMarginMs { get; private set; }
    // Tail evidence only prolongs protection learned from a real failure; it never raises a target alone.
    public double TailNeedMs { get; private set; }
    public int Samples => _count;
    public bool WarmedUp { get; private set; }
    public long Generation { get; private set; }
    public long DuplicatesIgnored { get; private set; }
    public long ReorderedPackets { get; private set; }
    public long OutdatedPacketsIgnored { get; private set; }
    public long Discontinuities { get; private set; }

    public bool Observe(ulong index, long tick)
    {
        int seenSlot = (int)((index / Protocol.SamplesPerFrame) % Capacity);
        if (_started)
        {
            if (_seenPresent[seenSlot] && _seen[seenSlot] == index) { DuplicatesIgnored++; return false; }
            if (index < _lastIndex && _lastIndex - index >= Capacity * Protocol.SamplesPerFrame)
            { OutdatedPacketsIgnored++; return false; }
            if (index < _lastIndex) ReorderedPackets++;
            double arrivalGap = (tick - _lastTick) * 1000.0 / Stopwatch.Frequency;
            double sourceGap = index > _lastIndex ? (index - _lastIndex) * 1000.0 / Protocol.SampleRate : 0;
            if (arrivalGap < 0 || arrivalGap > 250 || sourceGap > 250)
            {
                ClearWindow();
                Discontinuities++;
            }
        }
        if (!_started)
        {
            _originIndex = index;
            _lastIndex = index;
            _originTick = _assessmentTick = tick;
            _started = true;
        }
        _lastIndex = Math.Max(index, _lastIndex);
        _lastTick = tick;
        _seenPresent[seenSlot] = true;
        _seen[seenSlot] = index;
        double sourceOffset = index >= _originIndex ? (double)(index - _originIndex) : -(double)(_originIndex - index);
        _transit[_position] = (tick - _originTick) * 1000.0 / Stopwatch.Frequency -
            sourceOffset * 1000.0 / Protocol.SampleRate;
        _position = (_position + 1) % Capacity;
        _count = Math.Min(Capacity, _count + 1);
        if ((tick - _assessmentTick) * 1000.0 / Stopwatch.Frequency < 100) return true;
        _assessmentTick = tick;
        Generation++;
        WarmedUp = _count >= 100 && (tick - _originTick) * 1000.0 / Stopwatch.Frequency >= 500;
        if (!WarmedUp) return true;
        _transit.AsSpan(0, _count).CopyTo(_ordered);
        _ordered.AsSpan(0, _count).Sort();
        JitterSpreadMs = Math.Max(0, _ordered[(int)((_count - 1) * .98)] - _ordered[(int)((_count - 1) * .10)]);
        TailNeedMs = 15 + Math.Max(0, _ordered[_count - 1] - _ordered[(int)((_count - 1) * .10)]);
        if (!_estimated) _slowSpread = JitterSpreadMs;
        TrendMarginMs = Math.Clamp(2 * (JitterSpreadMs - _slowSpread), 0, 10);
        _slowSpread += .1 * (JitterSpreadMs - _slowSpread);
        double needed = Math.Min(5000, 15 + JitterSpreadMs + TrendMarginMs);
        if (!_estimated) EstimatedNeedMs = needed;
        else EstimatedNeedMs += (needed > EstimatedNeedMs ? .5 : .05) * (needed - EstimatedNeedMs);
        _estimated = true;
        return true;
    }

    private void ClearWindow()
    {
        _count = _position = 0;
        _seenPresent.AsSpan().Clear();
        _started = _estimated = WarmedUp = false;
        EstimatedNeedMs = JitterSpreadMs = TrendMarginMs = TailNeedMs = _slowSpread = 0;
        Generation++;
    }

    public void Reset()
    {
        ClearWindow();
        DuplicatesIgnored = ReorderedPackets = OutdatedPacketsIgnored = Discontinuities = 0;
    }
}
