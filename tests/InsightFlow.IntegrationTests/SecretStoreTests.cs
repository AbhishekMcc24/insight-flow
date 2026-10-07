using InsightFlow.Domain.Security;
using InsightFlow.Domain.Tenancy;
using InsightFlow.Persistence;
using InsightFlow.Persistence.Secrets;

namespace InsightFlow.IntegrationTests;

/// <summary>Secret store behaviour that does not need Docker (development file implementation).</summary>
public sealed class SecretStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"insightflow-secrets-{Guid.NewGuid():N}.json");
    private readonly LocalFileSecretStore _store;

    public SecretStoreTests() => _store = new LocalFileSecretStore(_path);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _store.Dispose();
        File.Delete(_path);
    }

    [Fact]
    public async Task SaveThenGet_ReturnsValue_AndFileHoldsOnlyReferences()
    {
        var tenant = TenantId.New();

        var reference = await _store.SaveAsync(tenant, "p@ssw0rd", Ct);

        (await _store.GetAsync(tenant, reference, Ct)).ShouldBe("p@ssw0rd");
        reference.TenantId.ShouldBe(tenant);
        reference.Name.ShouldNotContain("p@ssw0rd");
    }

    [Fact]
    public async Task Get_WithOtherTenant_IsRefused()
    {
        var reference = await _store.SaveAsync(TenantId.New(), "secret", Ct);

        await Should.ThrowAsync<TenantIsolationException>(() => _store.GetAsync(TenantId.New(), reference, Ct));
    }

    [Fact]
    public async Task Delete_RemovesSecret()
    {
        var tenant = TenantId.New();
        var reference = await _store.SaveAsync(tenant, "secret", Ct);

        await _store.DeleteAsync(tenant, reference, Ct);

        (await _store.GetAsync(tenant, reference, Ct)).ShouldBeNull();
    }

    [Fact]
    public void SecretReference_RoundTripsThroughItsName_AndIsKeyVaultSafe()
    {
        var tenant = TenantId.New();
        var reference = SecretReference.New(tenant);

        SecretReference.TryParse(reference.Name, out var parsed).ShouldBeTrue();
        parsed.ShouldBe(reference);
        parsed.TenantId.ShouldBe(tenant);
        reference.Name.ShouldMatch("^[A-Za-z0-9-]{1,127}$");
    }

    [Theory]
    [InlineData("")]
    [InlineData("t-nothex")]
    [InlineData("../../etc/passwd")]
    public void SecretReference_TryParse_RejectsGarbage(string name)
    {
        SecretReference.TryParse(name, out _).ShouldBeFalse();
    }
}
