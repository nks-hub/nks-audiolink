using System.Runtime.InteropServices;

namespace NksAudioLink.Client;

public sealed class WindowsTimerResolution : IDisposable
{
    private readonly bool _active;

    public WindowsTimerResolution()
    {
        if (OperatingSystem.IsWindows()) _active = timeBeginPeriod(1) == 0;
    }

    public void Dispose()
    {
        if (_active) _ = timeEndPeriod(1);
    }

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint timeEndPeriod(uint period);
}
