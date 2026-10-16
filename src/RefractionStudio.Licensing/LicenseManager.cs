using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RefractionStudio.Licensing;

/// <summary>Licence states shown to the user (each has its own explanation and actions).</summary>
public enum LicenseState
{
    NotActivated,
    TrialActive,
    TrialExpiring,
    TrialExpired,
    Licensed,
    LicenseOfflineGrace,
    LicenseExpired,
    LicenseRevoked,
    ActivationLimitReached,
    NetworkValidationRequired,
}

/// <summary>Current licence status.</summary>
/// <param name="State">State.</param>
/// <param name="Claims">Verified claims, if any.</param>
/// <param name="Title">Short headline, e.g. "Trial — 11 days remaining".</param>
/// <param name="Message">Explanation and what to do.</param>
/// <param name="DaysRemaining">Days left in a trial or licence term.</param>
/// <param name="LastValidated">Last successful online validation.</param>
/// <param name="CanEdit">True when editing is permitted. Projects can always be opened, viewed and exported.</param>
public sealed record LicenseStatus(LicenseState State, LicenseClaims? Claims, string Title, string Message, int? DaysRemaining, DateTimeOffset? LastValidated, bool CanEdit)
{
    /// <summary>"License last verified 3 days ago."</summary>
    public string? LastVerifiedText(DateTimeOffset now)
    {
        if (LastValidated is not { } lv) return null;
        var days = (int)Math.Floor((now - lv).TotalDays);
        return days <= 0 ? "License verified today." : days == 1 ? "License last verified 1 day ago." : $"License last verified {days} days ago.";
    }
}

/// <summary>Locally persisted licence data.</summary>
/// <param name="Token">Current signed token.</param>
/// <param name="LastValidated">Last successful online validation.</param>
/// <param name="LastSeen">Latest clock time observed by the application (rollback detection).</param>
/// <param name="ServerState">Last terminal state reported by the server (revoked/expired/limit), if any.</param>
public sealed record StoredLicense(string? Token, DateTimeOffset? LastValidated, DateTimeOffset LastSeen, LicenseState? ServerState, string? ServerMessage);

/// <summary>Persists <see cref="StoredLicense"/>.</summary>
public interface ILicenseStore
{
    StoredLicense? Load();
    void Save(StoredLicense data);
    void Clear();
}

/// <summary>
/// File store protected with AES-GCM under a key derived from the device fingerprint, so the file is
/// tamper-evident and cannot be copied to another computer. Tokens themselves are signed by the server;
/// this protection guards the clock and validation timestamps.
/// </summary>
public sealed class FileLicenseStore(string path, string deviceFingerprint) : ILicenseStore
{
    private byte[] Key => HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(deviceFingerprint), 32, Encoding.UTF8.GetBytes("RefractionStudio.LicenseStore.v1"), []);

    public StoredLicense? Load()
    {
        try
        {
            if (!File.Exists(path)) return null;
            var blob = File.ReadAllBytes(path);
            if (blob.Length < 12 + 16) return null;
            var nonce = blob.AsSpan(0, 12);
            var tag = blob.AsSpan(12, 16);
            var cipher = blob.AsSpan(28);
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(Key, 16);
            aes.Decrypt(nonce, cipher, tag, plain);
            return JsonSerializer.Deserialize<StoredLicense>(plain, LicenseJson.Options);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
        {
            // Damaged, tampered or copied from another computer: treated as absent (the server remains authoritative).
            return null;
        }
    }

    public void Save(StoredLicense data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var plain = JsonSerializer.SerializeToUtf8Bytes(data, LicenseJson.Options);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plain.Length];
        using (var aes = new AesGcm(Key, 16)) aes.Encrypt(nonce, plain, cipher, tag);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, [.. nonce, .. tag, .. cipher]);
        File.Move(temp, path, overwrite: true);
    }

    public void Clear()
    {
        if (File.Exists(path)) File.Delete(path);
    }
}

/// <summary>In-memory store (tests).</summary>
public sealed class MemoryLicenseStore : ILicenseStore
{
    private StoredLicense? _data;
    public StoredLicense? Load() => _data;
    public void Save(StoredLicense data) => _data = data;
    public void Clear() => _data = null;
}

/// <summary>Outcome of a call to the licence server.</summary>
public sealed record ApiResult(TokenResponse? Success, ErrorResponse? Error, bool NetworkFailure)
{
    public static ApiResult Ok(TokenResponse r) => new(r, null, false);
    public static ApiResult Fail(ErrorResponse e) => new(null, e, false);
    public static ApiResult Offline(string message) => new(null, new ErrorResponse("network", message), true);
}

