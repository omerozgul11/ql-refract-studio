using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RefractionStudio.AdminPortal.Infrastructure;
using RefractionStudio.Server.Data;
using RefractionStudio.Server.Data.Entities;
using RefractionStudio.Server.Data.Services;

namespace RefractionStudio.AdminPortal.Pages.Releases;

public sealed class CreateModel(LicensingDbContext db, ReleaseService releases, TimeProvider clock) : AdminPageModel
{
    [BindProperty] public string Product { get; set; } = LicensingDbContext.RefractionStudioProduct;
    [BindProperty] public string Version { get; set; } = "";
    [BindProperty] public string Channel { get; set; } = "stable";
    [BindProperty] public DateOnly ReleasedOn { get; set; }
    [BindProperty] public string DownloadUrl { get; set; } = "";
    [BindProperty] public string Sha256 { get; set; } = "";
    [BindProperty] public long Size { get; set; }
    [BindProperty] public string ReleaseNotes { get; set; } = "";
    [BindProperty] public bool Major { get; set; }
    [BindProperty] public string? MinimumVersion { get; set; }
    [BindProperty] public bool Publish { get; set; }

    public IReadOnlyList<LicenseProduct> Products { get; private set; } = [];

    public string? Error { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        ReleasedOn = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        Products = await db.LicenseProducts.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        try
        {
            var releasedAt = new DateTimeOffset(ReleasedOn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
            await releases.CreateAsync(new CreateReleaseCommand(Product, Version, Channel, releasedAt, DownloadUrl, Sha256, Size, ReleaseNotes, Major, MinimumVersion, Publish), CurrentActor, ct);
            StatusMessage = $"Release {Version} saved{(Publish ? " and published" : " as a draft")}.";
            return RedirectToPage("Index");
        }
        catch (LicensingOperationException ex)
        {
            Error = ex.Message;
            Products = await db.LicenseProducts.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct);
            return Page();
        }
    }
}
