using Microsoft.Extensions.Options;
using RefractionStudio.Licensing;
using RefractionStudio.Server.Data.Entities;

namespace RefractionStudio.Server.Data.Services;

/// <summary>
/// Issues and verifies signed licence and trial tokens. The ES256 private key is loaded from configuration
/// (environment variable or mounted file) by <see cref="ServerSecrets"/>; it never lives in source control.
/// </summary>
public sealed class TokenIssuer(ServerSecrets secrets, IOptions<LicensingServerOptions> options)
{
    /// <summary>Upper bound for the offline window of a licence token, whatever the licence says.</summary>
    public const int MaxOfflineDays = 365;

    /// <summary>Id of the key that signs new tokens.</summary>
    public string KeyId => secrets.TokenSigningKey.KeyId;

    /// <summary>Public key of the current signing key (embed it in the desktop application).</summary>
    public string PublicKeyPem => secrets.TokenSigningKey.PublicPem;

    /// <summary>
    /// Issues a licence token for one device. The offline window is the licence's <c>offline_days</c> but never
    /// extends beyond the licence's expiry.
    /// </summary>
    public string IssueLicense(License license, Customer customer, string deviceFingerprint, DateTimeOffset now)
    {
        var offlineDays = Math.Clamp(license.OfflineDays, 1, MaxOfflineDays);
        var offlineUntil = now.AddDays(offlineDays);
        if (license.ExpiresAt is { } exp && exp < offlineUntil) offlineUntil = exp;
        var claims = new LicenseClaims(
            TokenId: Guid.NewGuid().ToString("N"),
            Kind: LicenseKind.License,
            LicenseId: license.Id.ToString(),
            Product: license.Product,
            Edition: license.Edition,
            LicensedTo: customer.DisplayName,
            Email: customer.Email,
            KeyHint: license.KeyHint,
            DeviceHash: deviceFingerprint,
            IssuedAt: now,
            ExpiresAt: license.ExpiresAt,
            MaintenanceUntil: license.MaintenanceUntil,
            OfflineValidUntil: offlineUntil,
            TrialStart: null,
            TrialEnd: null,
            Features: [.. license.Features],
            Seats: license.SeatCount);
        return LicenseToken.Issue(claims, secrets.TokenSigningKey.PrivatePem, KeyId);
    }

    /// <summary>
    /// Issues a trial token. It may be used offline for <see cref="LicensingServerOptions.TrialOfflineDays"/> days
    /// (default 3), never beyond the trial end.
    /// </summary>
    public string IssueTrial(TrialDevice trial, LicenseProduct product, string deviceFingerprint, DateTimeOffset now)
    {
        var offlineUntil = now.AddDays(Math.Clamp(options.Value.TrialOfflineDays, 1, 30));
        if (trial.EndsAt < offlineUntil) offlineUntil = trial.EndsAt;
        var claims = new LicenseClaims(
            TokenId: Guid.NewGuid().ToString("N"),
            Kind: LicenseKind.Trial,
            LicenseId: trial.Id.ToString(),
            Product: trial.Product,
            Edition: product.DefaultEdition,
            LicensedTo: null,
            Email: null,
            KeyHint: null,
            DeviceHash: deviceFingerprint,
            IssuedAt: now,
            ExpiresAt: trial.EndsAt,
            MaintenanceUntil: trial.EndsAt,
            OfflineValidUntil: offlineUntil,
            TrialStart: trial.StartedAt,
            TrialEnd: trial.EndsAt,
            Features: [.. product.DefaultFeatures],
            Seats: 1);
        return LicenseToken.Issue(claims, secrets.TokenSigningKey.PrivatePem, KeyId);
    }

    /// <summary>Verifies a token against the current and retired signing keys; null when it is not genuine.</summary>
    public LicenseClaims? Verify(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 8192) return null;
        var (claims, error) = LicenseToken.Verify(token, [.. secrets.TrustedTokenPublicKeys]);
        return error == TokenError.None ? claims : null;
    }
}
