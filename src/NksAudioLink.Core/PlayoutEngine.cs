using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NksAudioLink.Core;

/// <summary>Receives indexed PCM frames and supplies drift-corrected samples to an output device.</summary>
public sealed class PlayoutEngine
{
    private readonly object _gate = new();
    private readonly JitterBuffer _jitter;
    private readonly DriftController _controller;
    private readonly AdaptiveBufferController? _adaptive;
    private readonly int _initialTargetMs;
    private readonly DriftResampler _resampler = new();
    private readonly byte[] _frame = new byte[Protocol.PcmBytesPerFrame];
    private readonly short[] _samples = new short[Protocol.SamplesPerFrame * Protocol.Channels];

    public PlayoutEngine(int targetLatencyMs = 30, AdaptiveBufferOptions? adaptiveBuffer = null)
    {
        _initialTargetMs = targetLatencyMs;
        if (adaptiveBuffer is { } options) _adaptive = new AdaptiveBufferController(targetLatencyMs, options);
        _jitter = new JitterBuffer(targetLatencyMs);
        _controller = new DriftController(targetLatencyMs);
    }

    public bool Receive(ulong sampleIndex, ReadOnlySpan<byte> pcm, bool silent, long? arrivalTimestamp = null)
    {
        lock (_gate)
        {
            long late = _jitter.Late;
            bool accepted = _jitter.Enqueue(sampleIndex, pcm, silent);
            _adaptive?.ObserveArrival(sampleIndex, arrivalTimestamp ?? Stopwatch.GetTimestamp(), _jitter.Late > late);
            return accepted;
        }
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
            if (_adaptive?.Observe(_jitter.Underruns, _jitter.Lost, _jitter.Late,
                    written == Protocol.SamplesPerFrame && _jitter.IsPrimed) == true)
            {
                var state = _adaptive.Status;
                _jitter.SetTargetLatency(state.TargetMs, state.Reason == AdaptiveBufferReason.Underrun);
                _controller.SetTarget(state.TargetMs);
            }
        }
    }

    public PlayoutStats GetStats()
    {
        lock (_gate)
            return new PlayoutStats(_jitter.BufferedMs, _controller.RatioPpm,
                _jitter.Underruns, _jitter.Overruns, _jitter.Lost, _jitter.Late, _jitter.Duplicates);
    }

    public AdaptiveBufferStatus? GetAdaptiveStatus()
    {
        lock (_gate) return _adaptive?.Status;
    }

    public PredictiveBufferStatus? GetPredictiveStatus()
    {
        lock (_gate) return _adaptive?.GetPrediction();
    }

    public (int TargetMs, AdaptiveBufferStatus? Adaptive, PredictiveBufferStatus? Predictor) GetBufferControlStatus()
    {
        lock (_gate) return (_adaptive?.Status.TargetMs ?? _initialTargetMs, _adaptive?.Status, _adaptive?.GetPrediction());
    }

    public int TargetLatencyMs
    {
        get { lock (_gate) return _adaptive?.Status.TargetMs ?? _initialTargetMs; }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _jitter.Reset();
            _controller.Reset();
            _resampler.Reset();
            _adaptive?.Reset(_jitter.Underruns, _jitter.Lost, _jitter.Late);
            _jitter.SetTargetLatency(_initialTargetMs);
            _controller.SetTarget(_initialTargetMs);
        }
    }
}

public readonly record struct PlayoutStats(int BufferMs, int RatioPpm, long Underruns, long Overruns, long Lost, long Late, long Duplicates);
