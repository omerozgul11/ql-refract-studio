using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RefractionStudio.AdminPortal.Infrastructure;
using RefractionStudio.Server.Data;
using RefractionStudio.Server.Data.Entities;

namespace RefractionStudio.AdminPortal.Pages.Audit;

public sealed class IndexModel(LicensingDbContext db) : AdminPageModel
{
    [BindProperty(SupportsGet = true, Name = "action")]
    public string? ActionFilter { get; set; }

    [BindProperty(SupportsGet = true, Name = "actor")]
    public string? ActorFilter { get; set; }

    [BindProperty(SupportsGet = true, Name = "p")]
    public int PageNumber { get; set; } = 1;

    public IReadOnlyList<AuditLogEntry> Rows { get; private set; } = [];

    public Paging Paging { get; private set; } = Paging.Create(1, 0);

    public async Task OnGetAsync(CancellationToken ct)
    {
        var q = db.AuditLog.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(ActionFilter)) q = q.Where(a => a.Action.StartsWith(ActionFilter.Trim()));
        if (!string.IsNullOrWhiteSpace(ActorFilter))
        {
            var pattern = "%" + ActorFilter.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            q = q.Where(a => EF.Functions.ILike(a.Actor, pattern));
        }
        Paging = Paging.Create(PageNumber, await q.CountAsync(ct));
        Rows = await q.OrderByDescending(a => a.Id).Skip(Paging.Skip).Take(Paging.PageSize).ToListAsync(ct);
    }

    public static string? Link(AuditLogEntry e) => e.EntityType switch
    {
        "license" => "/Licenses/Details/" + e.EntityId,
        "customer" => "/Customers/Details/" + e.EntityId,
        "device" => "/Devices/Details/" + e.EntityId,
        _ => null,
    };
}
