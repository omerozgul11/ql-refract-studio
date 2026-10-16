using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using RefractionStudio.Server.Data;
using RefractionStudio.Server.Data.Entities;
using RefractionStudio.Server.Data.Services;

namespace RefractionStudio.AdminPortal.Infrastructure;

/// <summary>Cookie session of an admin user.</summary>
public static class AdminSession
{
    public const string StampClaim = "rs:stamp";

    public static ClaimsPrincipal CreatePrincipal(AdminUser user)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Email),
            new Claim(ClaimTypes.GivenName, user.DisplayName),
            new Claim(ClaimTypes.Role, user.Role),
            new Claim(StampClaim, user.SecurityStamp),
        ], CookieAuthenticationDefaults.AuthenticationScheme);
        return new ClaimsPrincipal(identity);
    }

    /// <summary>
    /// Re-checks the account on every request: a disabled account, a changed password or role (security stamp)
    /// ends existing sessions immediately.
    /// </summary>
    public static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
    {
        var id = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        var stamp = context.Principal?.FindFirstValue(StampClaim);
        var valid = false;
        if (Guid.TryParse(id, out var userId) && stamp is not null)
        {
            var db = context.HttpContext.RequestServices.GetRequiredService<LicensingDbContext>();
            valid = await db.AdminUsers.AsNoTracking().AnyAsync(u => u.Id == userId && !u.Disabled && u.SecurityStamp == stamp, context.HttpContext.RequestAborted).ConfigureAwait(false);
        }
        if (!valid)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
        }
    }
}

/// <summary>Base page: current admin, permissions, flash messages and a helper for audited actions.</summary>
public abstract class AdminPageModel : PageModel
{
    public const int PageSize = 50;

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public Guid AdminId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public bool IsAdministrator => User.IsInRole(AdminRoles.Administrator);

    /// <summary>Administrators and support staff may change data; viewers may not.</summary>
    public bool CanWrite => IsAdministrator || User.IsInRole(AdminRoles.Support);

    /// <summary>The signed-in admin as the actor of audited operations.</summary>
    protected Actor CurrentActor
        => Actor.Admin(AdminId, User.Identity!.Name!, HttpContext.RequestServices.GetRequiredService<IpHasher>().Hash(HttpContext.Connection.RemoteIpAddress));

    /// <summary>
    /// Runs a state-changing operation when the user may write; a <see cref="LicensingOperationException"/>
    /// becomes an error message. Redirects (post/redirect/get) with the given route values.
    /// </summary>
    protected async Task<IActionResult> ActAsync(Func<Task> action, string success, object? routeValues = null, bool administratorOnly = false)
    {
        if (!CanWrite || (administratorOnly && !IsAdministrator)) return Forbid();
        try
        {
            await action().ConfigureAwait(false);
            StatusMessage = success;
        }
        catch (LicensingOperationException ex)
        {
            ErrorMessage = ex.Message;
        }
        return RedirectToPage(routeValues);
    }

    /// <summary>Prevents browsers and proxies from storing a page (used when a licence key is displayed).</summary>
    protected void NoStore()
    {
        Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
        Response.Headers.Pragma = "no-cache";
    }
}

/// <summary>Display helpers for Razor views.</summary>
public static class Fmt
{
    public static string Date(DateTimeOffset? d) => d is { } v ? v.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "—";

    public static string Time(DateTimeOffset? d) => d is { } v ? v.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC" : "—";

    public static string Expiry(DateTimeOffset? d) => d is null ? "Perpetual" : Date(d);

    public static string Short(Guid id) => id.ToString("N")[..8];

    public static string Hash(string? h) => string.IsNullOrEmpty(h) ? "—" : h.Length <= 12 ? h : h[..12] + "…";

    public static string Status(LicenseRecordStatus s) => s.ToString().ToLowerInvariant();

    /// <summary>A date picked in a form, interpreted as the end of that day in UTC.</summary>
    public static DateTimeOffset? EndOfDayUtc(DateOnly? date)
        => date is { } d ? new DateTimeOffset(d.ToDateTime(new TimeOnly(23, 59, 59), DateTimeKind.Utc)) : null;

    public static string Bytes(long size) => size switch
    {
        >= 1L << 30 => (size / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB",
        >= 1L << 20 => (size / (double)(1L << 20)).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
        >= 1L << 10 => (size / (double)(1L << 10)).ToString("0.0", CultureInfo.InvariantCulture) + " KB",
        _ => size.ToString(CultureInfo.InvariantCulture) + " B",
    };
}

/// <summary>Paging state of a list page.</summary>
public sealed record Paging(int PageNumber, int TotalCount, int PageSize)
{
    public int PageCount => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));

    public bool HasPrevious => PageNumber > 1;

    public bool HasNext => PageNumber < PageCount;

    public int Skip => (PageNumber - 1) * PageSize;

    public static Paging Create(int requested, int total, int pageSize = AdminPageModel.PageSize)
    {
        var pages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        return new Paging(Math.Clamp(requested, 1, pages), total, pageSize);
    }
}
