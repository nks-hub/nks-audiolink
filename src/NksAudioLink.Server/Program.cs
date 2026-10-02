using NksAudioLink.Core;
using NksAudioLink.Server;

try
{
    if (args.Length == 0 || args[0] is "help" or "--help")
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  NksAudioLink.Server run [--config FILE] [--sink null|wav:FILE|alsa|alsa:DEVICE] [--port PORT]");
        Console.WriteLine("    Receive UDP audio. FILE is a JSON config path for --config or a WAV output path for wav:FILE.");
        Console.WriteLine("    DEVICE is an ALSA device name, such as default. PORT is the UDP port number.");
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
    var config = new ServerConfig();
    for (int i = 1; i < args.Length; i++)
    {
        if (i + 1 >= args.Length) throw new ArgumentException($"Option '{args[i]}' needs a value.");
        switch (args[i++])
        {
            case "--config": config = ServerConfig.Load(args[i]); break;
            case "--sink": config = config with { Sink = args[i] }; break;
            case "--port": config = config with { Port = int.Parse(args[i]) }; break;
            default: throw new ArgumentException($"Unknown option '{args[i - 1]}'. Run --help to see the options.");
        }
    }
    config.Validate();
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
