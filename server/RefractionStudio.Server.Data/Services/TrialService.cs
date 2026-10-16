using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RefractionStudio.Licensing;
using RefractionStudio.Server.Data.Email;
using RefractionStudio.Server.Data.Entities;

namespace RefractionStudio.Server.Data.Services;

/// <summary>
/// Trials: one per device and product, recorded server-side so reinstalling never resets them. A device
/// matches an existing trial when its fingerprint is equal or at least <see cref="MinMatchingComponents"/>
/// component hashes are shared (one hardware change does not create a fresh trial).
/// </summary>
public sealed class TrialService(LicensingDbContext db, TokenIssuer tokens, EmailOutbox outbox, EmailTemplates templates, EmailOptions emailOptions,
    TimeProvider clock, ILogger<TrialService> log)
{
    /// <summary>Component hashes that must match for tolerant matching.</summary>
    public const int MinMatchingComponents = 2;

    /// <summary>Upper bound of component hashes remembered per trial.</summary>
    public const int MaxRememberedComponents = 32;

    /// <summary>Starts a trial on this computer or continues the existing one.</summary>
    public async Task<LicenseOutcome> StartAsync(TrialRequest request, Actor actor, CancellationToken ct)
    {
        if (RequestValidation.CheckDevice(request.Device) is { } deviceProblem) return LicenseOutcome.Fail(LicenseErrorCodes.InvalidRequest, deviceProblem);
        if (RequestValidation.CheckAppVersion(request.AppVersion) is { } versionProblem) return LicenseOutcome.Fail(LicenseErrorCodes.InvalidRequest, versionProblem);
        if (!RequestValidation.IsProductCode(request.Product)) return LicenseOutcome.Fail(LicenseErrorCodes.InvalidRequest, "The product is missing or invalid.");
        if (request.Email is { } givenEmail && !RequestValidation.IsEmail(givenEmail)) return LicenseOutcome.Fail(LicenseErrorCodes.InvalidRequest, "The e-mail address is not valid.");
        var product = await db.LicenseProducts.AsNoTracking().SingleOrDefaultAsync(p => p.Code == request.Product, ct).ConfigureAwait(false);
        if (product is null) return LicenseOutcome.Fail(LicenseErrorCodes.InvalidRequest, "This product is not licensed by this server.");
        if (!product.TrialsEnabled || !product.Active) return LicenseOutcome.Fail(LicenseErrorCodes.TrialExpired, $"Trials of {product.Name} are not available at the moment. Contact QuakeLogic for an evaluation licence.");

        return await db.InTransactionWithRetryAsync(async token =>
        {
            // Serialise trial creation per product so two simultaneous first starts cannot create two trials.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({"trial:" + product.Code}))", token).ConfigureAwait(false);
            var now = clock.GetDbNow();
            var blocked = await db.Devices.AnyAsync(d => d.Product == product.Code && d.Fingerprint == request.Device.Fingerprint && d.Blocked, token).ConfigureAwait(false);
            if (blocked) return LicenseOutcome.Fail(LicenseErrorCodes.TrialExpired, "A trial is not available on this computer. Contact QuakeLogic support.");

            var trial = await FindMatchAsync(product.Code, request.Device, token).ConfigureAwait(false);
            if (trial is null)
            {
                trial = new TrialDevice
                {
                    Id = Guid.NewGuid(),
                    Product = product.Code,
                    Fingerprint = request.Device.Fingerprint,
                    ComponentHashes = [.. request.Device.ComponentHashes.Distinct()],
                    StartedAt = now,
                    EndsAt = now.AddDays(product.TrialDays),
                    Email = request.Email?.Trim(),
                };
                db.TrialDevices.Add(trial);
                Touch(trial, request.Device, request.AppVersion, now);
                db.AddEvent(now, EventTypes.TrialStarted, actor, trialId: trial.Id, details: new { product = product.Code, trial.EndsAt, appVersion = trial.AppVersion });
                await db.SaveChangesAsync(token).ConfigureAwait(false);
                log.LogInformation("Trial {TrialId} of {Product} started, ends {EndsAt:o}", trial.Id, product.Code, trial.EndsAt);
                return LicenseOutcome.Ok(tokens.IssueTrial(trial, product, request.Device.Fingerprint, now), $"Your {product.TrialDays}-day trial of {product.Name} has started.");
            }

            var matchedBy = trial.Fingerprint == request.Device.Fingerprint ? "fingerprint" : "components";
            Touch(trial, request.Device, request.AppVersion, now);
            // A contact address recorded by staff is never replaced by the one typed in the application.
            if (trial.Email is null && request.Email is { } email) trial.Email = email.Trim();
            if (now >= trial.EndsAt)
            {
                db.AddEvent(now, EventTypes.TrialRefused, actor, trialId: trial.Id, details: new { code = LicenseErrorCodes.TrialExpired, matchedBy });
                await db.SaveChangesAsync(token).ConfigureAwait(false);
                return LicenseOutcome.Fail(LicenseErrorCodes.TrialExpired, TrialEndedMessage(product, trial));
            }
            db.AddEvent(now, EventTypes.TrialContinued, actor, trialId: trial.Id, details: new { matchedBy, appVersion = trial.AppVersion });
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            var days = (int)Math.Ceiling((trial.EndsAt - now).TotalDays);
            return LicenseOutcome.Ok(tokens.IssueTrial(trial, product, request.Device.Fingerprint, now),
                $"Your trial of {product.Name} continues: {days} day{(days == 1 ? "" : "s")} remaining.");
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Online validation of a trial token (called by <see cref="ActivationService.ValidateAsync"/>).</summary>
    public async Task<LicenseOutcome> ValidateTrialAsync(LicenseClaims claims, ValidationRequest request, Actor actor, CancellationToken ct)
    {
        if (!Guid.TryParse(claims.LicenseId, out var trialId)) return LicenseOutcome.Fail(LicenseErrorCodes.InvalidRequest, "The trial token is not valid.");
        var trial = await db.TrialDevices.SingleOrDefaultAsync(t => t.Id == trialId, ct).ConfigureAwait(false);
        if (trial is null) return LicenseOutcome.Fail(LicenseErrorCodes.TrialExpired, "This trial is no longer valid. Activate a licence to continue editing.");
        var product = await db.LicenseProducts.AsNoTracking().SingleAsync(p => p.Code == trial.Product, ct).ConfigureAwait(false);
        var now = clock.GetDbNow();
        if (await db.Devices.AnyAsync(d => d.Product == trial.Product && d.Fingerprint == request.Device.Fingerprint && d.Blocked, ct).ConfigureAwait(false))
            return LicenseOutcome.Fail(LicenseErrorCodes.TrialExpired, "A trial is not available on this computer. Contact QuakeLogic support.");
        Touch(trial, request.Device, request.AppVersion, now);
        if (now >= trial.EndsAt)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return LicenseOutcome.Fail(LicenseErrorCodes.TrialExpired, TrialEndedMessage(product, trial));
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return LicenseOutcome.Ok(tokens.IssueTrial(trial, product, request.Device.Fingerprint, now), "Trial verified.");
    }

    /// <summary>Extends a trial by <paramref name="days"/> days (from its end, or from now if it has already ended).</summary>
    public async Task ExtendAsync(Guid trialId, int days, Actor actor, CancellationToken ct)
    {
        if (days is < 1 or > 365) throw new LicensingOperationException("A trial can be extended by 1 to 365 days.");
        var trial = await db.TrialDevices.SingleOrDefaultAsync(t => t.Id == trialId, ct).ConfigureAwait(false)
                    ?? throw new LicensingOperationException("The trial does not exist.");
        var now = clock.GetDbNow();
        var oldEnd = trial.EndsAt;
        trial.EndsAt = (trial.EndsAt > now ? trial.EndsAt : now).AddDays(days);
        trial.ExtendedByAdmin = true;
        trial.ReminderSentAt = null;
        var details = new { days, oldEndsAt = oldEnd, newEndsAt = trial.EndsAt };
        db.AddEvent(now, EventTypes.TrialExtended, actor, trialId: trial.Id, details: details);
        db.AddAudit(now, actor, "trial.extend", "trial", trial.Id.ToString(), details);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Records a contact e-mail for a trial (enables the expiration reminder).</summary>
    public async Task SetEmailAsync(Guid trialId, string? email, Actor actor, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(email) && !RequestValidation.IsEmail(email)) throw new LicensingOperationException("Enter a valid e-mail address.");
        var trial = await db.TrialDevices.SingleOrDefaultAsync(t => t.Id == trialId, ct).ConfigureAwait(false)
                    ?? throw new LicensingOperationException("The trial does not exist.");
        trial.Email = string.IsNullOrWhiteSpace(email) ? null : RequestValidation.NormalizeEmail(email);
        db.AddAudit(clock.GetDbNow(), actor, "trial.set_email", "trial", trial.Id.ToString(), new { hasEmail = trial.Email is not null });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Queues "trial ends soon" reminders for trials with a known e-mail. Returns how many were queued.</summary>
    public async Task<int> QueueExpirationRemindersAsync(CancellationToken ct)
    {
        var now = clock.GetDbNow();
        var horizon = now.AddDays(emailOptions.TrialReminderDays);
        var due = await db.TrialDevices
            .Where(t => t.Email != null && t.ReminderSentAt == null && t.EndsAt > now && t.EndsAt <= horizon)
            .Take(200).ToListAsync(ct).ConfigureAwait(false);
        if (due.Count == 0) return 0;
        var names = await db.LicenseProducts.AsNoTracking().ToDictionaryAsync(p => p.Code, p => p.Name, ct).ConfigureAwait(false);
        foreach (var t in due)
        {
            outbox.Enqueue(t.Email!, null, templates.TrialExpirationReminder(names.GetValueOrDefault(t.Product, t.Product), t.EndsAt));
            t.ReminderSentAt = now;
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return due.Count;
    }

    /// <summary>Finds the trial of this device: exact fingerprint first, otherwise ≥ 2 shared component hashes.</summary>
    internal async Task<TrialDevice?> FindMatchAsync(string product, DeviceInfo device, CancellationToken ct)
    {
        var hashes = device.ComponentHashes.Distinct().ToArray();
        var matches = await db.TrialDevices.FromSqlInterpolated($@"
            SELECT * FROM trial_devices
            WHERE product = {product}
              AND (fingerprint = {device.Fingerprint}
                   OR (component_hashes && {hashes}
                       AND cardinality(ARRAY(SELECT unnest(component_hashes) INTERSECT SELECT unnest({hashes}::text[]))) >= {MinMatchingComponents}))
            ORDER BY (fingerprint = {device.Fingerprint}) DESC, started_at ASC
            LIMIT 1").ToListAsync(ct).ConfigureAwait(false);
        return matches.FirstOrDefault();
    }

    private static void Touch(TrialDevice trial, DeviceInfo device, string? appVersion, DateTimeOffset now)
    {
        trial.LastSeen = now;
        trial.MachineName = RequestValidation.Truncate(device.MachineName, 128) ?? trial.MachineName;
        trial.OsVersion = RequestValidation.Truncate(device.OsVersion, 256);
        trial.AppVersion = RequestValidation.Truncate(appVersion, 64);
        // Remember new component hashes so gradual hardware changes keep matching the same trial.
        var merged = trial.ComponentHashes.Union(device.ComponentHashes).ToList();
        if (merged.Count > MaxRememberedComponents) merged = merged[^MaxRememberedComponents..];
        if (merged.Count != trial.ComponentHashes.Count) trial.ComponentHashes = merged;
    }

    private static string TrialEndedMessage(LicenseProduct product, TrialDevice trial)
        => $"The {product.TrialDays}-day trial of {product.Name} on this computer ended on {EmailTemplates.FormatDate(trial.EndsAt)}. "
           + "Your projects remain on this computer and can still be opened and viewed. Activate a licence to continue editing.";
}
