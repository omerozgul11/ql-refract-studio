using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace RefractionStudio.Server.Data.Hosting;

/// <summary>Hosting conventions shared by the licence server and the admin portal.</summary>
public static class HostingExtensions
{
    /// <summary>
    /// Trusts X-Forwarded-For/Proto from the reverse proxy. Proxies are listed in
    /// <c>ReverseProxy:KnownProxies</c> (IP addresses) and <c>ReverseProxy:KnownNetworks</c> (CIDR), comma separated;
    /// loopback is always trusted.
    /// </summary>
    public static IServiceCollection AddReverseProxySupport(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.ForwardLimit = 2;
            foreach (var ip in Split(configuration["ReverseProxy:KnownProxies"]))
            {
                if (!IPAddress.TryParse(ip, out var address)) throw new InvalidOperationException($"ReverseProxy:KnownProxies contains an invalid IP address: {ip}");
                o.KnownProxies.Add(address);
            }
            foreach (var cidr in Split(configuration["ReverseProxy:KnownNetworks"]))
            {
                if (!System.Net.IPNetwork.TryParse(cidr, out var network)) throw new InvalidOperationException($"ReverseProxy:KnownNetworks contains an invalid network: {cidr}");
                o.KnownIPNetworks.Add(network);
            }
        });
        // Behind a TLS-terminating proxy requests arrive as https (X-Forwarded-Proto); anything else is redirected.
        services.AddHttpsRedirection(o => o.HttpsPort = configuration.GetValue<int?>("Hosting:HttpsPort") ?? 443);
        services.AddHsts(o =>
        {
            o.MaxAge = TimeSpan.FromDays(365);
            o.IncludeSubDomains = true;
        });
        return services;
    }

    /// <summary>Forwarded headers first; HSTS and HTTPS redirection outside Development (configurable for TLS-terminating proxies).</summary>
    public static WebApplication UseTransportSecurity(this WebApplication app)
    {
        app.UseForwardedHeaders();
        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
            if (app.Configuration.GetValue("Hosting:HttpsRedirection", true)) app.UseHttpsRedirection();
        }
        return app;
    }

    /// <summary>
    /// Container health probe: <c>dotnet App.dll --health-check</c> requests <paramref name="path"/> on the local
    /// HTTP port (as if forwarded over HTTPS by the proxy) and returns 0 when it answers 2xx.
    /// </summary>
    public static async Task<int> RunHealthCheckAsync(string path)
    {
        var ports = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS") ?? "8080";
        var port = ports.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "8080";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}{path}");
        request.Headers.Add("X-Forwarded-Proto", "https");
        try
        {
            using var response = await http.SendAsync(request).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return 1;
        }
    }

    private static IEnumerable<string> Split(string? list)
        => (list ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
