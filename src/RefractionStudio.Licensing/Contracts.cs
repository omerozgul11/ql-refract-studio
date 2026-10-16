using System.Text.Json;
using System.Text.Json.Serialization;

namespace RefractionStudio.Licensing;

/// <summary>Kind of entitlement in a token.</summary>
public enum LicenseKind
{
    Trial,
    License,
}

/// <summary>Claims of a signed licence or trial token. Issued only by the licence server.</summary>
/// <param name="TokenId">Unique token id.</param>
/// <param name="Kind">Trial or paid licence.</param>
/// <param name="LicenseId">Server licence id (trials: the trial record id).</param>
/// <param name="Product">Product code, e.g. "refraction-studio".</param>
/// <param name="Edition">Edition, e.g. "Professional".</param>
/// <param name="LicensedTo">Customer / company name (licences).</param>
/// <param name="Email">Customer e-mail (licences).</param>
/// <param name="KeyHint">Last characters of the licence key, for display.</param>
/// <param name="DeviceHash">Fingerprint of the activated computer.</param>
/// <param name="IssuedAt">Issue time (UTC).</param>
/// <param name="ExpiresAt">Entitlement end (UTC); null = perpetual.</param>
/// <param name="MaintenanceUntil">Updates included until (UTC).</param>
/// <param name="OfflineValidUntil">The token may be used without contacting the server until this time.</param>
/// <param name="TrialStart">Trial start (trials).</param>
/// <param name="TrialEnd">Trial end (trials).</param>
/// <param name="Features">Enabled feature codes.</param>
/// <param name="Seats">Seats of the licence.</param>
public sealed record LicenseClaims(
    string TokenId,
    LicenseKind Kind,
    string LicenseId,
    string Product,
    string Edition,
    string? LicensedTo,
    string? Email,
    string? KeyHint,
    string DeviceHash,
    DateTimeOffset IssuedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? MaintenanceUntil,
    DateTimeOffset OfflineValidUntil,
    DateTimeOffset? TrialStart,
    DateTimeOffset? TrialEnd,
    IReadOnlyList<string> Features,
    int Seats);

/// <summary>Device description sent to the server (hashed identifiers only).</summary>
/// <param name="Fingerprint">Salted SHA-256 fingerprint of the computer.</param>
/// <param name="ComponentHashes">Salted hashes of individual identifiers (tolerant matching after hardware changes).</param>
/// <param name="MachineName">Computer name, only if the user allowed it.</param>
/// <param name="OsVersion">Operating-system description.</param>
public sealed record DeviceInfo(string Fingerprint, IReadOnlyList<string> ComponentHashes, string? MachineName, string OsVersion);

/// <summary>Request to start (or continue) the trial on a computer.</summary>
/// <param name="Email">Optional contact address given by the user; used only for the trial-ending reminder.</param>
public sealed record TrialRequest(string Product, string AppVersion, DeviceInfo Device, string? Email = null);

public sealed record ActivationRequest(string LicenseKey, string? Email, string Product, string AppVersion, DeviceInfo Device);

public sealed record ValidationRequest(string Token, string AppVersion, DeviceInfo Device);

public sealed record DeactivationRequest(string Token, DeviceInfo Device);

/// <summary>Successful response carrying a signed token.</summary>
public sealed record TokenResponse(string Token, string? Message);

/// <summary>Error response. <see cref="Code"/> is one of <see cref="LicenseErrorCodes"/>.</summary>
public sealed record ErrorResponse(string Code, string Message);

/// <summary>Stable error codes of the licence API.</summary>
public static class LicenseErrorCodes
{
    public const string InvalidKey = "invalid_key";
    public const string Revoked = "revoked";
    public const string Suspended = "suspended";
    public const string Expired = "expired";
    public const string ActivationLimit = "activation_limit";
    public const string TrialExpired = "trial_expired";
    public const string DeviceMismatch = "device_mismatch";
    public const string NotActivated = "not_activated";
    public const string TransferLimit = "transfer_limit";
    public const string RateLimited = "rate_limited";
    public const string ProductMismatch = "product_mismatch";
    public const string InvalidRequest = "invalid_request";
}

/// <summary>JSON settings shared by client and server.</summary>
public static class LicenseJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        o.MakeReadOnly(populateMissingResolver: true);
        return o;
    }
}

/// <summary>A release in the update feed.</summary>
/// <param name="Product">Product code.</param>
/// <param name="Version">Semantic version.</param>
/// <param name="Channel">"stable" or "beta".</param>
/// <param name="ReleasedAt">Release date.</param>
/// <param name="DownloadUrl">HTTPS URL of the installer.</param>
/// <param name="Sha256">SHA-256 of the installer.</param>
/// <param name="Size">Installer size in bytes.</param>
/// <param name="ReleaseNotes">Release notes (Markdown).</param>
/// <param name="Major">True for major upgrades (release notes are shown before installing).</param>
/// <param name="MinimumVersion">Oldest version that can update directly (null = any).</param>
public sealed record ReleaseInfo(string Product, string Version, string Channel, DateTimeOffset ReleasedAt, string DownloadUrl, string Sha256, long Size, string ReleaseNotes, bool Major, string? MinimumVersion);

/// <summary>Signed update manifest as served by the update feed.</summary>
/// <param name="Payload">Base64url JSON of <see cref="ReleaseInfo"/>.</param>
/// <param name="Signature">Base64url ES256 signature of the payload bytes.</param>
/// <param name="KeyId">Signing key id.</param>
public sealed record SignedManifest(string Payload, string Signature, string KeyId);
