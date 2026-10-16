using System.Text;
using System.Text.Json;
using RefractionStudio.Server.Data.Payments;

namespace RefractionStudio.Server.Tests;

public sealed class StripeWebhookTests(ServerFixture fx)
{
    private static string CheckoutCompleted(string eventId, string email, string sessionId, int seats = 2, string paymentStatus = "paid", string type = "checkout.session.completed")
        => JsonSerializer.Serialize(new
        {
            id = eventId,
            @object = "event",
            type,
            data = new
            {
                @object = new
                {
                    id = sessionId,
                    @object = "checkout.session",
                    amount_total = 149_900,
                    currency = "usd",
                    customer = "cus_" + Guid.NewGuid().ToString("N")[..14],
                    customer_details = new { email, name = "Pat Geophysicist" },
                    payment_intent = "pi_" + sessionId,
                    payment_status = paymentStatus,
                    metadata = new { product = TestData.Product, edition = "Professional", seats = seats.ToString(System.Globalization.CultureInfo.InvariantCulture), company = "Survey Partners LLC" },
                },
            },
        });

    private async Task<HttpResponseMessage> PostAsync(string payload, string? signature)
    {
        using var http = fx.Server.CreateHttpsClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/webhooks/stripe") { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        if (signature is not null) request.Headers.TryAddWithoutValidation("Stripe-Signature", signature);
        return await http.SendAsync(request);
    }

