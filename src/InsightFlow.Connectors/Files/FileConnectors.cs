using System.Runtime.CompilerServices;
using InsightFlow.Connectors.Extraction;
using InsightFlow.Domain.Connections;

namespace InsightFlow.Connectors.Files;

/// <summary>
/// Shared behaviour of connectors that read one uploaded file. The profile is transient and carries the
/// <c>storedFileId</c>; the file is downloaded to scratch space and imported by DuckDB's native, streaming reader.
/// </summary>
public abstract class FileConnectorBase(ISourceFileAccessor files) : IDataSourceConnector
{
    /// <summary>The single "table" a file exposes.</summary>
    public const string FileTableId = "file";

    public abstract DataSourceKind Kind { get; }

    public ConnectorCapabilities Capabilities => ConnectorCapabilities.FileOnly;

    protected abstract ExtractFileFormat Format { get; }

    protected abstract string Extension { get; }

    public async Task<ConnectionTestResult> TestAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!TryGetFileId(profile, out var fileId))
        {
            return ConnectionTestResult.Failed("No uploaded file is referenced.");
        }

        return await files.ExistsAsync(profile.TenantId, fileId, cancellationToken)
            ? ConnectionTestResult.Ok("The file is available.")
            : ConnectionTestResult.Failed("The uploaded file no longer exists.");
    }

    public async IAsyncEnumerable<SourceTable> DiscoverAsync(ConnectionProfile profile, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        yield return new SourceTable(FileTableId, profile.Name, Kind: "file");
    }

    public async Task<ExtractResult> ExtractAsync(ExtractRequest request, IExtractWriter writer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(writer);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Table is not (null or FileTableId))
        {
            throw new ConnectorException($"A file has a single table named '{FileTableId}'.");
        }

        if (!TryGetFileId(request.Profile, out var fileId))
        {
            throw new ConnectorException("No uploaded file is referenced.");
        }

        await using var local = await files.DownloadAsync(request.Tenant, fileId, Extension, cancellationToken);
        await writer.ImportFileAsync(local.Path, Format, request.MaxRows, cancellationToken);
        return ExtractResult.Rows(-1);
    }

    private static bool TryGetFileId(ConnectionProfile profile, out Guid fileId) =>
        Guid.TryParse(profile.GetSetting(ConnectionProfile.StoredFileIdSetting), out fileId);
}

/// <summary>CSV / TSV files (delimiter, header and types auto-detected by DuckDB).</summary>
public sealed class CsvConnector(ISourceFileAccessor files) : FileConnectorBase(files)
{
    public override DataSourceKind Kind => DataSourceKind.Csv;

    protected override ExtractFileFormat Format => ExtractFileFormat.Csv;

    protected override string Extension => ".csv";
}

/// <summary>Parquet files (schema taken from the file).</summary>
public sealed class ParquetConnector(ISourceFileAccessor files) : FileConnectorBase(files)
{
    public override DataSourceKind Kind => DataSourceKind.Parquet;

    protected override ExtractFileFormat Format => ExtractFileFormat.Parquet;

    protected override string Extension => ".parquet";
}
