using NksAudioLink.Core;

namespace NksAudioLink.Client;

/// <summary>A terminal server refusal. A new user action is required to try again.</summary>
public sealed class ServerRejectedException : Exception
{
    public RejectReason Reason { get; }

    public ServerRejectedException(RejectReason reason) : base(reason switch
    {
        RejectReason.Busy => "Server už používá jiný klient. Odpojte jej a\u00A0připojte se znovu.",
        RejectReason.UnsupportedFormat => "Server nepodporuje tento formát zvuku. Zkontrolujte verzi serveru.",
        RejectReason.AddressDenied => "Server odmítl adresu tohoto počítače. Zkontrolujte povolené adresy na serveru.",
        RejectReason.BadAuthentication => "Server odmítl sdílený klíč. Zkontrolujte klíč na obou stranách.",
        _ => "Server odmítl připojení. Zkontrolujte jeho nastavení."
    })
    {
        Reason = reason;
    }
}
