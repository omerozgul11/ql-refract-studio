using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RefractionStudio.Licensing;
using RefractionStudio.Server.Data;
using RefractionStudio.Server.Data.Payments;
using RefractionStudio.Server.Data.Services;

namespace RefractionStudio.LicenseServer;

/// <summary>HTTP endpoints of the licence API.</summary>
public static class LicenseEndpoints
{
    /// <summary>Largest accepted licence API request body.</summary>
    public const int MaxApiBodyBytes = 32 * 1024;

    /// <summary>Largest accepted webhook body.</summary>
    public const int MaxWebhookBodyBytes = 256 * 1024;

    public static IEndpointRouteBuilder MapLicenseEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapPost("/trials/start", async (HttpContext http, TrialService trials, IpHasher ip, CancellationToken ct) =>
        {
            var request = await ReadAsync<TrialRequest>(http, ct).ConfigureAwait(false);
            if (request is null) return BadRequest();
            return ToResult(await trials.StartAsync(request, Actor.Api(ip.Hash(http.Connection.RemoteIpAddress)), ct).ConfigureAwait(false));
        }).RequireRateLimiting(RateLimiting.LicensePolicy);

        api.MapPost("/licenses/activate", async (HttpContext http, ActivationService activations, IpHasher ip, KeyAttemptLimiter keyLimiter, ServerSecrets secrets, CancellationToken ct) =>
        {
            var request = await ReadAsync<ActivationRequest>(http, ct).ConfigureAwait(false);
            if (request is null) return BadRequest();
            // Per-key limit on top of the per-IP limit: slows down guessing and abuse of one key from many addresses.
            var keyPartition = KeyAttemptLimiter.PartitionFor(request.LicenseKey, secrets.KeyPepper);
            if (keyPartition is not null && !keyLimiter.TryAcquire(keyPartition)) return RateLimited(http, TimeSpan.FromHours(1));
            return ToResult(await activations.ActivateAsync(request, Actor.Api(ip.Hash(http.Connection.RemoteIpAddress)), ct).ConfigureAwait(false));
        }).RequireRateLimiting(RateLimiting.ActivationPolicy);

        api.MapPost("/licenses/validate", async (HttpContext http, ActivationService activations, TrialService trials, IpHasher ip, CancellationToken ct) =>
        {
            var request = await ReadAsync<ValidationRequest>(http, ct).ConfigureAwait(false);
            if (request is null) return BadRequest();
            return ToResult(await activations.ValidateAsync(request, trials, Actor.Api(ip.Hash(http.Connection.RemoteIpAddress)), ct).ConfigureAwait(false));
        }).RequireRateLimiting(RateLimiting.LicensePolicy);

        api.MapPost("/licenses/deactivate", async (HttpContext http, ActivationService activations, IpHasher ip, CancellationToken ct) =>
        {
            var request = await ReadAsync<DeactivationRequest>(http, ct).ConfigureAwait(false);
            if (request is null) return BadRequest();
            return ToResult(await activations.DeactivateAsync(request, Actor.Api(ip.Hash(http.Connection.RemoteIpAddress)), ct).ConfigureAwait(false));
        }).RequireRateLimiting(RateLimiting.LicensePolicy);

        api.MapGet("/updates/{product}/{channel}", async (string product, string channel, [FromQuery] string? current, ReleaseService releases, CancellationToken ct) =>
        {
            if (!RequestValidation.IsProductCode(product)) return Error(LicenseErrorCodes.InvalidRequest, "Unknown product.");
            if (!ReleaseService.Channels.Contains(channel.ToLowerInvariant())) return Error(LicenseErrorCodes.InvalidRequest, "The update channel must be “stable” or “beta”.");
            if (!string.IsNullOrWhiteSpace(current) && !SemanticVersion.TryParse(current, out _)) return Error(LicenseErrorCodes.InvalidRequest, "The current version is not a valid version number.");
            var manifest = await releases.GetLatestAsync(product, channel, current, ct).ConfigureAwait(false);
            return manifest is null ? Results.NoContent() : Results.Json(manifest, LicenseJson.Options);
        }).RequireRateLimiting(RateLimiting.LicensePolicy);

        api.MapGet("/health", async (LicensingDbContext db, CancellationToken ct) =>
        {
            var database = await db.Database.CanConnectAsync(ct).ConfigureAwait(false);
            return Results.Json(new { status = database ? "ok" : "degraded", database = database ? "ok" : "unreachable" }, LicenseJson.Options,
                statusCode: database ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
        });

        api.MapPost("/webhooks/stripe", async (HttpContext http, StripePaymentProvider stripe, ILogger<StripePaymentProvider> log, CancellationToken ct) =>
        {
            var payload = await ReadBodyAsync(http.Request, MaxWebhookBodyBytes, ct).ConfigureAwait(false);
            if (payload is null) return Error(LicenseErrorCodes.InvalidRequest, "The request body is too large.", StatusCodes.Status413PayloadTooLarge);
            if (!stripe.VerifySignature(payload, http.Request.Headers["Stripe-Signature"]))
            {
                log.LogWarning("Rejected a Stripe webhook with a missing, invalid or expired signature");
                return Error(LicenseErrorCodes.InvalidRequest, "The webhook signature is not valid.");
            }
            try
            {
                var outcome = await stripe.HandleWebhookAsync(payload, ct).ConfigureAwait(false);
                return Results.Json(new { received = true, outcome = outcome.ToString().ToLowerInvariant() }, LicenseJson.Options);
            }
            catch (JsonException)
            {
                return Error(LicenseErrorCodes.InvalidRequest, "The webhook payload is not a valid Stripe event.");
            }
        });

        return app;
    }

    internal static IResult ToResult(LicenseOutcome outcome)
        => outcome.Success is { } ok
            ? Results.Json(ok, LicenseJson.Options)
            : Results.Json(outcome.Error, LicenseJson.Options, statusCode: outcome.StatusCode);

    private static IResult Error(string code, string message, int status = StatusCodes.Status400BadRequest)
        => Results.Json(new ErrorResponse(code, message), LicenseJson.Options, statusCode: status);

    private static IResult BadRequest()
        => Error(LicenseErrorCodes.InvalidRequest, "The request could not be read. Update Refraction Studio to the latest version.");

    private static IResult RateLimited(HttpContext http, TimeSpan retryAfter)
    {
        http.Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Error(LicenseErrorCodes.RateLimited, RateLimiting.Message, StatusCodes.Status429TooManyRequests);
    }

    /// <summary>Reads a JSON body; null when it is missing, too large or malformed.</summary>
    private static async Task<T?> ReadAsync<T>(HttpContext http, CancellationToken ct) where T : class
    {
        if (!http.Request.HasJsonContentType()) return null;
        var body = await ReadBodyAsync(http.Request, MaxApiBodyBytes, ct).ConfigureAwait(false);
        if (body is null) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(body, LicenseJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<string?> ReadBodyAsync(HttpRequest request, int maxBytes, CancellationToken ct)
    {
        if (request.ContentLength > maxBytes) return null;
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
