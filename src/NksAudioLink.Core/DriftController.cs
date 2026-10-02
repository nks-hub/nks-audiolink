namespace NksAudioLink.Core;

/// <summary>PI feedback that adjusts source samples consumed per output sample.</summary>
public sealed class DriftController
{
    private readonly double _targetMs;
    private double _filteredMs;
    private double _integralPpm;
    private bool _initialized;

    public DriftController(double targetMs)
    {
        if (targetMs is < 5 or > 200) throw new ArgumentOutOfRangeException(nameof(targetMs));
        _targetMs = targetMs;
    }

    public double Ratio { get; private set; } = 1;
    public int RatioPpm => (int)Math.Round((Ratio - 1) * 1_000_000);

    public double Update(double bufferedMs)
    {
        if (!double.IsFinite(bufferedMs) || bufferedMs < 0) throw new ArgumentOutOfRangeException(nameof(bufferedMs));
        if (!_initialized) { _filteredMs = bufferedMs; _initialized = true; }
        else _filteredMs += 0.02 * (bufferedMs - _filteredMs);

        double errorMs = _filteredMs - _targetMs;
        _integralPpm = Math.Clamp(_integralPpm + errorMs * 0.002, -5000, 5000);
        double wantedPpm = Math.Clamp(errorMs * 100 + _integralPpm, -5000, 5000);
        double currentPpm = (Ratio - 1) * 1_000_000;
        currentPpm += Math.Clamp(wantedPpm - currentPpm, -20, 20);
        Ratio = 1 + currentPpm / 1_000_000;
        return Ratio;
    }

    public void Reset()
    {
        _initialized = false;
        _integralPpm = 0;
        Ratio = 1;
    }
}
