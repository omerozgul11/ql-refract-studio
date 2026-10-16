using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RefractionStudio.AdminPortal.Infrastructure;
using RefractionStudio.Server.Data;
using RefractionStudio.Server.Data.Entities;

namespace RefractionStudio.AdminPortal.Pages.Licenses;

public sealed class IndexModel(LicensingDbContext db) : AdminPageModel
{
    public sealed record Row(Guid Id, string KeyHint, string Product, string Edition, LicenseRecordStatus Status, int Seats, int InUse,
        DateTimeOffset? ExpiresAt, DateTimeOffset? MaintenanceUntil, string CustomerEmail, string CustomerName, DateTimeOffset CreatedAt);

    [BindProperty(SupportsGet = true, Name = "q")]
    public string? Query { get; set; }

    [BindProperty(SupportsGet = true, Name = "status")]
    public string? Status { get; set; }

    [BindProperty(SupportsGet = true, Name = "p")]
    public int PageNumber { get; set; } = 1;

    public IReadOnlyList<Row> Rows { get; private set; } = [];

    public Paging Paging { get; private set; } = Paging.Create(1, 0);

    public async Task OnGetAsync(CancellationToken ct)
    {
        var q = db.Licenses.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(Query))
        {
            var term = Query.Trim();
            var hint = term.Replace("…", "").Replace(".", "").Trim().ToUpperInvariant();
            var pattern = "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            var isGuid = Guid.TryParse(term, out var id);
            q = q.Where(l => (isGuid && l.Id == id) || l.KeyHint == hint || EF.Functions.ILike(l.Customer!.Email, pattern)
                             || EF.Functions.ILike(l.Customer!.Name, pattern) || (l.Customer!.Company != null && EF.Functions.ILike(l.Customer!.Company, pattern)));
        }
        if (Enum.TryParse<LicenseRecordStatus>(Status, true, out var status)) q = q.Where(l => l.Status == status);
        Paging = Paging.Create(PageNumber, await q.CountAsync(ct));
        Rows = await q.OrderByDescending(l => l.CreatedAt).Skip(Paging.Skip).Take(Paging.PageSize)
            .Select(l => new Row(l.Id, l.KeyHint, l.Product, l.Edition, l.Status, l.SeatCount, l.Activations.Count(a => a.DeactivatedAt == null),
                l.ExpiresAt, l.MaintenanceUntil, l.Customer!.Email, l.Customer!.Name, l.CreatedAt))
            .ToListAsync(ct);
    }
}
