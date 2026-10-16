using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using RefractionStudio.Server.Data.Entities;

namespace RefractionStudio.Server.Data;

/// <summary>
/// EF Core model of the licensing database (PostgreSQL). Tables and columns use snake_case names;
/// all times are <c>timestamptz</c> in UTC. <c>license_events</c> and <c>audit_log</c> are append-only
/// (enforced here and by database triggers).
/// </summary>
public sealed class LicensingDbContext(DbContextOptions<LicensingDbContext> options) : DbContext(options)
{
    /// <summary>Product code of Refraction Studio.</summary>
    public const string RefractionStudioProduct = "refraction-studio";

    /// <summary>Name of the EF Core migrations history table.</summary>
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    /// <summary>The one place where the PostgreSQL provider is configured (application, tools and tests).</summary>
    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder builder, string connectionString)
        => builder.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(MigrationsHistoryTable));

    /// <summary>Creates options for a context outside dependency injection.</summary>
    public static DbContextOptions<LicensingDbContext> CreateOptions(string connectionString)
    {
        var builder = new DbContextOptionsBuilder<LicensingDbContext>();
        Configure(builder, connectionString);
        return builder.Options;
    }

    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<LicenseProduct> LicenseProducts => Set<LicenseProduct>();
    public DbSet<License> Licenses => Set<License>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Activation> Activations => Set<Activation>();
    public DbSet<TrialDevice> TrialDevices => Set<TrialDevice>();
    public DbSet<LicenseEvent> LicenseEvents => Set<LicenseEvent>();
    public DbSet<SoftwareVersion> SoftwareVersions => Set<SoftwareVersion>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();
    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();
    public DbSet<EmailOutboxMessage> EmailOutbox => Set<EmailOutboxMessage>();
    public DbSet<WebhookEvent> WebhookEvents => Set<WebhookEvent>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Customer>(e =>
        {
            e.ToTable("customers");
            e.HasKey(x => x.Id);
            e.Property(x => x.Email).HasMaxLength(320).IsRequired();
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Company).HasMaxLength(200);
            e.Property(x => x.ProviderCustomerId).HasMaxLength(100);
            e.Property(x => x.Notes).HasMaxLength(4000);
            e.Ignore(x => x.DisplayName);
        });

        b.Entity<LicenseProduct>(e =>
        {
            e.ToTable("license_products");
            e.HasKey(x => x.Code);
            e.Property(x => x.Code).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.KeyPrefix).HasMaxLength(8);
            e.Property(x => x.DefaultEdition).HasMaxLength(64);
        });

        b.Entity<License>(e =>
        {
            e.ToTable("licenses", t => t.HasCheckConstraint("ck_licenses_seat_count", "seat_count >= 1"));
            e.HasKey(x => x.Id);
            e.Property(x => x.LicenseKeyHash).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.LicenseKeyHash).IsUnique();
            e.Property(x => x.KeyHint).HasMaxLength(4).IsRequired();
            e.HasIndex(x => x.KeyHint);
            e.Property(x => x.Product).HasMaxLength(64);
            e.Property(x => x.Edition).HasMaxLength(64);
            e.Property(x => x.Status).HasConversion(LowerCaseEnum<LicenseRecordStatus>()).HasMaxLength(16);
            e.Property(x => x.StatusReason).HasMaxLength(1000);
            e.Property(x => x.Notes).HasMaxLength(4000);
            e.Property(x => x.MaxTransfersPer30Days).HasColumnName("max_transfers_per_30_days").HasDefaultValue(3);
            e.Property(x => x.OfflineDays).HasDefaultValue(30);
            e.HasOne(x => x.Customer).WithMany(c => c.Licenses).HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<LicenseProduct>().WithMany().HasForeignKey(x => x.Product).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.CustomerId);
            e.HasIndex(x => new { x.Status, x.ExpiresAt });
        });

        b.Entity<Device>(e =>
        {
            e.ToTable("devices");
            e.HasKey(x => x.Id);
            e.Property(x => x.Product).HasMaxLength(64);
            e.Property(x => x.Fingerprint).HasMaxLength(64).IsRequired();
            e.HasIndex(x => new { x.Product, x.Fingerprint }).IsUnique();
            e.Property(x => x.MachineName).HasMaxLength(128);
            e.Property(x => x.OsVersion).HasMaxLength(256);
            e.Property(x => x.BlockedReason).HasMaxLength(1000);
        });

        b.Entity<Activation>(e =>
        {
            e.ToTable("activations");
            e.HasKey(x => x.Id);
            e.Property(x => x.AppVersion).HasMaxLength(64);
            e.Property(x => x.DeactivationReason).HasMaxLength(32);
            e.HasOne(x => x.License).WithMany(l => l.Activations).HasForeignKey(x => x.LicenseId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Device).WithMany(d => d.Activations).HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.LicenseId, x.DeactivatedAt });
            e.HasIndex(x => x.DeviceId);
            // At most one seat-holding activation per licence and device.
            e.HasIndex(x => new { x.LicenseId, x.DeviceId }).IsUnique().HasFilter("deactivated_at IS NULL").HasDatabaseName("ux_activations_active_license_device");
            e.Ignore(x => x.IsActive);
        });

        b.Entity<TrialDevice>(e =>
        {
            e.ToTable("trial_devices");
            e.HasKey(x => x.Id);
            e.Property(x => x.Product).HasMaxLength(64);
            e.Property(x => x.Fingerprint).HasMaxLength(64).IsRequired();
            e.HasIndex(x => new { x.Product, x.Fingerprint });
            e.HasIndex(x => x.ComponentHashes).HasMethod("gin");
            e.HasIndex(x => x.EndsAt);
            e.Property(x => x.MachineName).HasMaxLength(128);
            e.Property(x => x.OsVersion).HasMaxLength(256);
            e.Property(x => x.AppVersion).HasMaxLength(64);
            e.Property(x => x.Email).HasMaxLength(320);
            e.HasOne<LicenseProduct>().WithMany().HasForeignKey(x => x.Product).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<LicenseEvent>(e =>
        {
            e.ToTable("license_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.Type).HasMaxLength(64);
            e.Property(x => x.Actor).HasMaxLength(128);
            e.Property(x => x.IpHash).HasMaxLength(64);
            e.Property(x => x.Details).HasColumnType("jsonb");
            e.HasIndex(x => new { x.LicenseId, x.OccurredAt });
            e.HasIndex(x => new { x.TrialId, x.OccurredAt });
            e.HasIndex(x => new { x.DeviceId, x.OccurredAt });
            e.HasIndex(x => x.OccurredAt);
        });

        b.Entity<AuditLogEntry>(e =>
        {
            e.ToTable("audit_log");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.Actor).HasMaxLength(400);
            e.Property(x => x.Action).HasMaxLength(64);
            e.Property(x => x.EntityType).HasMaxLength(64);
            e.Property(x => x.EntityId).HasMaxLength(100);
            e.Property(x => x.IpHash).HasMaxLength(64);
            e.Property(x => x.Details).HasColumnType("jsonb");
            e.HasIndex(x => x.OccurredAt);
            e.HasIndex(x => new { x.EntityType, x.EntityId });
            e.HasIndex(x => x.AdminUserId);
        });

        b.Entity<SoftwareVersion>(e =>
        {
            e.ToTable("software_versions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Product).HasMaxLength(64);
            e.Property(x => x.Version).HasMaxLength(64);
            e.Property(x => x.Channel).HasMaxLength(16);
            e.Property(x => x.DownloadUrl).HasMaxLength(2000);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.Property(x => x.MinimumVersion).HasMaxLength(64);
            e.HasIndex(x => new { x.Product, x.Version }).IsUnique();
            e.HasIndex(x => new { x.Product, x.Channel, x.Published });
            e.HasOne<LicenseProduct>().WithMany().HasForeignKey(x => x.Product).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Payment>(e =>
        {
            e.ToTable("payments");
            e.HasKey(x => x.Id);
            e.Property(x => x.Provider).HasMaxLength(32);
            e.Property(x => x.ProviderPaymentId).HasMaxLength(255);
            e.HasIndex(x => new { x.Provider, x.ProviderPaymentId }).IsUnique();
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.Status).HasMaxLength(16);
            e.HasOne(x => x.Customer).WithMany(c => c.Payments).HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<License>().WithMany().HasForeignKey(x => x.LicenseId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<AdminUser>(e =>
        {
            e.ToTable("admin_users");
            e.HasKey(x => x.Id);
            e.Property(x => x.Email).HasMaxLength(320).IsRequired();
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.DisplayName).HasMaxLength(200);
            e.Property(x => x.PasswordHash).HasMaxLength(500);
            e.Property(x => x.Role).HasMaxLength(32);
            e.Property(x => x.SecurityStamp).HasMaxLength(64);
        });

        b.Entity<EmailOutboxMessage>(e =>
        {
            e.ToTable("email_outbox");
            e.HasKey(x => x.Id);
            e.Property(x => x.ToAddress).HasMaxLength(320);
            e.Property(x => x.ToName).HasMaxLength(200);
            e.Property(x => x.Subject).HasMaxLength(400);
            e.Property(x => x.Template).HasMaxLength(64);
            e.Property(x => x.Status).HasConversion(LowerCaseEnum<EmailStatus>()).HasMaxLength(16);
            e.Property(x => x.LastError).HasMaxLength(2000);
            e.HasIndex(x => new { x.Status, x.NextAttemptAt });
            e.HasIndex(x => x.LicenseId);
        });

        b.Entity<WebhookEvent>(e =>
        {
            e.ToTable("webhook_events");
            e.HasKey(x => new { x.Provider, x.EventId });
            e.Property(x => x.Provider).HasMaxLength(32);
            e.Property(x => x.EventId).HasMaxLength(255);
            e.Property(x => x.Type).HasMaxLength(128);
            e.Property(x => x.Outcome).HasMaxLength(16);
            e.Property(x => x.Details).HasMaxLength(2000);
        });

        ApplySnakeCaseColumnNames(b);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardAppendOnly();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        GuardAppendOnly();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void GuardAppendOnly()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.Entity is LicenseEvent or AuditLogEntry && entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException($"{entry.Entity.GetType().Name} records are append-only and cannot be changed or deleted.");
        }
    }

    private static ValueConverter<TEnum, string> LowerCaseEnum<TEnum>() where TEnum : struct, Enum
        => new(v => v.ToString().ToLowerInvariant(), v => Enum.Parse<TEnum>(v, true));

    private static void ApplySnakeCaseColumnNames(ModelBuilder b)
    {
        foreach (var entity in b.Model.GetEntityTypes())
        {
            var table = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
            foreach (var property in entity.GetProperties())
            {
                if (property.GetColumnName(table) == property.Name) property.SetColumnName(ToSnakeCase(property.Name));
            }
            foreach (var key in entity.GetKeys()) key.SetName(key.GetDefaultName()!.ToLowerInvariant());
            foreach (var fk in entity.GetForeignKeys()) fk.SetConstraintName(fk.GetDefaultName()!.ToLowerInvariant());
            foreach (var index in entity.GetIndexes())
            {
                if (index.GetDatabaseName() is { } name && !name.StartsWith("ux_", StringComparison.Ordinal)) index.SetDatabaseName(name.ToLowerInvariant());
            }
        }
    }

    /// <summary>"MaxTransfersPer30Days" → "max_transfers_per30_days".</summary>
    internal static string ToSnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1])))) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}

/// <summary>
/// Design-time factory used by <c>dotnet ef</c>. The connection string comes from the
/// <c>ConnectionStrings__Licensing</c> environment variable; a local default is used only to generate migrations.
/// </summary>
public sealed class LicensingDbContextDesignFactory : IDesignTimeDbContextFactory<LicensingDbContext>
{
    public LicensingDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("ConnectionStrings__Licensing")
                 ?? "Host=localhost;Port=5432;Database=refraction_licensing;Username=licensing";
        return new LicensingDbContext(LicensingDbContext.CreateOptions(cs));
    }
}
