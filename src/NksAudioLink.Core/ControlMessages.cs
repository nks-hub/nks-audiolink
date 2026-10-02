using System.Buffers.Binary;
using System.Text;

namespace NksAudioLink.Core;

public readonly record struct StatsMessage(bool Accepted, ushort BufferMs, ushort SinkDelayMs,
    uint Underruns, uint Overruns, uint Lost, uint Late, int RatioPpm, ulong EchoTicks)
{
    public const int Size = 33;

    public int Write(Span<byte> destination)
    {
        if (destination.Length < Size) throw new ArgumentException("Stats buffer too small.", nameof(destination));
        destination[0] = Accepted ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[1..], BufferMs);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[3..], SinkDelayMs);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[5..], Underruns);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[9..], Overruns);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[13..], Lost);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[17..], Late);
        BinaryPrimitives.WriteInt32LittleEndian(destination[21..], RatioPpm);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[25..], EchoTicks);
        return Size;
    }

    public static bool TryRead(ReadOnlySpan<byte> body, out StatsMessage message)
    {
        message = default;
        if (body.Length != Size || body[0] > 1) return false;
        message = new StatsMessage(body[0] == 1,
            BinaryPrimitives.ReadUInt16LittleEndian(body[1..]),
            BinaryPrimitives.ReadUInt16LittleEndian(body[3..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[5..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[9..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[13..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[17..]),
            BinaryPrimitives.ReadInt32LittleEndian(body[21..]),
            BinaryPrimitives.ReadUInt64LittleEndian(body[25..]));
        return true;
    }
}

public readonly record struct DiscoverReplyMessage(string Name, uint SampleRate, byte Channels, bool AuthRequired, string Sink)
{
    public int Write(Span<byte> destination)
    {
        int nameSize = Encoding.UTF8.GetByteCount(Name);
        int sinkSize = Encoding.UTF8.GetByteCount(Sink);
        if (nameSize > 64 || sinkSize > 128 || destination.Length < 9 + nameSize + sinkSize)
            throw new ArgumentException("Discovery buffer or field length is invalid.", nameof(destination));
        destination[0] = (byte)nameSize;
        Encoding.UTF8.GetBytes(Name, destination[1..]);
        int offset = 1 + nameSize;
        destination[offset] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[(offset + 1)..], SampleRate);
        destination[offset + 5] = Channels;
        destination[offset + 6] = AuthRequired ? (byte)1 : (byte)0;
        destination[offset + 7] = (byte)sinkSize;
        Encoding.UTF8.GetBytes(Sink, destination[(offset + 8)..]);
        return offset + 8 + sinkSize;
    }

    public static bool TryRead(ReadOnlySpan<byte> body, out DiscoverReplyMessage message)
    {
        message = default;
        if (body.Length < 9 || body[0] > 64 || body.Length < 9 + body[0]) return false;
        int offset = 1 + body[0];
        int sinkSize = body[offset + 7];
        if (body[offset] != 1 || body[offset + 6] > 1 || sinkSize > 128 || body.Length != offset + 8 + sinkSize) return false;
        try
        {
            var utf8 = new UTF8Encoding(false, true);
            message = new DiscoverReplyMessage(utf8.GetString(body.Slice(1, body[0])),
                BinaryPrimitives.ReadUInt32LittleEndian(body[(offset + 1)..]),
                body[offset + 5], body[offset + 6] == 1,
                utf8.GetString(body.Slice(offset + 8, sinkSize)));
            return true;
        }
        catch (DecoderFallbackException) { return false; }
    }
}

public enum RejectReason : byte { Busy = 1, UnsupportedFormat = 2, AddressDenied = 3, BadAuthentication = 4 }
