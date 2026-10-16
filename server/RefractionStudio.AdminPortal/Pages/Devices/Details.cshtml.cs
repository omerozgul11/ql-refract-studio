using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RefractionStudio.AdminPortal.Infrastructure;
using RefractionStudio.Server.Data;
using RefractionStudio.Server.Data.Entities;
using RefractionStudio.Server.Data.Services;

namespace RefractionStudio.AdminPortal.Pages.Devices;

public sealed class DetailsModel(LicensingDbContext db, DeviceService devices) : AdminPageModel
{
    public Device Device { get; private set; } = null!;

    public IReadOnlyList<Activation> Activations { get; private set; } = [];

    public IReadOnlyList<LicenseEvent> Events { get; private set; } = [];

    [BindProperty]
    public string? Reason { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct);
        if (device is null) return NotFound();
        Device = device;
        Activations = await db.Activations.AsNoTracking().Include(a => a.License).Where(a => a.DeviceId == id).OrderByDescending(a => a.ActivatedAt).ToListAsync(ct);
        Events = await db.LicenseEvents.AsNoTracking().Where(e => e.DeviceId == id).OrderByDescending(e => e.Id).Take(100).ToListAsync(ct);
        return Page();
    }

    public Task<IActionResult> OnPostBlockAsync(Guid id, CancellationToken ct)
        => ActAsync(() => devices.BlockAsync(id, Reason, CurrentActor, ct), "The device has been blocked.", new { id });

    public Task<IActionResult> OnPostUnblockAsync(Guid id, CancellationToken ct)
        => ActAsync(() => devices.UnblockAsync(id, CurrentActor, ct), "The device has been unblocked.", new { id });
}
