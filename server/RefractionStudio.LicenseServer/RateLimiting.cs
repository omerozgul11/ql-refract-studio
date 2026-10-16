using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using RefractionStudio.Licensing;

namespace RefractionStudio.LicenseServer;

/// <summary>Rate limits (section "RateLimits").</summary>
public sealed class RateLimitSettings
{
    public const string Section = "RateLimits";

    /// <summary>Requests per minute and client IP to the trial, validation, deactivation and update endpoints.</summary>
    public int LicensePerMinute { get; set; } = 30;

    /// <summary>Activation requests per minute and client IP.</summary>
    public int ActivationPerMinute { get; set; } = 10;

    /// <summary>Activation attempts per hour for one licence key (from any address).</summary>
    public int ActivationsPerKeyPerHour { get; set; } = 20;
}

/// <summary>Per-IP fixed-window rate limiting of the licence API; refusals are 429 with <c>rate_limited</c>.</summary>
public static class RateLimiting
{
    public const string LicensePolicy = "license";
    public const string ActivationPolicy = "activation";
    public const string Message = "Too many requests from this network. Wait a minute and try again.";

    public static IServiceCollection AddLicenseRateLimiting(this IServiceCollection services)
    {
        services.AddOptions<RateLimitSettings>().BindConfiguration(RateLimitSettings.Section);
        services.AddSingleton<KeyAttemptLimiter>();
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = async (context, ct) =>
            {
                var retry = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var after) ? after : TimeSpan.FromMinutes(1);
                context.HttpContext.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                await context.HttpContext.Response.WriteAsJsonAsync(new ErrorResponse(LicenseErrorCodes.RateLimited, Message), LicenseJson.Options, ct).ConfigureAwait(false);
            };
            o.AddPolicy(LicensePolicy, http => PerIp(http, s => s.LicensePerMinute, LicensePolicy));
            o.AddPolicy(ActivationPolicy, http => PerIp(http, s => s.ActivationPerMinute, ActivationPolicy));
        });
        return services;
    }

    private static RateLimitPartition<string> PerIp(HttpContext http, Func<RateLimitSettings, int> limit, string policy)
    {
        var settings = http.RequestServices.GetRequiredService<IOptions<RateLimitSettings>>().Value;
        var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(policy + ":" + ip, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, limit(settings)),
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    }
}

/// <summary>Limits activation attempts per licence key, whatever the client address.</summary>
public sealed class KeyAttemptLimiter(IOptions<RateLimitSettings> settings) : IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter = PartitionedRateLimiter.Create<string, string>(key =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, settings.Value.ActivationsPerKeyPerHour),
            Window = TimeSpan.FromHours(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        }));

    /// <summary>
    /// Partition of a typed key: a keyed hash of its significant characters (prefix, separators, case and
    /// confusable letters ignored), so variants of the same key share one budget. Null for empty input.
    /// </summary>
    public static string? PartitionFor(string? typedKey, byte[] pepper)
    {
        if (string.IsNullOrWhiteSpace(typedKey)) return null;
        var s = new string([.. typedKey.ToUpperInvariant().Where(char.IsLetterOrDigit)]).Replace('O', '0').Replace('I', '1').Replace('L', '1');
        if (s.Length > 16) s = s[^16..];
        return Convert.ToHexStringLower(HMACSHA256.HashData(pepper, Encoding.UTF8.GetBytes("key-attempts|" + s)));
    }

    public bool TryAcquire(string partition)
    {
        using var lease = _limiter.AttemptAcquire(partition);
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();
}
