using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RefractionStudio.AdminPortal.Infrastructure;
using RefractionStudio.Server.Data;
using RefractionStudio.Server.Data.Entities;
using RefractionStudio.Server.Data.Services;

namespace RefractionStudio.AdminPortal.Pages.Customers;

public sealed class DetailsModel(LicensingDbContext db, CustomerService customers) : AdminPageModel
{
    public Customer Customer { get; private set; } = null!;

    public IReadOnlyList<License> Licenses { get; private set; } = [];

    public IReadOnlyList<Payment> Payments { get; private set; } = [];

    public IReadOnlyList<EmailOutboxMessage> Emails { get; private set; } = [];

    [BindProperty]
    public string Name { get; set; } = "";

    [BindProperty]
    public string? Company { get; set; }

    [BindProperty]
    public string? Notes { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        var customer = await db.Customers.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct);
        if (customer is null) return NotFound();
        Customer = customer;
        Name = customer.Name;
        Company = customer.Company;
        Notes = customer.Notes;
        Licenses = await db.Licenses.AsNoTracking().Where(l => l.CustomerId == id).OrderByDescending(l => l.CreatedAt).ToListAsync(ct);
        Payments = await db.Payments.AsNoTracking().Where(p => p.CustomerId == id).OrderByDescending(p => p.CreatedAt).ToListAsync(ct);
        Emails = await db.EmailOutbox.AsNoTracking().Where(m => m.CustomerId == id).OrderByDescending(m => m.CreatedAt).Take(50).ToListAsync(ct);
        return Page();
    }

    public Task<IActionResult> OnPostUpdateAsync(Guid id, CancellationToken ct)
        => ActAsync(() => customers.UpdateAsync(id, Name, Company, Notes, CurrentActor, ct), "Customer details saved.", new { id });
}
