using NksAudioLink.Core;

namespace NksAudioLink.Client;

/// <summary>A terminal server refusal. A new user action is required to try again.</summary>
public sealed class ServerRejectedException : Exception
{
    public RejectReason Reason { get; }

    public ServerRejectedException(RejectReason reason) : base(reason switch
    {
        RejectReason.Busy => "Another client is using the server. Disconnect it, then try again.",
        RejectReason.UnsupportedFormat => "The server does not support this audio format. Check the server version.",
        RejectReason.AddressDenied => "The server denied this PC's address. Check its allowed client addresses.",
        RejectReason.BadAuthentication => "The server rejected the shared key. Check that both sides use the same key.",
        _ => "The server rejected the connection. Check its settings."
    })
    {
        Reason = reason;
    }
}
