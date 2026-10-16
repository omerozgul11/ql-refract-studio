using System.Security.Cryptography;
using System.Text;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using RefractionStudio.Server.Data.Entities;
using RefractionStudio.Server.Data.Services;

namespace RefractionStudio.Server.Data.Email;

/// <summary>
/// Encrypts the bodies of sensitive outbox messages (those containing a licence key) with AES-GCM under a
/// key derived from the server pepper, so a database dump does not reveal keys waiting to be sent.
/// </summary>
public sealed class OutboxProtector(ServerSecrets secrets)
{
    private const string Prefix = "enc:v1:";
    private readonly Lazy<byte[]> _key = new(() => secrets.DeriveKey("email-outbox"));

    public string Protect(string plain)
    {
        var data = Encoding.UTF8.GetBytes(plain);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[data.Length];
        using (var aes = new AesGcm(_key.Value, 16)) aes.Encrypt(nonce, data, cipher, tag);
        return Prefix + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    public string Unprotect(string stored)
    {
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored;
        var blob = Convert.FromBase64String(stored[Prefix.Length..]);
        var plain = new byte[blob.Length - 28];
        using var aes = new AesGcm(_key.Value, 16);
        aes.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(28), blob.AsSpan(12, 16), plain);
        return Encoding.UTF8.GetString(plain);
    }
}

/// <summary>Queues e-mails in the transactional outbox; they are sent by the background sender.</summary>
public sealed class EmailOutbox(LicensingDbContext db, OutboxProtector protector, TimeProvider clock)
{
    /// <summary>Adds a message to the outbox (saved with the caller's next SaveChanges).</summary>
    public EmailOutboxMessage Enqueue(string toAddress, string? toName, EmailContent content, Guid? licenseId = null, Guid? customerId = null)
    {
        var now = clock.GetDbNow();
        var message = new EmailOutboxMessage
        {
            Id = Guid.NewGuid(),
            ToAddress = toAddress,
            ToName = toName,
            Subject = content.Subject,
            TextBody = content.Sensitive ? protector.Protect(content.Text) : content.Text,
            HtmlBody = content.Sensitive ? protector.Protect(content.Html) : content.Html,
            Template = content.Template,
            Sensitive = content.Sensitive,
            Status = EmailStatus.Pending,
            CreatedAt = now,
            NextAttemptAt = now,
            LicenseId = licenseId,
            CustomerId = customerId,
        };
        db.EmailOutbox.Add(message);
        return message;
    }
}

/// <summary>SMTP settings (section "Smtp"). The password comes from the environment / secret store.</summary>
public sealed class SmtpOptions
{
    public const string Section = "Smtp";

    public string? Host { get; set; }

    public int Port { get; set; } = 587;

    /// <summary>"StartTls" (default), "SslOnConnect", "Auto" or "None" (local relays only).</summary>
    public string Security { get; set; } = "StartTls";

    public string? Username { get; set; }

    public string? Password { get; set; }

    public string FromAddress { get; set; } = "licensing@quakelogic.net";

    public string FromName { get; set; } = "QuakeLogic Refraction Studio";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);
}

/// <summary>A message ready to be sent.</summary>
public sealed record OutgoingEmail(string ToAddress, string? ToName, string Subject, string Text, string Html);

/// <summary>Delivers e-mail.</summary>
public interface IEmailTransport
{
    /// <summary>False when no delivery mechanism is configured.</summary>
    bool IsConfigured { get; }

    Task SendAsync(OutgoingEmail email, CancellationToken ct);
}

/// <summary>MailKit SMTP transport.</summary>
public sealed class SmtpEmailTransport(IOptions<SmtpOptions> options) : IEmailTransport
{
    public bool IsConfigured => options.Value.IsConfigured;

