using InsightFlow.Domain.Tenancy;

namespace InsightFlow.Domain.Knowledge;

/// <summary>Where a definition came from.</summary>
public enum DefinitionSource
{
    /// <summary>Read from a connected database's catalog.</summary>
    Database,

    /// <summary>Created from a dataset in the Shared workspace.</summary>
    Dataset,

    /// <summary>Typed in by a person.</summary>
    Manual,

    /// <summary>Proposed by the AI and accepted by a person.</summary>
    Ai,
}

/// <summary>
/// A table (or view) in the knowledge base: what it is called, which connection it came from, and what it means.
/// Tables are the nodes the knowledge graph is built from. Definitions are edited by people and enriched by AI; nothing
/// reaches Neo4j until someone pushes the graph.
/// </summary>
public sealed class KnowledgeTable : ITenantOwned
{
    public const int MaxNameLength = 200;
    public const int MaxSchemaLength = 128;
    public const int MaxDescriptionLength = 1_000;

    private KnowledgeTable()
    {
        Name = string.Empty;
    }

    public Guid Id { get; private init; }

    public TenantId TenantId { get; private init; }

    /// <summary>The saved connection it was imported from; null for manual tables and datasets.</summary>
    public Guid? ConnectionId { get; private init; }

    /// <summary>The database on the connection's server the table lives in (null for manual tables and datasets).</summary>
    public string? DatabaseName { get; private init; }

    /// <summary>The dataset version holding a copy (Parquet extract) of the table, made when it was first queried; null until then.</summary>
    public Guid? ExtractVersionId { get; private set; }

    public DateTimeOffset? ExtractedAt { get; private set; }

    public long? ExtractRows { get; private set; }

    /// <summary>True when the table had more rows than the extract limit, so the copy holds only the first rows.</summary>
    public bool ExtractTruncated { get; private set; }

    public string? SchemaName { get; private set; }

    public string Name { get; private set; }

    public string? Description { get; private set; }

    public DefinitionSource Source { get; private init; }

    public DateTimeOffset CreatedAt { get; private init; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>The name shown to people and used as the graph label: <c>database.schema.name</c>, leaving out the parts that are not set.</summary>
    /// <summary>
    /// True for tables that mirror a database or a dataset: their columns, names and types come from the source and cannot be added to,
    /// renamed or retyped here (the source itself is never modified by this application). Descriptions can always be edited, and
    /// individual columns can be removed from the knowledge base.
    /// </summary>
    public bool IsStructureLocked => Source is DefinitionSource.Database or DefinitionSource.Dataset;

    public string DisplayName => string.Join(".", new[] { DatabaseName, SchemaName, Name }.Where(p => !string.IsNullOrEmpty(p)));

    public static KnowledgeTable Create(
        TenantId tenant, Guid? connectionId, string? schemaName, string name, string? description, DefinitionSource source, DateTimeOffset now, string? databaseName = null)
    {
        if (tenant.IsEmpty)
        {
            throw new DomainRuleException("tenant_required", "A table needs a tenant.");
        }

        var table = new KnowledgeTable
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant,
            ConnectionId = connectionId,
            DatabaseName = string.IsNullOrWhiteSpace(databaseName) ? null : databaseName.Trim(),
            Source = source,
            CreatedAt = now,
        };
        table.Apply(schemaName, name, description, now);
        return table;
    }

    /// <summary>Remembers the latest copy of the table's rows, which questions are answered from.</summary>
    public void RecordExtract(Guid versionId, long rows, bool truncated, DateTimeOffset now)
    {
        ExtractVersionId = versionId;
        ExtractRows = rows;
        ExtractTruncated = truncated;
        ExtractedAt = now;
    }

    public void Update(string? schemaName, string name, string? description, DateTimeOffset now) => Apply(schemaName, name, description, now);

    public void SetDescription(string? description, DateTimeOffset now)
    {
        Description = Clean(description, MaxDescriptionLength, "invalid_description");
        UpdatedAt = now;
    }

    private void Apply(string? schemaName, string name, string? description, DateTimeOffset now)
    {
        var trimmedName = name?.Trim() ?? string.Empty;
        if (trimmedName.Length is 0 or > MaxNameLength)
        {
            throw new DomainRuleException("invalid_table_name", $"A table name must be 1–{MaxNameLength} characters.");
        }

        var trimmedSchema = string.IsNullOrWhiteSpace(schemaName) ? null : schemaName.Trim();
        if (trimmedSchema is { Length: > MaxSchemaLength })
        {
            throw new DomainRuleException("invalid_schema_name", $"A schema name can have at most {MaxSchemaLength} characters.");
        }

        SchemaName = trimmedSchema;
        Name = trimmedName;
        Description = Clean(description, MaxDescriptionLength, "invalid_description");
        UpdatedAt = now;
    }

    internal static string? Clean(string? text, int max, string code)
    {
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        return value.Length > max ? throw new DomainRuleException(code, $"Text can have at most {max} characters.") : value;
    }
}

/// <summary>A column of a <see cref="KnowledgeTable"/>, with its type as the source calls it and a business description.</summary>
public sealed class KnowledgeColumn : ITenantOwned
{
    public const int MaxNameLength = 200;
    public const int MaxTypeLength = 100;
    public const int MaxDescriptionLength = 1_000;

    private KnowledgeColumn()
    {
        Name = string.Empty;
        DataType = string.Empty;
    }

    public Guid Id { get; private init; }

    public TenantId TenantId { get; private init; }

    public Guid TableId { get; private init; }

    public string Name { get; private set; }

