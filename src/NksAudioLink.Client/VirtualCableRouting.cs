using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.CoreAudioApi;

namespace NksAudioLink.Client;

/// <summary>
/// Routes Windows playback through an already installed VB-CABLE device.
/// A journal permits restoration at the next launch after an unclean exit.
/// </summary>
public static class VirtualCableRouting
{
    private sealed record RouteJournal(string CablePlaybackId, Dictionary<Role, string> Previous);

    private static readonly string JournalPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NksAudioLink", "default-playback-backup.json");
    private static readonly Role[] Roles = [Role.Console, Role.Multimedia, Role.Communications];

    public static (AudioDevice Playback, AudioDevice Capture)? FindInstalledPair()
    {
        var devices = WasapiSource.ListDevices();
        var playback = devices.FirstOrDefault(d => d.IsRender &&
            d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase));
        var capture = devices.FirstOrDefault(d => !d.IsRender &&
            d.Name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(playback.Id) || string.IsNullOrEmpty(capture.Id)) return null;
        return (playback, capture);
    }

    public static void Activate(string cablePlaybackId)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        RestorePending();
        using var enumerator = new MMDeviceEnumerator();
        using var cable = enumerator.GetDevice(cablePlaybackId);
        if (cable.DataFlow != DataFlow.Render || cable.State != DeviceState.Active ||
            !cable.FriendlyName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("CABLE Input is not an active playback device. Enable it in Windows sound settings.");

        var previous = new Dictionary<Role, string>();
        foreach (var role in Roles)
        {
            using var current = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, role);
            previous[role] = current.ID;
        }

        // Persist before the first switch so even a crash during switching is recoverable.
        WriteJournal(new RouteJournal(cablePlaybackId, previous));
        try
        {
            using var policy = new PolicyConfig();
            foreach (var role in Roles)
                if (previous[role] != cablePlaybackId) policy.SetDefaultEndpoint(cablePlaybackId, role);
        }
        catch
        {
            try { RestorePending(); } catch { /* Preserve the original failure and the journal. */ }
            throw;
        }
    }

    /// <summary>Restores a role only while it still points to our cable endpoint.</summary>
    public static void RestorePending()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (!File.Exists(JournalPath)) return;
        RouteJournal? journal = JsonSerializer.Deserialize<RouteJournal>(File.ReadAllText(JournalPath));
        if (journal is null || string.IsNullOrEmpty(journal.CablePlaybackId) || journal.Previous is null)
            throw new InvalidDataException("Could not read the default playback device backup. Check default-playback-backup.json.");

        using var enumerator = new MMDeviceEnumerator();
        using var policy = new PolicyConfig();
        var errors = new List<Exception>();
        foreach (var (role, originalId) in journal.Previous)
        {
            try
            {
                using var current = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, role);
                // Respect a manual change made after AudioLink routed playback.
                if (current.ID == journal.CablePlaybackId && originalId != journal.CablePlaybackId)
                    policy.SetDefaultEndpoint(originalId, role);
            }
            catch (Exception ex) { errors.Add(new InvalidOperationException($"Could not restore the default output for {role}. Check Windows sound settings.", ex)); }
        }
        if (errors.Count > 0) throw new AggregateException("Could not fully restore the previous default output. Check Windows sound settings.", errors);
        File.Delete(JournalPath);
    }

    private static void WriteJournal(RouteJournal journal)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(JournalPath)!);
        string temporary = JournalPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(journal));
        File.Move(temporary, JournalPath, true);
    }

    // Windows PolicyConfig is the COM API used by the Windows sound control panel to
    // change defaults. Keep all native details here; callers only pass device IDs.
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        void GetMixFormat();
        void GetDeviceFormat();
        void ResetDeviceFormat();
        void SetDeviceFormat();
        void GetProcessingPeriod();
        void SetProcessingPeriod();
        void GetShareMode();
        void SetShareMode();
        void GetPropertyValue();
        void SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, Role role);
        void SetEndpointVisibility();
    }

    private sealed class PolicyConfig : IDisposable
    {
        private readonly IPolicyConfig _native;

        public PolicyConfig()
        {
            var type = Type.GetTypeFromCLSID(new Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9"))
                ?? throw new InvalidOperationException("Windows could not change the default audio output. Check sound settings.");
            _native = (IPolicyConfig)(Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("Windows could not change the default audio output. Check sound settings."));
        }

        public void SetDefaultEndpoint(string deviceId, Role role)
        {
            int hr = _native.SetDefaultEndpoint(deviceId, role);
            Marshal.ThrowExceptionForHR(hr);
        }

        public void Dispose() => Marshal.ReleaseComObject(_native);
    }
}
