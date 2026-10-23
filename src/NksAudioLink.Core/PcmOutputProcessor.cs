using System.Buffers.Binary;

namespace NksAudioLink.Core;

/// <summary>
/// Stereo PCM gain/mute and an optional linked sample-peak limiter. The limiter
/// adds exactly 48 sample frames of lookahead (1 ms at 48 kHz); it is not an
/// oversampled true-peak limiter. One processing thread owns each instance.
/// </summary>
public sealed class PcmOutputProcessor
{
    public const int LookaheadFrames = Protocol.SampleRate / 1000;
    public const int TransitionFrames = Protocol.SampleRate / 200;
    public const short Ceiling = 32112;
    public const double ReleaseMilliseconds = 50;
    private const int HistoryFrames = LookaheadFrames + 1;
    private static readonly double ReleaseCoefficient = 1 - Math.Exp(-1 / (Protocol.SampleRate * ReleaseMilliseconds / 1000));
    private readonly double[] _left = new double[HistoryFrames];
    private readonly double[] _right = new double[HistoryFrames];
    private readonly double[] _requiredGain = new double[HistoryFrames];
    private LinearRamp _gain, _mute, _wet;
    private int _writeIndex;
    private double _limiterGain = 1;
    private bool _limiterRequested;

    /// <summary>Maximum delay during a live transition; zero when fully bypassed.</summary>
    public int LatencyFrames => _wet.Current > 0 || _wet.Target > 0 ? LookaheadFrames : 0;

    public PcmOutputProcessor(double gain = 1, bool muted = false, bool limiterEnabled = false)
        => Reset(gain, muted, limiterEnabled);

    /// <summary>Discard delayed PCM at a stream boundary; never replay a pre-outage tail.</summary>
    public void Reset(double gain = 1, bool muted = false, bool limiterEnabled = false)
    {
        ValidateGain(gain);
        Array.Clear(_left);
        Array.Clear(_right);
        Array.Fill(_requiredGain, 1);
        _writeIndex = 0;
        _limiterGain = 1;
        _gain = new(gain);
        _mute = new(muted ? 0 : 1);
        _wet = new(limiterEnabled ? 1 : 0);
        _limiterRequested = limiterEnabled;
    }

    /// <returns>Whether the output contains any nonzero PCM sample.</returns>
    public bool Process(Span<short> interleavedStereo, double gain, bool muted, bool limiterEnabled)
    {
        if (interleavedStereo.Length % Protocol.Channels != 0)
            throw new ArgumentException("Complete stereo frames are required.", nameof(interleavedStereo));
        Prepare(gain, muted, limiterEnabled);
        bool nonzero = false;
        for (int i = 0; i < interleavedStereo.Length; i += 2)
        {
            ProcessFrame(interleavedStereo[i], interleavedStereo[i + 1], out short left, out short right);
            interleavedStereo[i] = left;
            interleavedStereo[i + 1] = right;
            nonzero |= left != 0 || right != 0;
        }
        return nonzero;
    }

    public bool ProcessPcm16(Span<byte> littleEndianStereo, double gain, bool muted, bool limiterEnabled)
    {
        if (littleEndianStereo.Length % (sizeof(short) * Protocol.Channels) != 0)
            throw new ArgumentException("Complete PCM16 stereo frames are required.", nameof(littleEndianStereo));
        Prepare(gain, muted, limiterEnabled);
        bool nonzero = false;
        for (int i = 0; i < littleEndianStereo.Length; i += 4)
        {
            ProcessFrame(BinaryPrimitives.ReadInt16LittleEndian(littleEndianStereo[i..]),
                BinaryPrimitives.ReadInt16LittleEndian(littleEndianStereo[(i + 2)..]), out short left, out short right);
            BinaryPrimitives.WriteInt16LittleEndian(littleEndianStereo[i..], left);
            BinaryPrimitives.WriteInt16LittleEndian(littleEndianStereo[(i + 2)..], right);
            nonzero |= left != 0 || right != 0;
        }
        return nonzero;
    }

    private void Prepare(double gain, bool muted, bool limiterEnabled)
    {
        ValidateGain(gain);
        _gain.Set(gain);
        _mute.Set(muted ? 0 : 1);
        _wet.Set(limiterEnabled ? 1 : 0);
        _limiterRequested = limiterEnabled;
    }

    private static void ValidateGain(double gain)
    {
        if (!double.IsFinite(gain) || gain is < 0 or > 4) throw new ArgumentOutOfRangeException(nameof(gain));
    }

    private void ProcessFrame(short inputLeft, short inputRight, out short outputLeft, out short outputRight)
    {
        double volume = _gain.Next();
        double mute = _mute.Next();
        double wet = _wet.Next();
        double left = inputLeft * volume, right = inputRight * volume;
        _left[_writeIndex] = left;
        _right[_writeIndex] = right;
        double incomingPeak = Math.Max(Math.Abs(left), Math.Abs(right));
        _requiredGain[_writeIndex] = incomingPeak > Ceiling ? Ceiling / incomingPeak : 1;
        int oldest = _writeIndex + 1;
        if (oldest == HistoryFrames) oldest = 0;
        _writeIndex = oldest;

        if (wet == 0)
        {
            // Preserve the original rounding/clamping exactly at a static gain.
            // Keep only history while bypassed, so a live enable can crossfade.
            _limiterGain = 1;
            outputLeft = Quantize(left * mute);
            outputRight = Quantize(right * mute);
            return;
        }

        double desired = 1;
        int index = oldest;
        for (int ahead = 0; ahead <= LookaheadFrames; ahead++)
        {
            double required = _requiredGain[index];
            if (required < desired)
            {
                // A future peak constrains today's gain to a straight attack
                // ramp, reaching its required gain exactly when it is output.
                desired = Math.Min(desired, required + (1 - required) * ahead / LookaheadFrames);
            }
            if (++index == HistoryFrames) index = 0;
        }
        _limiterGain = Math.Min(desired, _limiterGain + (1 - _limiterGain) * ReleaseCoefficient);
        double limitedLeft = _left[oldest] * _limiterGain;
        double limitedRight = _right[oldest] * _limiterGain;
        left = Math.Clamp(left, short.MinValue, short.MaxValue) * (1 - wet) + limitedLeft * wet;
        right = Math.Clamp(right, short.MinValue, short.MaxValue) * (1 - wet) + limitedRight * wet;

        // A newly enabled limiter must protect even the dry side of its 5 ms
        // crossfade. On disable, open that guard gradually back to full scale.
        double upper = _limiterRequested ? Ceiling : Ceiling + (short.MaxValue - Ceiling) * (1 - wet);
        double lower = _limiterRequested ? -Ceiling : -Ceiling + (short.MinValue + Ceiling) * (1 - wet);
        outputLeft = Quantize(Math.Clamp(left, lower, upper) * mute);
        outputRight = Quantize(Math.Clamp(right, lower, upper) * mute);
    }

    private static short Quantize(double sample) => (short)Math.Clamp((int)Math.Round(sample), short.MinValue, short.MaxValue);

    private struct LinearRamp(double initial)
    {
        public double Current = initial;
        public double Target = initial;
        private double _step;
        private int _remaining;
        public void Set(double target)
        {
            if (target == Target) return;
            Target = target;
            _remaining = TransitionFrames;
            _step = (target - Current) / TransitionFrames;
        }
        public double Next()
        {
            if (_remaining > 0) Current = --_remaining == 0 ? Target : Current + _step;
            return Current;
        }
    }
}