/// <summary>The licence server API.</summary>
public interface ILicenseApi
{
    Task<ApiResult> StartTrialAsync(TrialRequest request, CancellationToken ct);
    Task<ApiResult> ActivateAsync(ActivationRequest request, CancellationToken ct);
    Task<ApiResult> ValidateAsync(ValidationRequest request, CancellationToken ct);
    Task<ApiResult> DeactivateAsync(DeactivationRequest request, CancellationToken ct);
}

/// <summary>HTTPS client of the licence server.</summary>
public sealed class HttpLicenseApi(HttpClient http) : ILicenseApi
{
    public Task<ApiResult> StartTrialAsync(TrialRequest request, CancellationToken ct) => PostAsync("api/trials/start", request, ct);
    public Task<ApiResult> ActivateAsync(ActivationRequest request, CancellationToken ct) => PostAsync("api/licenses/activate", request, ct);
    public Task<ApiResult> ValidateAsync(ValidationRequest request, CancellationToken ct) => PostAsync("api/licenses/validate", request, ct);
    public Task<ApiResult> DeactivateAsync(DeactivationRequest request, CancellationToken ct) => PostAsync("api/licenses/deactivate", request, ct);

    private async Task<ApiResult> PostAsync<T>(string path, T body, CancellationToken ct)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(path, body, LicenseJson.Options, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var ok = await response.Content.ReadFromJsonAsync<TokenResponse>(LicenseJson.Options, ct).ConfigureAwait(false);
                return ok is null ? ApiResult.Offline("The licence server returned an empty response.") : ApiResult.Ok(ok);
            }
            if ((int)response.StatusCode >= 500) return ApiResult.Offline($"The licence server is unavailable ({(int)response.StatusCode}).");
            ErrorResponse? error = null;
            try
            {
                error = await response.Content.ReadFromJsonAsync<ErrorResponse>(LicenseJson.Options, ct).ConfigureAwait(false);
            }
            catch (JsonException)
            {
            }
            return ApiResult.Fail(error ?? new ErrorResponse(LicenseErrorCodes.InvalidRequest, $"The licence server rejected the request ({(int)response.StatusCode})."));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            if (ct.IsCancellationRequested) throw;
            return ApiResult.Offline("The licence server could not be reached. Check the internet connection.");
        }
    }
}

/// <summary>Licensing configuration of a product.</summary>
/// <param name="Product">Product code.</param>
/// <param name="ProductName">Display name.</param>
/// <param name="AppVersion">Application version.</param>
/// <param name="TrustedPublicKeys">Public keys that may sign tokens (current and next, for rotation).</param>
/// <param name="TrialWarningDays">Days before the trial end when the warning becomes prominent.</param>
/// <param name="ClockRollbackTolerance">Backward clock movement tolerated before online validation is required.</param>
public sealed record LicensingOptions(string Product, string ProductName, string AppVersion, IReadOnlyCollection<string> TrustedPublicKeys,
    int TrialWarningDays = 3, TimeSpan? ClockRollbackTolerance = null);

/// <summary>
/// Evaluates and maintains the licence of this computer. Offline evaluation verifies the signed token
/// (device, product, expiry, offline window) and the clock guard; online calls refresh the token.
/// A licence problem never affects project data: at worst editing is disabled and projects open read-only.
/// </summary>
public sealed class LicenseManager
{
    private readonly ILicenseApi _api;
    private readonly ILicenseStore _store;
    private readonly DeviceInfo _device;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;
    private readonly TimeSpan _rollbackTolerance;

    public LicenseManager(LicensingOptions options, ILicenseApi api, ILicenseStore store, DeviceInfo device, TimeProvider? clock = null, ILogger<LicenseManager>? logger = null)
    {
        Options = options;
        _api = api;
        _store = store;
        _device = device;
        _clock = clock ?? TimeProvider.System;
        _log = (ILogger?)logger ?? NullLogger.Instance;
        _rollbackTolerance = options.ClockRollbackTolerance ?? TimeSpan.FromHours(24);
    }

    public LicensingOptions Options { get; }

    public DeviceInfo Device => _device;

    public event EventHandler<LicenseStatus>? StatusChanged;

