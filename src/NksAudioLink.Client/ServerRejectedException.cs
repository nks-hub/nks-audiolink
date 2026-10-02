using NksAudioLink.Core;

namespace NksAudioLink.Client;

/// <summary>A terminal server refusal. A new user action is required to try again.</summary>
public sealed class ServerRejectedException : Exception
{
    public RejectReason Reason { get; }

    public ServerRejectedException(RejectReason reason) : base(reason switch
    {
        RejectReason.Busy => "Server už používá jiný klient.",
        RejectReason.UnsupportedFormat => "Server nepodporuje formát přenosu.",
        RejectReason.AddressDenied => "Server odmítl adresu tohoto počítače.",
        RejectReason.BadAuthentication => "Server odmítl sdílený klíč.",
        _ => "Server odmítl připojení."
    })
    {
        Reason = reason;
    }
}
