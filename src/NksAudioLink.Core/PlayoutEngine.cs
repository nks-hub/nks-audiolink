using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace NksAudioLink.Core;

/// <summary>Receives indexed PCM frames and supplies drift-corrected samples to an output device.</summary>
public sealed class PlayoutEngine
{
    private readonly object _gate = new();
    private readonly JitterBuffer _jitter;
    private readonly DriftController _controller;
    private readonly DriftResampler _resampler = new();
    private readonly byte[] _frame = new byte[Protocol.PcmBytesPerFrame];
    private readonly short[] _samples = new short[Protocol.SamplesPerFrame * Protocol.Channels];

    public PlayoutEngine(int targetLatencyMs = 30)
    {
        _jitter = new JitterBuffer(targetLatencyMs);
        _controller = new DriftController(targetLatencyMs);
    }

    public bool Receive(ulong sampleIndex, ReadOnlySpan<byte> pcm, bool silent)
    {
        lock (_gate) return _jitter.Enqueue(sampleIndex, pcm, silent);
    }

    public void Read(Span<short> destination)
    {
        if (destination.Length != Protocol.SamplesPerFrame * Protocol.Channels)
            throw new ArgumentException("Exactly one 5 ms stereo frame is required.", nameof(destination));
        lock (_gate)
        {
            double bufferedMs = _jitter.BufferedMs + _resampler.BufferedFrames * 1000.0 / Protocol.SampleRate;
            double ratio = _controller.Update(bufferedMs);
            int needed = (int)Math.Ceiling(Protocol.SamplesPerFrame * ratio) + 2;
            while (_resampler.BufferedFrames < needed && _jitter.ReadNext(_frame))
            {
                if (BitConverter.IsLittleEndian)
                    MemoryMarshal.Cast<byte, short>(_frame).CopyTo(_samples);
                else
                    for (int i = 0; i < _samples.Length; i++)
                        _samples[i] = BinaryPrimitives.ReadInt16LittleEndian(_frame.AsSpan(i * 2));
                _resampler.Push(_samples);
            }
            int written = _resampler.Read(destination, ratio);
            destination[(written * Protocol.Channels)..].Clear();
        }
    }

    public PlayoutStats GetStats()
    {
        lock (_gate)
            return new PlayoutStats(_jitter.BufferedMs, _controller.RatioPpm,
                _jitter.Underruns, _jitter.Overruns, _jitter.Lost, _jitter.Late, _jitter.Duplicates);
    }

    public void Reset()
    {
        lock (_gate)
        {
            _jitter.Reset();
            _controller.Reset();
            _resampler.Reset();
        }
    }
}

public readonly record struct PlayoutStats(int BufferMs, int RatioPpm, long Underruns, long Overruns, long Lost, long Late, long Duplicates);
