using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RefractionStudio.Licensing;
using RefractionStudio.Server.Data.Entities;

namespace RefractionStudio.Server.Data.Services;

/// <summary>Who performed an operation. Admin actions carry the admin's id for the audit log.</summary>
/// <param name="Name">Display form stored in events: "api", "admin:{email}", "stripe", "system".</param>
/// <param name="AdminUserId">Admin user id, for admin actions.</param>
/// <param name="IpHash">Keyed hash of the client IP address.</param>
public sealed record Actor(string Name, Guid? AdminUserId, string? IpHash)
{
    public static Actor Api(string? ipHash) => new("api", null, ipHash);

    public static Actor Admin(Guid adminUserId, string email, string? ipHash) => new("admin:" + email, adminUserId, ipHash);

    public static Actor Stripe { get; } = new("stripe", null, null);

    public static Actor System { get; } = new("system", null, null);

    public bool IsAdmin => AdminUserId is not null;
}

/// <summary>Event types written to <c>license_events</c>.</summary>
public static class EventTypes
{
    public const string LicenseCreated = "license.created";
    public const string LicenseRevoked = "license.revoked";
    public const string LicenseSuspended = "license.suspended";
    public const string LicenseReinstated = "license.reinstated";
    public const string LicenseExtended = "license.extended";
    public const string LicenseExpired = "license.expired";
    public const string LicenseLimitsChanged = "license.limits_changed";
    public const string LicenseKeyReplaced = "license.key_replaced";
    public const string ActivationsReset = "license.activations_reset";
    public const string Activated = "activation.created";
    public const string Reactivated = "activation.reused";
    public const string ActivationRefused = "activation.refused";
    public const string Validated = "activation.validated";
    public const string ValidationRefused = "activation.validation_refused";
    public const string Deactivated = "activation.deactivated";
    public const string TrialStarted = "trial.started";
    public const string TrialContinued = "trial.continued";
    public const string TrialRefused = "trial.refused";
    public const string TrialExtended = "trial.extended";
    public const string DeviceBlocked = "device.blocked";
    public const string DeviceUnblocked = "device.unblocked";
}

/// <summary>Result of a licence API operation: a token or a refusal with an HTTP status.</summary>
public sealed record LicenseOutcome(TokenResponse? Success, ErrorResponse? Error, int StatusCode)
{
    public static LicenseOutcome Ok(string token, string? message) => new(new TokenResponse(token, message), null, 200);

    public static LicenseOutcome Fail(string code, string message) => new(null, new ErrorResponse(code, message), StatusFor(code));

    /// <summary>HTTP status used for each error code (all refusals are 4xx so the client never treats them as "offline").</summary>
    public static int StatusFor(string code) => code switch
    {
        LicenseErrorCodes.InvalidRequest or LicenseErrorCodes.ProductMismatch => 400,
        LicenseErrorCodes.InvalidKey or LicenseErrorCodes.NotActivated => 404,
        LicenseErrorCodes.Revoked or LicenseErrorCodes.Suspended or LicenseErrorCodes.Expired
            or LicenseErrorCodes.TrialExpired or LicenseErrorCodes.DeviceMismatch => 403,
        LicenseErrorCodes.ActivationLimit or LicenseErrorCodes.TransferLimit => 409,
        LicenseErrorCodes.RateLimited => 429,
        _ => 400,
    };
}

/// <summary>Writes licence events and audit entries (both append-only).</summary>
public static class Recorder
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void AddEvent(this LicensingDbContext db, DateTimeOffset now, string type, Actor actor, Guid? licenseId = null, Guid? trialId = null, Guid? deviceId = null, object? details = null)
        => db.LicenseEvents.Add(new LicenseEvent
        {
            Type = type,
            Actor = actor.Name,
            IpHash = actor.IpHash,
            OccurredAt = now,
            LicenseId = licenseId,
            TrialId = trialId,
            DeviceId = deviceId,
            Details = JsonSerializer.Serialize(details ?? new { }, Json),
        });

    public static void AddAudit(this LicensingDbContext db, DateTimeOffset now, Actor actor, string action, string? entityType, string? entityId, object? details = null)
        => db.AuditLog.Add(new AuditLogEntry
        {
            AdminUserId = actor.AdminUserId,
            Actor = actor.Name,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            OccurredAt = now,
            IpHash = actor.IpHash,
            Details = JsonSerializer.Serialize(details ?? new { }, Json),
        });
}

