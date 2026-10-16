using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RefractionStudio.AdminPortal.Infrastructure;
using RefractionStudio.Server.Data;

namespace RefractionStudio.AdminPortal.Pages.Customers;

public sealed class IndexModel(LicensingDbContext db) : AdminPageModel
{
    public sealed record Row(Guid Id, string Email, string Name, string? Company, DateTimeOffset CreatedAt, int Licenses);

    [BindProperty(SupportsGet = true, Name = "q")]
    public string? Query { get; set; }

    [BindProperty(SupportsGet = true, Name = "p")]
    public int PageNumber { get; set; } = 1;

    public IReadOnlyList<Row> Rows { get; private set; } = [];

    public Paging Paging { get; private set; } = Paging.Create(1, 0);

    public async Task OnGetAsync(CancellationToken ct)
    {
        var q = db.Customers.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(Query))
        {
            var pattern = "%" + Query.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            q = q.Where(c => EF.Functions.ILike(c.Email, pattern) || EF.Functions.ILike(c.Name, pattern) || (c.Company != null && EF.Functions.ILike(c.Company, pattern)));
        }
        Paging = Paging.Create(PageNumber, await q.CountAsync(ct));
        Rows = await q.OrderByDescending(c => c.CreatedAt).Skip(Paging.Skip).Take(Paging.PageSize)
            .Select(c => new Row(c.Id, c.Email, c.Name, c.Company, c.CreatedAt, c.Licenses.Count)).ToListAsync(ct);
    }
}
