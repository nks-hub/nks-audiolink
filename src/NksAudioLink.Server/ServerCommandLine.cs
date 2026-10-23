using System.Globalization;
using NksAudioLink.Core;

namespace NksAudioLink.Server;

public static class ServerCommandLine
{
    public static ServerConfig Parse(string[] args)
    {
        if (args.Length == 0 || args[0] != "run")
            throw new ArgumentException("Use run to start the audio receiver.");
        string? configPath = null;
        for (int i = 1; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length) throw new ArgumentException($"Option '{args[i]}' needs a value.");
            if (args[i] == "--config") configPath = args[i + 1];
        }
        var config = configPath is null ? new ServerConfig() : ServerConfig.Load(configPath);
        for (int i = 1; i < args.Length; i += 2)
        {
            string value = args[i + 1];
            config = args[i] switch
            {
                "--config" => config,
                "--sink" => config with { Sink = value },
                "--port" => config with { Port = int.Parse(value, CultureInfo.InvariantCulture) },
                "--http-port" => config with { HttpPort = int.Parse(value, CultureInfo.InvariantCulture) },
                "--http-bind" => config with { HttpBindAddress = value },
                _ => throw new ArgumentException($"Unknown option '{args[i]}'. Run --help to see the options.")
            };
        }
        config.Validate();
        return config;
    }
}
