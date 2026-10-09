using InsightFlow.Domain.Tenancy;
using InsightFlow.Domain.Threads;

namespace InsightFlow.Domain.Tests.Threads;

public sealed class LocalStorageTests
{
    [Fact]
    public void Resolve_RelativeIdPath_StaysUnderRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "insightflow-storage-tests", Guid.NewGuid().ToString("N"));
        var relative = StoragePaths.File(new TenantId(Guid.NewGuid()), Guid.NewGuid());

        var full = LocalStorage.Resolve(root, relative);

        full.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase).ShouldBeTrue();
        full.EndsWith(relative.Replace('/', Path.DirectorySeparatorChar), StringComparison.Ordinal).ShouldBeTrue();
    }

    [Fact]
    public void Resolve_ParentSegment_IsRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "insightflow-storage-tests");

        Should.Throw<ArgumentException>(() => LocalStorage.Resolve(root, "../outside.txt"));
    }

    [Fact]
    public async Task WriteNew_SecondWrite_Fails()
    {
        var root = Path.Combine(Path.GetTempPath(), "insightflow-storage-tests", Guid.NewGuid().ToString("N"));
        var relative = StoragePaths.Extract(new TenantId(Guid.NewGuid()), Guid.NewGuid());
        await LocalStorage.WriteNewAsync(root, relative, new MemoryStream("parquet"u8.ToArray()), CancellationToken.None);

        await Should.ThrowAsync<IOException>(() =>
            LocalStorage.WriteNewAsync(root, relative, new MemoryStream("again"u8.ToArray()), CancellationToken.None));
    }
}
