using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using RefractionStudio.AdminPortal.Infrastructure;
using RefractionStudio.Server.Data;
using RefractionStudio.Server.Data.Services;

namespace RefractionStudio.AdminPortal.Pages.Account;

public sealed class ChangePasswordModel(AdminUserService users, LicensingDbContext db) : AdminPageModel
{
    [BindProperty]
    public string CurrentPassword { get; set; } = "";

    [BindProperty]
    public string NewPassword { get; set; } = "";

    [BindProperty]
    public string ConfirmPassword { get; set; } = "";

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (NewPassword != ConfirmPassword)
        {
            ErrorMessage = "The new passwords do not match.";
            return RedirectToPage();
        }
        try
        {
            await users.ChangePasswordAsync(AdminId, CurrentPassword, NewPassword, CurrentActor, ct);
        }
        catch (LicensingOperationException ex)
        {
            ErrorMessage = ex.Message;
            return RedirectToPage();
        }
        // Re-issue this session's cookie with the new security stamp.
        var user = await db.AdminUsers.FindAsync([AdminId], ct);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, AdminSession.CreatePrincipal(user!));
        StatusMessage = "Your password has been changed.";
        return RedirectToPage("/Index");
    }
}
