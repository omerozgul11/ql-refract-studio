using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RefractionStudio.AdminPortal.Infrastructure;
using RefractionStudio.Server.Data;
using RefractionStudio.Server.Data.Entities;
using RefractionStudio.Server.Data.Services;

namespace RefractionStudio.AdminPortal.Pages.Trials;

public sealed class IndexModel(LicensingDbContext db, TrialService trials, TimeProvider clock) : AdminPageModel
{
    [BindProperty(SupportsGet = true, Name = "q")]
    public string? Query { get; set; }

    /// <summary>"active", "expiring", "expired" or empty for all.</summary>
    [BindProperty(SupportsGet = true, Name = "state")]
    public string? State { get; set; }

    [BindProperty(SupportsGet = true, Name = "p")]
    public int PageNumber { get; set; } = 1;

    [BindProperty]
    public int Days { get; set; } = 7;

    [BindProperty]
    public string? Email { get; set; }

    public DateTimeOffset Now { get; private set; }

    public IReadOnlyList<TrialDevice> Rows { get; private set; } = [];

    public Paging Paging { get; private set; } = Paging.Create(1, 0);

    public async Task OnGetAsync(CancellationToken ct)
    {
        Now = clock.GetUtcNow();
        var now = Now;
        var soon = now.AddDays(3);
        var q = db.TrialDevices.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(Query))
        {
            var term = Query.Trim();
            var prefix = term.ToLowerInvariant() + "%";
            var pattern = "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            q = q.Where(t => EF.Functions.Like(t.Fingerprint, prefix) || (t.MachineName != null && EF.Functions.ILike(t.MachineName, pattern))
                             || (t.Email != null && EF.Functions.ILike(t.Email, pattern)));
        }
        q = State switch
        {
            "active" => q.Where(t => t.EndsAt > now),
            "expiring" => q.Where(t => t.EndsAt > now && t.EndsAt <= soon),
            "expired" => q.Where(t => t.EndsAt <= now),
            _ => q,
        };
        Paging = Paging.Create(PageNumber, await q.CountAsync(ct));
        Rows = await q.OrderByDescending(t => t.LastSeen).Skip(Paging.Skip).Take(Paging.PageSize).ToListAsync(ct);
    }

    public Task<IActionResult> OnPostExtendAsync(Guid trialId, CancellationToken ct)
        => ActAsync(() => trials.ExtendAsync(trialId, Days, CurrentActor, ct), $"The trial has been extended by {Days} day(s). The computer receives the new end date at its next online check.",
            new { q = Query, state = State });

    public Task<IActionResult> OnPostEmailAsync(Guid trialId, CancellationToken ct)
        => ActAsync(() => trials.SetEmailAsync(trialId, Email, CurrentActor, ct), "Contact e-mail saved.", new { q = Query, state = State });
}
