namespace RefractionStudio.Server.Data.Entities;

/// <summary>A customer (person or company) that owns licences.</summary>
public sealed class Customer
{
    public Guid Id { get; set; }

    /// <summary>Contact e-mail, stored lower-case; unique.</summary>
    public string Email { get; set; } = "";

    public string Name { get; set; } = "";

    public string? Company { get; set; }

    /// <summary>Customer id at the payment provider (e.g. Stripe "cus_…"), if known.</summary>
    public string? ProviderCustomerId { get; set; }

    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public List<License> Licenses { get; set; } = [];

    public List<Payment> Payments { get; set; } = [];

    /// <summary>Name printed in tokens and e-mails: the company when known, otherwise the person.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Company) ? Name : Company!;
}

/// <summary>A product that can be licensed (e.g. "refraction-studio").</summary>
public sealed class LicenseProduct
{
    /// <summary>Product code used in API requests and tokens.</summary>
    public string Code { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>Licence key prefix, e.g. "RFST".</summary>
    public string KeyPrefix { get; set; } = "RFST";

    /// <summary>Edition granted to trials and used when a purchase does not name one.</summary>
    public string DefaultEdition { get; set; } = "Professional";

    /// <summary>Feature codes granted to trials and to new licences by default.</summary>
    public List<string> DefaultFeatures { get; set; } = [];

    public int TrialDays { get; set; } = 14;

    public bool TrialsEnabled { get; set; } = true;

    public bool Active { get; set; } = true;
}

/// <summary>Lifecycle status of a licence.</summary>
public enum LicenseRecordStatus
{
    Active,
    Suspended,
    Revoked,
    Expired,
}

/// <summary>
/// A licence. The key itself is never stored: only its HMAC-SHA-256 (with the server pepper) and the
/// last four characters for display.
/// </summary>
public sealed class License
{
    public Guid Id { get; set; }

    /// <summary>Hex HMAC-SHA-256 of the canonical key (<see cref="RefractionStudio.Licensing.LicenseKeys.Hash"/>).</summary>
    public string LicenseKeyHash { get; set; } = "";

    /// <summary>Last four characters of the key.</summary>
    public string KeyHint { get; set; } = "";

    public Guid CustomerId { get; set; }

    public Customer? Customer { get; set; }

    public string Product { get; set; } = "";

    public string Edition { get; set; } = "";

    public LicenseRecordStatus Status { get; set; } = LicenseRecordStatus.Active;

    /// <summary>Reason given when the licence was suspended or revoked.</summary>
    public string? StatusReason { get; set; }

    public int SeatCount { get; set; } = 1;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>End of the entitlement; null = perpetual.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Updates are included until this time.</summary>
    public DateTimeOffset? MaintenanceUntil { get; set; }

    public List<string> Features { get; set; } = [];

    public string? Notes { get; set; }

    /// <summary>Self-service transfers (deactivations) allowed in any 30-day window.</summary>
    public int MaxTransfersPer30Days { get; set; } = 3;

    /// <summary>Days a token stays usable without contacting the server.</summary>
    public int OfflineDays { get; set; } = 30;

    public List<Activation> Activations { get; set; } = [];
}

/// <summary>A computer, identified by the salted fingerprint computed by the desktop application.</summary>
public sealed class Device
{
    public Guid Id { get; set; }

    public string Product { get; set; } = "";

    public string Fingerprint { get; set; } = "";

    public List<string> ComponentHashes { get; set; } = [];

    public string? MachineName { get; set; }

    public string? OsVersion { get; set; }

    public DateTimeOffset FirstSeen { get; set; }

    public DateTimeOffset LastSeen { get; set; }

    public bool Blocked { get; set; }

    public string? BlockedReason { get; set; }

    public List<Activation> Activations { get; set; } = [];
}

/// <summary>Why an activation ended.</summary>
public static class DeactivationReasons
{
    /// <summary>The user deactivated the computer from the application (counts as a transfer).</summary>
    public const string User = "user";

    /// <summary>An administrator reset the activations of the licence.</summary>
    public const string AdminReset = "admin_reset";

    /// <summary>An administrator deactivated a single computer.</summary>
    public const string Admin = "admin";
}

/// <summary>Use of one seat of a licence by one device.</summary>
public sealed class Activation
{
    public Guid Id { get; set; }

    public Guid LicenseId { get; set; }

    public License? License { get; set; }

    public Guid DeviceId { get; set; }

    public Device? Device { get; set; }

    public DateTimeOffset ActivatedAt { get; set; }

    /// <summary>Null while the activation holds a seat.</summary>
    public DateTimeOffset? DeactivatedAt { get; set; }

    public DateTimeOffset? LastValidatedAt { get; set; }

    public string? AppVersion { get; set; }

    /// <summary>One of <see cref="DeactivationReasons"/>.</summary>
    public string? DeactivationReason { get; set; }

    public bool IsActive => DeactivatedAt is null;
}

/// <summary>A trial of one product on one device. Trials are never reset by reinstalling.</summary>
public sealed class TrialDevice
{
    public Guid Id { get; set; }

    public string Product { get; set; } = "";

    /// <summary>Fingerprint of the computer that started the trial.</summary>
    public string Fingerprint { get; set; } = "";

    /// <summary>Every component hash seen for this trial (tolerant matching after hardware changes).</summary>
    public List<string> ComponentHashes { get; set; } = [];

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset EndsAt { get; set; }

    /// <summary>True once an administrator has extended the trial.</summary>
    public bool ExtendedByAdmin { get; set; }

    public DateTimeOffset LastSeen { get; set; }

    public string? MachineName { get; set; }

    public string? OsVersion { get; set; }

    public string? AppVersion { get; set; }

    /// <summary>Contact e-mail recorded by sales/support (optional); enables the expiration reminder.</summary>
    public string? Email { get; set; }

