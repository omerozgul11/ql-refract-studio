using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RefractionStudio.AdminPortal.Infrastructure;
using RefractionStudio.Server.Data;
using RefractionStudio.Server.Data.Entities;
using RefractionStudio.Server.Data.Services;

namespace RefractionStudio.AdminPortal.Pages.Licenses;

public sealed class DetailsModel(LicensingDbContext db, LicenseService licenses, TimeProvider clock) : AdminPageModel
{
    public License License { get; private set; } = null!;

    public IReadOnlyList<Activation> Activations { get; private set; } = [];

    public IReadOnlyList<LicenseEvent> Events { get; private set; } = [];

    public IReadOnlyList<Payment> Payments { get; private set; } = [];

    public int TransfersLast30Days { get; private set; }

    /// <summary>A replacement key, shown once right after "Replace key".</summary>
    public string? ReplacementKey { get; private set; }

    [BindProperty]
    public string? Reason { get; set; }

    [BindProperty]
    public DateOnly? NewExpiresOn { get; set; }

    [BindProperty]
    public bool Perpetual { get; set; }

    [BindProperty]
    public DateOnly? NewMaintenanceUntil { get; set; }

    [BindProperty]
    public bool NotifyCustomer { get; set; } = true;

    [BindProperty]
    public int Seats { get; set; }

    [BindProperty]
    public int MaxTransfers { get; set; }

    [BindProperty]
    public int OfflineDays { get; set; }

    [BindProperty]
    public string? Notes { get; set; }

    [BindProperty]
    public bool SendKeyEmail { get; set; } = true;

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
        => await LoadAsync(id, ct) ? Page() : NotFound();

    private async Task<bool> LoadAsync(Guid id, CancellationToken ct)
    {
        var license = await db.Licenses.AsNoTracking().Include(l => l.Customer).SingleOrDefaultAsync(l => l.Id == id, ct);
        if (license is null) return false;
        License = license;
        Seats = license.SeatCount;
        MaxTransfers = license.MaxTransfersPer30Days;
        OfflineDays = license.OfflineDays;
        Notes = license.Notes;
        Activations = await db.Activations.AsNoTracking().Include(a => a.Device).Where(a => a.LicenseId == id)
            .OrderBy(a => a.DeactivatedAt != null).ThenByDescending(a => a.ActivatedAt).ToListAsync(ct);
        Events = await db.LicenseEvents.AsNoTracking().Where(e => e.LicenseId == id).OrderByDescending(e => e.Id).Take(200).ToListAsync(ct);
        Payments = await db.Payments.AsNoTracking().Where(p => p.LicenseId == id).ToListAsync(ct);
        var since = clock.GetUtcNow() - ActivationService.TransferWindow;
        TransfersLast30Days = Activations.Count(a => a.DeactivatedAt > since && a.DeactivationReason == DeactivationReasons.User);
        return true;
    }

    public Task<IActionResult> OnPostRevokeAsync(Guid id, CancellationToken ct)
        => ActAsync(() => licenses.RevokeAsync(id, Reason, CurrentActor, ct), "The licence has been revoked. Activated computers lose it at their next online check.", new { id });

    public Task<IActionResult> OnPostSuspendAsync(Guid id, CancellationToken ct)
        => ActAsync(() => licenses.SuspendAsync(id, Reason, CurrentActor, ct), "The licence has been suspended.", new { id });

    public Task<IActionResult> OnPostReinstateAsync(Guid id, CancellationToken ct)
        => ActAsync(() => licenses.ReinstateAsync(id, Reason, CurrentActor, ct), "The licence is active again.", new { id });

    public Task<IActionResult> OnPostExtendAsync(Guid id, CancellationToken ct)
        => ActAsync(() => licenses.ExtendAsync(id, Fmt.EndOfDayUtc(NewExpiresOn), Perpetual, Fmt.EndOfDayUtc(NewMaintenanceUntil), CurrentActor, NotifyCustomer, ct),
            "The licence dates have been updated.", new { id });

    public Task<IActionResult> OnPostResetActivationsAsync(Guid id, CancellationToken ct)
        => ActAsync(() => licenses.ResetActivationsAsync(id, CurrentActor, ct), "All computers have been deactivated. Their seats are free.", new { id });

    public Task<IActionResult> OnPostDeactivateAsync(Guid id, Guid activationId, CancellationToken ct)
        => ActAsync(() => licenses.DeactivateActivationAsync(activationId, CurrentActor, ct), "The computer has been deactivated.", new { id });

    public Task<IActionResult> OnPostLimitsAsync(Guid id, CancellationToken ct)
        => ActAsync(() => licenses.SetLimitsAsync(id, Seats, MaxTransfers, OfflineDays, CurrentActor, ct), "Limits updated.", new { id });

    public Task<IActionResult> OnPostNotesAsync(Guid id, CancellationToken ct)
        => ActAsync(() => licenses.SetNotesAsync(id, Notes, CurrentActor, ct), "Notes saved.", new { id });

    public Task<IActionResult> OnPostSendInstructionsAsync(Guid id, CancellationToken ct)
        => ActAsync(() => licenses.SendActivationInstructionsAsync(id, CurrentActor, ct), "Activation instructions queued for e-mail.", new { id });

    public async Task<IActionResult> OnPostReplaceKeyAsync(Guid id, CancellationToken ct)
    {
        if (!CanWrite) return Forbid();
        try
        {
            ReplacementKey = await licenses.ReplaceKeyAsync(id, CurrentActor, SendKeyEmail, ct);
        }
        catch (LicensingOperationException ex)
        {
            ErrorMessage = ex.Message;
            return RedirectToPage(new { id });
        }
        NoStore();
        return await LoadAsync(id, ct) ? Page() : NotFound();
    }
}
