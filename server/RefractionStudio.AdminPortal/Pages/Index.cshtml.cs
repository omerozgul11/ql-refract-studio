using Microsoft.EntityFrameworkCore;
using RefractionStudio.AdminPortal.Infrastructure;
using RefractionStudio.Server.Data;
using RefractionStudio.Server.Data.Entities;

namespace RefractionStudio.AdminPortal.Pages;

public sealed class IndexModel(LicensingDbContext db, TimeProvider clock) : AdminPageModel
{
    public int ActiveLicenses { get; private set; }
    public int SeatsInUse { get; private set; }
    public int ActiveTrials { get; private set; }
    public int TrialsExpiringSoon { get; private set; }
    public int ActivationsLast30Days { get; private set; }
    public int LicensesExpiringSoon { get; private set; }
    public int PendingEmails { get; private set; }
    public int FailedEmails { get; private set; }
    public IReadOnlyList<TrialDevice> ExpiringTrials { get; private set; } = [];
    public IReadOnlyList<LicenseEvent> RecentEvents { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var soon = now.AddDays(3);
        var monthAgo = now.AddDays(-30);
        ActiveLicenses = await db.Licenses.CountAsync(l => l.Status == LicenseRecordStatus.Active && (l.ExpiresAt == null || l.ExpiresAt > now), ct);
        SeatsInUse = await db.Activations.CountAsync(a => a.DeactivatedAt == null, ct);
        ActiveTrials = await db.TrialDevices.CountAsync(t => t.EndsAt > now, ct);
        TrialsExpiringSoon = await db.TrialDevices.CountAsync(t => t.EndsAt > now && t.EndsAt <= soon, ct);
        ActivationsLast30Days = await db.Activations.CountAsync(a => a.ActivatedAt >= monthAgo, ct);
        LicensesExpiringSoon = await db.Licenses.CountAsync(l => l.Status == LicenseRecordStatus.Active && l.ExpiresAt > now && l.ExpiresAt <= now.AddDays(30), ct);
        PendingEmails = await db.EmailOutbox.CountAsync(m => m.Status == EmailStatus.Pending, ct);
        FailedEmails = await db.EmailOutbox.CountAsync(m => m.Status == EmailStatus.Failed, ct);
        ExpiringTrials = await db.TrialDevices.AsNoTracking().Where(t => t.EndsAt > now && t.EndsAt <= soon).OrderBy(t => t.EndsAt).Take(10).ToListAsync(ct);
        RecentEvents = await db.LicenseEvents.AsNoTracking().OrderByDescending(e => e.Id).Take(25).ToListAsync(ct);
    }
}
