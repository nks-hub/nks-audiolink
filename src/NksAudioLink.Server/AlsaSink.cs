using System.Runtime.InteropServices;
using NksAudioLink.Core;

namespace NksAudioLink.Server;

public sealed class AlsaSink : IAudioSink
{
    private IntPtr _pcm;

    public AlsaSink(string device, int latencyMs = 20)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Výstup ALSA je dostupný jen v Linuxu.");
        if (latencyMs is < 5 or > 200)
            throw new ArgumentOutOfRangeException(nameof(latencyMs), "Latence výstupu musí být 5 až 200\u00A0ms.");
        int result = snd_pcm_open(out _pcm, device, 0, 0);
        if (result < 0) throw Error("open", result);
        try
        {
            int format = snd_pcm_format_value("S16_LE");
            if (format < 0) throw new InvalidOperationException("Knihovna ALSA nezná formát S16_LE.");
            result = snd_pcm_set_params(_pcm, format, 3, Protocol.Channels, Protocol.SampleRate, 1, (uint)(latencyMs * 1000));
            if (result < 0) throw Error("set_params", result);
        }
        catch
        {
            snd_pcm_close(_pcm);
            _pcm = IntPtr.Zero;
            throw;
        }
    }

    public int DelayMs
    {
        get
        {
            if (_pcm == IntPtr.Zero || snd_pcm_delay(_pcm, out long frames) < 0) return 0;
            return (int)Math.Clamp(frames * 1000 / Protocol.SampleRate, 0, 1000);
        }
    }

    public unsafe void Write(ReadOnlySpan<short> samples)
    {
        if (samples.Length != Protocol.SamplesPerFrame * Protocol.Channels)
            throw new ArgumentException("Očekává se jeden zvukový rámec o délce 5\u00A0ms.", nameof(samples));
        if (_pcm == IntPtr.Zero) throw new ObjectDisposedException(nameof(AlsaSink), "Zařízení ALSA je zavřené.");
        fixed (short* pcm = samples)
        {
            int written = 0;
            while (written < Protocol.SamplesPerFrame)
            {
                long result = snd_pcm_writei(_pcm, (IntPtr)(pcm + written * Protocol.Channels),
                    (ulong)(Protocol.SamplesPerFrame - written));
                if (result < 0)
                {
                    int recovered = snd_pcm_recover(_pcm, (int)result, 1);
                    if (recovered < 0) throw Error("writei/recover", recovered);
                    continue;
                }
                if (result == 0) throw new IOException("Zápis do zařízení ALSA nepřenesl žádné vzorky.");
                written += checked((int)result);
            }
        }
    }

    public void Dispose()
    {
        if (_pcm != IntPtr.Zero)
        {
            snd_pcm_close(_pcm);
            _pcm = IntPtr.Zero;
        }
    }

    public static IEnumerable<string> ListCards()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Výpis zařízení ALSA je dostupný jen v Linuxu.");
        int card = -1;
        while (true)
        {
            int result = snd_card_next(ref card);
            if (result < 0) throw Error("card_next", result);
            if (card < 0) yield break;
            result = snd_card_get_name(card, out IntPtr name);
            if (result < 0) throw Error("card_get_name", result);
            try { yield return $"{card}: {Marshal.PtrToStringUTF8(name)}"; }
            finally { free(name); }
        }
    }

    private static Exception Error(string operation, long code) =>
        new IOException($"Operace ALSA {operation} selhala: {Marshal.PtrToStringAnsi(snd_strerror((int)code))} (kód {code}).");

    [DllImport("libasound.so.2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int snd_pcm_open(out IntPtr pcm, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int stream, int mode);
    [DllImport("libasound.so.2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int snd_pcm_set_params(IntPtr pcm, int format, int access, int channels, int rate, int softResample, uint latencyUs);
    [DllImport("libasound.so.2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int snd_pcm_format_value([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport("libasound.so.2", CallingConvention = CallingConvention.Cdecl)]
    private static extern long snd_pcm_writei(IntPtr pcm, IntPtr buffer, ulong frames);
    [DllImport("libasound.so.2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int snd_pcm_recover(IntPtr pcm, int err, int silent);
    [DllImport("libasound.so.2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int snd_pcm_delay(IntPtr pcm, out long frames);
    [DllImport("libasound.so.2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int snd_pcm_close(IntPtr pcm);
    [DllImport("libasound.so.2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int snd_card_next(ref int card);
    [DllImport("libasound.so.2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int snd_card_get_name(int card, out IntPtr name);
    [DllImport("libasound.so.2", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr snd_strerror(int errnum);
    [DllImport("libc.so.6", CallingConvention = CallingConvention.Cdecl)]
    private static extern void free(IntPtr pointer);
}
