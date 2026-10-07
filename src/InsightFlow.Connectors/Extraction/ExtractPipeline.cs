using System.Diagnostics;
using InsightFlow.Domain.Threads;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InsightFlow.Connectors.Extraction;

/// <summary>Outcome of one pipeline run: the new (not yet persisted) dataset version plus connector warnings.</summary>
public sealed record ExtractPipelineResult(DatasetVersion Version, IReadOnlyList<string> Warnings);

/// <summary>
/// connector → <see cref="DuckDbExtractWriter"/> → Parquet → Blob. Produces a new immutable <see cref="DatasetVersion"/>
/// (a refresh of <c>previous</c> when given); persisting it is the caller's job, so the pipeline has no database
/// dependency and can run in the Worker, a test or a CLI. Scratch files are always removed.
/// </summary>
public sealed partial class ExtractPipeline(
    IConnectorRegistry connectors,
    IExtractUploader uploader,
    IOptions<ConnectorOptions> options,
    TimeProvider clock,
    ILogger<ExtractPipeline> logger)
{
    public static readonly ActivitySource ActivitySource = new("InsightFlow.Connectors");

    public async Task<ExtractPipelineResult> RunAsync(ExtractRequest request, DatasetVersion? previous, string createdBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (previous is not null && previous.TenantId != request.Tenant)
        {
            throw new InvalidOperationException("The previous version belongs to another tenant.");
        }

        using var activity = ActivitySource.StartActivity("extract.run");
        activity?.SetTag("insightflow.source_kind", request.Profile.Kind.ToString());
        var started = Stopwatch.GetTimestamp();
        var connector = connectors.Resolve(request.Profile.Kind);
        var workDirectory = Path.Combine(options.Value.WorkDirectory, Guid.NewGuid().ToString("N"));

        try
        {
            ExtractResult result;
            ExtractOutput output;
            await using (var writer = await DuckDbExtractWriter.CreateAsync(workDirectory, options.Value.MemoryLimit, cancellationToken))
            {
                result = await connector.ExtractAsync(request, writer, cancellationToken);
                output = await writer.CompleteAsync(cancellationToken);
            }

            var id = DatasetVersion.NewId();
            await uploader.UploadAsync(request.Tenant, id, output.ParquetPath, cancellationToken);

            var now = clock.GetUtcNow();
            var version = previous is null
                ? DatasetVersion.CreateSource(id, request.Tenant, output.Schema, output.RowCount, createdBy, now)
                : DatasetVersion.CreateExtract(id, previous, output.Schema, output.RowCount, createdBy, now);

            activity?.SetTag("insightflow.rows", output.RowCount);
            LogExtracted(logger, request.Profile.Kind.ToString(), id, output.RowCount, output.Schema.Columns.Count, Stopwatch.GetElapsedTime(started).TotalSeconds);
            return new ExtractPipelineResult(version, result.Warnings);
        }
        finally
        {
            TryDelete(workDirectory);
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Scratch space; a locked file is removed by the next cleanup.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Extracted {SourceKind} into dataset version {DatasetVersionId}: {RowCount} rows, {ColumnCount} columns in {Seconds:F1}s")]
    private static partial void LogExtracted(ILogger logger, string sourceKind, Guid datasetVersionId, long rowCount, int columnCount, double seconds);
}
