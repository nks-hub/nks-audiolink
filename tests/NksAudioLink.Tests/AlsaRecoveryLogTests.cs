using System.Diagnostics;
using NksAudioLink.Server;

namespace NksAudioLink.Tests;

public sealed class AlsaRecoveryLogTests
{
    [Theory]
    [InlineData(-32, "playback-underrun/EPIPE", "playbackUnderruns=1")]
    [InlineData(-86, "stream-suspended/ESTRPIPE", "streamSuspensions=1")]
    [InlineData(-4, "native-write-error", "playbackUnderruns=0")]
    public void SuccessfulRecoveryReportsNativeCauseAndIndependentCounts(int code, string reason, string count)
    {
        var messages = new List<string>();
        var log = new AlsaRecoveryLog(messages.Add, () => 0);
        log.Record(code, 0);
        string message = Assert.Single(messages);
        Assert.Contains($"nativeError={code}", message);
        Assert.Contains(reason, message);
        Assert.Contains(count, message);
        Assert.Contains("recoverResult=0, outcome=succeeded", message);
        Assert.Contains("totalWriteErrors=1, successfulRecoveries=1, failedRecoveries=0", message);
    }

    [Fact]
    public void ErrorBurstIsThrottledButNextHealthyWriteFlushesAllCounts()
    {
        long now = 0;
        var messages = new List<string>();
        var log = new AlsaRecoveryLog(messages.Add, () => now);
        for (int i = 0; i < 50; i++) log.Record(-32, 0);
        Assert.Single(messages);
        now = Stopwatch.Frequency - 1;
        log.FlushIfDue();
        Assert.Single(messages);
        now++;
        log.FlushIfDue();
        Assert.Equal(2, messages.Count);
        Assert.Contains("summary", messages[1]);
        Assert.Contains("totalWriteErrors=50, successfulRecoveries=50", messages[1]);
        Assert.Contains("playbackUnderruns=50", messages[1]);
        Assert.Contains("suppressedSinceLastLog=49", messages[1]);
        log.FlushIfDue();
        log.Flush();
        Assert.Equal(2, messages.Count);
    }

    [Fact]
    public void UnrecoverableFailureBypassesThrottleAndPreservesPriorCounts()
    {
        var messages = new List<string>();
        var log = new AlsaRecoveryLog(messages.Add, () => 0);
        log.Record(-32, 0);
        log.Record(-86, 0);
        log.Record(-19, -19);
        Assert.Equal(2, messages.Count);
        Assert.Contains("nativeError=-19", messages[1]);
        Assert.Contains("recoverResult=-19, outcome=failed", messages[1]);
        Assert.Contains("totalWriteErrors=3, successfulRecoveries=2, failedRecoveries=1", messages[1]);
        Assert.Contains("playbackUnderruns=1, streamSuspensions=1", messages[1]);
        Assert.Contains("suppressedSinceLastLog=1", messages[1]);
    }

    [Fact]
    public void CloseFlushesPendingEventsBeforeTheThrottleIntervalExpires()
    {
        var messages = new List<string>();
        var log = new AlsaRecoveryLog(messages.Add, () => 0);
        log.Record(-32, 0);
        log.Record(-32, 0);
        log.Flush();
        Assert.Equal(2, messages.Count);
        Assert.Contains("totalWriteErrors=2", messages[1]);
        Assert.Contains("suppressedSinceLastLog=1", messages[1]);
    }

    [Fact]
    public void BrokenDiagnosticOutputDoesNotBreakAudioRecovery()
    {
        var log = new AlsaRecoveryLog(_ => throw new IOException("Closed journal pipe."), () => 0);
        log.Record(-32, 0);
        log.Record(-32, 0);
        log.Flush();
    }
}
