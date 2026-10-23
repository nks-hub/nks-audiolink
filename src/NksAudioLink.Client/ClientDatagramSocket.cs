using System.Net;
using System.Net.Sockets;

namespace NksAudioLink.Client;

// Keep network failures distinguishable from capture/configuration failures and
// allow recovery tests to invalidate an otherwise real connected UDP socket.
internal interface IClientDatagramSocket : IDisposable
{
    void Connect(IPEndPoint endpoint);
    void Send(byte[] packet, int length);
    ValueTask<int> SendAsync(ReadOnlyMemory<byte> packet);
    ValueTask<UdpReceiveResult> ReceiveAsync(CancellationToken cancellationToken);
}

internal sealed class ClientDatagramSocket : IClientDatagramSocket
{
    private readonly UdpClient _udp = new(AddressFamily.InterNetwork);
    public void Connect(IPEndPoint endpoint) => _udp.Connect(endpoint);
    public void Send(byte[] packet, int length) => _udp.Send(packet, length);
    public ValueTask<int> SendAsync(ReadOnlyMemory<byte> packet) => _udp.SendAsync(packet);
    public ValueTask<UdpReceiveResult> ReceiveAsync(CancellationToken cancellationToken) => _udp.ReceiveAsync(cancellationToken);
    public void Dispose() => _udp.Dispose();
}
