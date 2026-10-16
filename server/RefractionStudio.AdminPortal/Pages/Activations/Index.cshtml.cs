using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RefractionStudio.AdminPortal.Infrastructure;
using RefractionStudio.Server.Data;
using RefractionStudio.Server.Data.Entities;

namespace RefractionStudio.AdminPortal.Pages.Activations;

public sealed class IndexModel(LicensingDbContext db) : AdminPageModel
{
    [BindProperty(SupportsGet = true, Name = "q")]
    public string? Query { get; set; }

    [BindProperty(SupportsGet = true, Name = "active")]
    public bool ActiveOnly { get; set; }

    [BindProperty(SupportsGet = true, Name = "p")]
    public int PageNumber { get; set; } = 1;

    public IReadOnlyList<Activation> Rows { get; private set; } = [];

    public Paging Paging { get; private set; } = Paging.Create(1, 0);

    public async Task OnGetAsync(CancellationToken ct)
    {
        var q = db.Activations.AsNoTracking().Include(a => a.License).ThenInclude(l => l!.Customer).Include(a => a.Device).AsQueryable();
        if (!string.IsNullOrWhiteSpace(Query))
        {
            var term = Query.Trim();
            var hint = term.Replace("…", "").Trim().ToUpperInvariant();
            var pattern = "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            q = q.Where(a => a.License!.KeyHint == hint || EF.Functions.ILike(a.License!.Customer!.Email, pattern)
                             || (a.Device!.MachineName != null && EF.Functions.ILike(a.Device!.MachineName, pattern)));
        }
        if (ActiveOnly) q = q.Where(a => a.DeactivatedAt == null);
        Paging = Paging.Create(PageNumber, await q.CountAsync(ct));
        Rows = await q.OrderByDescending(a => a.ActivatedAt).Skip(Paging.Skip).Take(Paging.PageSize).ToListAsync(ct);
    }
}
