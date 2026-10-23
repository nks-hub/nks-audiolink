using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NksAudioLink.Core;

namespace NksAudioLink.Server;

/// <summary>Optional read-only monitoring endpoint, independent of the audio clock.</summary>
public sealed class HttpStatusServer(ServerConfig config, Func<ServerStatus> snapshot)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        config.Validate();
        if (config.HttpPort == 0)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return;
        }

        IPAddress bindAddress = IPAddress.Parse(config.HttpBindAddress);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxConcurrentConnections = 32;
            options.Limits.MaxRequestBodySize = 0;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
            options.Listen(bindAddress, config.HttpPort);
        });
        await using var app = builder.Build();
        var allowed = config.AllowCidrs.Select(CidrRange.Parse).ToArray();
        app.Use(async (context, next) =>
        {
            // A remote page must not gain same-origin access by rebinding its DNS
            // hostname to this local monitor. LAN bindings may use local DNS names.
            if (IPAddress.IsLoopback(bindAddress) &&
                !string.Equals(context.Request.Host.Host, "localhost", StringComparison.OrdinalIgnoreCase) &&
                !(IPAddress.TryParse(context.Request.Host.Host, out var hostAddress) && hostAddress.Equals(bindAddress)))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            var address = context.Connection.RemoteIpAddress;
            if (address?.IsIPv4MappedToIPv6 == true) address = address.MapToIPv4();
            if (address is null || !allowed.Any(range => range.Contains(address)))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers.ContentSecurityPolicy =
                "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; img-src data:; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
            await next(context);
        });
        using var resource = typeof(HttpStatusServer).Assembly.GetManifestResourceStream("NksAudioLink.Server.Dashboard.html")
            ?? throw new InvalidOperationException("The server dashboard resource is missing.");
        using var reader = new StreamReader(resource);
        string dashboard = await reader.ReadToEndAsync(cancellationToken);
        app.MapGet("/", () => Results.Content(dashboard, "text/html; charset=utf-8"));
        app.MapGet("/api/stats", () => Results.Json(snapshot()));
        await app.StartAsync(cancellationToken);
        Console.WriteLine($"Server status: http://{config.HttpBindAddress}:{config.HttpPort}/ (read-only)");
        try { await Task.Delay(Timeout.Infinite, cancellationToken); }
        finally { await app.StopAsync(CancellationToken.None); }
    }
}