    /// <summary>Offline evaluation (no network). Also records the observed clock.</summary>
    public LicenseStatus Evaluate()
    {
        var now = _clock.GetUtcNow();
        var stored = _store.Load();
        if (stored is null) return Status(LicenseState.NotActivated, null, null);
        if (now < stored.LastSeen - _rollbackTolerance)
        {
            _log.LogWarning("Clock moved backwards by {Hours:0} hours; online validation required", (stored.LastSeen - now).TotalHours);
            return new LicenseStatus(LicenseState.NetworkValidationRequired, Verify(stored.Token), "Licence verification required",
                "Your system clock changed significantly. Connect to the internet to verify your license. Your projects are not affected.", null, stored.LastValidated, false);
        }
        if (now > stored.LastSeen) _store.Save(stored with { LastSeen = now });
        if (stored.ServerState is { } terminal) return Status(terminal, Verify(stored.Token), stored.LastValidated, stored.ServerMessage);
        var claims = Verify(stored.Token);
        if (claims is null) return Status(LicenseState.NotActivated, null, stored.LastValidated, "The stored licence could not be verified on this computer. Activate again.");
        return FromClaims(claims, now, stored.LastValidated);
    }

    private LicenseClaims? Verify(string? token)
    {
        if (token is null) return null;
        var (claims, error) = LicenseToken.Verify(token, Options.TrustedPublicKeys);
        if (error != TokenError.None || claims is null) return null;
        if (claims.Product != Options.Product || claims.DeviceHash != _device.Fingerprint) return null;
        return claims;
    }

    private LicenseStatus FromClaims(LicenseClaims c, DateTimeOffset now, DateTimeOffset? lastValidated)
    {
        if (c.Kind == LicenseKind.Trial)
        {
            var end = c.TrialEnd ?? c.ExpiresAt ?? now;
            if (now >= end) return Status(LicenseState.TrialExpired, c, lastValidated);
            if (now > c.OfflineValidUntil) return Status(LicenseState.NetworkValidationRequired, c, lastValidated,
                "Connect to the internet to continue the trial (trials are verified online every few days).");
            var days = (int)Math.Ceiling((end - now).TotalDays);
            return Status(days <= Options.TrialWarningDays ? LicenseState.TrialExpiring : LicenseState.TrialActive, c, lastValidated, days: days);
        }
        if (c.ExpiresAt is { } exp && now >= exp) return Status(LicenseState.LicenseExpired, c, lastValidated);
        if (now > c.OfflineValidUntil) return Status(LicenseState.NetworkValidationRequired, c, lastValidated,
            "This licence has not been verified online for too long. Connect to the internet to continue editing; your projects can still be opened.");
        var stale = lastValidated is { } lv && now - lv > TimeSpan.FromDays(2);
        return Status(stale ? LicenseState.LicenseOfflineGrace : LicenseState.Licensed, c, lastValidated,
            days: c.ExpiresAt is { } e ? (int)Math.Ceiling((e - now).TotalDays) : null);
    }

    private LicenseStatus Status(LicenseState state, LicenseClaims? c, DateTimeOffset? lastValidated, string? message = null, int? days = null)
    {
        var name = Options.ProductName;
        var (title, text, canEdit) = state switch
        {
            LicenseState.NotActivated => ("Not activated", $"Start a 14-day free trial or activate a licence to use {name}.", false),
            LicenseState.TrialActive => ($"{name} Trial — {days} days remaining", "The trial includes all features.", true),
            LicenseState.TrialExpiring => ($"{name} Trial — {days} day{(days == 1 ? "" : "s")} remaining", "Your trial ends soon. Activate a licence to keep editing; your projects stay intact.", true),
            LicenseState.TrialExpired => ("Trial ended", "Your 14-day trial has ended. Your projects remain on this computer and can still be opened and viewed. Activate a licence to continue editing.", false),
            LicenseState.Licensed => ($"{name} {c?.Edition}", c?.LicensedTo is { } to ? $"Licensed to {to}." : "Licensed.", true),
            LicenseState.LicenseOfflineGrace => ($"{name} {c?.Edition} (offline)", $"Working offline. Online verification is required by {c?.OfflineValidUntil:d MMMM yyyy}.", true),
            LicenseState.LicenseExpired => ("Licence expired", "This licence term has ended. Renew it to continue editing; projects can still be opened and viewed.", false),
            LicenseState.LicenseRevoked => ("Licence revoked", "This licence is no longer valid. Contact QuakeLogic support if you believe this is a mistake. Projects can still be opened and viewed.", false),
            LicenseState.ActivationLimitReached => ("Activation limit reached", "This licence is already active on its permitted number of computers. Deactivate another computer or contact support.", false),
            _ => ("Licence verification required", "Connect to the internet to verify the licence. Projects can still be opened and viewed.", false),
        };
        return new LicenseStatus(state, c, title, message ?? text, days, lastValidated, canEdit);
    }

    /// <param name="email">Optional contact address for the trial-ending reminder.</param>
    public async Task<LicenseStatus> StartTrialAsync(string? email = null, CancellationToken ct = default)
        => await HandleAsync(await _api.StartTrialAsync(new TrialRequest(Options.Product, Options.AppVersion, _device, string.IsNullOrWhiteSpace(email) ? null : email.Trim()), ct)
            .ConfigureAwait(false), "trial").ConfigureAwait(false);

