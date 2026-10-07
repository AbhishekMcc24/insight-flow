using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using InsightFlow.Contracts;
using InsightFlow.Domain.Tenancy;
using InsightFlow.Domain.Workspace;
using InsightFlow.Persistence;
using InsightFlow.ServiceDefaults.Security;
using Microsoft.EntityFrameworkCore;

namespace InsightFlow.IntegrationTests;

/// <summary>Authentication, tenant resolution and authorization policies over real HTTP, plus the development seed.</summary>
public sealed class SecurityTests(AppHostFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<HttpResponseMessage> GetMeAsync(string? authorization)
    {
        using var client = fixture.CreateHttpClient("api");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        if (authorization is not null)
        {
            request.Headers.Authorization = AuthenticationHeaderValue.Parse(authorization);
        }

        return await client.SendAsync(request, Ct);
    }

    [Fact]
    public async Task Me_WithoutToken_IsTheSeededDevelopmentUser()
    {
        fixture.RequireRunning();

        using var response = await GetMeAsync(null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var me = await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.CurrentUserResponse, Ct);
        me!.TenantId.ShouldBe(DevelopmentIdentity.TenantId);
        me.UserId.ShouldBe(DevelopmentIdentity.UserId);
        me.Roles.ShouldBe(["Creator", "TenantAdmin"]);
    }

    [Fact]
    public async Task Me_WithDevToken_ResolvesThatTenant()
    {
        fixture.RequireRunning();
        var tenant = Guid.NewGuid();

        using var response = await GetMeAsync(new DevToken("bob", tenant, [InsightFlowRoles.Viewer]).ToHeaderValue());

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.CurrentUserResponse, Ct))!.TenantId.ShouldBe(tenant);
    }

    [Fact]
    public async Task Me_WithMalformedToken_IsUnauthorized()
    {
        fixture.RequireRunning();

        using var response = await GetMeAsync("Dev not-base64-json!!");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Me_WithoutTenantClaim_IsForbidden()
    {
        fixture.RequireRunning();

        using var response = await GetMeAsync(new DevToken("bob", Guid.Empty, [InsightFlowRoles.Creator]).ToHeaderValue());

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Me_WithoutAnyRole_IsForbidden()
    {
        fixture.RequireRunning();

        using var response = await GetMeAsync(new DevToken("bob", Guid.NewGuid(), []).ToHeaderValue());

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Health_IsAnonymous_EvenWithInvalidToken()
    {
        fixture.RequireRunning();
        using var client = fixture.CreateHttpClient("api");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Authorization = AuthenticationHeaderValue.Parse("Dev garbage");

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task MigrationService_SeedsDevelopmentTenantAndRoots()
    {
        fixture.RequireRunning();
        var tenant = new TenantId(DevelopmentIdentity.TenantId);

        await using var db = fixture.CreateDbContext(new FixedCurrentTenant(tenant));

        (await db.Tenants.SingleAsync(Ct)).Name.ShouldBe(DevelopmentIdentity.TenantName);
        var roots = await db.Folders.Where(f => f.ParentId == null).ToListAsync(Ct);
        roots.ShouldContain(f => f.Scope == FolderScope.Shared);
        roots.ShouldContain(f => f.Scope == FolderScope.Personal && f.OwnerUserId == DevelopmentIdentity.UserId);
        (await db.Folders.AnyAsync(f => f.Name == ItemName.Create("Sample Data"), Ct)).ShouldBeTrue();
    }
}