    public string DataType { get; private set; }

    public string? Description { get; private set; }

    public bool IsPrimaryKey { get; private set; }

    public bool IsNullable { get; private set; }

    public int Ordinal { get; private set; }

    public static KnowledgeColumn Create(
        KnowledgeTable table, string name, string dataType, string? description, bool isPrimaryKey, bool isNullable, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(table);
        var column = new KnowledgeColumn { Id = Guid.CreateVersion7(), TenantId = table.TenantId, TableId = table.Id };
        column.Apply(name, dataType, description, isPrimaryKey, isNullable, ordinal);
        return column;
    }

    public void Update(string name, string dataType, string? description, bool isPrimaryKey, bool isNullable, int ordinal) =>
        Apply(name, dataType, description, isPrimaryKey, isNullable, ordinal);

    public void SetDescription(string? description) =>
        Description = KnowledgeTable.Clean(description, MaxDescriptionLength, "invalid_description");

    private void Apply(string name, string dataType, string? description, bool isPrimaryKey, bool isNullable, int ordinal)
    {
        var trimmedName = name?.Trim() ?? string.Empty;
        if (trimmedName.Length is 0 or > MaxNameLength)
        {
            throw new DomainRuleException("invalid_column_name", $"A column name must be 1–{MaxNameLength} characters.");
        }

        var trimmedType = string.IsNullOrWhiteSpace(dataType) ? "unknown" : dataType.Trim();
        if (trimmedType.Length > MaxTypeLength)
        {
            throw new DomainRuleException("invalid_column_type", $"A data type can have at most {MaxTypeLength} characters.");
        }

        Name = trimmedName;
        DataType = trimmedType;
        Description = KnowledgeTable.Clean(description, MaxDescriptionLength, "invalid_description");
        IsPrimaryKey = isPrimaryKey;
        IsNullable = isNullable;
        Ordinal = ordinal;
    }
}

/// <summary>A link between two columns: a foreign key from the database, an AI suggestion a person accepted, or one added by hand.</summary>
public sealed class KnowledgeRelationship : ITenantOwned
{
    public const int MaxReasonLength = 300;

    private KnowledgeRelationship()
    {
        Reason = string.Empty;
    }

    public Guid Id { get; private init; }

    public TenantId TenantId { get; private init; }

    public Guid FromColumnId { get; private init; }

    public Guid ToColumnId { get; private init; }

    public RelationshipKind Kind { get; private set; }

    public double Confidence { get; private set; }

    public string Reason { get; private set; }

    public DefinitionSource Source { get; private init; }

    /// <summary>
    /// Set when several column pairs together form one relationship (a composite key: every pair must match at once). All pairs of the
    /// relationship share the id; null for an ordinary single-pair relationship.
    /// </summary>
    public Guid? GroupId { get; private init; }

    /// <summary>The most column pairs one composite relationship can have.</summary>
    public const int MaxGroupSize = 16;

    public static KnowledgeRelationship Create(
        TenantId tenant, Guid fromColumnId, Guid toColumnId, RelationshipKind kind, double confidence, string? reason, DefinitionSource source, Guid? groupId = null)
    {
        if (fromColumnId == toColumnId)
        {
            throw new DomainRuleException("invalid_relationship", "A column cannot be related to itself.");
        }

        if (!Enum.IsDefined(kind) || double.IsNaN(confidence))
        {
            throw new DomainRuleException("invalid_relationship", "The relationship kind or confidence is not valid.");
        }

        var text = reason?.Trim() ?? string.Empty;
        return new KnowledgeRelationship
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant,
            FromColumnId = fromColumnId,
            ToColumnId = toColumnId,
            Kind = kind,
            Confidence = Math.Clamp(confidence, 0, 1),
            Reason = text.Length > MaxReasonLength ? text[..MaxReasonLength] : text,
            Source = source,
            GroupId = groupId,
        };
    }
}

/// <summary>
/// A database on a connection's server that the knowledge base draws tables from, with a description of what it holds. Descriptions
/// start here and run down through the tables to the columns, so a question can be matched to the right database first. A server can
/// have several databases and relationships may cross them. Rows are created when a description is first written; a database with no
/// row simply has no description yet.
/// </summary>
public sealed class KnowledgeDatabase : ITenantOwned
{
    public const int MaxNameLength = 128;
    public const int MaxDescriptionLength = 1_000;

    private KnowledgeDatabase()
    {
        Name = string.Empty;
    }

    public Guid Id { get; private init; }

    public TenantId TenantId { get; private init; }

    /// <summary>The saved connection (server) the database is on; null for tables that have no connection.</summary>
    public Guid? ConnectionId { get; private init; }

    public string Name { get; private init; }

    public string? Description { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static KnowledgeDatabase Create(TenantId tenant, Guid? connectionId, string name, string? description, DateTimeOffset now)
    {
        if (tenant.IsEmpty)
        {
            throw new DomainRuleException("tenant_required", "A database needs a tenant.");
        }

        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > MaxNameLength)
        {
            throw new DomainRuleException("invalid_database_name", $"A database name must be 1–{MaxNameLength} characters.");
        }

        var database = new KnowledgeDatabase { Id = Guid.CreateVersion7(), TenantId = tenant, ConnectionId = connectionId, Name = trimmed };
        database.SetDescription(description, now);
        return database;
    }

    public void SetDescription(string? description, DateTimeOffset now)
    {
        Description = KnowledgeTable.Clean(description, MaxDescriptionLength, "invalid_description");
        UpdatedAt = now;
    }
}
