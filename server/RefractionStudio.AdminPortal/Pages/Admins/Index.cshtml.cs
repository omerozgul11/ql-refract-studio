using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RefractionStudio.AdminPortal.Infrastructure;
using RefractionStudio.Server.Data;
using RefractionStudio.Server.Data.Entities;
using RefractionStudio.Server.Data.Services;

namespace RefractionStudio.AdminPortal.Pages.Admins;

public sealed class IndexModel(LicensingDbContext db, AdminUserService users, TimeProvider clock) : AdminPageModel
{
    [BindProperty] public string Email { get; set; } = "";
    [BindProperty] public string DisplayName { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty] public string Role { get; set; } = AdminRoles.Support;

    public DateTimeOffset Now { get; private set; }

    public IReadOnlyList<AdminUser> Users { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        Now = clock.GetUtcNow();
        Users = await db.AdminUsers.AsNoTracking().OrderBy(u => u.Email).ToListAsync(ct);
    }

    public Task<IActionResult> OnPostCreateAsync(CancellationToken ct)
        => ActAsync(() => users.CreateAsync(Email, DisplayName, Password, Role, CurrentActor, ct), $"Account {Email} created.", administratorOnly: true);

    public Task<IActionResult> OnPostUnlockAsync(Guid userId, CancellationToken ct)
        => ActAsync(() => users.UnlockAsync(userId, CurrentActor, ct), "Account unlocked.", administratorOnly: true);

    public Task<IActionResult> OnPostDisableAsync(Guid userId, bool disabled, CancellationToken ct)
        => ActAsync(() => users.SetDisabledAsync(userId, disabled, CurrentActor, ct), disabled ? "Account disabled; its sessions have ended." : "Account enabled.", administratorOnly: true);

    public Task<IActionResult> OnPostRoleAsync(Guid userId, string role, CancellationToken ct)
        => ActAsync(() => users.SetRoleAsync(userId, role, CurrentActor, ct), "Role changed.", administratorOnly: true);

    public Task<IActionResult> OnPostResetPasswordAsync(Guid userId, string newPassword, CancellationToken ct)
        => ActAsync(() => users.ResetPasswordAsync(userId, newPassword, CurrentActor, ct), "Password reset; the user's sessions have ended.", administratorOnly: true);
}
