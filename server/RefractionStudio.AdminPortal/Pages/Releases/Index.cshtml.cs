using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RefractionStudio.AdminPortal.Infrastructure;
using RefractionStudio.Server.Data;
using RefractionStudio.Server.Data.Entities;
using RefractionStudio.Server.Data.Services;

namespace RefractionStudio.AdminPortal.Pages.Releases;

public sealed class IndexModel(LicensingDbContext db, ReleaseService releases) : AdminPageModel
{
    public IReadOnlyList<SoftwareVersion> Rows { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        var all = await db.SoftwareVersions.AsNoTracking().ToListAsync(ct);
        Rows = [.. all.OrderBy(r => r.Product).ThenByDescending(r => SemanticVersion.TryParse(r.Version, out var v) ? v : new SemanticVersion(0, 0, 0, null))];
    }

    public Task<IActionResult> OnPostPublishAsync(Guid releaseId, bool publish, CancellationToken ct)
        => ActAsync(() => releases.SetPublishedAsync(releaseId, publish, CurrentActor, ct), publish ? "The release is now offered in the update feed." : "The release has been withdrawn from the update feed.",
            administratorOnly: true);
}
