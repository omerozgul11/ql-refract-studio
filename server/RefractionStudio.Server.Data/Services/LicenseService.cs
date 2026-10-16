using Microsoft.EntityFrameworkCore;
using RefractionStudio.Licensing;
using RefractionStudio.Server.Data.Email;
using RefractionStudio.Server.Data.Entities;

namespace RefractionStudio.Server.Data.Services;

/// <summary>Parameters of a new licence.</summary>
/// <param name="CustomerId">Owner.</param>
/// <param name="Product">Product code.</param>
/// <param name="Edition">Edition; null = the product's default edition.</param>
/// <param name="Seats">Number of computers that may be active at the same time.</param>
/// <param name="ExpiresAt">End of the entitlement; null = perpetual.</param>
/// <param name="MaintenanceUntil">Updates included until.</param>
/// <param name="Features">Feature codes; null = the product's default features.</param>
/// <param name="Notes">Internal notes.</param>
/// <param name="MaxTransfersPer30Days">Self-service transfers per 30 days.</param>
/// <param name="OfflineDays">Offline window of tokens in days.</param>
public sealed record CreateLicenseCommand(
    Guid CustomerId,
    string Product,
    string? Edition,
    int Seats,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? MaintenanceUntil,
    IReadOnlyList<string>? Features = null,
    string? Notes = null,
    int MaxTransfersPer30Days = 3,
    int OfflineDays = 30);

/// <summary>A newly created licence and its key. The key is never stored: show it once and e-mail it.</summary>
public sealed record CreatedLicense(License License, string Key);

/// <summary>Raised when an operation is not allowed; the message is written for the administrator.</summary>
public sealed class LicensingOperationException(string message) : Exception(message);

/// <summary>Licence lifecycle operations used by the admin portal and the payment webhooks.</summary>
public sealed class LicenseService(LicensingDbContext db, ServerSecrets secrets, EmailOutbox outbox, EmailTemplates templates, TimeProvider clock)
{
    public const int MaxSeats = 10_000;

    /// <summary>Creates a licence with a fresh random key. Saves changes.</summary>
    /// <param name="sendKeyEmail">Queue the licence-key e-mail to the customer.</param>
    public async Task<CreatedLicense> CreateAsync(CreateLicenseCommand command, Actor actor, bool sendKeyEmail, CancellationToken ct)
    {
        var product = await db.LicenseProducts.SingleOrDefaultAsync(p => p.Code == command.Product, ct).ConfigureAwait(false)
                      ?? throw new LicensingOperationException($"Unknown product “{command.Product}”.");
        if (!product.Active) throw new LicensingOperationException($"{product.Name} is not available for new licences.");
        var customer = await db.Customers.SingleOrDefaultAsync(c => c.Id == command.CustomerId, ct).ConfigureAwait(false)
                       ?? throw new LicensingOperationException("The customer does not exist.");
        CheckLimits(command.Seats, command.MaxTransfersPer30Days, command.OfflineDays);
        var now = clock.GetDbNow();
        if (command.ExpiresAt is { } exp && exp <= now) throw new LicensingOperationException("The expiry date must be in the future.");

        string key;
        string hash;
        do
        {
            key = LicenseKeys.Generate(product.KeyPrefix);
            hash = LicenseKeys.Hash(key, secrets.KeyPepper);
        }
        while (await db.Licenses.AnyAsync(l => l.LicenseKeyHash == hash, ct).ConfigureAwait(false));

        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKeyHash = hash,
            KeyHint = LicenseKeys.Hint(key),
            CustomerId = customer.Id,
            Customer = customer,
            Product = product.Code,
            Edition = string.IsNullOrWhiteSpace(command.Edition) ? product.DefaultEdition : command.Edition.Trim(),
            Status = LicenseRecordStatus.Active,
            SeatCount = command.Seats,
            CreatedAt = now,
            ExpiresAt = command.ExpiresAt?.ToUniversalTime(),
            MaintenanceUntil = command.MaintenanceUntil?.ToUniversalTime(),
            Features = [.. (command.Features ?? product.DefaultFeatures).Select(f => f.Trim()).Where(f => f.Length > 0).Distinct()],
            Notes = command.Notes,
            MaxTransfersPer30Days = command.MaxTransfersPer30Days,
            OfflineDays = command.OfflineDays,
        };
        db.Licenses.Add(license);
        var details = new { license.Product, license.Edition, seats = license.SeatCount, license.ExpiresAt, license.MaintenanceUntil, keyHint = license.KeyHint };
        db.AddEvent(now, EventTypes.LicenseCreated, actor, license.Id, details: details);
        Audit(now, actor, "license.create", license.Id, details);
        if (sendKeyEmail)
        {
            outbox.Enqueue(customer.Email, customer.Name,
                templates.LicenseKey(customer.Name, product.Name, key, license.Edition, license.SeatCount, license.ExpiresAt, license.MaintenanceUntil),
                license.Id, customer.Id);
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new CreatedLicense(license, key);
    }