    /// <summary>When the "trial ends soon" reminder was queued.</summary>
    public DateTimeOffset? ReminderSentAt { get; set; }
}

/// <summary>Append-only history of what happened to licences, trials and devices.</summary>
public sealed class LicenseEvent
{
    public long Id { get; set; }

    public Guid? LicenseId { get; set; }

    public Guid? TrialId { get; set; }

    public Guid? DeviceId { get; set; }

    /// <summary>Event type, e.g. "license.activated" (see <see cref="Services.EventTypes"/>).</summary>
    public string Type { get; set; } = "";

    /// <summary>Who caused it: "api", "admin:{id}", "stripe", "system".</summary>
    public string Actor { get; set; } = "";

    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>Keyed hash of the client IP address (never the address itself).</summary>
    public string? IpHash { get; set; }

    /// <summary>JSON details.</summary>
    public string Details { get; set; } = "{}";
}

/// <summary>Append-only record of administrative and security-relevant actions.</summary>
public sealed class AuditLogEntry
{
    public long Id { get; set; }

    public Guid? AdminUserId { get; set; }

    /// <summary>Who acted: "admin:{email}", "stripe", "system".</summary>
    public string Actor { get; set; } = "";

    /// <summary>Action, e.g. "license.revoke".</summary>
    public string Action { get; set; } = "";

    public string? EntityType { get; set; }

    public string? EntityId { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public string? IpHash { get; set; }

    public string Details { get; set; } = "{}";
}

/// <summary>A software release in the update feed.</summary>
public sealed class SoftwareVersion
{
    public Guid Id { get; set; }

    public string Product { get; set; } = "";

    public string Version { get; set; } = "";

    /// <summary>"stable" or "beta".</summary>
    public string Channel { get; set; } = "stable";

    public DateTimeOffset ReleasedAt { get; set; }

    public string DownloadUrl { get; set; } = "";

    /// <summary>Lower-case hex SHA-256 of the installer.</summary>
    public string Sha256 { get; set; } = "";

    public long Size { get; set; }

    public string ReleaseNotes { get; set; } = "";

    public bool Major { get; set; }

    public string? MinimumVersion { get; set; }

    public bool Published { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A payment reported by the payment provider. Card data is never received or stored.</summary>
public sealed class Payment
{
    public Guid Id { get; set; }

    public string Provider { get; set; } = "";

    public string ProviderPaymentId { get; set; } = "";

    public Guid CustomerId { get; set; }

    public Customer? Customer { get; set; }

    public Guid? LicenseId { get; set; }

    /// <summary>Amount in the currency's minor unit (e.g. cents).</summary>
    public long Amount { get; set; }

    /// <summary>ISO 4217 code, lower-case.</summary>
    public string Currency { get; set; } = "";

    /// <summary>"paid", "pending", "refunded" or "failed".</summary>
    public string Status { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Admin portal roles.</summary>
public static class AdminRoles
{
    /// <summary>Full access, including user management.</summary>
    public const string Administrator = "administrator";

    /// <summary>Licence, trial and device operations; no user management or releases.</summary>
    public const string Support = "support";

    /// <summary>Read-only access.</summary>
    public const string Viewer = "viewer";

    public static readonly IReadOnlyList<string> All = [Administrator, Support, Viewer];
}

/// <summary>An admin portal user.</summary>
public sealed class AdminUser
{
    public Guid Id { get; set; }

    /// <summary>Login e-mail, stored lower-case; unique.</summary>
    public string Email { get; set; } = "";

    public string DisplayName { get; set; } = "";

    /// <summary>ASP.NET Core Identity PBKDF2 hash.</summary>
    public string PasswordHash { get; set; } = "";

    /// <summary>One of <see cref="AdminRoles"/>.</summary>
    public string Role { get; set; } = AdminRoles.Viewer;

    public int FailedAttempts { get; set; }

    public DateTimeOffset? LockedUntil { get; set; }

    public bool Disabled { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastLoginAt { get; set; }

    /// <summary>Changes whenever the password or role changes; invalidates existing sessions.</summary>
    public string SecurityStamp { get; set; } = "";
}

/// <summary>Delivery status of a queued e-mail.</summary>
public enum EmailStatus
{
    Pending,
    Sent,
    Failed,
    Skipped,
}

/// <summary>A queued e-mail (transactional outbox).</summary>
public sealed class EmailOutboxMessage
{
    public Guid Id { get; set; }

    public string ToAddress { get; set; } = "";

    public string? ToName { get; set; }

    public string Subject { get; set; } = "";

    /// <summary>Plain-text body (encrypted at rest when <see cref="Sensitive"/>).</summary>
    public string TextBody { get; set; } = "";

    /// <summary>HTML body (encrypted at rest when <see cref="Sensitive"/>).</summary>
    public string HtmlBody { get; set; } = "";

    /// <summary>Template name, e.g. "license_key".</summary>
    public string Template { get; set; } = "";

    /// <summary>True when the bodies contain a licence key: they are encrypted while queued and erased after delivery.</summary>
    public bool Sensitive { get; set; }

    public EmailStatus Status { get; set; } = EmailStatus.Pending;

    public int Attempts { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; }

    public DateTimeOffset? SentAt { get; set; }

    public string? LastError { get; set; }

    public Guid? LicenseId { get; set; }

    public Guid? CustomerId { get; set; }
}

/// <summary>A processed payment-provider webhook (idempotency record).</summary>
public sealed class WebhookEvent
{
    public string Provider { get; set; } = "";

    public string EventId { get; set; } = "";

    public string Type { get; set; } = "";

    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>"processed", "ignored" or "failed".</summary>
    public string Outcome { get; set; } = "";

    public string? Details { get; set; }
}
