using System.Linq.Expressions;
using System.Text;
using InsightFlow.Domain.Tenancy;
using InsightFlow.Domain.Threads;
using InsightFlow.Domain.Workspace;
using Microsoft.EntityFrameworkCore;

namespace InsightFlow.Persistence;

/// <summary>
/// The metadata store (D10). Tenant isolation is enforced here, not in callers:
/// <list type="bullet">
/// <item>a named global query filter (<see cref="TenantFilter"/>) on every <see cref="ITenantOwned"/> entity limits reads to <see cref="ICurrentTenant"/>;</item>
/// <item><see cref="SaveChangesAsync(bool, CancellationToken)"/> refuses to insert, update or delete rows of any other tenant.</item>
/// </list>
/// Soft-deleted workspace rows are hidden by a second named filter (<see cref="SoftDeleteFilter"/>) that callers can
/// switch off on its own (e.g. a trash view) without ever disabling tenant isolation.
/// </summary>
public sealed class InsightFlowDbContext(DbContextOptions<InsightFlowDbContext> options, ICurrentTenant currentTenant)
    : DbContext(options)
{
    public const string TenantFilter = "tenant";
    public const string SoftDeleteFilter = "soft_delete";

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<DatasetVersion> DatasetVersions => Set<DatasetVersion>();

    public DbSet<DataThread> DataThreads => Set<DataThread>();

    public DbSet<ThreadNode> ThreadNodes => Set<ThreadNode>();

    public DbSet<SemanticModelRecord> SemanticModels => Set<SemanticModelRecord>();

    public DbSet<Folder> Folders => Set<Folder>();

    public DbSet<ContentItem> ContentItems => Set<ContentItem>();

    public DbSet<StoredFile> StoredFiles => Set<StoredFile>();

    /// <summary>Referenced by the query filters; EF Core re-evaluates it per query, so one model serves every tenant.</summary>
    private TenantId CurrentTenantId => currentTenant.TenantId ?? default;

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforceTenantOnWrites();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnforceTenantOnWrites();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
        configurationBuilder.Properties<TenantId>().HaveConversion<TenantIdConverter>();
        configurationBuilder.Properties<ItemName>().HaveConversion<ItemNameConverter>().HaveMaxLength(ItemName.MaxLength);
        configurationBuilder.Properties<DatasetVersionKind>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<FolderScope>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<ContentKind>().HaveConversion<string>().HaveMaxLength(32);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(InsightFlowDbContext).Assembly);

        modelBuilder.Entity<Tenant>().HasQueryFilter(TenantFilter, t => t.Id == CurrentTenantId);
        ApplyTenantFilter<DatasetVersion>(modelBuilder);
        ApplyTenantFilter<DataThread>(modelBuilder);
        ApplyTenantFilter<ThreadNode>(modelBuilder);
        ApplyTenantFilter<SemanticModelRecord>(modelBuilder);
        ApplyTenantFilter<Folder>(modelBuilder);
        ApplyTenantFilter<ContentItem>(modelBuilder);
        ApplyTenantFilter<StoredFile>(modelBuilder);

        modelBuilder.Entity<Folder>().HasQueryFilter(SoftDeleteFilter, f => f.DeletedAt == null);
        modelBuilder.Entity<ContentItem>().HasQueryFilter(SoftDeleteFilter, i => i.DeletedAt == null);

        ApplySnakeCaseColumnNames(modelBuilder);
    }

    private void ApplyTenantFilter<T>(ModelBuilder modelBuilder)
        where T : class, ITenantOwned
    {
        Expression<Func<T, bool>> filter = e => e.TenantId == CurrentTenantId;
        modelBuilder.Entity<T>().HasQueryFilter(TenantFilter, filter);
    }

    private void EnforceTenantOnWrites()
    {
        if (currentTenant.IsSystem)
        {
            return;
        }

        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            var owner = entry.Entity switch
            {
                ITenantOwned owned => owned.TenantId,
                Tenant tenant => tenant.Id,
                _ => (TenantId?)null,
            };

            if (owner is null)
            {
                continue;
            }

            var scope = currentTenant.TenantId
                ?? throw new TenantIsolationException("No tenant is in scope; tenant-owned rows cannot be written.");

            if (owner != scope)
            {
                throw new TenantIsolationException(
                    $"Refused to {entry.State.ToString().ToLowerInvariant()} a {entry.Metadata.ClrType.Name} that belongs to another tenant.");
            }

            if (entry.State == EntityState.Modified
                && entry.Properties.FirstOrDefault(p => p.Metadata.Name == nameof(ITenantOwned.TenantId)) is { IsModified: true })
            {
                throw new TenantIsolationException($"The tenant of a {entry.Metadata.ClrType.Name} cannot be changed.");
            }
        }
    }

    /// <summary>Postgres-friendly snake_case column names (table names are set explicitly in the configurations).</summary>
    private static void ApplySnakeCaseColumnNames(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.Name));
            }
        }
    }

    internal static string ToSnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                var startsWord = i > 0 && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1])));
                if (startsWord)
                {
                    sb.Append('_');
                }

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
