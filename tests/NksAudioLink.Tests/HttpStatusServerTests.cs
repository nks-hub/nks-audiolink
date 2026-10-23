using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using NksAudioLink.Core;
using NksAudioLink.Server;

namespace NksAudioLink.Tests;

public class HttpStatusServerTests
{
    [Fact]
    public async Task DisabledHttpDoesNotSampleOrAllocateHistoryWork()
    {
        int reads = 0;
        using var stop = new CancellationTokenSource();
        Task running = new HttpStatusServer(new ServerConfig(), () => { Interlocked.Increment(ref reads); throw new InvalidOperationException(); }).RunAsync(stop.Token);
        await Task.Delay(50);
        Assert.Equal(0, reads);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task HistorySurvivesNewBrowserRequestsAndValidatesItsBoundedReadOnlyQuery()
    {
        int port = FreeTcpPort();
        var config = new ServerConfig { HttpPort = port, PskBase64 = Convert.ToBase64String(new byte[32]), Name = "private-name" };
        var status = new AudioServer(config);
        using var stop = new CancellationTokenSource();
        Task running = new HttpStatusServer(config, status.GetStatus).RunAsync(stop.Token);
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        try
        {
            using var ready = await GetWhenReady(http, "api/history");
            await Task.Delay(1100);
            var first = (await http.GetFromJsonAsync<StatusHistoryResponse>("api/history?minutes=60"))!;
            Assert.InRange(first.Samples.Length, 2, 4);
            Assert.All(first.Samples, sample => Assert.Null(sample.Session));
            var incremental = (await http.GetFromJsonAsync<StatusHistoryResponse>($"api/history?minutes=60&after={first.NextSequence}&epoch={first.Epoch}"))!;
            Assert.False(incremental.Reset);
            Assert.All(incremental.Samples, sample => Assert.True(sample.Sequence > first.NextSequence));
            // A new browser has no cursor; it still sees samples collected earlier.
            var refresh = (await http.GetFromJsonAsync<StatusHistoryResponse>("api/history?minutes=60"))!;
            Assert.Equal(first.Epoch, refresh.Epoch);
            Assert.Equal(first.Samples[0], refresh.Samples[0]);
            string json = await http.GetStringAsync("api/history");
            Assert.DoesNotContain(config.Name, json);
            Assert.DoesNotContain(config.PskBase64, json);
            Assert.DoesNotContain("127.0.0.1", json);
            foreach (string query in new[] { "minutes=0", "minutes=2", "minutes=61", "minutes=999999999999", "after=-1", "after=abc", "after=9223372036854775808" })
            {
                using var invalid = await http.GetAsync("api/history?" + query);
                Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            }
            using var write = await http.PostAsync("api/history", null);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, write.StatusCode);
        }
        finally
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task DashboardAndApiReportActualSessionAndClearItAfterBye()
    {
        int httpPort = FreeTcpPort();
        var config = new ServerConfig { Port = FreeUdpPort(), HttpPort = httpPort, Name = "<test receiver>" };
        var server = new AudioServer(config);
        using var stop = new CancellationTokenSource();
        Task running = server.RunAsync(stop.Token);
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{httpPort}/"), Timeout = TimeSpan.FromSeconds(3) };
        using var udp = new UdpClient();
        udp.Connect(IPAddress.Loopback, config.Port);
        try
        {
            using var page = await GetWhenReady(http, "/");
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Equal("text/html", page.Content.Headers.ContentType!.MediaType);
            Assert.Contains("Server monitor", await page.Content.ReadAsStringAsync());
            Assert.Contains("no-store", page.Headers.CacheControl!.ToString());
            Assert.Contains("frame-ancestors 'none'", page.Headers.GetValues("Content-Security-Policy").Single());
            var idle = await http.GetFromJsonAsync<ServerStatus>("api/stats");
            Assert.NotNull(idle);
            Assert.Equal(config.Name, idle.Name);
            string expectedVersion = typeof(AudioServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
            Assert.Equal(expectedVersion, idle.Version);
            Assert.Null(idle.Session);

            byte[] hello = new byte[74];
            int length = new HelloMessage(48000, 2, 1, 5, 30, "http-test").Write(hello);
            await Send(udp, PacketType.Hello, hello[..length]);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await udp.ReceiveAsync(timeout.Token);
            var active = await http.GetFromJsonAsync<ServerStatus>("api/stats");
            Assert.NotNull(active!.Session);
            Assert.Equal(42u, active.Session.Id);
            Assert.Equal(48000, active.SampleRate);
            Assert.InRange(active.Session.LastPacketAgeMs, 0, 3000);
            Assert.NotNull(active.Session.Levels);
            Assert.Equal(new AudioLevelSnapshot(-90, -90, -90, -90, false), active.Session.Levels);
            using var levelsJson = JsonDocument.Parse(await http.GetStringAsync("api/stats"));
            Assert.Equal(-90, levelsJson.RootElement.GetProperty("session").GetProperty("levels").GetProperty("leftRmsDbfs").GetDouble());
            await Send(udp, PacketType.Bye, []);
            for (int i = 0; i < 50 && server.GetStatus().Session is not null; i++) await Task.Delay(10);
            Assert.Null((await http.GetFromJsonAsync<ServerStatus>("api/stats"))!.Session);
        }
        finally
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        // Shutdown must free the HTTP listener as well as the audio socket.
        using var rebound = new TcpListener(IPAddress.Loopback, httpPort);
        rebound.Start();
    }

    [Fact]
    public async Task ApiOmitsSecretsAndRejectsWritesAndUnknownRoutes()
    {
        int port = FreeTcpPort();
        var config = new ServerConfig { HttpPort = port, PskBase64 = Convert.ToBase64String(new byte[32]) };
        var status = new AudioServer(config);
        using var stop = new CancellationTokenSource();
        Task running = new HttpStatusServer(config, status.GetStatus).RunAsync(stop.Token);
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        try
        {
            using var response = await GetWhenReady(http, "api/stats");
            string json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            Assert.False(doc.RootElement.TryGetProperty("pskBase64", out _));
            Assert.DoesNotContain(config.PskBase64, json);
            Assert.DoesNotContain("allowCidrs", json);
            using var write = await http.PostAsync("api/stats", null);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, write.StatusCode);
            using var missing = await http.GetAsync("missing");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        finally
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task LoopbackMonitorRejectsUntrustedHostNames()
    {
        int port = FreeTcpPort();
        var config = new ServerConfig { HttpPort = port };
        var status = new AudioServer(config);
        using var stop = new CancellationTokenSource();
        Task running = new HttpStatusServer(config, status.GetStatus).RunAsync(stop.Token);
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        try
        {
            using var ready = await GetWhenReady(http, "api/stats");
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
            using var malicious = new HttpRequestMessage(HttpMethod.Get, "api/stats");
            malicious.Headers.Host = $"rebound.example:{port}";
            using var denied = await http.SendAsync(malicious);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.Empty(await denied.Content.ReadAsStringAsync());

            using var local = new HttpRequestMessage(HttpMethod.Get, "api/stats");
            local.Headers.Host = $"localhost:{port}";
            using var allowed = await http.SendAsync(local);
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
            Assert.False(allowed.Headers.Contains("Access-Control-Allow-Origin"));
        }
        finally
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task HttpUsesTheConfiguredAllowedNetworks()
    {
        int port = FreeTcpPort();
        var config = new ServerConfig { HttpPort = port, AllowCidrs = ["192.0.2.0/24"] };
        var status = new AudioServer(config);
        using var stop = new CancellationTokenSource();
        Task running = new HttpStatusServer(config, status.GetStatus).RunAsync(stop.Token);
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        try
        {
            using var response = await GetWhenReady(http, "api/stats");
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsStringAsync());
        }
        finally
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    private static async Task<HttpResponseMessage> GetWhenReady(HttpClient http, string path)
    {
        for (int i = 0; ; i++)
        {
            try { return await http.GetAsync(path); }
            catch (HttpRequestException) when (i < 50) { await Task.Delay(20); }
        }
    }

    private static async Task Send(UdpClient udp, PacketType type, byte[] body)
    {
        byte[] packet = new byte[Protocol.MaxDatagramSize];
        int length = Protocol.Write(packet, new PacketHeader(type, PacketFlags.None, 42, 1), body);
        await udp.SendAsync(packet.AsMemory(0, length));
    }

    private static int FreeTcpPort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }

    private static int FreeUdpPort()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }
}