    [Fact]
    public async Task Valid_checkout_creates_customer_licence_payment_and_emails()
    {
        var email = $"buyer-{Guid.NewGuid():N}@example.com";
        var session = "cs_test_" + Guid.NewGuid().ToString("N");
        var payload = CheckoutCompleted("evt_" + Guid.NewGuid().ToString("N"), email, session, seats: 3);

        using var response = await PostAsync(payload, StripeSignature.Sign(payload, fx.StripeSecret, DateTimeOffset.UtcNow));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("processed", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await using var db = fx.CreateDbContext();
        var customer = await db.Customers.SingleAsync(c => c.Email == email);
        Assert.Equal("Survey Partners LLC", customer.Company);
        var license = await db.Licenses.SingleAsync(l => l.CustomerId == customer.Id);
        Assert.Equal(3, license.SeatCount);
        Assert.Equal(LicenseRecordStatus.Active, license.Status);
        Assert.Null(license.ExpiresAt);
        Assert.NotNull(license.MaintenanceUntil);
        var payment = await db.Payments.SingleAsync(p => p.CustomerId == customer.Id);
        Assert.Equal(("stripe", "pi_" + session, 149_900L, "usd", "paid", (Guid?)license.Id),
            (payment.Provider, payment.ProviderPaymentId, payment.Amount, payment.Currency, payment.Status, payment.LicenseId));
        var templates = await db.EmailOutbox.Where(m => m.CustomerId == customer.Id).Select(m => m.Template).OrderBy(t => t).ToListAsync();
        Assert.Equal(["license_key", "purchase_confirmation"], templates);
    }

    [Fact]
    public async Task Invalid_or_missing_signature_is_rejected_and_nothing_is_created()
    {
        var email = $"forger-{Guid.NewGuid():N}@example.com";
        var payload = CheckoutCompleted("evt_" + Guid.NewGuid().ToString("N"), email, "cs_" + Guid.NewGuid().ToString("N"));

        using var wrongSecret = await PostAsync(payload, StripeSignature.Sign(payload, "whsec_wrong", DateTimeOffset.UtcNow));
        using var missing = await PostAsync(payload, null);
        using var tampered = await PostAsync(payload.Replace("149900", "1", StringComparison.Ordinal), StripeSignature.Sign(payload, fx.StripeSecret, DateTimeOffset.UtcNow));
        using var stale = await PostAsync(payload, StripeSignature.Sign(payload, fx.StripeSecret, DateTimeOffset.UtcNow.AddMinutes(-6)));

        Assert.All(new[] { wrongSecret, missing, tampered, stale }, r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        await using var db = fx.CreateDbContext();
        Assert.False(await db.Customers.AnyAsync(c => c.Email == email));
    }

    [Fact]
    public async Task Replayed_event_is_processed_only_once()
    {
        var email = $"replay-{Guid.NewGuid():N}@example.com";
        var eventId = "evt_" + Guid.NewGuid().ToString("N");
        var payload = CheckoutCompleted(eventId, email, "cs_" + Guid.NewGuid().ToString("N"));

        using var first = await PostAsync(payload, StripeSignature.Sign(payload, fx.StripeSecret, DateTimeOffset.UtcNow));
        using var replay = await PostAsync(payload, StripeSignature.Sign(payload, fx.StripeSecret, DateTimeOffset.UtcNow));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Contains("duplicate", await replay.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await using var db = fx.CreateDbContext();
        var customer = await db.Customers.SingleAsync(c => c.Email == email);
        Assert.Equal(1, await db.Licenses.CountAsync(l => l.CustomerId == customer.Id));
        Assert.Equal(1, await db.WebhookEvents.CountAsync(w => w.EventId == eventId));
    }

    [Fact]
    public async Task Delayed_payment_is_fulfilled_when_it_succeeds()
    {
        var email = $"delayed-{Guid.NewGuid():N}@example.com";
        var session = "cs_" + Guid.NewGuid().ToString("N");
        var completed = CheckoutCompleted("evt_" + Guid.NewGuid().ToString("N"), email, session, paymentStatus: "unpaid");
        var succeeded = CheckoutCompleted("evt_" + Guid.NewGuid().ToString("N"), email, session, type: "checkout.session.async_payment_succeeded");

        using var r1 = await PostAsync(completed, StripeSignature.Sign(completed, fx.StripeSecret, DateTimeOffset.UtcNow));
        await using (var db = fx.CreateDbContext())
        {
            var customer = await db.Customers.SingleAsync(c => c.Email == email);
            Assert.False(await db.Licenses.AnyAsync(l => l.CustomerId == customer.Id));
            Assert.Equal("pending", (await db.Payments.SingleAsync(p => p.CustomerId == customer.Id)).Status);
        }
        using var r2 = await PostAsync(succeeded, StripeSignature.Sign(succeeded, fx.StripeSecret, DateTimeOffset.UtcNow));

        await using var db2 = fx.CreateDbContext();
        var c2 = await db2.Customers.SingleAsync(c => c.Email == email);
        Assert.Equal(1, await db2.Licenses.CountAsync(l => l.CustomerId == c2.Id));
        Assert.Equal("paid", (await db2.Payments.SingleAsync(p => p.CustomerId == c2.Id)).Status);
    }

    [Fact]
    public void Signature_verification_accepts_any_matching_v1_and_enforces_tolerance()
    {
        const string secret = "whsec_test";
        const string payload = "{\"id\":\"evt_1\"}";
        var now = DateTimeOffset.UtcNow;
        var header = StripeSignature.Sign(payload, secret, now);
        var rotated = header.Replace("v1=", "v1=" + new string('0', 64) + ",v1=", StringComparison.Ordinal);

        Assert.True(StripeSignature.Verify(payload, header, secret, now, TimeSpan.FromMinutes(5)));
        Assert.True(StripeSignature.Verify(payload, rotated, secret, now, TimeSpan.FromMinutes(5)));
        Assert.False(StripeSignature.Verify(payload, header, secret, now.AddMinutes(6), TimeSpan.FromMinutes(5)));
        Assert.False(StripeSignature.Verify(payload + " ", header, secret, now, TimeSpan.FromMinutes(5)));
        Assert.False(StripeSignature.Verify(payload, "garbage", secret, now, TimeSpan.FromMinutes(5)));
    }
}
