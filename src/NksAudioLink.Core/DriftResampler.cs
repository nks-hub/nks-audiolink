namespace NksAudioLink.Core;

/// <summary>Streaming stereo s16 resampler with linear interpolation and bounded storage.</summary>
public sealed class DriftResampler
{
    private readonly short[] _samples;
    private int _head;
    private int _count;
    private double _phase;

    public DriftResampler(int capacityFrames = 8192)
    {
        if (capacityFrames < Protocol.SamplesPerFrame * 2) throw new ArgumentOutOfRangeException(nameof(capacityFrames));
        _samples = new short[capacityFrames * Protocol.Channels];
    }

    public int BufferedFrames => _count;
    public int CapacityFrames => _samples.Length / Protocol.Channels;

    public void Push(ReadOnlySpan<short> interleaved)
    {
        if (interleaved.Length % Protocol.Channels != 0) throw new ArgumentException("Stereo samples required.", nameof(interleaved));
        int frames = interleaved.Length / Protocol.Channels;
        if (_count + frames > CapacityFrames) throw new InvalidOperationException("Resampler input buffer is full.");
        int writeFrame = _head + _count;
        if (writeFrame >= CapacityFrames) writeFrame -= CapacityFrames;
        int firstFrames = Math.Min(frames, CapacityFrames - writeFrame);
        int firstSamples = firstFrames * Protocol.Channels;
        interleaved[..firstSamples].CopyTo(_samples.AsSpan(writeFrame * Protocol.Channels));
        interleaved[firstSamples..].CopyTo(_samples);
        _count += frames;
    }

    /// <returns>Number of stereo frames written.</returns>
    public int Read(Span<short> destination, double ratio)
    {
        if (destination.Length % Protocol.Channels != 0) throw new ArgumentException("Stereo destination required.", nameof(destination));
        if (!double.IsFinite(ratio) || ratio is < 0.995 or > 1.005) throw new ArgumentOutOfRangeException(nameof(ratio));
        int written = 0;
        int wanted = destination.Length / Protocol.Channels;
        while (written < wanted)
        {
            int first = (int)_phase;
            if (first + 1 >= _count) break;
            double fraction = _phase - first;
            int firstFrame = _head + first;
            if (firstFrame >= CapacityFrames) firstFrame -= CapacityFrames;
            int nextFrame = firstFrame + 1;
            if (nextFrame == CapacityFrames) nextFrame = 0;
            int a = firstFrame * Protocol.Channels;
            int b = nextFrame * Protocol.Channels;
            int offset = written * Protocol.Channels;
            destination[offset] = Interpolate(_samples[a], _samples[b], fraction);
            destination[offset + 1] = Interpolate(_samples[a + 1], _samples[b + 1], fraction);
            _phase += ratio;
            written++;
        }
        int consumed = Math.Min((int)_phase, _count);
        _head = (_head + consumed) % CapacityFrames;
        _count -= consumed;
        _phase -= consumed;
        return written;
    }

    public void Reset()
    {
        _head = 0;
        _count = 0;
        _phase = 0;
    }

    private static short Interpolate(short a, short b, double fraction) =>
        (short)Math.Clamp((int)Math.Round(a + (b - a) * fraction), short.MinValue, short.MaxValue);
}
