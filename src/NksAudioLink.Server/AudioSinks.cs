using System.Buffers.Binary;
using NksAudioLink.Core;

namespace NksAudioLink.Server;

public interface IAudioSink : IDisposable
{
    int DelayMs { get; }
    void Write(ReadOnlySpan<short> samples);
}

public sealed class NullSink : IAudioSink
{
    public int DelayMs => 0;
    public void Write(ReadOnlySpan<short> samples) { }
    public void Dispose() { }
}

public sealed class WavSink : IAudioSink
{
    private readonly FileStream _stream;
    private readonly byte[] _buffer = new byte[Protocol.PcmBytesPerFrame];
    private long _audioBytes;

    public WavSink(string path)
    {
        _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        _stream.Write(new byte[44]);
    }

    public int DelayMs => 0;

    public void Write(ReadOnlySpan<short> samples)
    {
        if (samples.Length != Protocol.SamplesPerFrame * Protocol.Channels)
            throw new ArgumentException("Expected one 5 ms frame.", nameof(samples));
        if (_audioBytes > uint.MaxValue - 36L - _buffer.Length)
            throw new IOException("WAV reached the RIFF size limit.");
        for (int i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(_buffer.AsSpan(i * 2), samples[i]);
        _stream.Write(_buffer);
        _audioBytes += _buffer.Length;
    }

    public void Dispose()
    {
        try
        {
            Span<byte> header = stackalloc byte[44];
            "RIFF"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..], checked((uint)(36 + _audioBytes)));
            "WAVEfmt "u8.CopyTo(header[8..]);
            BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);
            BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(header[22..], Protocol.Channels);
            BinaryPrimitives.WriteUInt32LittleEndian(header[24..], Protocol.SampleRate);
            BinaryPrimitives.WriteUInt32LittleEndian(header[28..], Protocol.SampleRate * Protocol.Channels * sizeof(short));
            BinaryPrimitives.WriteUInt16LittleEndian(header[32..], Protocol.Channels * sizeof(short));
            BinaryPrimitives.WriteUInt16LittleEndian(header[34..], 16);
            "data"u8.CopyTo(header[36..]);
            BinaryPrimitives.WriteUInt32LittleEndian(header[40..], checked((uint)_audioBytes));
            _stream.Position = 0;
            _stream.Write(header);
        }
        finally { _stream.Dispose(); }
    }
}
