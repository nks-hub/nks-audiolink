using System.Diagnostics;
using System.Windows;
using NksAudioLink.Core;

namespace NksAudioLink.App;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        // Keep the product version readable; detailed build identity is available on hover.
        VersionText.Text = $"Version {ProductVersion.Display}";
        VersionText.ToolTip = ProductVersion.Informational;
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
