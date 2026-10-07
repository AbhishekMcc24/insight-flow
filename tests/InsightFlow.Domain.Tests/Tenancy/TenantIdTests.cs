using System.Text.Json;
using InsightFlow.Domain.Tenancy;
using InsightFlow.Domain.Threads;

namespace InsightFlow.Domain.Tests.Tenancy;

public sealed class TenantIdTests
{
    [Fact]
    public void TryParse_EmptyGuid_IsRejected()
    {
        TenantId.TryParse(Guid.Empty.ToString(), null, out _).ShouldBeFalse();
        TenantId.TryParse("not-a-guid", null, out _).ShouldBeFalse();
        TenantId.TryParse(null, null, out _).ShouldBeFalse();
    }

    [Fact]
    public void Json_RoundTripsAsPlainGuid()
    {
        var id = TenantId.New();

        var json = JsonSerializer.Serialize(id);

        json.ShouldBe($"\"{id.Value:D}\"");
        JsonSerializer.Deserialize<TenantId>(json).ShouldBe(id);
    }

    [Fact]
    public void StoragePaths_EmptyTenant_Throws()
    {
        Should.Throw<ArgumentException>(() => StoragePaths.Extract(default, Guid.NewGuid()));
    }
}
