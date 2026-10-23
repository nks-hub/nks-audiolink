using NksAudioLink.Client;
using NksAudioLink.Core;

namespace NksAudioLink.Client.Tests;

public sealed class ClientDiagnosticsTests
{
    [Fact]
    public void ReportIncludesVersionAndUsefulMeasurementsButRedactsSecretsAndIdentities()
    {
        const string key = "cHJpdmF0ZS1zaGFyZWQta2V5LXRlc3Q=";
        const string deviceId = "{0.0.0.00000000}.{12345678-1234-1234-1234-123456789abc}";
        var state = new ClientDiagnosticSnapshot("Connected", CaptureMode.Loopback, 40, 156, 7355, 7356,
            new StatsMessage(true, 35, 20, 1, 2, 3, 4, 5, 6), 0.75, 85.375,
            new AudioLevelSnapshot(-18.5, -19.25, -3, -4, false));
        string[] activity =
        [
            $"12:00:00 Connecting: Server old-host.private:7355 · Someone's old named speakers",
            $"12:00:01 Key={key}; current-host.private; {deviceId}; Alice's speakers",
            "12:00:02 Computer PC-ALICE user Alice at C:\\Users\\Alice\\private.json",
            "12:00:03 Previous peer 192.0.2.10 and 2001:db8::1; opened http://old-host.private:7356/",
            "12:00:04 The server 'unrelated-old-host' has no IPv4 address.",
            "12:00:05 Connected: Sending audio: 48 kHz, 16-bit stereo."
        ];
        string report = ClientDiagnostics.Format(state, activity, ["current-host.private", deviceId, "Alice's speakers", "Alice", "PC-ALICE", @"C:\Users\Alice"], key);
        Assert.Contains($"Version: {ProductVersion.Informational}", report);
        Assert.Contains("Target buffer: 40 ms; gain: 156%", report);
        Assert.Contains("Audio UDP port: 7355; monitor HTTP port: 7356", report);
        Assert.Contains("Network RTT: 0.75 ms", report);
        Assert.Contains("L RMS=-18.5, peak=-3", report);
        Assert.Contains("12:00:05 Connected", report);
        foreach (string identity in new[] { key, deviceId, "old-host.private", "current-host.private", "Someone's old named speakers", "Alice", "PC-ALICE", "192.0.2.10", "2001:db8::1", "unrelated-old-host", "private.json" })
            Assert.DoesNotContain(identity, report, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ActivityIsBoundedAndSecretsAreRemovedBeforeTruncation()
    {
        const string key = "cHJpdmF0ZS1zaGFyZWQta2V5LXRlc3Q=";
        string[] activity = Enumerable.Range(0, 200).Select(index => $"event-{index:000} " + new string('x', 475) + key + new string('y', 600)).ToArray();
        string report = ClientDiagnostics.Format(Disconnected(), activity, [], key);
        Assert.DoesNotContain("event-149 ", report);
        Assert.Contains("event-150 ", report);
        Assert.Contains("event-199 ", report);
        Assert.DoesNotContain(key[..12], report);
        Assert.True(report.Length < 27_000);
        Assert.Equal(50, report.Split('\n').Count(line => line.StartsWith("event-", StringComparison.Ordinal)));
    }

    [Fact]
    public void DisconnectedReportDoesNotInventStatisticsOrLevels()
    {
        string report = ClientDiagnostics.Format(Disconnected(), [], [], null);
        Assert.Contains("Stream statistics: unavailable", report);
        Assert.Contains("Sent levels: unavailable", report);
        Assert.DoesNotContain("Jitter counters:", report);
        Assert.DoesNotContain("L RMS=", report);
    }

    [Fact]
    public void SharedKeyIsRedactedEvenWhenItMatchesAnAllowlistedBuildIdentifier()
    {
        string key = ProductVersion.Informational.Split('+')[^1];
        string report = ClientDiagnostics.Format(Disconnected(), [], [], key);
        Assert.DoesNotContain(key, report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[redacted]", report);
    }

    private static ClientDiagnosticSnapshot Disconnected() =>
        new("Disconnected", CaptureMode.Input, 30, 100, 7355, 7356, null, null, null, null);
}
