using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RefractionStudio.AdminPortal.Infrastructure;
using RefractionStudio.Server.Data;
using RefractionStudio.Server.Data.Entities;
using RefractionStudio.Server.Data.Services;

namespace RefractionStudio.AdminPortal.Pages.Licenses;

public sealed class CreateModel(LicensingDbContext db, CustomerService customers, LicenseService licenses, TimeProvider clock) : AdminPageModel
{
    [BindProperty(SupportsGet = true)]
    public string Email { get; set; } = "";

    [BindProperty]
    public string CustomerName { get; set; } = "";

    [BindProperty]
    public string? Company { get; set; }

    [BindProperty]
    public string Product { get; set; } = LicensingDbContext.RefractionStudioProduct;

    [BindProperty]
    public string? Edition { get; set; }

    [BindProperty]
    public int Seats { get; set; } = 1;

    [BindProperty]
    public DateOnly? ExpiresOn { get; set; }

    [BindProperty]
    public DateOnly? MaintenanceUntil { get; set; }

    [BindProperty]
    public string? Features { get; set; }

    [BindProperty]
    public int MaxTransfers { get; set; } = 3;

    [BindProperty]
    public int OfflineDays { get; set; } = 30;

    [BindProperty]
    public string? Notes { get; set; }

    [BindProperty]
    public bool SendKeyEmail { get; set; } = true;

    public IReadOnlyList<LicenseProduct> Products { get; private set; } = [];

    /// <summary>The new key: shown once in the POST response and never again.</summary>
    public string? CreatedKey { get; private set; }

    public License? Created { get; private set; }

    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!CanWrite) return Forbid();
        MaintenanceUntil = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime.AddYears(1));
        if (!string.IsNullOrWhiteSpace(Email))
        {
            var normalized = RequestValidation.NormalizeEmail(Email);
            var existing = await db.Customers.AsNoTracking().SingleOrDefaultAsync(c => c.Email == normalized, ct);
            if (existing is not null)
            {
                CustomerName = existing.Name;
                Company = existing.Company;
            }
        }
        await LoadProductsAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!CanWrite) return Forbid();
        await LoadProductsAsync(ct);
        try
        {
            var actor = CurrentActor;
            var customer = await customers.FindOrCreateAsync(Email, CustomerName, Company, actor, ct);
            await db.SaveChangesAsync(ct);
            var features = Features?.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var created = await licenses.CreateAsync(new CreateLicenseCommand(customer.Id, Product, Edition, Seats, Fmt.EndOfDayUtc(ExpiresOn), Fmt.EndOfDayUtc(MaintenanceUntil),
                features is { Length: > 0 } ? features : null, Notes, MaxTransfers, OfflineDays), actor, SendKeyEmail, ct);
            Created = created.License;
            CreatedKey = created.Key;
            NoStore();
        }
        catch (LicensingOperationException ex)
        {
            Error = ex.Message;
        }
        return Page();
    }

    private async Task LoadProductsAsync(CancellationToken ct)
        => Products = await db.LicenseProducts.AsNoTracking().Where(p => p.Active).OrderBy(p => p.Name).ToListAsync(ct);
}
