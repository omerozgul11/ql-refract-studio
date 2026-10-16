using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RefractionStudio.Server.Data.Email;
using RefractionStudio.Server.Data.Entities;
using RefractionStudio.Server.Data.Services;

namespace RefractionStudio.Server.Data.Payments;

/// <summary>Result of processing a webhook delivery.</summary>
public enum WebhookOutcome
{
    /// <summary>The event was handled now.</summary>
    Processed,

    /// <summary>The event was already handled earlier (replayed delivery); nothing was done.</summary>
    Duplicate,

    /// <summary>The event type is not relevant; it was recorded and acknowledged.</summary>
    Ignored,

    /// <summary>The event could not be fulfilled (e.g. missing data); it was recorded for follow-up and acknowledged.</summary>
    Rejected,
}

/// <summary>A payment provider integration (webhook verification and fulfilment).</summary>
public interface IPaymentProvider
{
    /// <summary>Provider name stored with payments, e.g. "stripe".</summary>
    string Name { get; }

    /// <summary>Verifies the authenticity and freshness of a webhook delivery.</summary>
    bool VerifySignature(string payload, string? signatureHeader);

    /// <summary>Processes a verified webhook payload idempotently.</summary>
    Task<WebhookOutcome> HandleWebhookAsync(string payload, CancellationToken ct);
}

/// <summary>Stripe settings (section "Stripe").</summary>
public sealed class StripeOptions
{
    public const string Section = "Stripe";

    /// <summary>Webhook endpoint signing secret ("whsec_…").</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>Accepted age of a signed delivery.</summary>
    public int ToleranceSeconds { get; set; } = 300;

    /// <summary>Product used when the Checkout Session metadata does not name one.</summary>
    public string DefaultProduct { get; set; } = LicensingDbContext.RefractionStudioProduct;

    /// <summary>Maintenance (updates) included with a purchase when the metadata does not say otherwise.</summary>
    public int DefaultMaintenanceDays { get; set; } = 365;
}

/// <summary>
/// Verifies Stripe's <c>Stripe-Signature</c> header (HMAC-SHA-256 of "{t}.{payload}" with the endpoint secret,
/// constant-time comparison, 5-minute tolerance) without the Stripe SDK.
/// </summary>
public static class StripeSignature
{
    public static bool Verify(string payload, string? header, string secret, DateTimeOffset now, TimeSpan tolerance)
    {
        if (string.IsNullOrWhiteSpace(header) || string.IsNullOrEmpty(secret)) return false;
        long? timestamp = null;
        var signatures = new List<byte[]>();
        foreach (var part in header.Split(','))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0) continue;
            var name = part[..eq].Trim();
            var value = part[(eq + 1)..].Trim();
            if (name == "t" && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var t)) timestamp = t;
            else if (name == "v1" && value.Length == 64)
            {
                try
                {
                    signatures.Add(Convert.FromHexString(value));
                }
                catch (FormatException)
                {
                    // A malformed signature simply does not match.
                }
            }
        }
        if (timestamp is not { } ts || signatures.Count == 0) return false;
        var signedAt = DateTimeOffset.FromUnixTimeSeconds(ts);
        if ((now - signedAt).Duration() > tolerance) return false;
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(ts.ToString(CultureInfo.InvariantCulture) + "." + payload));
        var match = false;
        foreach (var s in signatures) match |= CryptographicOperations.FixedTimeEquals(expected, s);
        return match;
    }

    /// <summary>Builds a header value (tests and local tooling).</summary>
    public static string Sign(string payload, string secret, DateTimeOffset at)
    {
        var ts = at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var sig = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(ts + "." + payload)));
        return $"t={ts},v1={sig}";
    }
}

