using InsightFlow.Domain.Tenancy;
using InsightFlow.Domain.Workspace;
using InsightFlow.Persistence;
using InsightFlow.ServiceDefaults.Security;
using Microsoft.EntityFrameworkCore;

namespace InsightFlow.MigrationService;

/// <summary>
/// Idempotently seeds the local "Contoso Retail" tenant used by the development auth handler: the tenant row,
/// its Shared root with a "Sample Data" folder, and the dev user's personal root. The sample CSV, its dataset
/// version and the retail semantic model are seeded by the extract pipeline (Milestone 5) so they are real data.
/// </summary>
internal sealed partial class DevelopmentDataSeeder(InsightFlowDbContext db, TimeProvider clock, ILogger<DevelopmentDataSeeder> logger)
{
    public static readonly ItemName SampleDataFolderName = ItemName.Create("Sample Data");

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        // Runs inside an execution strategy: a retry must start from a clean change tracker.
        db.ChangeTracker.Clear();
        var tenantId = new TenantId(DevelopmentIdentity.TenantId);
        var now = clock.GetUtcNow();

        // Seeding runs as the system scope, so tenant filters are bypassed explicitly and only here.
        var tenants = db.Tenants.IgnoreQueryFilters();
        var folders = db.Folders.IgnoreQueryFilters();

        if (!await tenants.AnyAsync(t => t.Id == tenantId, cancellationToken))
        {
            db.Tenants.Add(Tenant.Create(tenantId, DevelopmentIdentity.TenantName, now));
        }

        var sharedRoot = await folders.SingleOrDefaultAsync(
            f => f.TenantId == tenantId && f.ParentId == null && f.Scope == FolderScope.Shared, cancellationToken);
        if (sharedRoot is null)
        {
            sharedRoot = Folder.CreateSharedRoot(tenantId, now);
            db.Folders.Add(sharedRoot);
        }

        var sampleExists = await folders.AnyAsync(
            f => f.TenantId == tenantId && f.ParentId == sharedRoot.Id && f.Name == SampleDataFolderName && f.DeletedAt == null, cancellationToken);
        if (!sampleExists)
        {
            db.Folders.Add(Folder.CreateChild(sharedRoot, 0, SampleDataFolderName, "system", now));
        }

        var personalExists = await folders.AnyAsync(
            f => f.TenantId == tenantId && f.ParentId == null && f.Scope == FolderScope.Personal && f.OwnerUserId == DevelopmentIdentity.UserId,
            cancellationToken);
        if (!personalExists)
        {
            db.Folders.Add(Folder.CreatePersonalRoot(tenantId, DevelopmentIdentity.UserId, now));
        }

        var changes = await db.SaveChangesAsync(cancellationToken);
        LogSeeded(logger, changes);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Development data seeded ({Changes} rows written)")]
    private static partial void LogSeeded(ILogger logger, int changes);
}
