using System.Diagnostics;
using System.Reflection;
using System.Windows;

namespace NksAudioLink.App;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        var assembly = typeof(AboutWindow).Assembly;
        string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString() ?? "unknown";
        // Keep the product version readable; detailed build identity is available on hover.
        VersionText.Text = $"Version {version.Split('+')[0]}";
        VersionText.ToolTip = version;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Documentation_Click(object sender, RoutedEventArgs e) => OpenLink("https://github.com/nks-hub/nks-audiolink#readme");
    private void Support_Click(object sender, RoutedEventArgs e) => OpenLink("https://github.com/nks-hub/nks-audiolink/issues");
    private void Notices_Click(object sender, RoutedEventArgs e) => OpenLink("https://github.com/nks-hub/nks-audiolink/blob/main/THIRD_PARTY_NOTICES.md");
    private void License_Click(object sender, RoutedEventArgs e) => OpenLink("https://github.com/nks-hub/nks-audiolink/blob/main/LICENSE");

    private void OpenLink(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { LinkError.Text = $"Could not open your browser. {ex.Message}"; }
    }
}
