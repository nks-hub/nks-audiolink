using NksAudioLink.Client;
using NksAudioLink.Cli;

namespace NksAudioLink.Client.Tests;

public sealed class CaptureProcessTests
{
    [Fact]
    public async Task PreCancelledActivationDoesNotOpenAnyProcessOrAudioInterface()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var invalid = new CaptureProcess(int.MaxValue, "missing", 0);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WasapiSource.CreateProcessAsync(invalid, stop.Token));
    }

    [Fact]
    public void SelectedInstanceCanBeOpenedButReusedPidIdentityCannot()
    {
        var selected = CaptureProcess.FromId(Environment.ProcessId);
        using var pinned = selected.OpenVerified();
        Assert.Equal(Environment.ProcessId, pinned.Id);
        var reusedPid = selected with { StartedUtcTicks = selected.StartedUtcTicks - 1 };
        Assert.Throws<CaptureProcessUnavailableException>(() => reusedPid.OpenVerified());
    }

    [Fact]
    public void MissingProcessDoesNotFallBackToAnotherProcessWithSameName()
    {
        var missing = new CaptureProcess(int.MaxValue, System.Diagnostics.Process.GetCurrentProcess().ProcessName, DateTime.UtcNow.Ticks);
        Assert.Throws<CaptureProcessUnavailableException>(() => missing.OpenVerified());
    }

    [Fact]
    public void ProcessEnumerationDoesNotIncludeCapturingApplication()
    {
        Assert.DoesNotContain(CaptureProcess.List(), candidate => candidate.Id == Environment.ProcessId);
    }

    [Fact]
    public void ProcessCaptureAndAdaptiveOptionsComposeWithoutChangingManualTarget()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{\"TargetLatencyMs\":150}");
            var options = CommandLineOptions.Parse(["send", "--process", "123", "--config", path, "--adaptive-buffer", "true", "--buffer-min", "30", "--buffer-max", "80", "--mute", "true", "--limiter", "true"]);
            Assert.Equal(123, options.ProcessId);
            Assert.True(options.Config.AdaptiveBuffer);
            Assert.Equal(150, options.Config.TargetLatencyMs);
            Assert.Equal(40, options.Config.EffectiveInitialLatencyMs);
            Assert.Equal(30, options.Config.AdaptiveMinLatencyMs);
            Assert.True(options.Config.Muted);
            Assert.True(options.Config.LimiterEnabled);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("AdaptiveBuffer")]
    [InlineData("Muted")]
    [InlineData("LimiterEnabled")]
    public void TestToneCannotSilentlyIgnoreSendOnlyConfiguration(string setting)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, $"{{\"{setting}\":true}}");
            var error = Assert.Throws<ArgumentException>(() => CommandLineOptions.Parse(["test-tone", "--config", path]));
            Assert.Contains("send only", error.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ExplicitManualLatencyCannotBeCombinedWithAdaptiveMode()
    {
        Assert.Throws<ArgumentException>(() => CommandLineOptions.Parse(["send", "--latency", "20", "--adaptive-buffer", "true"]));
        Assert.Throws<ArgumentException>(() => CommandLineOptions.Parse(["send", "--adaptive-buffer", "true", "--latency", "20"]));
    }

    [Theory]
    [InlineData("--process", "0")]
    [InlineData("--process", "-1")]
    [InlineData("--process", "123", "--mode", "loopback")]
    [InlineData("--process", "123", "--device", "device-id")]
    public void InvalidOrAmbiguousProcessOptionsFail(params string[] options) =>
        Assert.ThrowsAny<ArgumentException>(() => CommandLineOptions.Parse(["send", .. options]));
}
