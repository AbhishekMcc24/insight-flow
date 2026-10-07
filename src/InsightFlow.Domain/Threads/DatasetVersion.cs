using InsightFlow.Domain.Tenancy;

namespace InsightFlow.Domain.Threads;

/// <summary>How a dataset version came to exist.</summary>
public enum DatasetVersionKind
{
    /// <summary>First ingestion of a source (uploaded file or connector extract). No parents.</summary>
    Source,

    /// <summary>A refresh of an existing source. Exactly one parent: the version it supersedes.</summary>
    Extract,

    /// <summary>The result of SQL (usually AI-written) over one or more parent versions.</summary>
    Derived,
}

/// <summary>
/// An immutable node in the Data Thread DAG (D12): a Parquet extract plus how it was produced. New data never
/// overwrites an old version; it becomes a child, so every chart and every AI answer can be reproduced and any
/// node can be branched from. Instances are created only through the factory methods, which enforce the
/// per-kind invariants.
/// </summary>
public sealed class DatasetVersion : ITenantOwned
{
    /// <summary>Upper bound on parents of a derived version (the sandbox exposes them as input, input_2, …).</summary>
    public const int MaxParents = 8;

    private DatasetVersion()
    {
        ParentIds = [];
        Schema = DatasetSchema.Empty;
        ParquetPath = string.Empty;
        CreatedBy = string.Empty;
    }

    public Guid Id { get; private init; }

    public TenantId TenantId { get; private init; }

    public IReadOnlyList<Guid> ParentIds { get; private init; }

    public DatasetVersionKind Kind { get; private init; }

    /// <summary>DuckDB SQL that produced a <see cref="DatasetVersionKind.Derived"/> version; null otherwise.</summary>
    public string? SqlText { get; private init; }

    /// <summary>Natural-language request that led to the SQL, if any (shown in the thread, never logged).</summary>
    public string? Prompt { get; private init; }

    /// <summary>Blob path of the Parquet file (see <see cref="StoragePaths.Extract"/>).</summary>
    public string ParquetPath { get; private init; }

    public DatasetSchema Schema { get; private init; }

    public long RowCount { get; private init; }

    public string CreatedBy { get; private init; }

    public DateTimeOffset CreatedAt { get; private init; }

    /// <summary>Ids are allocated before the Parquet file is written because the blob path contains the id.</summary>
    public static Guid NewId() => Guid.CreateVersion7();

    public static DatasetVersion CreateSource(
        Guid id, TenantId tenant, DatasetSchema schema, long rowCount, string createdBy, DateTimeOffset createdAt) =>
        Create(id, tenant, DatasetVersionKind.Source, [], null, null, schema, rowCount, createdBy, createdAt);

    public static DatasetVersion CreateExtract(
        Guid id, DatasetVersion previous, DatasetSchema schema, long rowCount, string createdBy, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(previous);
        if (previous.Kind == DatasetVersionKind.Derived)
        {
            throw new DomainRuleException("extract_of_derived", "Only source or extract versions can be refreshed.");
        }

        return Create(id, previous.TenantId, DatasetVersionKind.Extract, [previous], null, null, schema, rowCount, createdBy, createdAt);
    }

    public static DatasetVersion CreateDerived(
        Guid id,
        IReadOnlyList<DatasetVersion> parents,
        string sqlText,
        string? prompt,
        DatasetSchema schema,
        long rowCount,
        string createdBy,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(parents);
        if (string.IsNullOrWhiteSpace(sqlText))
        {
            throw new DomainRuleException("derived_requires_sql", "A derived version must record the SQL that produced it.");
        }

        if (parents.Count is 0 or > MaxParents)
        {
            throw new DomainRuleException("derived_parent_count", $"A derived version needs between 1 and {MaxParents} parents.");
        }

        return Create(id, parents[0].TenantId, DatasetVersionKind.Derived, parents, sqlText, prompt, schema, rowCount, createdBy, createdAt);
    }

    private static DatasetVersion Create(
        Guid id,
        TenantId tenant,
        DatasetVersionKind kind,
        IReadOnlyList<DatasetVersion> parents,
        string? sqlText,
        string? prompt,
        DatasetSchema schema,
        long rowCount,
        string createdBy,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(createdBy);

        if (id == Guid.Empty)
        {
            throw new DomainRuleException("id_required", "A dataset version needs an id.");
        }

        if (tenant.IsEmpty)
        {
            throw new DomainRuleException("tenant_required", "A dataset version needs a tenant.");
        }

        if (parents.Any(p => p.TenantId != tenant))
        {
            throw new DomainRuleException("cross_tenant_lineage", "All parents must belong to the same tenant.");
        }

        if (parents.Any(p => p.Id == id))
        {
            throw new DomainRuleException("self_parent", "A dataset version cannot be its own parent.");
        }

        if (rowCount < 0)
        {
            throw new DomainRuleException("negative_row_count", "Row count cannot be negative.");
        }

        return new DatasetVersion
        {
            Id = id,
            TenantId = tenant,
            Kind = kind,
            ParentIds = parents.Select(p => p.Id).Distinct().ToArray(),
            SqlText = sqlText,
            Prompt = prompt,
            ParquetPath = StoragePaths.Extract(tenant, id),
            Schema = schema,
            RowCount = rowCount,
            CreatedBy = createdBy,
            CreatedAt = createdAt,
        };
    }
}
