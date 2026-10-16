using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RefractionStudio.Server.Data.Entities;

namespace RefractionStudio.Server.Data.Services;

/// <summary>Customer records.</summary>
public sealed class CustomerService(LicensingDbContext db, TimeProvider clock)
{
    /// <summary>Finds a customer by e-mail or creates one (not saved until the caller saves).</summary>
    public async Task<Customer> FindOrCreateAsync(string email, string? name, string? company, Actor actor, CancellationToken ct)
    {
        if (!RequestValidation.IsEmail(email)) throw new LicensingOperationException("Enter a valid e-mail address.");
        var normalized = RequestValidation.NormalizeEmail(email);
        var customer = await db.Customers.SingleOrDefaultAsync(c => c.Email == normalized, ct).ConfigureAwait(false)
                       ?? db.Customers.Local.FirstOrDefault(c => c.Email == normalized);
        if (customer is not null)
        {
            if (string.IsNullOrWhiteSpace(customer.Name) && !string.IsNullOrWhiteSpace(name)) customer.Name = RequestValidation.Truncate(name, 200)!;
            if (string.IsNullOrWhiteSpace(customer.Company) && !string.IsNullOrWhiteSpace(company)) customer.Company = RequestValidation.Truncate(company, 200);
            return customer;
        }
        var now = clock.GetDbNow();
        customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = normalized,
            Name = RequestValidation.Truncate(name, 200) ?? "",
            Company = string.IsNullOrWhiteSpace(company) ? null : RequestValidation.Truncate(company, 200),
            CreatedAt = now,
        };
        db.Customers.Add(customer);
        db.AddAudit(now, actor, "customer.create", "customer", customer.Id.ToString(), new { email = normalized });
        return customer;
    }

    /// <summary>Updates contact details.</summary>
    public async Task UpdateAsync(Guid id, string name, string? company, string? notes, Actor actor, CancellationToken ct)
    {
        var customer = await db.Customers.SingleOrDefaultAsync(c => c.Id == id, ct).ConfigureAwait(false)
                       ?? throw new LicensingOperationException("The customer does not exist.");
        customer.Name = RequestValidation.Truncate(name, 200) ?? "";
        customer.Company = string.IsNullOrWhiteSpace(company) ? null : RequestValidation.Truncate(company, 200);
        customer.Notes = RequestValidation.Truncate(notes, 4000);
        db.AddAudit(clock.GetDbNow(), actor, "customer.update", "customer", id.ToString(), null);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>Device administration.</summary>
public sealed class DeviceService(LicensingDbContext db, TimeProvider clock)
{
    /// <summary>Blocks a device: it can no longer activate licences, validate or start trials.</summary>
    public async Task BlockAsync(Guid deviceId, string? reason, Actor actor, CancellationToken ct)
    {
        var device = await LoadAsync(deviceId, ct).ConfigureAwait(false);
        if (device.Blocked) throw new LicensingOperationException("The device is already blocked.");
        var now = clock.GetDbNow();
        device.Blocked = true;
        device.BlockedReason = RequestValidation.Truncate(reason, 1000);
        db.AddEvent(now, EventTypes.DeviceBlocked, actor, deviceId: device.Id, details: new { reason });
        db.AddAudit(now, actor, "device.block", "device", device.Id.ToString(), new { reason });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task UnblockAsync(Guid deviceId, Actor actor, CancellationToken ct)
    {
        var device = await LoadAsync(deviceId, ct).ConfigureAwait(false);
        if (!device.Blocked) throw new LicensingOperationException("The device is not blocked.");
        var now = clock.GetDbNow();
        device.Blocked = false;
        device.BlockedReason = null;
        db.AddEvent(now, EventTypes.DeviceUnblocked, actor, deviceId: device.Id);
        db.AddAudit(now, actor, "device.unblock", "device", device.Id.ToString(), null);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task<Device> LoadAsync(Guid id, CancellationToken ct)
        => await db.Devices.SingleOrDefaultAsync(d => d.Id == id, ct).ConfigureAwait(false) ?? throw new LicensingOperationException("The device does not exist.");
}

/// <summary>Outcome of a sign-in attempt.</summary>
public enum AdminSignInResult
{
    Success,
    InvalidCredentials,
    LockedOut,
}

/// <summary>Admin portal accounts: sign-in with lockout, password changes, user management.</summary>
public sealed class AdminUserService(LicensingDbContext db, TimeProvider clock, ILogger<AdminUserService> log)
{
    public const int MaxFailedAttempts = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    public const int MinPasswordLength = 12;

    private static readonly PasswordHasher<AdminUser> Hasher = new();

    /// <summary>A hash of a random password, verified for unknown accounts so timing does not reveal which e-mails exist.</summary>
    private static readonly string DummyHash = Hasher.HashPassword(new AdminUser(), Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)));

    /// <summary>Checks credentials. Five consecutive failures lock the account for 15 minutes.</summary>
    public async Task<(AdminSignInResult Result, AdminUser? User)> SignInAsync(string email, string password, string? ipHash, CancellationToken ct)
    {
        var normalized = RequestValidation.NormalizeEmail(email ?? "");
        var user = await db.AdminUsers.SingleOrDefaultAsync(u => u.Email == normalized, ct).ConfigureAwait(false);
        var now = clock.GetDbNow();
        if (user is null || user.Disabled)
        {
            Hasher.VerifyHashedPassword(new AdminUser(), DummyHash, password ?? "");
            db.AddAudit(now, new Actor("anonymous", null, ipHash), "admin.login_failed", "admin_user", user?.Id.ToString(), new { reason = user is null ? "unknown_account" : "disabled" });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return (AdminSignInResult.InvalidCredentials, null);
        }
        var actor = Actor.Admin(user.Id, user.Email, ipHash);
        if (user.LockedUntil is { } until && until > now)
        {
            db.AddAudit(now, actor, "admin.login_locked", "admin_user", user.Id.ToString(), new { lockedUntil = until });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return (AdminSignInResult.LockedOut, user);
        }
        var verification = Hasher.VerifyHashedPassword(user, user.PasswordHash, password ?? "");
        if (verification == PasswordVerificationResult.Failed)
        {
            user.FailedAttempts++;
            var locked = user.FailedAttempts >= MaxFailedAttempts;
            if (locked)
            {
                user.LockedUntil = now + LockoutDuration;
                user.FailedAttempts = 0;
                log.LogWarning("Admin account {AdminId} locked after {Attempts} failed sign-in attempts", user.Id, MaxFailedAttempts);
            }
            db.AddAudit(now, actor, locked ? "admin.locked_out" : "admin.login_failed", "admin_user", user.Id.ToString(), new { reason = "bad_password" });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return (locked ? AdminSignInResult.LockedOut : AdminSignInResult.InvalidCredentials, user);
        }
        if (verification == PasswordVerificationResult.SuccessRehashNeeded) user.PasswordHash = Hasher.HashPassword(user, password!);
        user.FailedAttempts = 0;
        user.LockedUntil = null;
        user.LastLoginAt = now;
        db.AddAudit(now, actor, "admin.login", "admin_user", user.Id.ToString(), null);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return (AdminSignInResult.Success, user);
    }

    /// <summary>Creates an admin user.</summary>
    public async Task<AdminUser> CreateAsync(string email, string displayName, string password, string role, Actor actor, CancellationToken ct)
    {
        if (!RequestValidation.IsEmail(email)) throw new LicensingOperationException("Enter a valid e-mail address.");
        if (!AdminRoles.All.Contains(role)) throw new LicensingOperationException("Unknown role.");
        CheckPassword(password);
        var normalized = RequestValidation.NormalizeEmail(email);
        if (await db.AdminUsers.AnyAsync(u => u.Email == normalized, ct).ConfigureAwait(false)) throw new LicensingOperationException("An account with this e-mail already exists.");
        var now = clock.GetDbNow();
        var user = new AdminUser
        {
            Id = Guid.NewGuid(),
            Email = normalized,
            DisplayName = RequestValidation.Truncate(displayName, 200) ?? normalized,
            Role = role,
            CreatedAt = now,
            SecurityStamp = NewStamp(),
        };
        user.PasswordHash = Hasher.HashPassword(user, password);
        db.AdminUsers.Add(user);
        db.AddAudit(now, actor, "admin_user.create", "admin_user", user.Id.ToString(), new { email = normalized, role });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return user;
    }

    /// <summary>Creates the first administrator when no admin account exists (bootstrap from configuration).</summary>
    public async Task<bool> BootstrapAsync(string? email, string? password, CancellationToken ct)
    {
        if (await db.AdminUsers.AnyAsync(ct).ConfigureAwait(false)) return false;
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
        {
            log.LogWarning("No admin account exists. Set Admin__BootstrapEmail and Admin__BootstrapPassword to create the first administrator.");
            return false;
        }
        await CreateAsync(email, "Administrator", password, AdminRoles.Administrator, Actor.System, ct).ConfigureAwait(false);
        log.LogInformation("Created the initial administrator account from configuration. Remove Admin__BootstrapPassword from the environment.");
        return true;
    }

    public async Task ChangePasswordAsync(Guid userId, string currentPassword, string newPassword, Actor actor, CancellationToken ct)
    {
        var user = await LoadAsync(userId, ct).ConfigureAwait(false);
        if (Hasher.VerifyHashedPassword(user, user.PasswordHash, currentPassword) == PasswordVerificationResult.Failed)
            throw new LicensingOperationException("The current password is not correct.");
        CheckPassword(newPassword);
        user.PasswordHash = Hasher.HashPassword(user, newPassword);
        user.SecurityStamp = NewStamp();
        db.AddAudit(clock.GetDbNow(), actor, "admin_user.change_password", "admin_user", user.Id.ToString(), null);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Sets a new password for another user (administrators only).</summary>
    public async Task ResetPasswordAsync(Guid userId, string newPassword, Actor actor, CancellationToken ct)
    {
        CheckPassword(newPassword);
        var user = await LoadAsync(userId, ct).ConfigureAwait(false);
        user.PasswordHash = Hasher.HashPassword(user, newPassword);
        user.SecurityStamp = NewStamp();
        user.FailedAttempts = 0;
        user.LockedUntil = null;
        db.AddAudit(clock.GetDbNow(), actor, "admin_user.reset_password", "admin_user", user.Id.ToString(), null);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task UnlockAsync(Guid userId, Actor actor, CancellationToken ct)
    {
        var user = await LoadAsync(userId, ct).ConfigureAwait(false);
        user.FailedAttempts = 0;
        user.LockedUntil = null;
        db.AddAudit(clock.GetDbNow(), actor, "admin_user.unlock", "admin_user", user.Id.ToString(), null);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task SetDisabledAsync(Guid userId, bool disabled, Actor actor, CancellationToken ct)
    {
        if (actor.AdminUserId == userId) throw new LicensingOperationException("You cannot disable your own account.");
        var user = await LoadAsync(userId, ct).ConfigureAwait(false);
        user.Disabled = disabled;
        user.SecurityStamp = NewStamp();
        db.AddAudit(clock.GetDbNow(), actor, disabled ? "admin_user.disable" : "admin_user.enable", "admin_user", user.Id.ToString(), null);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task SetRoleAsync(Guid userId, string role, Actor actor, CancellationToken ct)
    {
        if (!AdminRoles.All.Contains(role)) throw new LicensingOperationException("Unknown role.");
        if (actor.AdminUserId == userId) throw new LicensingOperationException("You cannot change your own role.");
        var user = await LoadAsync(userId, ct).ConfigureAwait(false);
        var old = user.Role;
        user.Role = role;
        user.SecurityStamp = NewStamp();
        db.AddAudit(clock.GetDbNow(), actor, "admin_user.set_role", "admin_user", user.Id.ToString(), new { from = old, to = role });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static void CheckPassword(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
            throw new LicensingOperationException($"Passwords must have at least {MinPasswordLength} characters.");
        if (password.Distinct().Count() < 5) throw new LicensingOperationException("The password is too simple.");
    }

    private static string NewStamp() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    private async Task<AdminUser> LoadAsync(Guid id, CancellationToken ct)
        => await db.AdminUsers.SingleOrDefaultAsync(u => u.Id == id, ct).ConfigureAwait(false) ?? throw new LicensingOperationException("The account does not exist.");
}