/// <summary>
/// Stripe Checkout fulfilment. Handles <c>checkout.session.completed</c> (and the asynchronous-payment
/// follow-ups): finds or creates the customer by e-mail, creates the licence (edition and seats from the
/// session metadata), records the payment and queues the purchase confirmation and licence-key e-mails.
/// Every event id is processed at most once.
/// </summary>
public sealed class StripePaymentProvider(LicensingDbContext db, LicenseService licenses, CustomerService customers, EmailOutbox outbox, EmailTemplates templates,
    IOptions<StripeOptions> options, TimeProvider clock, ILogger<StripePaymentProvider> log) : IPaymentProvider
{
    public string Name => "stripe";

    public bool VerifySignature(string payload, string? signatureHeader)
    {
        var o = options.Value;
        if (string.IsNullOrEmpty(o.WebhookSecret))
        {
            log.LogError("Stripe:WebhookSecret is not configured; webhook deliveries are rejected");
            return false;
        }
        return StripeSignature.Verify(payload, signatureHeader, o.WebhookSecret, clock.GetDbNow(), TimeSpan.FromSeconds(o.ToleranceSeconds));
    }

    public async Task<WebhookOutcome> HandleWebhookAsync(string payload, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        var eventId = Str(root, "id") ?? throw new JsonException("The event has no id.");
        var type = Str(root, "type") ?? "";
        if (!root.TryGetProperty("data", out var data) || !data.TryGetProperty("object", out var obj)) throw new JsonException("The event has no data.object.");

        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var now = clock.GetDbNow();
        // Idempotency: the event row is inserted in the same transaction as the fulfilment.
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO webhook_events (provider, event_id, type, received_at, outcome) VALUES ({Name}, {eventId}, {type}, {now}, 'processing') ON CONFLICT DO NOTHING", ct).ConfigureAwait(false);
        if (inserted == 0)
        {
            log.LogInformation("Stripe event {EventId} ({Type}) was already processed", eventId, type);
            return WebhookOutcome.Duplicate;
        }

        var (outcome, details) = type switch
        {
            "checkout.session.completed" or "checkout.session.async_payment_succeeded" => await FulfilAsync(obj, ct).ConfigureAwait(false),
            "checkout.session.async_payment_failed" => await MarkFailedAsync(obj, ct).ConfigureAwait(false),
            _ => (WebhookOutcome.Ignored, null),
        };
        var record = await db.WebhookEvents.SingleAsync(w => w.Provider == Name && w.EventId == eventId, ct).ConfigureAwait(false);
        record.Outcome = outcome.ToString().ToLowerInvariant();
        record.Details = details is null ? null : RequestValidation.Truncate(details, 2000);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        if (outcome == WebhookOutcome.Rejected) log.LogError("Stripe event {EventId} ({Type}) could not be fulfilled: {Details}", eventId, type, details);
        else log.LogInformation("Stripe event {EventId} ({Type}): {Outcome}", eventId, type, outcome);
        return outcome;
    }

    private async Task<(WebhookOutcome, string?)> FulfilAsync(JsonElement session, CancellationToken ct)
    {
        var sessionId = Str(session, "id") ?? "";
        var paymentStatus = Str(session, "payment_status");
        var paymentId = Str(session, "payment_intent") ?? sessionId;
        if (paymentId.Length == 0) return (WebhookOutcome.Rejected, "The session has no id.");
        var amount = session.TryGetProperty("amount_total", out var at) && at.ValueKind == JsonValueKind.Number ? at.GetInt64() : 0;
        var currency = (Str(session, "currency") ?? "usd").ToLowerInvariant();
        var details = session.TryGetProperty("customer_details", out var cd) && cd.ValueKind == JsonValueKind.Object ? cd : default;
        var email = (details.ValueKind == JsonValueKind.Object ? Str(details, "email") : null) ?? Str(session, "customer_email");
        var name = details.ValueKind == JsonValueKind.Object ? Str(details, "name") : null;
        var metadata = session.TryGetProperty("metadata", out var md) && md.ValueKind == JsonValueKind.Object ? md : default;
        string? Meta(string key) => metadata.ValueKind == JsonValueKind.Object ? Str(metadata, key) : null;

        if (string.IsNullOrWhiteSpace(email) || !RequestValidation.IsEmail(email)) return (WebhookOutcome.Rejected, $"Session {sessionId} has no valid customer e-mail.");
        var o = options.Value;
        var productCode = Meta("product") ?? o.DefaultProduct;
        var product = await db.LicenseProducts.SingleOrDefaultAsync(p => p.Code == productCode, ct).ConfigureAwait(false);
        if (product is null) return (WebhookOutcome.Rejected, $"Session {sessionId} names unknown product “{productCode}”.");
        var seats = int.TryParse(Meta("seats"), NumberStyles.None, CultureInfo.InvariantCulture, out var s) && s is >= 1 and <= LicenseService.MaxSeats ? s : 1;
        var edition = Meta("edition") ?? product.DefaultEdition;
        var now = clock.GetDbNow();
        DateTimeOffset? expires = int.TryParse(Meta("license_term_days"), NumberStyles.None, CultureInfo.InvariantCulture, out var term) && term > 0 ? now.AddDays(term) : null;
        var maintenanceDays = int.TryParse(Meta("maintenance_days"), NumberStyles.None, CultureInfo.InvariantCulture, out var md2) && md2 > 0 ? md2 : o.DefaultMaintenanceDays;

        var customer = await customers.FindOrCreateAsync(email, name, Meta("company"), Actor.Stripe, ct).ConfigureAwait(false);
        if (Str(session, "customer") is { } stripeCustomer) customer.ProviderCustomerId ??= stripeCustomer;

        var payment = await db.Payments.SingleOrDefaultAsync(p => p.Provider == Name && p.ProviderPaymentId == paymentId, ct).ConfigureAwait(false);
        if (payment is null)
        {
            payment = new Payment
            {
                Id = Guid.NewGuid(),
                Provider = Name,
                ProviderPaymentId = paymentId,
                CustomerId = customer.Id,
                Amount = amount,
                Currency = currency,
                CreatedAt = now,
            };
            db.Payments.Add(payment);
        }
        var paid = paymentStatus is "paid" or "no_payment_required";
        if (!paid)
        {
            // Delayed payment methods: fulfilment happens on checkout.session.async_payment_succeeded.
            payment.Status = "pending";
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return (WebhookOutcome.Processed, $"Payment {paymentId} pending.");
        }
        payment.Status = "paid";
        if (payment.LicenseId is not null)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return (WebhookOutcome.Processed, $"Payment {paymentId} already fulfilled.");
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var created = await licenses.CreateAsync(new CreateLicenseCommand(customer.Id, product.Code, edition, seats, expires, now.AddDays(maintenanceDays),
            Notes: $"Stripe Checkout {sessionId}"), Actor.Stripe, sendKeyEmail: false, ct).ConfigureAwait(false);
        payment.LicenseId = created.License.Id;
        outbox.Enqueue(customer.Email, customer.Name,
            templates.PurchaseConfirmation(customer.Name, product.Name, created.License.Edition, seats, amount, currency, sessionId), created.License.Id, customer.Id);
        outbox.Enqueue(customer.Email, customer.Name,
            templates.LicenseKey(customer.Name, product.Name, created.Key, created.License.Edition, seats, created.License.ExpiresAt, created.License.MaintenanceUntil), created.License.Id, customer.Id);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return (WebhookOutcome.Processed, $"Licence {created.License.Id} (key …{created.License.KeyHint}) created for payment {paymentId}.");
    }

    private async Task<(WebhookOutcome, string?)> MarkFailedAsync(JsonElement session, CancellationToken ct)
    {
        var paymentId = Str(session, "payment_intent") ?? Str(session, "id") ?? "";
        var payment = await db.Payments.SingleOrDefaultAsync(p => p.Provider == Name && p.ProviderPaymentId == paymentId, ct).ConfigureAwait(false);
        if (payment is null) return (WebhookOutcome.Ignored, $"No payment {paymentId} recorded.");
        if (payment.LicenseId is null) payment.Status = "failed";
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return (WebhookOutcome.Processed, $"Payment {paymentId} failed.");
    }

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
}
