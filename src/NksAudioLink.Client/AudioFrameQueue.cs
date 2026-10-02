using System.Buffers.Binary;
using NksAudioLink.Core;

namespace NksAudioLink.Client;

/// <summary>
/// Bounded capture FIFO and fractional-rate bridge from the WASAPI clock to the 5 ms sender clock.
/// The sender always emits 240 samples; the number of source samples consumed varies slightly.
/// </summary>
public sealed class AudioFrameQueue
{
    public const int TargetBufferMs = 15;
    private const int BytesPerSampleFrame = Protocol.Channels * sizeof(short);
    private const int TargetFrames = Protocol.SampleRate * TargetBufferMs / 1000;
    private const int StartupFrames = TargetFrames;
    private const int LatencyLimitFrames = Protocol.SampleRate * 40 / 1000;
    private const double FillFilter = 0.005; // About one second at a 5 ms sender period.
    private const double RatePerFrame = 0.000005;
    private const double MaxRateCorrection = 0.005;

    private readonly short[] _data;
    private readonly int _capacityFrames;
    private readonly object _gate = new();
    private int _head;
    private int _count;
    private double _phase;
    private double _filteredCount = TargetFrames;
    private bool _primed;
    private long _droppedBytes;

    public AudioFrameQueue(int capacityMs = 500)
    {
        if (capacityMs < 20 || capacityMs % Protocol.FrameMs != 0)
            throw new ArgumentOutOfRangeException(nameof(capacityMs));
        _capacityFrames = capacityMs * Protocol.SampleRate / 1000;
        _data = new short[_capacityFrames * Protocol.Channels];
    }

    public long DroppedBytes { get { lock (_gate) return _droppedBytes; } }
    public int BufferedBytes { get { lock (_gate) return _count * BytesPerSampleFrame; } }

    public void Clear()
    {
        lock (_gate) Reset();
    }

    public void Write(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length % BytesPerSampleFrame != 0)
            throw new ArgumentException("Stereo s16 PCM required.", nameof(pcm));
        lock (_gate)
        {
            int incoming = pcm.Length / BytesPerSampleFrame;
            if (incoming > _capacityFrames)
            {
                int discarded = incoming - _capacityFrames;
                _droppedBytes += (long)discarded * BytesPerSampleFrame;
                pcm = pcm[(discarded * BytesPerSampleFrame)..];
                incoming = _capacityFrames;
            }
            DropOldest(Math.Max(0, _count + incoming - _capacityFrames));
            for (int i = 0; i < incoming; i++)
            {
                int target = ((_head + _count + i) % _capacityFrames) * Protocol.Channels;
                int source = i * BytesPerSampleFrame;
                _data[target] = BinaryPrimitives.ReadInt16LittleEndian(pcm[source..]);
                _data[target + 1] = BinaryPrimitives.ReadInt16LittleEndian(pcm[(source + sizeof(short))..]);
            }
            _count += incoming;

            // A delayed sender must resume with recent audio, never replay seconds of stale sound.
            int limit = Math.Min(_capacityFrames, LatencyLimitFrames);
            if (_count > limit)
                DropOldest(_count - Math.Min(_capacityFrames, TargetFrames + Protocol.SamplesPerFrame));
        }
    }

    public bool TryReadFrame(Span<byte> destination)
    {
        if (destination.Length != Protocol.PcmBytesPerFrame)
            throw new ArgumentException("Exactly one 5 ms frame required.", nameof(destination));
        lock (_gate)
        {
            if (!_primed)
            {
                if (_count < StartupFrames) return false;
                _primed = true;
                _filteredCount = TargetFrames;
            }
            if (_count < Protocol.SamplesPerFrame)
            {
                // WASAPI loopback stops callbacks at silence. Discard a partial tail so a
                // later sound starts from fresh captured samples after the 15 ms prefill.
                Reset();
                return false;
            }

            _filteredCount += FillFilter * (_count - _filteredCount);
            double ratio = Math.Clamp(1 + (_filteredCount - TargetFrames) * RatePerFrame,
                1 - MaxRateCorrection, 1 + MaxRateCorrection);
            double availableRatio = (_count - _phase) / Protocol.SamplesPerFrame;
            if (availableRatio < 1 - MaxRateCorrection)
            {
                Reset();
                return false;
            }
            ratio = Math.Min(ratio, availableRatio);

            for (int frame = 0; frame < Protocol.SamplesPerFrame; frame++)
            {
                double source = _phase + frame * ratio;
                int first = (int)source;
                int second = Math.Min(first + 1, _count - 1);
                double fraction = source - first;
                for (int channel = 0; channel < Protocol.Channels; channel++)
                {
                    short a = Sample(first, channel);
                    short b = Sample(second, channel);
                    short value = (short)Math.Round(a + (b - a) * fraction);
                    BinaryPrimitives.WriteInt16LittleEndian(destination[(frame * BytesPerSampleFrame + channel * sizeof(short))..], value);
                }
            }

            double advanced = _phase + Protocol.SamplesPerFrame * ratio;
            int consumed = Math.Min((int)advanced, _count);
            _head = (_head + consumed) % _capacityFrames;
            _count -= consumed;
            _phase = _count == 0 ? 0 : advanced - consumed;
            return true;
        }
    }

    private short Sample(int offset, int channel) =>
        _data[((_head + offset) % _capacityFrames) * Protocol.Channels + channel];

    private void DropOldest(int frames)
    {
        if (frames == 0) return;
        _head = (_head + frames) % _capacityFrames;
        _count -= frames;
        _phase = 0;
        _filteredCount = TargetFrames;
        _droppedBytes += (long)frames * BytesPerSampleFrame;
    }

    private void Reset()
    {
        _head = 0;
        _count = 0;
        _phase = 0;
        _filteredCount = TargetFrames;
        _primed = false;
    }
}
