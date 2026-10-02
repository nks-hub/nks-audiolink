using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace NksAudioLink.Core;

public sealed record ServerConfig
{
    public int Port { get; init; } = 7355;
    public string Name { get; init; } = "NKS AudioLink";
    public string Sink { get; init; } = "null";
    public string[] AllowCidrs { get; init; } = ["127.0.0.0/8", "::1/128"];
    public string? PskBase64 { get; init; }
    public int IdleReleaseSec { get; init; } = 5;
    public int TargetLatencyMs { get; init; } = 30;

    public static ServerConfig Load(string path) =>
        JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(path), JsonOptions()) ?? throw new InvalidDataException("Empty server configuration.");

    public void Validate()
    {
        if (Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(Name) || Name.Length > 64 ||
            string.IsNullOrWhiteSpace(Sink) || AllowCidrs is null || AllowCidrs.Length == 0 ||
            IdleReleaseSec is < 1 or > 300 || TargetLatencyMs is < 10 or > 200 || TargetLatencyMs % 5 != 0)
            throw new InvalidDataException("Invalid server configuration.");
        foreach (string cidr in AllowCidrs) _ = CidrRange.Parse(cidr);
        _ = GetPsk();
    }

    public byte[]? GetPsk()
    {
        if (string.IsNullOrEmpty(PskBase64)) return null;
        byte[] key;
        try { key = Convert.FromBase64String(PskBase64); }
        catch (FormatException ex) { throw new InvalidDataException("PSK must be Base64.", ex); }
        if (key.Length < 16) throw new InvalidDataException("PSK must contain at least 16 random bytes.");
        return key;
    }

    internal static JsonSerializerOptions JsonOptions() => new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
}

public sealed record ClientConfig
{
    public string Server { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 7355;
    public int TargetLatencyMs { get; init; } = 30;
    public double Gain { get; init; } = 1;
    public string? PskBase64 { get; init; }

    public static ClientConfig Load(string path) =>
        JsonSerializer.Deserialize<ClientConfig>(File.ReadAllText(path), ServerConfig.JsonOptions()) ?? throw new InvalidDataException("Empty client configuration.");

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Server) || Port is < 1 or > 65535 ||
            TargetLatencyMs is < 10 or > 200 || TargetLatencyMs % 5 != 0 ||
            !double.IsFinite(Gain) || Gain is < 0 or > 4)
            throw new InvalidDataException("Invalid client configuration.");
        if (PskBase64 is not null)
            _ = (new ServerConfig { PskBase64 = PskBase64 }).GetPsk();
    }
}

public readonly record struct CidrRange(IPAddress Network, int PrefixBits)
{
    public static CidrRange Parse(string text)
    {
        string[] parts = text.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) ||
            !int.TryParse(parts[1], out int bits) || bits < 0 || bits > address.GetAddressBytes().Length * 8)
            throw new InvalidDataException($"Invalid CIDR range: {text}");
        return new CidrRange(address, bits);
    }

    public bool Contains(IPAddress address)
    {
        if (Network.AddressFamily != address.AddressFamily) return false;
        var networkBytes = Network.GetAddressBytes();
        var addressBytes = address.GetAddressBytes();
        int whole = PrefixBits / 8;
        int rest = PrefixBits % 8;
        for (int i = 0; i < whole; i++)
            if (networkBytes[i] != addressBytes[i]) return false;
        return rest == 0 || ((networkBytes[whole] ^ addressBytes[whole]) & (0xff << (8 - rest))) == 0;
    }
}
