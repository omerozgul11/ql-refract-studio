using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics;
using RefractionStudio.Licensing;
using RefractionStudio.Server.Data;
using RefractionStudio.Server.Data.Hosting;
using RefractionStudio.Server.Data.Services;

namespace RefractionStudio.LicenseServer;

/// <summary>
/// Refraction Studio licence server: trials, activation, validation, deactivation, update feed and the
/// Stripe webhook. Public partial for <c>WebApplicationFactory</c> integration tests.
/// </summary>
public partial class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--health-check")) return await HostingExtensions.RunHealthCheckAsync("/api/health").ConfigureAwait(false);
        var app = BuildApp(args);
        await app.Services.MigrateIfConfiguredAsync().ConfigureAwait(false);
        await app.RunAsync().ConfigureAwait(false);
        return 0;
    }

    /// <summary>Configures services and the request pipeline.</summary>
    public static WebApplication BuildApp(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = 512 * 1024;
        });

        builder.Services.AddLicensingData(builder.Configuration);
        builder.Services.AddReverseProxySupport(builder.Configuration);
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        });
        builder.Services.AddLicenseRateLimiting();
        builder.Services.AddHostedService<EmailSenderService>();
        builder.Services.AddHostedService<MaintenanceService>();

        var app = builder.Build();
        // Fail fast on missing or malformed keys instead of on the first request.
        app.Services.GetRequiredService<ServerSecrets>().ValidateForLicenseServer();

        app.UseExceptionHandler(handler => handler.Run(async context =>
        {
            var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
            context.RequestServices.GetRequiredService<ILogger<Program>>().LogError(error, "Unhandled error on {Method} {Path}", context.Request.Method, context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new ErrorResponse("server_error", "The licence server had a problem. Try again later."), LicenseJson.Options).ConfigureAwait(false);
        }));
        app.UseTransportSecurity();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.CacheControl = "no-store";
            await next(context).ConfigureAwait(false);
        });
        app.UseRateLimiter();
        app.MapLicenseEndpoints();
        return app;
    }
}