    public async Task SendAsync(OutgoingEmail email, CancellationToken ct)
    {
        var o = options.Value;
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(o.FromName, o.FromAddress));
        message.To.Add(new MailboxAddress(email.ToName ?? "", email.ToAddress));
        message.Subject = email.Subject;
        message.Body = new BodyBuilder { TextBody = email.Text, HtmlBody = email.Html }.ToMessageBody();
        var security = o.Security.ToUpperInvariant() switch
        {
            "SSLONCONNECT" => SecureSocketOptions.SslOnConnect,
            "AUTO" => SecureSocketOptions.Auto,
            "NONE" => SecureSocketOptions.None,
            _ => SecureSocketOptions.StartTls,
        };
        using var client = new SmtpClient { Timeout = 30_000 };
        await client.ConnectAsync(o.Host ?? throw new InvalidOperationException("Smtp:Host is not configured."), o.Port, security, ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(o.Username)) await client.AuthenticateAsync(o.Username, o.Password ?? "", ct).ConfigureAwait(false);
        await client.SendAsync(message, ct).ConfigureAwait(false);
        await client.DisconnectAsync(true, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Sends due outbox messages. Rows are claimed with <c>FOR UPDATE SKIP LOCKED</c>, so several server
/// instances can run the sender concurrently. Failures are retried with exponential back-off; bodies of
/// sensitive messages are erased once the message reaches a final state.
/// </summary>
public sealed class EmailDispatcher(LicensingDbContext db, IEmailTransport transport, OutboxProtector protector, TimeProvider clock,
    IHostEnvironment environment, ILogger<EmailDispatcher> log)
{
    public const int MaxAttempts = 8;
    private const string Erased = "[erased after delivery]";

    /// <summary>Processes up to <paramref name="batchSize"/> due messages; returns how many were handled.</summary>
    public async Task<int> ProcessBatchAsync(int batchSize, CancellationToken ct)
    {
        if (!transport.IsConfigured && !environment.IsDevelopment()) return 0;
        var now = clock.GetDbNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var due = await db.EmailOutbox
            .FromSqlInterpolated($"SELECT * FROM email_outbox WHERE status = 'pending' AND next_attempt_at <= {now} ORDER BY created_at LIMIT {batchSize} FOR UPDATE SKIP LOCKED")
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var m in due)
        {
            if (!transport.IsConfigured)
            {
                log.LogInformation("SMTP is not configured (Development): e-mail {Template} to {Recipient} marked as skipped", m.Template, MaskAddress(m.ToAddress));
                Finish(m, EmailStatus.Skipped, "SMTP not configured in Development.");
                continue;
            }
            try
            {
                await transport.SendAsync(new OutgoingEmail(m.ToAddress, m.ToName, m.Subject, protector.Unprotect(m.TextBody), protector.Unprotect(m.HtmlBody)), ct).ConfigureAwait(false);
                m.Attempts++;
                m.SentAt = clock.GetDbNow();
                Finish(m, EmailStatus.Sent, null);
                log.LogInformation("Sent e-mail {Template} ({MessageId}) to {Recipient}", m.Template, m.Id, MaskAddress(m.ToAddress));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                m.Attempts++;
                var error = ex.GetType().Name + ": " + ex.Message;
                if (m.Attempts >= MaxAttempts)
                {
                    Finish(m, EmailStatus.Failed, error);
                    log.LogError(ex, "Giving up on e-mail {Template} ({MessageId}) after {Attempts} attempts", m.Template, m.Id, m.Attempts);
                }
                else
                {
                    m.LastError = Truncate(error);
                    m.NextAttemptAt = clock.GetDbNow().AddMinutes(Math.Pow(2, m.Attempts));
                    log.LogWarning(ex, "E-mail {Template} ({MessageId}) failed (attempt {Attempts}); retrying later", m.Template, m.Id, m.Attempts);
                }
            }
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return due.Count;
    }

    private static void Finish(EmailOutboxMessage m, EmailStatus status, string? error)
    {
        m.Status = status;
        m.LastError = error is null ? null : Truncate(error);
        if (m.Sensitive)
        {
            m.TextBody = Erased;
            m.HtmlBody = Erased;
        }
    }

    private static string Truncate(string s) => s.Length <= 2000 ? s : s[..2000];

    /// <summary>"jane.doe@example.com" → "j***@example.com" (logs only).</summary>
    public static string MaskAddress(string address)
    {
        var at = address.IndexOf('@', StringComparison.Ordinal);
        return at <= 0 ? "***" : address[0] + "***" + address[at..];
    }
}
