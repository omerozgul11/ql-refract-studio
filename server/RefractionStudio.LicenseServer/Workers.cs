using Microsoft.EntityFrameworkCore;
using RefractionStudio.Server.Data;
using RefractionStudio.Server.Data.Email;
using RefractionStudio.Server.Data.Services;

namespace RefractionStudio.LicenseServer;

/// <summary>
/// Sends queued e-mail through SMTP (MailKit). In Development without SMTP settings messages are logged and
/// marked as skipped; in other environments they stay queued until SMTP is configured.
/// </summary>
public sealed class EmailSenderService(IServiceScopeFactory scopes, IEmailTransport transport, IConfiguration configuration, IHostEnvironment environment,
    ILogger<EmailSenderService> log) : BackgroundService
{
    private const int BatchSize = 20;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Workers:EmailPollSeconds", 15), 1, 3600));
        if (!transport.IsConfigured && !environment.IsDevelopment())
            log.LogWarning("Smtp:Host is not configured: customer e-mails stay queued in email_outbox until SMTP is configured");
        while (!stoppingToken.IsCancellationRequested)
        {
            var handled = 0;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                handled = await scope.ServiceProvider.GetRequiredService<EmailDispatcher>().ProcessBatchAsync(BatchSize, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "The e-mail sender failed; retrying in {Interval}", interval);
            }
            if (handled < BatchSize)
            {
                try
                {
                    await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}

/// <summary>
/// Periodic housekeeping: marks licences past their expiry as expired and queues trial-expiration reminders.
/// A PostgreSQL advisory lock makes sure only one server instance runs it at a time.
/// </summary>
public sealed class MaintenanceService(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<MaintenanceService> log) : BackgroundService
{
    private const long LockId = 0x52_53_4C_49_43_4D_4E_54; // "RSLICMNT"

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Clamp(configuration.GetValue("Workers:MaintenanceMinutes", 60), 1, 24 * 60));
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "Licence housekeeping failed");
                }
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <summary>One housekeeping pass.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LicensingDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var locked = await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({LockId}) AS \"Value\"").SingleAsync(ct).ConfigureAwait(false);
        if (!locked) return;
        var expired = await scope.ServiceProvider.GetRequiredService<LicenseService>().ExpireDueAsync(ct).ConfigureAwait(false);
        var reminders = await scope.ServiceProvider.GetRequiredService<TrialService>().QueueExpirationRemindersAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        if (expired > 0 || reminders > 0) log.LogInformation("Housekeeping: {Expired} licence(s) expired, {Reminders} trial reminder(s) queued", expired, reminders);
    }
}