/// <summary>Keyed, truncated hash of client IP addresses (correlation without storing the address).</summary>
public sealed class IpHasher(ServerSecrets secrets)
{
    private readonly Lazy<byte[]> _key = new(() => secrets.DeriveKey("ip-hash"));

    public string? Hash(IPAddress? address)
    {
        if (address is null) return null;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return Convert.ToHexStringLower(HMACSHA256.HashData(_key.Value, Encoding.UTF8.GetBytes(address.ToString())))[..32];
    }
}

/// <summary>Validation of client-supplied request fields.</summary>
public static partial class RequestValidation
{
    public const int MaxComponentHashes = 16;

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Hex();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProductCode();

    public static bool IsSha256Hex(string? s) => s is not null && Sha256Hex().IsMatch(s);

    public static bool IsProductCode(string? s) => s is not null && ProductCode().IsMatch(s);

    /// <summary>Returns a human-readable problem with the device description, or null when it is acceptable.</summary>
    public static string? CheckDevice(DeviceInfo? device)
    {
        if (device is null) return "The request does not describe this computer.";
        if (!IsSha256Hex(device.Fingerprint)) return "The computer fingerprint is not valid.";
        if (device.ComponentHashes is null) return "The computer description is incomplete.";
        if (device.ComponentHashes.Count > MaxComponentHashes) return "The computer description has too many components.";
        if (device.ComponentHashes.Any(h => !IsSha256Hex(h))) return "The computer description is not valid.";
        return null;
    }

    public static string? CheckAppVersion(string? version)
        => string.IsNullOrWhiteSpace(version) || version.Length > 64 ? "The application version is missing or invalid." : null;

    public static string? Truncate(string? s, int max) => s is null ? null : s.Length <= max ? s.Trim() : s[..max].Trim();

    /// <summary>Normalises an e-mail address for storage and lookup.</summary>
    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    /// <summary>Basic syntactic check of an e-mail address.</summary>
    public static bool IsEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > 320) return false;
        try
        {
            var address = new System.Net.Mail.MailAddress(email.Trim());
            return address.Address == email.Trim() && address.Host.Contains('.', StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>Clock helpers.</summary>
public static class ClockExtensions
{
    /// <summary>
    /// Current UTC time truncated to whole microseconds — the precision of PostgreSQL <c>timestamptz</c> — so
    /// values issued in tokens are identical to the values stored and read back.
    /// </summary>
    public static DateTimeOffset GetDbNow(this TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        return new DateTimeOffset(now.Ticks - now.Ticks % 10, TimeSpan.Zero);
    }
}

/// <summary>Database helpers.</summary>
public static class DbExtensions
{
    /// <summary>True for unique-violation or serialization failures that justify retrying the operation.</summary>
    public static bool IsRetryableConflict(this DbUpdateException ex)
        => ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.SerializationFailure };

    /// <summary>
    /// Runs <paramref name="operation"/> in a transaction; on a concurrency conflict (unique violation) the
    /// change tracker is cleared and the operation runs once more against fresh data.
    /// </summary>
    public static async Task<T> InTransactionWithRetryAsync<T>(this LicensingDbContext db, Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                var result = await operation(ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
                return result;
            }
            catch (DbUpdateException ex) when (attempt < 3 && ex.IsRetryableConflict())
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                db.ChangeTracker.Clear();
            }
        }
    }
}
