using InsightFlow.Domain.Modeling;
using InsightFlow.Domain.Tenancy;
using InsightFlow.Domain.Threads;
using InsightFlow.Domain.Workspace;
using InsightFlow.Persistence;
using Microsoft.EntityFrameworkCore;

namespace InsightFlow.IntegrationTests;

/// <summary>
/// Proves tenant isolation against the real PostgreSQL schema: a tenant cannot read, modify, delete or create rows
/// of another tenant, and without a tenant in scope nothing is readable or writable.
/// </summary>
public sealed class TenantIsolationTests(AppHostFixture fixture)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Creates a fresh tenant with a shared root, a sub-folder, a dataset version and a semantic model.</summary>
    private async Task<(TenantId Tenant, Folder Root, Folder Child, DatasetVersion Version, SemanticModelRecord Model)> SeedTenantAsync()
    {
        var tenant = TenantId.New();
        await using var db = fixture.CreateDbContext(new FixedCurrentTenant(tenant));

        var root = Folder.CreateSharedRoot(tenant, Now);
        var child = Folder.CreateChild(root, 0, ItemName.Create("Reports"), "alice", Now);
        var version = DatasetVersion.CreateSource(DatasetVersion.NewId(), tenant, new DatasetSchema([new SchemaColumn("x", DataType.Integer)]), 3, "alice", Now);
        var model = SemanticModelRecord.FromDomain(
            new SemanticModel(Guid.NewGuid(), tenant, "Model", [new ModelTable("t", version.Id, [new ModelColumn("x", DataType.Integer, ColumnRole.Measure)])]),
            Now);

        db.Tenants.Add(Tenant.Create(tenant, $"Tenant {tenant}", Now));
        db.Folders.AddRange(root, child);
        db.DatasetVersions.Add(version);
        db.SemanticModels.Add(model);
        await db.SaveChangesAsync(Ct);

        return (tenant, root, child, version, model);
    }

    [Fact]
    public async Task Read_OtherTenantsRows_ReturnsNothing()
    {
        fixture.RequireRunning();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();

        await using var asB = fixture.CreateDbContext(new FixedCurrentTenant(b.Tenant));

        (await asB.Folders.Where(f => f.Id == a.Child.Id).ToListAsync(Ct)).ShouldBeEmpty();
        (await asB.Folders.FindAsync([a.Root.Id], Ct)).ShouldBeNull();
        (await asB.DatasetVersions.AnyAsync(v => v.Id == a.Version.Id, Ct)).ShouldBeFalse();
        (await asB.SemanticModels.AnyAsync(m => m.Id == a.Model.Id, Ct)).ShouldBeFalse();
        (await asB.Tenants.AnyAsync(t => t.Id == a.Tenant, Ct)).ShouldBeFalse();

        // B sees exactly its own rows.
        (await asB.Folders.Select(f => f.TenantId).Distinct().ToListAsync(Ct)).ShouldBe([b.Tenant]);
        (await asB.DatasetVersions.CountAsync(Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Read_IgnoringSoftDeleteFilter_StillAppliesTenantFilter()
    {
        fixture.RequireRunning();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();

        await using var asB = fixture.CreateDbContext(new FixedCurrentTenant(b.Tenant));
        var visible = await asB.Folders.IgnoreQueryFilters([InsightFlowDbContext.SoftDeleteFilter]).ToListAsync(Ct);

        visible.ShouldNotContain(f => f.TenantId == a.Tenant);
    }

    [Fact]
    public async Task Update_OtherTenantsRow_IsRefused()
    {
        fixture.RequireRunning();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();

        // Even if B obtains A's entity (e.g. via a bug that bypasses filters), saving changes to it is refused.
        await using var asB = fixture.CreateDbContext(new FixedCurrentTenant(b.Tenant));
        var stolen = await asB.Folders.IgnoreQueryFilters().SingleAsync(f => f.Id == a.Child.Id, Ct);
        stolen.Rename(ItemName.Create("pwned"));

        await Should.ThrowAsync<TenantIsolationException>(() => asB.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task Delete_OtherTenantsRow_IsRefused()
    {
        fixture.RequireRunning();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();

        await using var asB = fixture.CreateDbContext(new FixedCurrentTenant(b.Tenant));
        var version = await asB.DatasetVersions.IgnoreQueryFilters().SingleAsync(v => v.Id == a.Version.Id, Ct);
        asB.DatasetVersions.Remove(version);

        await Should.ThrowAsync<TenantIsolationException>(() => asB.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task Insert_RowForOtherTenant_IsRefused()
    {
        fixture.RequireRunning();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();

        await using var asB = fixture.CreateDbContext(new FixedCurrentTenant(b.Tenant));
        asB.Folders.Add(Folder.CreateChild(a.Root, 0, ItemName.Create("Injected"), "mallory", Now));

        await Should.ThrowAsync<TenantIsolationException>(() => asB.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task NoTenantInScope_ReadsNothing_AndRefusesWrites()
    {
        fixture.RequireRunning();
        await SeedTenantAsync();

        await using var anonymous = fixture.CreateDbContext(new DelegateCurrentTenant(() => null));

        (await anonymous.Folders.AnyAsync(Ct)).ShouldBeFalse();
        (await anonymous.DatasetVersions.AnyAsync(Ct)).ShouldBeFalse();

        anonymous.Tenants.Add(Tenant.Create(TenantId.New(), "Sneaky", Now));
        await Should.ThrowAsync<TenantIsolationException>(() => anonymous.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task SiblingFolderNames_AreUniqueCaseInsensitively_InTheDatabase()
    {
        fixture.RequireRunning();
        var a = await SeedTenantAsync();

        await using var db = fixture.CreateDbContext(new FixedCurrentTenant(a.Tenant));
        db.Folders.Add(Folder.CreateChild(a.Root, 0, ItemName.Create("REPORTS"), "alice", Now));

        await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task SoftDeletedFolder_FreesItsNameAndIsHidden()
    {
        fixture.RequireRunning();
        var a = await SeedTenantAsync();

        await using (var db = fixture.CreateDbContext(new FixedCurrentTenant(a.Tenant)))
        {
            var child = await db.Folders.SingleAsync(f => f.Id == a.Child.Id, Ct);
            child.SoftDelete(Now);
            db.Folders.Add(Folder.CreateChild(a.Root, 0, ItemName.Create("Reports"), "alice", Now));
            await db.SaveChangesAsync(Ct);
        }

        await using var check = fixture.CreateDbContext(new FixedCurrentTenant(a.Tenant));
        (await check.Folders.CountAsync(f => f.ParentId == a.Root.Id, Ct)).ShouldBe(1);
        (await check.Folders.IgnoreQueryFilters([InsightFlowDbContext.SoftDeleteFilter]).CountAsync(f => f.ParentId == a.Root.Id, Ct)).ShouldBe(2);
    }

    [Fact]
    public async Task JsonColumns_RoundTrip()
    {
        fixture.RequireRunning();
        var a = await SeedTenantAsync();

        await using var db = fixture.CreateDbContext(new FixedCurrentTenant(a.Tenant));
        var version = await db.DatasetVersions.SingleAsync(v => v.Id == a.Version.Id, Ct);
        var model = (await db.SemanticModels.SingleAsync(m => m.Id == a.Model.Id, Ct)).ToDomain();

        version.Schema.ShouldBe(a.Version.Schema);
        model.Tables.Single().Columns.Single().DataType.ShouldBe(DataType.Integer);
    }
}
