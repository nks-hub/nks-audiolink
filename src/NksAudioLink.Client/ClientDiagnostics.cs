using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using NksAudioLink.Core;

namespace NksAudioLink.Client;

public enum CaptureMode { Loopback, VirtualCable, Input, Application }

public sealed record ClientDiagnosticSnapshot(
    string Status, CaptureMode Mode, int TargetBufferMs, double GainPercent, int UdpPort, int HttpPort,
    StatsMessage? Stats, double? RttMs, double? EstimatedDelayMs, AudioLevelSnapshot? Levels,
    bool AdaptiveBuffer = false, int AdaptiveMinimumMs = 20, int AdaptiveMaximumMs = 100,
    bool Muted = false, bool LimiterEnabled = false, int ProcessingLatencyMs = 0);

/// <summary>Formats an allowlisted support report; it does not access the clipboard or network.</summary>
public static class ClientDiagnostics
{
    public const int MaxActivityEvents = 50;
    public const int MaxActivityLineLength = 500;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    public static string Format(ClientDiagnosticSnapshot state, IEnumerable<string> activity, IEnumerable<string?> sensitiveValues, string? sharedKey)
    {
        // Redact before truncating, so a long message cannot expose a partial key.
        string[] secretText = Variants([sharedKey]);
        string[] privateText = Variants(sensitiveValues.Concat(secretText));
        string Safe(string value)
        {
            value = OneLine(value);
            // Historical connections may refer to a different host/device than the current settings.
            value = Replace(value, @"(?i)(Connecting:\s*Server\s+).*", "$1[endpoint and device redacted]");
            value = Replace(value, @"(?i)(\bserver\s+)(['""])[^'""]*\2", "$1[redacted]");
            value = Replace(value, @"(?i)https?://\S+", "[address redacted]");
            value = Replace(value, @"(?i)(?:[a-z]:[\\/]|\\\\)[^\r\n]*", "[path redacted]");
            value = Replace(value, @"(?i)/(?:home|users|root|tmp)/[^\r\n]*", "[path redacted]");
            value = Replace(value, @"\b(?:\d{1,3}\.){3}\d{1,3}\b", "[address redacted]");
            value = Regex.Replace(value, @"(?<![a-zA-Z0-9])(?:[a-fA-F0-9]{0,4}:){2,}[a-fA-F0-9:.%]*",
                match => IPAddress.TryParse(match.Value, out _) ? "[address redacted]" : match.Value,
                RegexOptions.CultureInvariant, RegexTimeout);
            value = Replace(value, @"(?i)\{?[a-f0-9]{8}-(?:[a-f0-9]{4}-){3}[a-f0-9]{12}\}?", "[identifier redacted]");
            foreach (string sensitive in privateText)
                value = value.Replace(sensitive, "[redacted]", StringComparison.OrdinalIgnoreCase);
            return value.Length > MaxActivityLineLength ? value[..MaxActivityLineLength] + "…" : value;
        }

        var report = new StringBuilder();
        report.AppendLine("NKS AudioLink client diagnostics");
        report.AppendLine($"Version: {ProductVersion.Informational}");
        report.AppendLine($"Created UTC: {DateTimeOffset.UtcNow:O}");
        report.AppendLine($"Status: {Safe(state.Status)}");
        report.AppendLine($"Capture mode: {state.Mode}");
        report.AppendLine(state.AdaptiveBuffer ? $"Gain: {Number(state.GainPercent)}%"
            : $"Target buffer: {state.TargetBufferMs} ms; gain: {Number(state.GainPercent)}%");
        report.AppendLine($"Muted: {state.Muted}; peak limiter: {state.LimiterEnabled}; processing lookahead: {state.ProcessingLatencyMs} ms");
        report.AppendLine(state.AdaptiveBuffer
            ? $"Buffer mode: predictive adaptive; requested range: {state.AdaptiveMinimumMs}–{state.AdaptiveMaximumMs} ms"
            : "Buffer mode: manual");
        report.AppendLine($"Audio UDP port: {state.UdpPort}; monitor HTTP port: {state.HttpPort}");
        report.AppendLine("Server, audio-device and user identifiers: redacted");
        if (state.Stats is { } stats)
        {
            if (stats.AdaptiveBuffer is { } adaptive)
                report.AppendLine($"Active adaptive target: {adaptive.TargetMs} ms; reason: {adaptive.Reason}; changes: {adaptive.Changes}");
            report.AppendLine($"Estimated delay: {Number(state.EstimatedDelayMs)} ms (excludes receiver/speakers)");
            report.AppendLine($"Network RTT: {Number(state.RttMs)} ms; server buffer: {stats.BufferMs} ms; output queue: {stats.SinkDelayMs} ms; drift: {stats.RatioPpm} ppm");
            report.AppendLine($"Jitter counters: lost={stats.Lost}; late={stats.Late}; underruns={stats.Underruns}; overruns={stats.Overruns}");
        }
        else report.AppendLine("Stream statistics: unavailable");
        if (state.Levels is { } levels)
        {
            report.AppendLine($"Sent levels after gain/mute/limiter (dBFS): L RMS={Number(levels.LeftRmsDbfs)}, peak={Number(levels.LeftPeakDbfs)}; R RMS={Number(levels.RightRmsDbfs)}, peak={Number(levels.RightPeakDbfs)}; clipping={levels.Clipping}");
        }
        else report.AppendLine("Sent levels: unavailable");
        report.AppendLine($"Recent activity (last {MaxActivityEvents} events; local time {DateTimeOffset.Now:zzz}):");
        foreach (string entry in activity.TakeLast(MaxActivityEvents))
            report.AppendLine(Safe(entry));
        string result = report.ToString();
        // The key must not escape through any field, even if it happens to equal
        // a public build identifier or another otherwise allowlisted value.
        foreach (string secret in secretText)
            result = result.Replace(secret, "[redacted]", StringComparison.OrdinalIgnoreCase);
        return result;
    }

    private static string[] Variants(IEnumerable<string?> values) => values
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .SelectMany(value => new[] { value!, OneLine(value!), string.Concat(value!.Where(c => !char.IsWhiteSpace(c))) })
        .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(value => value.Length).ToArray();
    private static string Number(double? value) => value is { } number && double.IsFinite(number)
        ? number.ToString("0.##", CultureInfo.InvariantCulture) : "unavailable";
    private static string OneLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ');
    private static string Replace(string value, string pattern, string replacement) =>
        Regex.Replace(value, pattern, replacement, RegexOptions.CultureInvariant, RegexTimeout);
}
