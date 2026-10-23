using System.Diagnostics;

namespace NksAudioLink.Client;

/// <summary>A selected process instance, not a reusable PID or a name-based fallback.</summary>
public sealed record CaptureProcess(int Id, string ProcessName, long StartedUtcTicks)
{
    public string Name => $"{ProcessName} (PID {Id})";
    public static bool PlatformMaySupportCapture => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);
    public const string UnsupportedMessage = "Application capture requires an updated Windows 10 (2004 or later) or Windows 11 with process-loopback support. Use All PC audio on unsupported systems.";

    public static CaptureProcess FromId(int processId)
    {
        if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId), "Choose a positive process ID.");
        try
        {
            using var process = Process.GetProcessById(processId);
            return new(process.Id, process.ProcessName, process.StartTime.ToUniversalTime().Ticks);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new CaptureProcessUnavailableException("The selected application is no longer available. Refresh and select its current process.", error);
        }
    }

    public static IReadOnlyList<CaptureProcess> List()
    {
        using var current = Process.GetCurrentProcess();
        var result = new List<CaptureProcess>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id != current.Id && process.SessionId == current.SessionId && !process.HasExited)
                        result.Add(new(process.Id, process.ProcessName, process.StartTime.ToUniversalTime().Ticks));
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            }
        }
        return result.OrderBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id).ToArray();
    }

    internal Process OpenVerified()
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(Id);
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != StartedUtcTicks)
                throw new CaptureProcessUnavailableException("The selected application exited or its process ID changed. Refresh and select it again.");
            // Retain this process handle for the capture lifetime; never reselect by name.
            _ = process.Handle;
            return process;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or CaptureProcessUnavailableException)
        {
            process?.Dispose();
            throw new CaptureProcessUnavailableException("The selected application is no longer available. Refresh and select its current process.", error);
        }
    }
}

public sealed class CaptureProcessUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
