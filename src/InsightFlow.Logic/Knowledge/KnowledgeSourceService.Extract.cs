using InsightFlow.Infrastructure.Connectors;
using InsightFlow.Infrastructure.Connectors.Extraction;
using InsightFlow.Domain.Contracts.Knowledge;
using InsightFlow.Domain.Knowledge;
using InsightFlow.Repository;
using Microsoft.EntityFrameworkCore;

namespace InsightFlow.Logic.Knowledge;

public sealed partial class KnowledgeSourceService
{
    /// <summary>A copy newer than this is reused; older ones are copied again the next time they are needed.</summary>
    public static readonly TimeSpan ExtractMaxAge = TimeSpan.FromHours(12);

    /// <summary>Most rows copied from one table. Questions over a bigger table use its first rows, and say so.</summary>
    public const long ExtractMaxRows = 1_000_000;

    /// <summary>
    /// Makes sure a table has a recent Parquet copy, which questions are answered from (AI SQL never runs on the live source).
    /// Tables that came from a workspace dataset already are one. The copy is read-only data about the caller's own table: the
    /// connection, database and table come from the stored knowledge base, never from the request.
    /// </summary>
    public async Task<QueryExtractDto> PrepareQueryTableAsync(Guid tableId, bool refresh, CancellationToken cancellationToken)
    {
        var table = await db.KnowledgeTables.SingleOrDefaultAsync(t => t.Id == tableId, cancellationToken) ?? throw ApiProblemException.NotFound("The table");
        var now = clock.GetUtcNow();

        if (table.Source == DefinitionSource.Dataset)
        {
            var baseline = await db.LoadDatasetBaselineAsync(cancellationToken);
            var dataset = baseline.Tables.FirstOrDefault(t => string.Equals(t.Name, table.Name, StringComparison.OrdinalIgnoreCase))
                ?? throw new ApiProblemException(StatusCodes.Status409Conflict, "dataset_missing", $"The dataset “{table.Name}” is no longer in the shared workspace.");
            return new QueryExtractDto(table.Id, table.DisplayName, dataset.Id, dataset.RowCount, now, Cached: true, Truncated: false);
        }

        if (table.ConnectionId is not { } connectionId)
        {
            throw new ApiProblemException(StatusCodes.Status409Conflict, "no_source", $"“{table.DisplayName}” has no database connection, so there is no data to query.");
        }

        if (!refresh && table.ExtractVersionId is { } existing && table.ExtractedAt is { } at && now - at < ExtractMaxAge)
        {
            return new QueryExtractDto(table.Id, table.DisplayName, existing, table.ExtractRows ?? 0, at, Cached: true, table.ExtractTruncated);
        }

        var profile = WithDatabase(await LoadAsync(connectionId, cancellationToken), table.DatabaseName);
        await EnsureHostAllowedAsync(profile.Kind, profile.Settings, cancellationToken);

        var previous = table.ExtractVersionId is { } last ? await db.DatasetVersions.SingleOrDefaultAsync(v => v.Id == last, cancellationToken) : null;
        var sourceTable = string.IsNullOrEmpty(table.SchemaName) ? table.Name : $"{table.SchemaName}.{table.Name}";

        ExtractPipelineResult result;
        try
        {
            result = await pipeline.RunAsync(new ExtractRequest(profile, sourceTable, ExtractMaxRows), previous, UserId, cancellationToken);
        }
        catch (ConnectorException ex)
        {
            throw new ApiProblemException(StatusCodes.Status422UnprocessableEntity, "extract_failed", $"Copying “{table.DisplayName}” failed: {ex.Message}");
        }

        var version = result.Version;
        var truncated = version.RowCount >= ExtractMaxRows;
        db.DatasetVersions.Add(version);
        table.RecordExtract(version.Id, version.RowCount, truncated, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        LogExtracted(logger, table.Id, version.RowCount, truncated);
        return new QueryExtractDto(table.Id, table.DisplayName, version.Id, version.RowCount, table.ExtractedAt ?? now, Cached: false, truncated);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Query copy of knowledge table {TableId}: {Rows} rows (truncated {Truncated})")]
    private static partial void LogExtracted(ILogger logger, Guid tableId, long rows, bool truncated);
}
