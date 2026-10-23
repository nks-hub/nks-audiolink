using NksAudioLink.Core;
using NksAudioLink.Server;

try
{
    if (args is ["--version", ..])
    {
        Console.WriteLine($"NKS AudioLink Server {ProductVersion.Informational}");
        return;
    }
    if (args.Length == 0 || args[0] is "help" or "--help")
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  NksAudioLink.Server --version");
        Console.WriteLine("    Print the product version and build identity without starting the server.");
        Console.WriteLine("  NksAudioLink.Server run [--config FILE] [--sink null|wav:FILE|alsa|alsa:DEVICE] [--port PORT]");
        Console.WriteLine("    Receive UDP audio. FILE is a JSON config path for --config or a WAV output path for wav:FILE.");
        Console.WriteLine("    DEVICE is an ALSA device name, such as default. PORT is the UDP port number.");
        Console.WriteLine("  Optional status dashboard: --http-port PORT [--http-bind IPv4]");
        Console.WriteLine("    HTTP is disabled by default (port 0); binds to 127.0.0.1 and uses allowCidrs.");
        Console.WriteLine("  NksAudioLink.Server devices");
        Console.WriteLine("    List available ALSA sound cards.");
        return;
    }
    if (args[0] == "devices")
    {
        foreach (var card in AlsaSink.ListCards()) Console.WriteLine(card);
        return;
    }
    if (args[0] != "run") throw new ArgumentException($"Unknown command '{args[0]}'. Use run or devices.");
    var config = ServerCommandLine.Parse(args);
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    await new AudioServer(config).RunAsync(stop.Token);
}
catch (OperationCanceledException) { }
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    Environment.ExitCode = 1;
}
