using System.Security.Cryptography;
using InsightFlow.Connectors;
using InsightFlow.Domain.Connections;
using InsightFlow.Domain.Modeling;
using InsightFlow.Domain.Tenancy;
using InsightFlow.Domain.Threads;
using InsightFlow.Domain.Workspace;
using InsightFlow.Persistence;
using InsightFlow.ServiceDefaults.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace InsightFlow.Worker.Development;

/// <summary>
/// Development only: puts the sample <c>retail_sales.csv</c> into Contoso's "Shared / Sample Data" folder, turns it into a
/// dataset through the real extract queue and pipeline, and adds a curated semantic model on top. Idempotent: it does
/// nothing once the sample model exists.
/// </summary>
internal sealed partial class DevelopmentSampleDataService(
    IServiceScopeFactory scopes,
    IOptions<ConnectorOptions> options,
    TimeProvider clock,
    ILogger<DevelopmentSampleDataService> logger) : BackgroundService
{
    public const string ModelName = "Contoso Retail (sample)";
    public static readonly ItemName DatasetName = ItemName.Create("Retail sales");
    private static readonly ItemName FileName = ItemName.Create("retail_sales.csv");
    private static readonly TimeSpan WaitForExtract = TimeSpan.FromMinutes(3);

    private static TenantId Contoso => new(DevelopmentIdentity.TenantId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (await WithTenantAsync(db => db.SemanticModels.AnyAsync(m => m.Name == ModelName, stoppingToken)))
            {
                return;
            }

            var definitionId = await WithTenantAsync(db => db.ExtractDefinitions
                .Where(d => d.Name == DatasetName && d.StoredFileId != null).Select(d => (Guid?)d.Id).FirstOrDefaultAsync(stoppingToken))
                ?? await UploadAndQueueAsync(stoppingToken);
            if (definitionId == Guid.Empty)
            {
                return;
            }

            var versionId = await WaitForVersionAsync(definitionId, stoppingToken);
            if (versionId is null)
            {
                LogExtractNotReady(logger);
                return;
            }

            await WithTenantAsync(async db =>
            {
                db.SemanticModels.Add(SemanticModelRecord.FromDomain(CreateModel(versionId.Value), clock.GetUtcNow()));
                return await db.SaveChangesAsync(stoppingToken);
            });
            LogSeeded(logger, versionId.Value);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSeedFailed(logger, ex);
        }
    }

    private async Task<Guid> UploadAndQueueAsync(CancellationToken ct)
    {
        return await WithTenantAsync(async db =>
        {
            var folder = await db.Folders.FirstOrDefaultAsync(f => f.Scope == FolderScope.Shared && f.Name == ItemName.Create("Sample Data"), ct);
            if (folder is null)
            {
                return Guid.Empty;
            }

            await using var resource = typeof(DevelopmentSampleDataService).Assembly.GetManifestResourceStream("InsightFlow.Worker.Samples.retail_sales.csv")
                ?? throw new InvalidOperationException("Embedded sample CSV is missing.");
            using var buffer = new MemoryStream();
            await resource.CopyToAsync(buffer, ct);
            var bytes = buffer.ToArray();

            var fileId = StoredFile.NewId();
            await LocalStorage.WriteNewAsync(options.Value.StorageRoot, StoragePaths.File(Contoso, fileId), new MemoryStream(bytes), ct);

            var now = clock.GetUtcNow();
            var stored = StoredFile.Create(fileId, Contoso, FileName, "text/csv", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)), "system", now);
            var names = await db.ContentItems.Where(i => i.FolderId == folder.Id).Select(i => i.Name).ToListAsync(ct);
            db.StoredFiles.Add(stored);
            db.ContentItems.Add(ContentItem.Create(folder, NameConflicts.NextAvailable(FileName, names), ContentKind.File, stored.Id, "system", now));
            var definition = ExtractDefinition.ForFile(stored, TabularFormat.Csv, folder, DatasetName, "system", now);
            db.ExtractDefinitions.Add(definition);
            db.ExtractRuns.Add(ExtractRun.Enqueue(definition, "system", now));
            await db.SaveChangesAsync(ct);
            return definition.Id;
        });
    }

    private async Task<Guid?> WaitForVersionAsync(Guid definitionId, CancellationToken ct)
    {
        var deadline = clock.GetUtcNow() + WaitForExtract;
        while (clock.GetUtcNow() < deadline)
        {
            var latest = await WithTenantAsync(db => db.ExtractDefinitions.Where(d => d.Id == definitionId).Select(d => d.LatestVersionId).SingleAsync(ct));
            if (latest is not null)
            {
                return latest;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), clock, ct);
        }

        return null;
    }

    private async Task<T> WithTenantAsync<T>(Func<InsightFlowDbContext, Task<T>> work)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<JobCurrentTenant>().Enter(Contoso);
        return await work(scope.ServiceProvider.GetRequiredService<InsightFlowDbContext>());
    }

    /// <summary>Curated model over the denormalized sample: display names, synonyms and measures that ground the agents.</summary>
    internal static SemanticModel CreateModel(Guid versionId) => new(
        Guid.CreateVersion7(),
        Contoso,
        ModelName,
        [
            new ModelTable("retail_sales", versionId,
            [
                new("order_id", DataType.Integer, ColumnRole.Dimension, "Order ID"),
                new("order_date", DataType.Date, ColumnRole.Dimension, "Order date", Synonyms: ["date", "day", "when"]),
                new("channel", DataType.String, ColumnRole.Dimension, "Sales channel", "Online or Store", ["online or in-store"]),
                new("store_name", DataType.String, ColumnRole.Dimension, "Store"),
                new("region", DataType.String, ColumnRole.Dimension, "Region", Synonyms: ["area", "territory"]),
                new("country", DataType.String, ColumnRole.Dimension, "Country"),
                new("product_name", DataType.String, ColumnRole.Dimension, "Product"),
                new("category", DataType.String, ColumnRole.Dimension, "Category", Synonyms: ["product category"]),
                new("subcategory", DataType.String, ColumnRole.Dimension, "Subcategory"),
                new("quantity", DataType.Integer, ColumnRole.Measure, "Units sold", Synonyms: ["units", "volume"]),
                new("revenue", DataType.Decimal, ColumnRole.Measure, "Revenue", Synonyms: ["sales", "turnover"], Format: "C2"),
                new("cost", DataType.Decimal, ColumnRole.Measure, "Cost", Format: "C2"),
                new("is_returned", DataType.Boolean, ColumnRole.Dimension, "Returned"),
            ],
            "Retail sales", "One row per order line (sample data)."),
        ],
        [],
        [
            new CalculatedMeasure("profit", "SUM(revenue) - SUM(cost)", DataType.Decimal, "Profit", Format: "C2"),
            new CalculatedMeasure("margin_pct", "(SUM(revenue) - SUM(cost)) / NULLIF(SUM(revenue), 0)", DataType.Decimal, "Margin %", Format: "P1"),
        ]);

    [LoggerMessage(Level = LogLevel.Information, Message = "Development sample data ready (dataset version {DatasetVersionId})")]
    private static partial void LogSeeded(ILogger logger, Guid datasetVersionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sample extract did not finish in time; the semantic model will be created on the next start")]
    private static partial void LogExtractNotReady(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Seeding development sample data failed")]
    private static partial void LogSeedFailed(ILogger logger, Exception exception);
}
