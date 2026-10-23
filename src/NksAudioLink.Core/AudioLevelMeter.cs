using System.Buffers.Binary;

namespace NksAudioLink.Core;

public readonly record struct AudioLevelSnapshot(
    double LeftRmsDbfs, double RightRmsDbfs,
    double LeftPeakDbfs, double RightPeakDbfs, bool Clipping);

/// <summary>
/// Stereo 48 kHz PCM meter with a 300 ms RMS window, one-second peak hold,
/// and 20 dB/second peak decay. Time advances with processed samples, so silent
/// transport frames must be processed too. Reset when a stream ends.
/// </summary>
public sealed class AudioLevelMeter
{
    public const double FloorDbfs = -90;
    private const int WindowFrames = Protocol.SampleRate * 300 / 1000;
    private const int HoldFrames = Protocol.SampleRate;
    private const double FullScale = 32768;
    private static readonly double PeakDecay = Math.Pow(10, -20.0 / (20 * Protocol.SampleRate));
    private readonly object _gate = new();
    private readonly int[] _leftSquares = new int[WindowFrames];
    private readonly int[] _rightSquares = new int[WindowFrames];
    private long _leftSum, _rightSum;
    private int _position, _leftHold, _rightHold, _clipHold;
    private double _leftPeak, _rightPeak;

    public void ProcessPcm16(ReadOnlySpan<byte> interleavedStereo)
    {
        if (interleavedStereo.Length % 4 != 0)
            throw new ArgumentException("Complete little-endian stereo PCM16 sample frames are required.", nameof(interleavedStereo));
        lock (_gate)
        {
            for (int i = 0; i < interleavedStereo.Length; i += 4)
                AddSample(BinaryPrimitives.ReadInt16LittleEndian(interleavedStereo[i..]),
                    BinaryPrimitives.ReadInt16LittleEndian(interleavedStereo[(i + 2)..]));
        }
    }

    public void Process(ReadOnlySpan<short> interleavedStereo)
    {
        if (interleavedStereo.Length % 2 != 0)
            throw new ArgumentException("Complete stereo sample frames are required.", nameof(interleavedStereo));
        lock (_gate)
        {
            for (int i = 0; i < interleavedStereo.Length; i += 2)
                AddSample(interleavedStereo[i], interleavedStereo[i + 1]);
        }
    }

    public void ProcessSilence(int sampleFrames)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sampleFrames);
        lock (_gate)
        {
            for (int i = 0; i < sampleFrames; i++) AddSample(0, 0);
        }
    }

    public AudioLevelSnapshot GetSnapshot()
    {
        lock (_gate)
            return new AudioLevelSnapshot(
                ToDbfs(Math.Sqrt((double)_leftSum / WindowFrames)),
                ToDbfs(Math.Sqrt((double)_rightSum / WindowFrames)),
                ToDbfs(_leftPeak), ToDbfs(_rightPeak), _clipHold > 0);
    }

    public void Reset()
    {
        lock (_gate)
        {
            Array.Clear(_leftSquares);
            Array.Clear(_rightSquares);
            _leftSum = _rightSum = 0;
            _position = _leftHold = _rightHold = _clipHold = 0;
            _leftPeak = _rightPeak = 0;
        }
    }

    private void AddSample(short left, short right)
    {
        int leftSquare = left * left, rightSquare = right * right;
        _leftSum += (long)leftSquare - _leftSquares[_position];
        _rightSum += (long)rightSquare - _rightSquares[_position];
        _leftSquares[_position] = leftSquare;
        _rightSquares[_position] = rightSquare;
        if (++_position == WindowFrames) _position = 0;

        UpdatePeak(Math.Abs((int)left), ref _leftPeak, ref _leftHold);
        UpdatePeak(Math.Abs((int)right), ref _rightPeak, ref _rightHold);
        if (left is short.MinValue or short.MaxValue || right is short.MinValue or short.MaxValue)
            _clipHold = HoldFrames;
        else if (_clipHold > 0) _clipHold--;
    }

    private static void UpdatePeak(int magnitude, ref double peak, ref int hold)
    {
        if (magnitude >= peak)
        {
            peak = magnitude;
            hold = HoldFrames;
        }
        else if (hold > 0) hold--;
        else peak = Math.Max(magnitude, peak * PeakDecay);
    }

    private static double ToDbfs(double magnitude) => magnitude <= 0
        ? FloorDbfs : Math.Clamp(20 * Math.Log10(magnitude / FullScale), FloorDbfs, 0);
}
