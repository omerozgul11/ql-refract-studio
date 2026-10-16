using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RefractionStudio.Server.Data.Email;
using RefractionStudio.Server.Data.Payments;
using RefractionStudio.Server.Data.Services;

namespace RefractionStudio.Server.Data;

/// <summary>Dependency-injection registration shared by the licence server and the admin portal.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the database context, secrets and domain services. Configuration is read lazily so hosts
    /// (and tests) can override it after registration.
    /// </summary>
    public static IServiceCollection AddLicensingData(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<LicensingServerOptions>().Bind(configuration.GetSection(LicensingServerOptions.Section));
        services.AddOptions<EmailOptions>().Bind(configuration.GetSection(EmailOptions.Section));
        services.AddOptions<SmtpOptions>().Bind(configuration.GetSection(SmtpOptions.Section));
        services.AddOptions<StripeOptions>().Bind(configuration.GetSection(StripeOptions.Section));

        services.AddDbContext<LicensingDbContext>((sp, o) =>
        {
            var cs = sp.GetRequiredService<IConfiguration>().GetConnectionString("Licensing");
            if (string.IsNullOrWhiteSpace(cs)) throw new InvalidOperationException("ConnectionStrings:Licensing is not configured.");
            LicensingDbContext.Configure(o, cs);
        });

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ServerSecrets>();
        services.AddSingleton<TokenIssuer>();
        services.AddSingleton<IpHasher>();
        services.AddSingleton<OutboxProtector>();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<EmailOptions>>().Value);
        services.AddSingleton<EmailTemplates>();
        services.AddSingleton<IEmailTransport, SmtpEmailTransport>();

        services.AddScoped<EmailOutbox>();
        services.AddScoped<EmailDispatcher>();
        services.AddScoped<LicenseService>();
        services.AddScoped<ActivationService>();
        services.AddScoped<TrialService>();
        services.AddScoped<ReleaseService>();
        services.AddScoped<CustomerService>();
        services.AddScoped<DeviceService>();
        services.AddScoped<AdminUserService>();
        services.AddScoped<StripePaymentProvider>();
        services.AddScoped<IPaymentProvider>(sp => sp.GetRequiredService<StripePaymentProvider>());
        return services;
    }

    /// <summary>Applies pending migrations when <c>Licensing:AutoMigrate</c> is true.</summary>
    public static async Task MigrateIfConfiguredAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        var options = services.GetRequiredService<IOptions<LicensingServerOptions>>().Value;
        if (!options.AutoMigrate) return;
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LicensingDbContext>();
        var log = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ServiceCollectionExtensions));
        var pending = (await db.Database.GetPendingMigrationsAsync(ct).ConfigureAwait(false)).ToList();
        if (pending.Count == 0) return;
        log.LogInformation("Applying {Count} database migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
        // EF Core holds a database-wide migration lock, so the licence server and the admin portal may start together.
        await db.Database.MigrateAsync(ct).ConfigureAwait(false);
    }
}
