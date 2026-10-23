using System.Diagnostics;
using System.Reflection;
using NksAudioLink.Cli;

namespace NksAudioLink.Client.Tests;

public sealed class VersionProcessTests
{
    [Fact]
    public async Task VersionPreservesPrereleaseAndDoesNotReadConfigurationOrOpenAudio()
    {
        var assembly = typeof(CommandLineOptions).Assembly;
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(assembly.Location);
        start.ArgumentList.Add("--version");
        start.ArgumentList.Add("--config");
        start.ArgumentList.Add(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.json"));
        using var process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.Empty(await error);
            string expected = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
            Assert.Equal($"NKS AudioLink CLI {expected}", (await output).Trim());
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }
}
