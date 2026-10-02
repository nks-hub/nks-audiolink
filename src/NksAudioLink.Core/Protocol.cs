using System.Buffers.Binary;
using System.Security.Cryptography;

namespace NksAudioLink.Core;

public enum PacketType : byte
{
    Hello = 1, Audio = 2, Bye = 3, Stats = 4, Discover = 5,
    DiscoverReply = 6, Ping = 7, Pong = 8, Reject = 9
}

[Flags]
public enum PacketFlags : ushort
{
    None = 0, Auth = 1, Silent = 2, Takeover = 4
}

public readonly record struct PacketHeader(PacketType Type, PacketFlags Flags, uint Session, uint Sequence);

/// <summary>Version 1 UDP wire format. All multibyte fields are little endian.</summary>
public static class Protocol
{
    public const int HeaderSize = 16;
    public const int TagSize = 16;
    public const int MaxDatagramSize = 1399;
    public const int SampleRate = 48000;
    public const int Channels = 2;
    public const int FrameMs = 5;
    public const int SamplesPerFrame = SampleRate * FrameMs / 1000;
    public const int PcmBytesPerFrame = SamplesPerFrame * Channels * sizeof(short);

    public static int Write(Span<byte> destination, PacketHeader header, ReadOnlySpan<byte> body, ReadOnlySpan<byte> key = default)
    {
        bool authenticated = !key.IsEmpty;
        var flags = authenticated ? header.Flags | PacketFlags.Auth : header.Flags & ~PacketFlags.Auth;
        int length = checked(HeaderSize + body.Length + (authenticated ? TagSize : 0));
        if (length > MaxDatagramSize || destination.Length < length)
            throw new ArgumentException("Packet exceeds the datagram limit or output buffer.", nameof(destination));
        if (!Enum.IsDefined(header.Type) || (flags & ~(PacketFlags.Auth | PacketFlags.Silent | PacketFlags.Takeover)) != 0)
            throw new ArgumentException("Invalid packet header.", nameof(header));

        var packet = destination[..length];
        "NKAL"u8.CopyTo(packet);
        packet[4] = 1;
        packet[5] = (byte)header.Type;
        BinaryPrimitives.WriteUInt16LittleEndian(packet[6..], (ushort)flags);
        BinaryPrimitives.WriteUInt32LittleEndian(packet[8..], header.Session);
        BinaryPrimitives.WriteUInt32LittleEndian(packet[12..], header.Sequence);
        body.CopyTo(packet[HeaderSize..]);
        if (authenticated)
        {
            Span<byte> digest = stackalloc byte[32];
            HMACSHA256.HashData(key, packet[..^TagSize], digest);
            digest[..TagSize].CopyTo(packet[^TagSize..]);
            CryptographicOperations.ZeroMemory(digest);
        }
        return length;
    }

    public static bool TryRead(ReadOnlySpan<byte> packet, ReadOnlySpan<byte> key, out PacketHeader header, out ReadOnlySpan<byte> body)
    {
        header = default;
        body = default;
        if (packet.Length < HeaderSize || packet.Length > MaxDatagramSize || !packet[..4].SequenceEqual("NKAL"u8) || packet[4] != 1)
            return false;
        var type = (PacketType)packet[5];
        var flags = (PacketFlags)BinaryPrimitives.ReadUInt16LittleEndian(packet[6..]);
        if (!Enum.IsDefined(type) || (flags & ~(PacketFlags.Auth | PacketFlags.Silent | PacketFlags.Takeover)) != 0)
            return false;
        bool authenticated = (flags & PacketFlags.Auth) != 0;
        if (authenticated && packet.Length < HeaderSize + TagSize || !key.IsEmpty && !authenticated)
            return false;
        if (authenticated && !key.IsEmpty)
        {
            Span<byte> digest = stackalloc byte[32];
            HMACSHA256.HashData(key, packet[..^TagSize], digest);
            bool valid = CryptographicOperations.FixedTimeEquals(digest[..TagSize], packet[^TagSize..]);
            CryptographicOperations.ZeroMemory(digest);
            if (!valid) return false;
        }
        else if (authenticated) return false;

        header = new PacketHeader(type, flags,
            BinaryPrimitives.ReadUInt32LittleEndian(packet[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(packet[12..]));
        body = authenticated ? packet[HeaderSize..^TagSize] : packet[HeaderSize..];
        return true;
    }
}

public readonly record struct HelloMessage(uint SampleRate, byte Channels, byte Format, byte FrameMs, ushort TargetLatencyMs, string Name)
{
    public int Write(Span<byte> destination)
    {
        int nameBytes = System.Text.Encoding.UTF8.GetByteCount(Name);
        if (nameBytes > 64 || destination.Length < 10 + nameBytes)
            throw new ArgumentException("Name or output buffer is invalid.", nameof(destination));
        BinaryPrimitives.WriteUInt32LittleEndian(destination, SampleRate);
        destination[4] = Channels;
        destination[5] = Format;
        destination[6] = FrameMs;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[7..], TargetLatencyMs);
        destination[9] = (byte)nameBytes;
        System.Text.Encoding.UTF8.GetBytes(Name, destination[10..]);
        return 10 + nameBytes;
    }

    public static bool TryRead(ReadOnlySpan<byte> body, out HelloMessage message)
    {
        message = default;
        if (body.Length < 10 || body[9] > 64 || body.Length != 10 + body[9]) return false;
        try
        {
            var name = new System.Text.UTF8Encoding(false, true).GetString(body[10..]);
            message = new HelloMessage(BinaryPrimitives.ReadUInt32LittleEndian(body), body[4], body[5], body[6], BinaryPrimitives.ReadUInt16LittleEndian(body[7..]), name);
            return true;
        }
        catch (System.Text.DecoderFallbackException) { return false; }
    }

    public bool IsSupported => SampleRate == Protocol.SampleRate && Channels == Protocol.Channels && Format == 1 && FrameMs == Protocol.FrameMs && TargetLatencyMs is >= 10 and <= 200;
}

public static class AudioMessage
{
    public const int PrefixSize = 10;

    public static int Write(Span<byte> destination, ulong sampleIndex, ushort frameCount, ReadOnlySpan<byte> pcm, bool silent)
    {
        if (frameCount != Protocol.SamplesPerFrame || (silent ? !pcm.IsEmpty : pcm.Length != frameCount * Protocol.Channels * sizeof(short)) || destination.Length < PrefixSize + pcm.Length)
            throw new ArgumentException("Invalid audio frame.", nameof(pcm));
        BinaryPrimitives.WriteUInt64LittleEndian(destination, sampleIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[8..], frameCount);
        pcm.CopyTo(destination[PrefixSize..]);
        return PrefixSize + pcm.Length;
    }

    public static bool TryRead(ReadOnlySpan<byte> body, bool silent, out ulong sampleIndex, out ReadOnlySpan<byte> pcm)
    {
        sampleIndex = 0;
        pcm = default;
        if (body.Length < PrefixSize || BinaryPrimitives.ReadUInt16LittleEndian(body[8..]) != Protocol.SamplesPerFrame ||
            body.Length != PrefixSize + (silent ? 0 : Protocol.PcmBytesPerFrame)) return false;
        sampleIndex = BinaryPrimitives.ReadUInt64LittleEndian(body);
        pcm = body[PrefixSize..];
        return true;
    }
}
