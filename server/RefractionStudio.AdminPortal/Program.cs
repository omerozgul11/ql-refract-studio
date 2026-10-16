using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using RefractionStudio.AdminPortal.Infrastructure;
using RefractionStudio.Server.Data;
using RefractionStudio.Server.Data.Entities;
using RefractionStudio.Server.Data.Hosting;
using RefractionStudio.Server.Data.Services;

namespace RefractionStudio.AdminPortal;

/// <summary>
/// Internal admin portal (Razor Pages): customers, licences, devices, trials, activations, releases and the
/// audit log. Every page requires sign-in. Public partial for <c>WebApplicationFactory</c> tests.
/// </summary>
public partial class Program
{
    public const string AdministratorPolicy = "Administrator";
    public const string LoginRateLimitPolicy = "login";

    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--health-check")) return await HostingExtensions.RunHealthCheckAsync("/health").ConfigureAwait(false);
        var app = BuildApp(args);
        await app.Services.MigrateIfConfiguredAsync().ConfigureAwait(false);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<AdminUserService>()
                .BootstrapAsync(app.Configuration["Admin:BootstrapEmail"], app.Configuration["Admin:BootstrapPassword"], CancellationToken.None).ConfigureAwait(false);
        }
        await app.RunAsync().ConfigureAwait(false);
        return 0;
    }

    public static WebApplication BuildApp(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.ConfigureKestrel(k => k.AddServerHeader = false);
        var development = builder.Environment.IsDevelopment();

        builder.Services.AddLicensingData(builder.Configuration);
        builder.Services.AddReverseProxySupport(builder.Configuration);

        var dataProtection = builder.Services.AddDataProtection().SetApplicationName("RefractionStudio.AdminPortal");
        var keysPath = builder.Configuration["DataProtection:KeysPath"];
        if (!string.IsNullOrWhiteSpace(keysPath)) dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
        else builder.Services.AddSingleton<IDataProtectionProvider>(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());

        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
        {
            o.LoginPath = "/Account/Login";
            o.LogoutPath = "/Account/Logout";
            o.AccessDeniedPath = "/Account/AccessDenied";
            o.Cookie.Name = "rs_admin";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = development ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            o.ExpireTimeSpan = TimeSpan.FromHours(8);
            o.SlidingExpiration = true;
            o.Events.OnValidatePrincipal = AdminSession.ValidatePrincipalAsync;
        });
        builder.Services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(AdministratorPolicy, p => p.RequireRole(AdminRoles.Administrator));
        builder.Services.AddAntiforgery(o =>
        {
            o.Cookie.Name = "rs_admin_af";
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = development ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        });
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(LoginRateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = Math.Max(1, http.RequestServices.GetRequiredService<IConfiguration>().GetValue("Admin:LoginAttemptsPerMinute", 20)),
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
        });
        builder.Services.AddRazorPages(o =>
        {
            o.Conventions.AllowAnonymousToPage("/Account/Login");
            o.Conventions.AllowAnonymousToPage("/Account/AccessDenied");
            o.Conventions.AuthorizeFolder("/Admins", AdministratorPolicy);
            o.Conventions.AuthorizePage("/Releases/Create", AdministratorPolicy);
        });

        var app = builder.Build();
        _ = app.Services.GetRequiredService<ServerSecrets>().KeyPepper; // fail fast when the pepper is missing

        if (!development) app.UseExceptionHandler("/Error");
        app.UseTransportSecurity();
        app.Use(async (context, next) =>
        {
            var h = context.Response.Headers;
            h.XContentTypeOptions = "nosniff";
            h.XFrameOptions = "DENY";
            h["Referrer-Policy"] = "no-referrer";
            h.ContentSecurityPolicy = "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; frame-ancestors 'none'; form-action 'self'; base-uri 'self'";
            await next(context).ConfigureAwait(false);
        });
        app.UseStaticFiles();
        app.UseRouting();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
        app.MapRazorPages();
        return app;
    }
}