    /// <summary>Replaces the key of a licence (e.g. after a leak). The old key stops working immediately.</summary>
    public async Task<string> ReplaceKeyAsync(Guid licenseId, Actor actor, bool sendKeyEmail, CancellationToken ct)
    {
        var license = await LoadAsync(licenseId, ct).ConfigureAwait(false);
        var product = await db.LicenseProducts.SingleAsync(p => p.Code == license.Product, ct).ConfigureAwait(false);
        string key;
        string hash;
        do
        {
            key = LicenseKeys.Generate(product.KeyPrefix);
            hash = LicenseKeys.Hash(key, secrets.KeyPepper);
        }
        while (await db.Licenses.AnyAsync(l => l.LicenseKeyHash == hash, ct).ConfigureAwait(false));
        var now = clock.GetDbNow();
        var oldHint = license.KeyHint;
        license.LicenseKeyHash = hash;
        license.KeyHint = LicenseKeys.Hint(key);
        var details = new { oldKeyHint = oldHint, newKeyHint = license.KeyHint };
        db.AddEvent(now, EventTypes.LicenseKeyReplaced, actor, license.Id, details: details);
        Audit(now, actor, "license.replace_key", license.Id, details);
        if (sendKeyEmail)
        {
            var c = license.Customer!;
            outbox.Enqueue(c.Email, c.Name, templates.LicenseKey(c.Name, product.Name, key, license.Edition, license.SeatCount, license.ExpiresAt, license.MaintenanceUntil), license.Id, c.Id);
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return key;
    }

    /// <summary>Permanently revokes a licence. Activated computers are refused at their next validation.</summary>
    public Task RevokeAsync(Guid licenseId, string? reason, Actor actor, CancellationToken ct)
        => ChangeStatusAsync(licenseId, LicenseRecordStatus.Revoked, reason, actor, EventTypes.LicenseRevoked, "license.revoke", ct);

    /// <summary>Temporarily suspends a licence (e.g. payment dispute).</summary>
    public Task SuspendAsync(Guid licenseId, string? reason, Actor actor, CancellationToken ct)
        => ChangeStatusAsync(licenseId, LicenseRecordStatus.Suspended, reason, actor, EventTypes.LicenseSuspended, "license.suspend", ct);

    /// <summary>Makes a suspended, revoked or expired licence active again.</summary>
    public async Task ReinstateAsync(Guid licenseId, string? reason, Actor actor, CancellationToken ct)
    {
        var license = await LoadAsync(licenseId, ct).ConfigureAwait(false);
        if (license.ExpiresAt is { } exp && exp <= clock.GetDbNow())
            throw new LicensingOperationException("This licence has passed its expiry date. Extend the expiry date to reinstate it.");
        await ChangeStatusAsync(licenseId, LicenseRecordStatus.Active, reason, actor, EventTypes.LicenseReinstated, "license.reinstate", ct).ConfigureAwait(false);
    }

    private async Task ChangeStatusAsync(Guid licenseId, LicenseRecordStatus status, string? reason, Actor actor, string eventType, string action, CancellationToken ct)
    {
        var license = await LoadAsync(licenseId, ct).ConfigureAwait(false);
        if (license.Status == status) throw new LicensingOperationException($"The licence is already {status.ToString().ToLowerInvariant()}.");
        var now = clock.GetDbNow();
        var details = new { from = license.Status.ToString().ToLowerInvariant(), to = status.ToString().ToLowerInvariant(), reason };
        license.Status = status;
        license.StatusReason = status == LicenseRecordStatus.Active ? null : RequestValidation.Truncate(reason, 1000);
        db.AddEvent(now, eventType, actor, license.Id, details: details);
        Audit(now, actor, action, license.Id, details);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets new expiry and/or maintenance dates (renewal). An expired licence becomes active again.
    /// </summary>
    /// <param name="perpetual">True to remove the expiry date.</param>
    /// <param name="notifyCustomer">Queue the renewal e-mail.</param>
    public async Task ExtendAsync(Guid licenseId, DateTimeOffset? newExpiresAt, bool perpetual, DateTimeOffset? newMaintenanceUntil, Actor actor, bool notifyCustomer, CancellationToken ct)
    {
        var license = await LoadAsync(licenseId, ct).ConfigureAwait(false);
        var now = clock.GetDbNow();
        if (!perpetual && newExpiresAt is null && newMaintenanceUntil is null) throw new LicensingOperationException("Enter a new expiry date, a new maintenance date, or both.");
        if (newExpiresAt is { } e && e <= now) throw new LicensingOperationException("The new expiry date must be in the future.");
        var details = new { oldExpiresAt = license.ExpiresAt, oldMaintenanceUntil = license.MaintenanceUntil, newExpiresAt = perpetual ? null : newExpiresAt ?? license.ExpiresAt, newMaintenanceUntil = newMaintenanceUntil ?? license.MaintenanceUntil };
        if (perpetual) license.ExpiresAt = null;
        else if (newExpiresAt is { } exp) license.ExpiresAt = exp.ToUniversalTime();
        if (newMaintenanceUntil is { } m) license.MaintenanceUntil = m.ToUniversalTime();
        if (license.Status == LicenseRecordStatus.Expired) license.Status = LicenseRecordStatus.Active;
        db.AddEvent(now, EventTypes.LicenseExtended, actor, license.Id, details: details);
        Audit(now, actor, "license.extend", license.Id, details);
        if (notifyCustomer)
        {
            var product = await db.LicenseProducts.SingleAsync(p => p.Code == license.Product, ct).ConfigureAwait(false);
            var c = license.Customer!;
            outbox.Enqueue(c.Email, c.Name, templates.LicenseRenewal(c.Name, product.Name, license.KeyHint, license.ExpiresAt, license.MaintenanceUntil), license.Id, c.Id);
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Deactivates every computer of the licence (support action; does not count as transfers).</summary>
    public async Task<int> ResetActivationsAsync(Guid licenseId, Actor actor, CancellationToken ct)
    {
        var license = await LoadAsync(licenseId, ct).ConfigureAwait(false);
        var now = clock.GetDbNow();
        var active = await db.Activations.Where(a => a.LicenseId == licenseId && a.DeactivatedAt == null).ToListAsync(ct).ConfigureAwait(false);
        foreach (var a in active)
        {
            a.DeactivatedAt = now;
            a.DeactivationReason = DeactivationReasons.AdminReset;
        }
        var details = new { deactivated = active.Count };
        db.AddEvent(now, EventTypes.ActivationsReset, actor, license.Id, details: details);
        Audit(now, actor, "license.reset_activations", license.Id, details);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return active.Count;
    }

    /// <summary>Deactivates one computer of the licence (support action; does not count as a transfer).</summary>
    public async Task DeactivateActivationAsync(Guid activationId, Actor actor, CancellationToken ct)
    {
        var activation = await db.Activations.SingleOrDefaultAsync(a => a.Id == activationId, ct).ConfigureAwait(false)
                         ?? throw new LicensingOperationException("The activation does not exist.");
        if (!activation.IsActive) throw new LicensingOperationException("This activation has already ended.");
        var now = clock.GetDbNow();
        activation.DeactivatedAt = now;
        activation.DeactivationReason = DeactivationReasons.Admin;
        db.AddEvent(now, EventTypes.Deactivated, actor, activation.LicenseId, deviceId: activation.DeviceId, details: new { activationId, reason = DeactivationReasons.Admin });
        Audit(now, actor, "activation.deactivate", activation.LicenseId, new { activationId, deviceId = activation.DeviceId });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Administrative override of seat count, transfer limit and offline window.</summary>
    public async Task SetLimitsAsync(Guid licenseId, int seats, int maxTransfersPer30Days, int offlineDays, Actor actor, CancellationToken ct)
    {
        CheckLimits(seats, maxTransfersPer30Days, offlineDays);
        var license = await LoadAsync(licenseId, ct).ConfigureAwait(false);
        var now = clock.GetDbNow();
        var details = new
        {
            oldSeats = license.SeatCount, seats,
            oldMaxTransfers = license.MaxTransfersPer30Days, maxTransfers = maxTransfersPer30Days,
            oldOfflineDays = license.OfflineDays, offlineDays,
        };
        license.SeatCount = seats;
        license.MaxTransfersPer30Days = maxTransfersPer30Days;
        license.OfflineDays = offlineDays;
        db.AddEvent(now, EventTypes.LicenseLimitsChanged, actor, license.Id, details: details);
        Audit(now, actor, "license.set_limits", license.Id, details);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Updates the internal notes.</summary>
    public async Task SetNotesAsync(Guid licenseId, string? notes, Actor actor, CancellationToken ct)
    {
        var license = await LoadAsync(licenseId, ct).ConfigureAwait(false);
        license.Notes = RequestValidation.Truncate(notes, 4000);
        Audit(clock.GetDbNow(), actor, "license.set_notes", license.Id, null);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Queues the activation-instructions e-mail to the licence owner.</summary>
    public async Task SendActivationInstructionsAsync(Guid licenseId, Actor actor, CancellationToken ct)
    {
        var license = await LoadAsync(licenseId, ct).ConfigureAwait(false);
        var product = await db.LicenseProducts.SingleAsync(p => p.Code == license.Product, ct).ConfigureAwait(false);
        var c = license.Customer!;
        outbox.Enqueue(c.Email, c.Name, templates.ActivationInstructions(c.Name, product.Name, license.KeyHint, license.SeatCount), license.Id, c.Id);
        Audit(clock.GetDbNow(), actor, "license.send_activation_instructions", license.Id, null);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Marks licences past their expiry date as expired (housekeeping). Returns the number changed.</summary>
    public async Task<int> ExpireDueAsync(CancellationToken ct)
    {
        var now = clock.GetDbNow();
        var due = await db.Licenses.Where(l => l.Status == LicenseRecordStatus.Active && l.ExpiresAt != null && l.ExpiresAt <= now).Take(500).ToListAsync(ct).ConfigureAwait(false);
        foreach (var l in due)
        {
            l.Status = LicenseRecordStatus.Expired;
            db.AddEvent(now, EventTypes.LicenseExpired, Actor.System, l.Id, details: new { l.ExpiresAt });
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return due.Count;
    }

    private static void CheckLimits(int seats, int maxTransfers, int offlineDays)
    {
        if (seats is < 1 or > MaxSeats) throw new LicensingOperationException($"Seats must be between 1 and {MaxSeats}.");
        if (maxTransfers is < 0 or > 1000) throw new LicensingOperationException("Transfers per 30 days must be between 0 and 1000.");
        if (offlineDays is < 1 or > TokenIssuer.MaxOfflineDays) throw new LicensingOperationException($"Offline days must be between 1 and {TokenIssuer.MaxOfflineDays}.");
    }

    private async Task<License> LoadAsync(Guid id, CancellationToken ct)
        => await db.Licenses.Include(l => l.Customer).SingleOrDefaultAsync(l => l.Id == id, ct).ConfigureAwait(false)
           ?? throw new LicensingOperationException("The licence does not exist.");

    private void Audit(DateTimeOffset now, Actor actor, string action, Guid licenseId, object? details)
        => db.AddAudit(now, actor, action, "license", licenseId.ToString(), details);
}
