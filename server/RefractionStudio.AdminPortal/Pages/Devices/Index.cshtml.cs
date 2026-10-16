using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RefractionStudio.AdminPortal.Infrastructure;
using RefractionStudio.Server.Data;

namespace RefractionStudio.AdminPortal.Pages.Devices;

public sealed class IndexModel(LicensingDbContext db) : AdminPageModel
{
    public sealed record Row(Guid Id, string Product, string Fingerprint, string? MachineName, string? OsVersion, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, bool Blocked, int ActiveActivations);

    [BindProperty(SupportsGet = true, Name = "q")]
    public string? Query { get; set; }

    [BindProperty(SupportsGet = true, Name = "blocked")]
    public bool BlockedOnly { get; set; }

    [BindProperty(SupportsGet = true, Name = "p")]
    public int PageNumber { get; set; } = 1;

    public IReadOnlyList<Row> Rows { get; private set; } = [];

    public Paging Paging { get; private set; } = Paging.Create(1, 0);

    public async Task OnGetAsync(CancellationToken ct)
    {
        var q = db.Devices.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(Query))
        {
            var term = Query.Trim();
            var prefix = term.ToLowerInvariant() + "%";
            var pattern = "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            q = q.Where(d => EF.Functions.Like(d.Fingerprint, prefix) || (d.MachineName != null && EF.Functions.ILike(d.MachineName, pattern)));
        }
        if (BlockedOnly) q = q.Where(d => d.Blocked);
        Paging = Paging.Create(PageNumber, await q.CountAsync(ct));
        Rows = await q.OrderByDescending(d => d.LastSeen).Skip(Paging.Skip).Take(Paging.PageSize)
            .Select(d => new Row(d.Id, d.Product, d.Fingerprint, d.MachineName, d.OsVersion, d.FirstSeen, d.LastSeen, d.Blocked, d.Activations.Count(a => a.DeactivatedAt == null)))
            .ToListAsync(ct);
    }
}
