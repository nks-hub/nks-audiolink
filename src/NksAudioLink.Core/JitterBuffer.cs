namespace NksAudioLink.Core;

/// <summary>Fixed-capacity frame buffer indexed by source sample position.</summary>
public sealed class JitterBuffer
{
    private readonly Slot[] _slots;
    private readonly int _targetFrames;
    private ulong _nextIndex;
    private bool _started;
    private bool _primed;
    private bool _hasPlayed;
    private int _count;

    private sealed class Slot
    {
        public ulong Index;
        public bool Present;
        public readonly byte[] Data = new byte[Protocol.PcmBytesPerFrame];
    }

    public JitterBuffer(int targetLatencyMs = 30, int capacityFrames = 128)
    {
        if (targetLatencyMs < Protocol.FrameMs || targetLatencyMs > 200 || targetLatencyMs % Protocol.FrameMs != 0)
            throw new ArgumentOutOfRangeException(nameof(targetLatencyMs));
        if (capacityFrames <= targetLatencyMs / Protocol.FrameMs + 1)
            throw new ArgumentOutOfRangeException(nameof(capacityFrames));
        _targetFrames = targetLatencyMs / Protocol.FrameMs;
        _slots = Enumerable.Range(0, capacityFrames).Select(_ => new Slot()).ToArray();
    }

    public int BufferedFrames => _count;
    public int BufferedMs => _count * Protocol.FrameMs;
    public bool IsPrimed => _primed;
    public long Lost { get; private set; }
    public long Late { get; private set; }
    public long Duplicates { get; private set; }
    public long Overruns { get; private set; }
    public long Underruns { get; private set; }

    public void Reset()
    {
        foreach (var slot in _slots) slot.Present = false;
        _count = 0;
        _started = false;
        _primed = false;
        _hasPlayed = false;
        _nextIndex = 0;
    }

    public bool Enqueue(ulong sampleIndex, ReadOnlySpan<byte> pcm, bool silent)
    {
        if (sampleIndex % Protocol.SamplesPerFrame != 0 ||
            (silent ? !pcm.IsEmpty : pcm.Length != Protocol.PcmBytesPerFrame))
            throw new ArgumentException("Invalid audio frame.", nameof(pcm));
        if (!_started)
        {
            _nextIndex = sampleIndex;
            _started = true;
        }
        if (sampleIndex < _nextIndex)
        {
            if (!_hasPlayed && !_primed && (_nextIndex - sampleIndex) / Protocol.SamplesPerFrame < (ulong)_slots.Length)
                _nextIndex = sampleIndex;
            else { Late++; return false; }
        }
        ulong distance = (sampleIndex - _nextIndex) / Protocol.SamplesPerFrame;
        if (distance >= (ulong)_slots.Length) { Overruns++; return false; }
        var slot = _slots[(int)((sampleIndex / Protocol.SamplesPerFrame) % (ulong)_slots.Length)];
        if (slot.Present)
        {
            if (slot.Index == sampleIndex) Duplicates++;
            else Overruns++;
            return false;
        }
        slot.Index = sampleIndex;
        slot.Present = true;
        if (silent) slot.Data.AsSpan().Clear();
        else pcm.CopyTo(slot.Data);
        _count++;
        if (!_primed && HasContiguousFrames(_targetFrames)) _primed = true;
        return true;
    }

    /// <summary>Reads one 5 ms PCM frame. Returns false while waiting for target fill.</summary>
    public bool ReadNext(Span<byte> destination)
    {
        if (destination.Length != Protocol.PcmBytesPerFrame)
            throw new ArgumentException("Destination must contain exactly one PCM frame.", nameof(destination));
        destination.Clear();
        if (!_primed) return false;
        _hasPlayed = true;
        var slot = _slots[(int)((_nextIndex / Protocol.SamplesPerFrame) % (ulong)_slots.Length)];
        if (slot.Present && slot.Index == _nextIndex)
        {
            slot.Data.CopyTo(destination);
            slot.Present = false;
            _count--;
        }
        else
        {
            Lost++;
            Underruns++;
            _primed = false;
        }
        _nextIndex += Protocol.SamplesPerFrame;
        if (!_primed && HasContiguousFrames(_targetFrames)) _primed = true;
        return true;
    }

    private bool HasContiguousFrames(int amount)
    {
        for (int i = 0; i < amount; i++)
        {
            ulong index = _nextIndex + (ulong)(i * Protocol.SamplesPerFrame);
            var slot = _slots[(int)((index / Protocol.SamplesPerFrame) % (ulong)_slots.Length)];
            if (!slot.Present || slot.Index != index) return false;
        }
        return true;
    }
}
