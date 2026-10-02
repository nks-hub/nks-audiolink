using NksAudioLink.Core;
using NksAudioLink.Server;

try
{
    if (args.Length == 0 || args[0] is "help" or "--help")
    {
        Console.WriteLine("nksaudio-server run [--config FILE] [--sink null|wav:FILE|alsa:DEVICE] [--port PORT]");
        Console.WriteLine("nksaudio-server devices");
        return;
    }
    if (args[0] == "devices")
    {
        foreach (var card in AlsaSink.ListCards()) Console.WriteLine(card);
        return;
    }
    if (args[0] != "run") throw new ArgumentException("Unknown server command.");
    var config = new ServerConfig();
    for (int i = 1; i < args.Length; i++)
    {
        if (i + 1 >= args.Length) throw new ArgumentException($"Missing value for {args[i]}.");
        switch (args[i++])
        {
            case "--config": config = ServerConfig.Load(args[i]); break;
            case "--sink": config = config with { Sink = args[i] }; break;
            case "--port": config = config with { Port = int.Parse(args[i]) }; break;
            default: throw new ArgumentException($"Unknown option: {args[i - 1]}");
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
