using System.Runtime.InteropServices;

namespace NksAudioLink.Server;

internal sealed class TimerResolution : IDisposable
{
    private readonly bool _active;

    public TimerResolution()
    {
        if (OperatingSystem.IsWindows())
            _active = timeBeginPeriod(1) == 0;
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
