using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using NksAudioLink.Cli;
using NksAudioLink.Core;

namespace NksAudioLink.Client.Tests;

public sealed class TestToneProcessTests
{
    [Fact]
    public async Task RejectedActiveToneStopsEarlyAndReleasesSession()
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        start.ArgumentList.Add(typeof(CommandLineOptions).Assembly.Location);
        foreach (string argument in new[] { "test-tone", "--server", "127.0.0.1", "--port",
            ((IPEndPoint)udp.Client.LocalEndPoint!).Port.ToString(CultureInfo.InvariantCulture), "--seconds", "30" })
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        try
        {
            var hello = await udp.ReceiveAsync(timeout.Token);
            Assert.True(Protocol.TryRead(hello.Buffer, [], out var header, out _));
            Assert.Equal(PacketType.Hello, header.Type);
            uint session = header.Session;
            byte[] stats = new byte[StatsMessage.Size];
            new StatsMessage(true, 30, 0, 0, 0, 0, 0, 0, 0).Write(stats);
            await Reply(PacketType.Stats, stats);

            var audio = await udp.ReceiveAsync(timeout.Token);
            Assert.True(Protocol.TryRead(audio.Buffer, [], out header, out _));
            Assert.Equal(PacketType.Audio, header.Type);
            await Reply(PacketType.Reject, [(byte)RejectReason.Busy]);

            bool bye = false;
            while (!bye)
            {
                var response = await udp.ReceiveAsync(timeout.Token);
                bye = Protocol.TryRead(response.Buffer, [], out header, out _) && header.Type == PacketType.Bye;
            }
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(1, process.ExitCode);
            Assert.Contains("Another client", await error);
            Assert.DoesNotContain("Done.", await output);

            async Task Reply(PacketType type, byte[] payload)
            {
                byte[] response = new byte[Protocol.MaxDatagramSize];
                int length = Protocol.Write(response, new PacketHeader(type, PacketFlags.None, session, 1), payload);
                await udp.SendAsync(response.AsMemory(0, length), hello.RemoteEndPoint, timeout.Token);
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }
}
