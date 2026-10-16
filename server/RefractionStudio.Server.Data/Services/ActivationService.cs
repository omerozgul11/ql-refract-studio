using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RefractionStudio.Licensing;
using RefractionStudio.Server.Data.Email;
using RefractionStudio.Server.Data.Entities;

namespace RefractionStudio.Server.Data.Services;

/// <summary>
/// Activation, online validation and deactivation of licences on computers (the licence API).
/// Seat accounting runs under a row lock on the licence, so concurrent activations cannot oversubscribe it.
/// </summary>
public sealed class ActivationService(LicensingDbContext db, ServerSecrets secrets, TokenIssuer tokens, EmailOutbox outbox, EmailTemplates templates,
    TimeProvider clock, ILogger<ActivationService> log)
{
    /// <summary>Window of the transfer limit.</summary>
    public static readonly TimeSpan TransferWindow = TimeSpan.FromDays(30);

    private const string InvalidKeyMessage = "This licence key was not recognised. Check it for typing errors, or contact QuakeLogic support if you copied it from your licence e-mail.";

    /// <summary>Activates a licence on a computer (or re-issues the token for a computer that is already activated).</summary>
    public async Task<LicenseOutcome> ActivateAsync(ActivationRequest request, Actor actor, CancellationToken ct)
    {
        if (RequestValidation.CheckDevice(request.Device) is { } deviceProblem) return LicenseOutcome.Fail(LicenseErrorCodes.InvalidRequest, deviceProblem);
        if (RequestValidation.CheckAppVersion(request.AppVersion) is { } versionProblem) return LicenseOutcome.Fail(LicenseErrorCodes.InvalidRequest, versionProblem);
        if (!RequestValidation.IsProductCode(request.Product)) return LicenseOutcome.Fail(LicenseErrorCodes.InvalidRequest, "The product is missing or invalid.");

        var product = await db.LicenseProducts.AsNoTracking().SingleOrDefaultAsync(p => p.Code == request.Product, ct).ConfigureAwait(false);
        if (product is null) return LicenseOutcome.Fail(LicenseErrorCodes.InvalidRequest, "This product is not licensed by this server.");

        var key = LicenseKeys.Normalize(request.LicenseKey, product.KeyPrefix);
        if (key is null) return LicenseOutcome.Fail(LicenseErrorCodes.InvalidKey, InvalidKeyMessage);
        var hash = LicenseKeys.Hash(key, secrets.KeyPepper);

        return await db.InTransactionWithRetryAsync(async token =>
        {
            // Lock the licence row: seat counting and transfer accounting must be serialised per licence.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM licenses WHERE license_key_hash = {hash} FOR UPDATE", token).ConfigureAwait(false);
            var license = await db.Licenses.Include(l => l.Customer).SingleOrDefaultAsync(l => l.LicenseKeyHash == hash, token).ConfigureAwait(false);
            var now = clock.GetDbNow();
            if (license is null)
            {
                log.LogInformation("Activation refused: unknown key ending {KeyHint}", LicenseKeys.Hint(key));
                return LicenseOutcome.Fail(LicenseErrorCodes.InvalidKey, InvalidKeyMessage);
            }
            if (license.Product != product.Code)
                return await RefuseAsync(license, null, actor, now, LicenseErrorCodes.ProductMismatch, $"This licence key is for a different product, not {product.Name}.", token).ConfigureAwait(false);
            if (StatusRefusal(license, now) is { } refusal)
                return await RefuseAsync(license, null, actor, now, refusal.Code, refusal.Message, token).ConfigureAwait(false);

            var device = await UpsertDeviceAsync(product.Code, request.Device, now, token).ConfigureAwait(false);
            if (device.Blocked)
                return await RefuseAsync(license, device, actor, now, LicenseErrorCodes.Revoked, "This computer has been blocked from activating licences. Contact QuakeLogic support.", token).ConfigureAwait(false);

            var appVersion = RequestValidation.Truncate(request.AppVersion, 64);
            var existing = await db.Activations.SingleOrDefaultAsync(a => a.LicenseId == license.Id && a.DeviceId == device.Id && a.DeactivatedAt == null, token).ConfigureAwait(false);
            if (existing is not null)
            {
                // Same computer activating again (reinstall, lost local file): no extra seat.
                existing.LastValidatedAt = now;
                existing.AppVersion = appVersion;
                db.AddEvent(now, EventTypes.Reactivated, actor, license.Id, deviceId: device.Id, details: new { activationId = existing.Id, appVersion });
                await db.SaveChangesAsync(token).ConfigureAwait(false);
                log.LogInformation("Licence {KeyHint} re-activated on known device {DeviceId}", license.KeyHint, device.Id);
                return LicenseOutcome.Ok(tokens.IssueLicense(license, license.Customer!, device.Fingerprint, now), "This computer was already activated. The licence has been restored.");
            }

            var active = await db.Activations.CountAsync(a => a.LicenseId == license.Id && a.DeactivatedAt == null, token).ConfigureAwait(false);
            if (active >= license.SeatCount)
            {
                var seats = license.SeatCount == 1 ? "1 computer" : $"{license.SeatCount} computers";
                return await RefuseAsync(license, device, actor, now, LicenseErrorCodes.ActivationLimit,
                    $"This licence is already active on {seats}, its permitted number. Deactivate it on another computer (Help → Licence → Deactivate this computer) or contact QuakeLogic support.", token).ConfigureAwait(false);
            }

            var since = now - TransferWindow;
            var transfers = await db.Activations.CountAsync(a => a.LicenseId == license.Id && a.DeactivatedAt != null && a.DeactivatedAt > since
                                                                 && a.DeactivationReason == DeactivationReasons.User, token).ConfigureAwait(false);
            if (transfers > license.MaxTransfersPer30Days)
            {
                return await RefuseAsync(license, device, actor, now, LicenseErrorCodes.TransferLimit,
                    $"This licence has been moved between computers too often recently (more than {license.MaxTransfersPer30Days} times in 30 days). Contact QuakeLogic support to activate this computer.", token).ConfigureAwait(false);
            }

            var activation = new Activation
            {
                Id = Guid.NewGuid(),
                LicenseId = license.Id,
                DeviceId = device.Id,
                ActivatedAt = now,
                LastValidatedAt = now,
                AppVersion = appVersion,
            };
            db.Activations.Add(activation);
            db.AddEvent(now, EventTypes.Activated, actor, license.Id, deviceId: device.Id, details: new { activationId = activation.Id, appVersion, seatsInUse = active + 1, seats = license.SeatCount });
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            log.LogInformation("Licence {KeyHint} activated on device {DeviceId} ({Used}/{Seats} seats)", license.KeyHint, device.Id, active + 1, license.SeatCount);
            var remaining = license.SeatCount - active - 1;
            var message = remaining > 0
                ? $"Activation successful. {remaining} more computer{(remaining == 1 ? "" : "s")} can be activated with this licence."
                : "Activation successful.";
            return LicenseOutcome.Ok(tokens.IssueLicense(license, license.Customer!, device.Fingerprint, now), message);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Online validation of a licence or trial token: verifies it, re-checks the server state and returns a
    /// refreshed token with a new offline window.
    /// </summary>
    public async Task<LicenseOutcome> ValidateAsync(ValidationRequest request, TrialService trials, Actor actor, CancellationToken ct)
    {
        if (RequestValidation.CheckDevice(request.Device) is { } deviceProblem) return LicenseOutcome.Fail(LicenseErrorCodes.InvalidRequest, deviceProblem);
        if (RequestValidation.CheckAppVersion(request.AppVersion) is { } versionProblem) return LicenseOutcome.Fail(LicenseErrorCodes.InvalidRequest, versionProblem);
        var claims = tokens.Verify(request.Token);
        if (claims is null) return LicenseOutcome.Fail(LicenseErrorCodes.InvalidRequest, "The licence on this computer could not be verified. Activate the licence again.");
        if (claims.DeviceHash != request.Device.Fingerprint)
            return LicenseOutcome.Fail(LicenseErrorCodes.DeviceMismatch, "This licence was activated on a different computer. Activate the licence on this computer.");
        if (claims.Kind == LicenseKind.Trial) return await trials.ValidateTrialAsync(claims, request, actor, ct).ConfigureAwait(false);
        if (!Guid.TryParse(claims.LicenseId, out var licenseId)) return LicenseOutcome.Fail(LicenseErrorCodes.InvalidRequest, "The licence token is not valid.");

        var now = clock.GetDbNow();
        var license = await db.Licenses.Include(l => l.Customer).SingleOrDefaultAsync(l => l.Id == licenseId, ct).ConfigureAwait(false);
        if (license is null) return LicenseOutcome.Fail(LicenseErrorCodes.Revoked, "This licence no longer exists. Contact QuakeLogic support.");
        var device = await db.Devices.SingleOrDefaultAsync(d => d.Product == license.Product && d.Fingerprint == request.Device.Fingerprint, ct).ConfigureAwait(false);
        if (StatusRefusal(license, now) is { } refusal)
            return await RefuseAsync(license, device, actor, now, refusal.Code, refusal.Message, ct, EventTypes.ValidationRefused).ConfigureAwait(false);
        if (device is null)
            return LicenseOutcome.Fail(LicenseErrorCodes.NotActivated, "This computer is no longer activated for this licence. Activate it again.");
        if (device.Blocked)
            return await RefuseAsync(license, device, actor, now, LicenseErrorCodes.Revoked, "This computer has been blocked from using licences. Contact QuakeLogic support.", ct, EventTypes.ValidationRefused).ConfigureAwait(false);
        var activation = await db.Activations.SingleOrDefaultAsync(a => a.LicenseId == license.Id && a.DeviceId == device.Id && a.DeactivatedAt == null, ct).ConfigureAwait(false);
        if (activation is null)
            return await RefuseAsync(license, device, actor, now, LicenseErrorCodes.NotActivated,
                "This computer is no longer activated for this licence (it was deactivated). Activate the licence again to continue.", ct, EventTypes.ValidationRefused).ConfigureAwait(false);

        activation.LastValidatedAt = now;
        activation.AppVersion = RequestValidation.Truncate(request.AppVersion, 64);
        UpdateDevice(device, request.Device, now);
        db.AddEvent(now, EventTypes.Validated, actor, license.Id, deviceId: device.Id, details: new { activationId = activation.Id, appVersion = activation.AppVersion });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return LicenseOutcome.Ok(tokens.IssueLicense(license, license.Customer!, device.Fingerprint, now), "Licence verified.");
    }

    /// <summary>Deactivates this computer, freeing its seat. Idempotent: an already deactivated computer succeeds.</summary>
    public async Task<LicenseOutcome> DeactivateAsync(DeactivationRequest request, Actor actor, CancellationToken ct)
    {
        if (RequestValidation.CheckDevice(request.Device) is { } deviceProblem) return LicenseOutcome.Fail(LicenseErrorCodes.InvalidRequest, deviceProblem);
        var claims = tokens.Verify(request.Token);
        if (claims is null) return LicenseOutcome.Fail(LicenseErrorCodes.InvalidRequest, "The licence on this computer could not be verified, so it cannot be deactivated online. Contact QuakeLogic support.");
        if (claims.Kind == LicenseKind.Trial) return LicenseOutcome.Fail(LicenseErrorCodes.InvalidRequest, "A trial cannot be deactivated; it is tied to this computer.");
        if (claims.DeviceHash != request.Device.Fingerprint)
            return LicenseOutcome.Fail(LicenseErrorCodes.DeviceMismatch, "This licence belongs to a different computer and cannot be deactivated here.");
        if (!Guid.TryParse(claims.LicenseId, out var licenseId)) return LicenseOutcome.Fail(LicenseErrorCodes.InvalidRequest, "The licence token is not valid.");

        return await db.InTransactionWithRetryAsync(async token =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM licenses WHERE id = {licenseId} FOR UPDATE", token).ConfigureAwait(false);
            var license = await db.Licenses.Include(l => l.Customer).SingleOrDefaultAsync(l => l.Id == licenseId, token).ConfigureAwait(false);
            if (license is null) return LicenseOutcome.Fail(LicenseErrorCodes.Revoked, "This licence no longer exists.");
            var now = clock.GetDbNow();
            var device = await db.Devices.SingleOrDefaultAsync(d => d.Product == license.Product && d.Fingerprint == request.Device.Fingerprint, token).ConfigureAwait(false);
            var activation = device is null ? null
                : await db.Activations.SingleOrDefaultAsync(a => a.LicenseId == license.Id && a.DeviceId == device.Id && a.DeactivatedAt == null, token).ConfigureAwait(false);
            if (activation is null)
                return LicenseOutcome.Ok("", "This computer was already deactivated.");

            activation.DeactivatedAt = now;
            activation.DeactivationReason = DeactivationReasons.User;
            db.AddEvent(now, EventTypes.Deactivated, actor, license.Id, deviceId: device!.Id, details: new { activationId = activation.Id, reason = DeactivationReasons.User });
            var product = await db.LicenseProducts.SingleAsync(p => p.Code == license.Product, token).ConfigureAwait(false);
            var c = license.Customer!;
            outbox.Enqueue(c.Email, c.Name, templates.DeviceDeactivated(c.Name, product.Name, license.KeyHint, device.MachineName, now), license.Id, c.Id);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            log.LogInformation("Licence {KeyHint} deactivated on device {DeviceId}", license.KeyHint, device.Id);
            return LicenseOutcome.Ok("", "This computer has been deactivated. The licence can now be activated on another computer.");
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Refusal caused by the licence's own state, if any.</summary>
    internal static (string Code, string Message)? StatusRefusal(License license, DateTimeOffset now)
    {
        switch (license.Status)
        {
            case LicenseRecordStatus.Revoked:
                return (LicenseErrorCodes.Revoked, "This licence has been revoked and can no longer be used. Contact QuakeLogic support if you believe this is a mistake.");
            case LicenseRecordStatus.Suspended:
                return (LicenseErrorCodes.Suspended, "This licence is suspended. Contact QuakeLogic support to restore it.");
        }
        if (license.Status == LicenseRecordStatus.Expired || license.ExpiresAt is { } exp && exp <= now)
            return (LicenseErrorCodes.Expired, $"This licence expired on {EmailTemplates.FormatDate(license.ExpiresAt)}. Renew it to continue editing.");
        return null;
    }

    private async Task<LicenseOutcome> RefuseAsync(License license, Device? device, Actor actor, DateTimeOffset now, string code, string message, CancellationToken ct, string eventType = EventTypes.ActivationRefused)
    {
        db.AddEvent(now, eventType, actor, license.Id, deviceId: device?.Id, details: new { code });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        log.LogInformation("Licence {KeyHint} refused for device {DeviceId}: {Code}", license.KeyHint, device?.Id, code);
        return LicenseOutcome.Fail(code, message);
    }

    private async Task<Device> UpsertDeviceAsync(string product, DeviceInfo info, DateTimeOffset now, CancellationToken ct)
    {
        var device = await db.Devices.SingleOrDefaultAsync(d => d.Product == product && d.Fingerprint == info.Fingerprint, ct).ConfigureAwait(false);
        if (device is null)
        {
            device = new Device
            {
                Id = Guid.NewGuid(),
                Product = product,
                Fingerprint = info.Fingerprint,
                FirstSeen = now,
            };
            db.Devices.Add(device);
        }
        UpdateDevice(device, info, now);
        return device;
    }

    internal static void UpdateDevice(Device device, DeviceInfo info, DateTimeOffset now)
    {
        device.ComponentHashes = [.. info.ComponentHashes.Distinct()];
        device.MachineName = RequestValidation.Truncate(info.MachineName, 128) ?? device.MachineName;
        device.OsVersion = RequestValidation.Truncate(info.OsVersion, 256);
        device.LastSeen = now;
    }
}