    public async Task<LicenseStatus> ActivateAsync(string licenseKey, string? email, string keyPrefix, CancellationToken ct = default)
    {
        var key = LicenseKeys.Normalize(licenseKey, keyPrefix);
        if (key is null)
            return Status(LicenseState.NotActivated, null, null, $"“{licenseKey}” is not a valid licence key. Keys look like {keyPrefix}-XXXX-XXXX-XXXX-XXXX; check for typing errors.");
        return await HandleAsync(await _api.ActivateAsync(new ActivationRequest(key, email, Options.Product, Options.AppVersion, _device), ct).ConfigureAwait(false), "activation").ConfigureAwait(false);
    }

    /// <summary>Online validation; on network failure the offline evaluation stands.</summary>
    public async Task<LicenseStatus> RefreshAsync(CancellationToken ct = default)
    {
        var stored = _store.Load();
        if (stored?.Token is null) return Evaluate();
        var result = await _api.ValidateAsync(new ValidationRequest(stored.Token, Options.AppVersion, _device), ct).ConfigureAwait(false);
        return await HandleAsync(result, "validation").ConfigureAwait(false);
    }

    public async Task<(bool Success, string Message)> DeactivateAsync(CancellationToken ct = default)
    {
        var stored = _store.Load();
        if (stored?.Token is null) return (false, "This computer is not activated.");
        var result = await _api.DeactivateAsync(new DeactivationRequest(stored.Token, _device), ct).ConfigureAwait(false);
        if (result.NetworkFailure) return (false, "The licence server could not be reached. Deactivation requires an internet connection.");
        if (result.Error is { } e) return (false, e.Message);
        _store.Clear();
        _log.LogInformation("Computer deactivated");
        StatusChanged?.Invoke(this, Evaluate());
        return (true, result.Success?.Message ?? "This computer has been deactivated. The licence can now be activated on another computer.");
    }

    private Task<LicenseStatus> HandleAsync(ApiResult result, string operation)
    {
        var now = _clock.GetUtcNow();
        var stored = _store.Load();
        LicenseStatus status;
        if (result.Success is { } ok)
        {
            var claims = Verify(ok.Token);
            if (claims is null)
            {
                _log.LogError("The licence server returned a token that failed verification during {Operation}", operation);
                status = Status(LicenseState.NotActivated, null, stored?.LastValidated, "The licence server's response could not be verified. Contact support.");
            }
            else
            {
                _store.Save(new StoredLicense(ok.Token, now, Max(now, stored?.LastSeen), null, null));
                status = FromClaims(claims, now, now);
                _log.LogInformation("Licence {Operation} succeeded: {State}", operation, status.State);
            }
        }
        else if (result.NetworkFailure)
        {
            var offline = Evaluate();
            status = offline.State == LicenseState.NotActivated
                ? offline with { Message = result.Error!.Message + " An internet connection is needed for the first activation." }
                : offline;
        }
        else
        {
            var e = result.Error!;
            var state = e.Code switch
            {
                LicenseErrorCodes.Revoked or LicenseErrorCodes.Suspended => LicenseState.LicenseRevoked,
                LicenseErrorCodes.Expired => LicenseState.LicenseExpired,
                LicenseErrorCodes.TrialExpired => LicenseState.TrialExpired,
                LicenseErrorCodes.ActivationLimit => LicenseState.ActivationLimitReached,
                _ => (LicenseState?)null,
            };
            if (operation == "validation" && e.Code is LicenseErrorCodes.NotActivated or LicenseErrorCodes.DeviceMismatch)
            {
                // The server no longer recognises this computer (deactivated or reset by an administrator):
                // the stored token must not keep the software usable until its offline window ends.
                _store.Clear();
                _log.LogWarning("Licence validation: this computer is no longer activated ({Code})", e.Code);
                status = Status(LicenseState.NotActivated, null, null, e.Message + " Activate again to continue editing.");
                StatusChanged?.Invoke(this, status);
                return Task.FromResult(status);
            }
            if (state is { } terminal && stored is not null && operation == "validation")
                _store.Save(stored with { ServerState = terminal, ServerMessage = e.Message, LastValidated = now });
            status = state is { } s ? Status(s, Verify(stored?.Token), stored?.LastValidated, e.Message) : Evaluate() with { Message = e.Message };
            _log.LogWarning("Licence {Operation} refused: {Code}", operation, e.Code);
        }
        StatusChanged?.Invoke(this, status);
        return Task.FromResult(status);
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset? b) => b is { } x && x > a ? x : a;
}
