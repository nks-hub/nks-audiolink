using System.Configuration;
using System.Data;
using System.Windows;

namespace NksAudioLink.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    private Mutex? _instanceMutex;
    private bool _ownsInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(false, @"Local\NksAudioLink.Client.SingleInstance");
        try { _ownsInstanceMutex = _instanceMutex.WaitOne(0); }
        catch (AbandonedMutexException) { _ownsInstanceMutex = true; }
        if (!_ownsInstanceMutex)
        {
            System.Windows.MessageBox.Show("NKS AudioLink už běží. Najdete jej v\u00A0oznamovací oblasti.", "NKS AudioLink",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsInstanceMutex) _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
