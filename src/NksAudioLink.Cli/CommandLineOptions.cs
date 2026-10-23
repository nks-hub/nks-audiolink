using System.Globalization;
using NksAudioLink.Core;

namespace NksAudioLink.Cli;

public sealed record CommandLineOptions(ClientConfig Config, bool Loopback, bool Takeover, int Seconds, string? DeviceId, int? ProcessId = null)
{
    public static CommandLineOptions Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("discover" or "send" or "test-tone"))
            throw new ArgumentException("Unknown command. Run NksAudioLink.Cli.exe --help to list commands.");

        string command = args[0];
        string? configPath = null;
        for (int i = 1; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length) throw new ArgumentException($"Missing value after {args[i]}.");
            if (args[i] == "--config") configPath = args[i + 1];
        }

        // Explicit options override the configuration regardless of argument order.
        var config = configPath is null ? new ClientConfig() : ClientConfig.Load(configPath);
        bool loopback = true;
        bool takeover = false;
        int seconds = command == "test-tone" ? 5 : 0;
        string? deviceId = null;
        int? processId = null;
        bool explicitMode = false;
        for (int i = 1; i < args.Length; i += 2)
        {
            string value = args[i + 1];
            switch (args[i])
            {
                case "--config": break;
                case "--port": config = config with { Port = int.Parse(value, CultureInfo.InvariantCulture) }; break;
                case "--server" when command != "discover": config = config with { Server = value }; break;
                case "--gain" when command != "discover": config = config with { Gain = double.Parse(value, CultureInfo.InvariantCulture) }; break;
                case "--seconds" when command != "discover": seconds = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "--latency" when command == "send": config = config with { TargetLatencyMs = int.Parse(value, CultureInfo.InvariantCulture) }; break;
                case "--adaptive-buffer" when command == "send": config = config with { AdaptiveBuffer = bool.Parse(value) }; break;
                case "--buffer-min" when command == "send": config = config with { AdaptiveMinLatencyMs = int.Parse(value, CultureInfo.InvariantCulture) }; break;
                case "--buffer-max" when command == "send": config = config with { AdaptiveMaxLatencyMs = int.Parse(value, CultureInfo.InvariantCulture) }; break;
                case "--process" when command == "send": processId = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "--takeover" when command == "send": takeover = bool.Parse(value); break;
                case "--device" when command == "send": deviceId = value; break;
                case "--mode" when command == "send":
                    explicitMode = true;
                    loopback = value switch { "loopback" => true, "capture" => false, _ => throw new ArgumentException("Use loopback or capture with --mode.") };
                    break;
                default: throw new ArgumentException($"Unknown option: {args[i]}");
            }
        }
        config.Validate();
        if (command == "test-tone" && config.AdaptiveBuffer)
            throw new ArgumentException("Adaptive buffering is supported by send only. Disable AdaptiveBuffer in the test-tone configuration.");
        if (processId is <= 0) throw new ArgumentOutOfRangeException(nameof(args), "Use a positive PID with --process.");
        if (processId is not null && (deviceId is not null || explicitMode))
            throw new ArgumentException("Use --process PID on its own; it cannot be combined with --mode or --device.");
        if (seconds < (command == "test-tone" ? 1 : 0) || seconds > 3600)
            throw new ArgumentOutOfRangeException(nameof(args), "Use --seconds between 1 and 3600 (or 0 for continuous send).");
        return new(config, loopback, takeover, seconds, deviceId, processId);
    }
}
