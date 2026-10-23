namespace NksAudioLink.Core;

/// <summary>Fixed-capacity frame buffer indexed by source sample position.</summary>
public sealed class JitterBuffer
{
    private readonly Slot[] _slots;
    private int _targetFrames;
    private ulong _nextIndex;
    private bool _started;
    private bool _primed;
    private bool _positionCommitted;
    private bool _waitingForFill;
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

    public void SetTargetLatency(int targetLatencyMs, bool rebuffer = false)
    {
        if (targetLatencyMs < Protocol.FrameMs || targetLatencyMs > 200 || targetLatencyMs % Protocol.FrameMs != 0 ||
            _slots.Length <= targetLatencyMs / Protocol.FrameMs + 1)
            throw new ArgumentOutOfRangeException(nameof(targetLatencyMs));
        _targetFrames = targetLatencyMs / Protocol.FrameMs;
        if (rebuffer) { _primed = false; _waitingForFill = _started; }
        TryPrime();
    }

    public void Reset()
    {
        foreach (var slot in _slots) slot.Present = false;
        _count = 0;
        _started = false;
        _primed = false;
        _positionCommitted = false;
        _waitingForFill = false;
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
            if (!_positionCommitted && !_primed && FitsStartupWindow(sampleIndex))
                _nextIndex = sampleIndex;
            else { Late++; return false; }
        }
        ulong distance = (sampleIndex - _nextIndex) / Protocol.SamplesPerFrame;
        if (distance >= (ulong)_slots.Length)
        {
            // A healthy stream rejects distant packets. While playout is waiting
            // for a refill, follow the source so a long outage is not permanent.
            if (_primed || !_waitingForFill) { Overruns++; return false; }
            AdvanceTo(sampleIndex - (ulong)(_slots.Length - 1) * Protocol.SamplesPerFrame);
        }
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
        TryPrime();
        return true;
    }

    /// <summary>Reads one 5 ms PCM frame. Returns false while waiting for target fill.</summary>
    public bool ReadNext(Span<byte> destination)
    {
        if (destination.Length != Protocol.PcmBytesPerFrame)
            throw new ArgumentException("Destination must contain exactly one PCM frame.", nameof(destination));
        destination.Clear();
        if (!_primed)
        {
            _waitingForFill = _started;
            return false;
        }
        _positionCommitted = true;
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
            _waitingForFill = true;
        }
        _nextIndex += Protocol.SamplesPerFrame;
        TryPrime();
        return true;
    }

    private void TryPrime()
    {
        if (_primed || _count < _targetFrames) return;
        int contiguous = 0;
        ulong index = _nextIndex;
        for (int i = 0; i < _slots.Length; i++)
        {
            var slot = _slots[(int)((index / Protocol.SamplesPerFrame) % (ulong)_slots.Length)];
            contiguous = slot.Present && slot.Index == index ? contiguous + 1 : 0;
            if (contiguous == _targetFrames)
            {
                // Do not wait forever for an already lost packet. A complete target
                // buffer is enough evidence to resume at the earliest usable run.
                AdvanceTo(index - (ulong)(_targetFrames - 1) * Protocol.SamplesPerFrame);
                _primed = true;
                _waitingForFill = false;
                return;
            }
            if (ulong.MaxValue - index < Protocol.SamplesPerFrame) break;
            index += Protocol.SamplesPerFrame;
        }
    }

    private void AdvanceTo(ulong index)
    {
        if (index > _nextIndex) _positionCommitted = true;
        Lost += (long)((index - _nextIndex) / Protocol.SamplesPerFrame);
        foreach (var slot in _slots)
        {
            if (!slot.Present || slot.Index >= index) continue;
            slot.Present = false;
            _count--;
            Lost--; // A received frame discarded during recovery is an overrun.
            Overruns++;
        }
        _nextIndex = index;
    }

    private bool FitsStartupWindow(ulong index)
    {
        foreach (var slot in _slots)
            if (slot.Present && (slot.Index - index) / Protocol.SamplesPerFrame >= (ulong)_slots.Length)
                return false;
        return true;
    }
}
