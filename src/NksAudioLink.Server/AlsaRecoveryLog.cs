using System.Diagnostics;

namespace NksAudioLink.Server;

/// <summary>Native sink diagnostics, separate from network/jitter-buffer counters.</summary>
internal sealed class AlsaRecoveryLog(Action<string> write, Func<long>? timestamp = null)
{
    private readonly Func<long> _timestamp = timestamp ?? Stopwatch.GetTimestamp;
    private readonly object _gate = new();
    private long _errors, _recoveries, _failures, _underruns, _suspends, _suppressed;
    private long _lastLogTick;
    private bool _hasLogged;
    private int _lastError, _lastResult;

    public void Record(int nativeError, int recoveryResult)
    {
        lock (_gate) RecordLocked(nativeError, recoveryResult);
    }

    public NativeOutputStatus GetSnapshot()
    {
        lock (_gate) return new(_errors, _recoveries, _failures, _underruns, _suspends);
    }

    private void RecordLocked(int nativeError, int recoveryResult)
    {
        _errors++;
        if (recoveryResult >= 0) _recoveries++; else _failures++;
        // Linux errno values: EPIPE=32, ESTRPIPE=86. For a playback handle,
        // ALSA documents EPIPE as underrun and ESTRPIPE as suspended stream.
        if (nativeError == -32) _underruns++;
        if (nativeError == -86) _suspends++;
        _lastError = nativeError;
        _lastResult = recoveryResult;
        long now = _timestamp();
        if (recoveryResult >= 0 && _hasLogged && now - _lastLogTick < Stopwatch.Frequency)
        {
            _suppressed++;
            return;
        }
        Emit(now, "event");
    }

    // The normal write path only pays this branch when there is no pending log.
    public void FlushIfDue()
    {
        if (Volatile.Read(ref _suppressed) == 0) return;
        lock (_gate)
        {
            long now = _timestamp();
            if (_suppressed > 0 && now - _lastLogTick >= Stopwatch.Frequency) Emit(now, "summary");
        }
    }

    public void Flush()
    {
        lock (_gate) if (_suppressed > 0) Emit(_timestamp(), "summary");
    }

    private void Emit(long now, string kind)
    {
        string reason = _lastError switch { -32 => "playback-underrun/EPIPE", -86 => "stream-suspended/ESTRPIPE", _ => "native-write-error" };
        string message = $"ALSA recovery {kind}: nativeError={_lastError} ({reason}), recoverResult={_lastResult}, " +
            $"outcome={(_lastResult >= 0 ? "succeeded" : "failed")}, totalWriteErrors={_errors}, " +
            $"successfulRecoveries={_recoveries}, failedRecoveries={_failures}, " +
            $"playbackUnderruns={_underruns}, streamSuspensions={_suspends}, suppressedSinceLastLog={_suppressed}.";
        _lastLogTick = now;
        _hasLogged = true;
        _suppressed = 0;
        try { write(message); }
        catch (IOException) { /* Diagnostic output must not turn successful recovery into audio failure. */ }
        catch (ObjectDisposedException) { }
    }
}
