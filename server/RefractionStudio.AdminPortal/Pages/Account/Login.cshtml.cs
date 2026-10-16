using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using RefractionStudio.AdminPortal.Infrastructure;
using RefractionStudio.Server.Data.Services;

namespace RefractionStudio.AdminPortal.Pages.Account;

[EnableRateLimiting(Program.LoginRateLimitPolicy)]
public sealed class LoginModel(AdminUserService users, IpHasher ip) : PageModel
{
    [BindProperty]
    public string Email { get; set; } = "";

    [BindProperty]
    public string Password { get; set; } = "";

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public string? Error { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var (result, user) = await users.SignInAsync(Email, Password, ip.Hash(HttpContext.Connection.RemoteIpAddress), ct);
        switch (result)
        {
            case AdminSignInResult.Success:
                await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, AdminSession.CreatePrincipal(user!),
                    new AuthenticationProperties { IsPersistent = false });
                return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/");
            case AdminSignInResult.LockedOut:
                Error = "This account is locked after repeated failed sign-in attempts. Try again in 15 minutes or ask an administrator to unlock it.";
                break;
            default:
                Error = "The e-mail address or password is not correct.";
                break;
        }
        Password = "";
        return Page();
    }
}
