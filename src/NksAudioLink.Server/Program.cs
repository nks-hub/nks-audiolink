using NksAudioLink.Core;
using NksAudioLink.Server;

try
{
    if (args.Length == 0 || args[0] is "help" or "--help")
    {
        Console.WriteLine("Použití:");
        Console.WriteLine("  NksAudioLink.Server run [--config FILE] [--sink null|wav:FILE|alsa:DEVICE] [--port PORT]");
        Console.WriteLine("    Spustí příjem zvuku. FILE je cesta k\u00A0souboru JSON pro --config nebo k\u00A0souboru WAV pro wav:FILE.");
        Console.WriteLine("    DEVICE je název zařízení ALSA, třeba default. PORT je číslo UDP portu.");
        Console.WriteLine("  NksAudioLink.Server devices");
        Console.WriteLine("    Vypíše dostupné zvukové karty ALSA.");
        return;
    }
    if (args[0] == "devices")
    {
        foreach (var card in AlsaSink.ListCards()) Console.WriteLine(card);
        return;
    }
    if (args[0] != "run") throw new ArgumentException($"Neznámý příkaz „{args[0]}“. Použijte run nebo devices.");
    var config = new ServerConfig();
    for (int i = 1; i < args.Length; i++)
    {
        if (i + 1 >= args.Length) throw new ArgumentException($"Za volbou „{args[i]}“ chybí hodnota.");
        switch (args[i++])
        {
            case "--config": config = ServerConfig.Load(args[i]); break;
            case "--sink": config = config with { Sink = args[i] }; break;
            case "--port": config = config with { Port = int.Parse(args[i]) }; break;
            default: throw new ArgumentException($"Neznámá volba „{args[i - 1]}“. Seznam voleb zobrazí --help.");
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
